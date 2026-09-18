// TakeTopDshTeam — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This software is the intellectual property of 泰顶拓鼎信息科技（上海）有限公司
// (TaiDingTuoDing Information Technology (Shanghai) Co., Ltd.). All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TakeTopDshLauncher;

public class DshService
{
    private readonly string _root;
    private readonly string _dshHome;
    private Process? _proc;
    private int _currentPort = 46000;
    private readonly ConcurrentQueue<string> _logs = new();
    private int _maxLog = 1000;
    private string? _tokenUrl;   // live "dsh web:" URL, captured on stdout (survives log rolling)
    private readonly object _gate = new();
    // Session backup (copy dsh sessions into docs/ for shared experience).
    private readonly CancellationTokenSource _backupCts = new();
    private int _backupIntervalSec = 60;      // seconds between scans
    private readonly Dictionary<string, (DateTime lwt, long len)> _backupSeen = new();
    private InstanceManager? _instMgr;

    public DshService(string root)
    {
        _root = root;
        var wsHome = Path.Combine(ResolveWorkspacePath(root), ".dsh");
        var legacyHome = Path.Combine(root, ".dsh");
        _dshHome = Directory.Exists(wsHome) ? wsHome : (Directory.Exists(legacyHome) ? legacyHome : wsHome);
        // Move any legacy install-root settings into the workspace config once.
        try { EnsureSettingsMigrated(); } catch { }
    }

    public void SetInstanceManager(InstanceManager mgr) => _instMgr = mgr;

    public string Root => _root;

    public bool IsRunning => _proc is { HasExited: false };

    public int? CurrentPid => _proc is { HasExited: false } ? _proc.Id : null;

    public string NodeExe()
    {
        if (OperatingSystem.IsWindows())
        {
            var win = Path.Combine(_root, "node", "node.exe");
            if (File.Exists(win)) return win;
            return "node";
        }
        // Linux / macOS: platform sub-dir
        var sub = PlatformSubDir();
        var p = Path.Combine(_root, "node", sub, "bin", "node");
        return File.Exists(p) ? p : "node";
    }

    public string BinJs()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(_root, "node", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        }
        else
        {
            var sub = PlatformSubDir();
            return Path.Combine(_root, "node", sub, "lib", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        }
    }

    private static string PlatformSubDir()
    {
        if (OperatingSystem.IsWindows()) return "";
        if (OperatingSystem.IsMacOS()) return "macos-arm64";
        // Linux
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        return arch.Equals("Arm64", StringComparison.OrdinalIgnoreCase) ? "linux-arm64" : "linux-x64";
    }

    public object Status()
    {
        if (_proc is { HasExited: false })
        {
            // A live process is NOT enough: DSH may still be loading, or stuck in an
            // uninterruptible-I/O hang so it never binds the port. Only report
            // "running" once the HTTP service actually answers, otherwise report
            // "starting" so the control page never shows a false "Running".
            return IsDshReady(_currentPort)
                ? new { state = "running", pid = _proc.Id }
                : new { state = "starting", pid = _proc.Id };
        }
        // If our process is gone but the port still serves HTTP, an external or
        // pre-existing dsh web instance is running — treat it as running.
        if (IsPortInUse(_currentPort) && IsDshReady(_currentPort))
            return new { state = "running", pid = (int?)null, external = true };
        if (_proc is { HasExited: true })
            return new { state = "stopped", exitCode = _proc.ExitCode };
        return new { state = "stopped" };
    }

    public IReadOnlyList<string> Logs(int from)
    {
        var all = _logs.ToArray();
        return from < all.Length ? all[from..] : Array.Empty<string>();
    }

    public int TotalLogCount => _logs.Count;

    // Cached set of locally-listening TCP ports. A plain TcpClient.Connect probe
    // could hang ~2s per call on machines where the SYN is dropped (firewall /
    // sandbox), and startup probes every instance twice -> several seconds. The
    // IP global listener table is one cheap call, reused for a short TTL.
    private static readonly object _listenLock = new();
    private static HashSet<int> _listenCache = new();
    private static DateTime _listenAt = DateTime.MinValue;

    public static bool IsPortInUse(int port)
    {
        lock (_listenLock)
        {
            if ((DateTime.UtcNow - _listenAt).TotalSeconds >= 2)
            {
                var set = new HashSet<int>();
                try
                {
                    foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                        set.Add(ep.Port);
                }
                catch { }
                _listenCache = set;
                _listenAt = DateTime.UtcNow;
            }
            return _listenCache.Contains(port);
        }
    }

    // Uncached "is something listening on this port right now?" — used for config
    // validation (a just-started service must be seen immediately).
    public static bool IsPortListening(int port)
    {
        try
        {
            foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == port) return true;
        }
        catch { }
        return false;
    }

    // Stronger "is this port usable?" check: only true if we can actually BIND
    // 127.0.0.1:port right now. Catches ports reserved/excluded by the OS that a
    // plain connect test would wrongly report as free.
    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // A port being open (TCP) does not mean DSH's web service is actually ready:
    // right after a reboot a stale/orphaned dsh may still hold the port while our
    // own instance is being (re)started, and proxying to it yields a broken page
    // ("site not found"). Probe the HTTP endpoint: DSH returns 404/401 on "/" when
    // no auth token is present, which is NORMAL — any HTTP response (even an error
    // status) means the DSH web service is up and safe to proxy to. Only a failure
    // to connect / no HTTP response at all means it is not ready yet.
    public static bool IsDshReady(int port)
    {
        try
        {
            using var h = new System.Net.Http.HttpClient();
            h.Timeout = TimeSpan.FromSeconds(3);
            using var resp = h.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
            // Ready only once DSH serves real content: it answers 401 (auth) / 200
            // (with token) when up, and 404 during the brief window before its web
            // routes are mounted. Treat 404 as "still starting" so the launcher shows
            // the spinner instead of proxying a transient 404 to the browser.
            return resp.StatusCode != System.Net.HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }

    public void Start(int port)
    {
        lock (_gate)
        {
            _currentPort = port;
            // A previous DSH crash can leave a stale writer lock behind; on the next
            // boot atomic-write then times out and DSH never starts. Remove it first.
            try
            {
                var staleLock = Path.Combine(_dshHome, "profiles", "node_modules.lock");
                if (File.Exists(staleLock))
                {
                    File.Delete(staleLock);
                    AddLog("[dsh] removed stale profiles/node_modules.lock");
                }
            }
            catch { /* best effort */ }
            if (_proc is { HasExited: false }) return;

            // If the target port is already serving, it is likely a stale (orphaned)
            // dsh left over from a previous launcher that exited. Reusing it means we
            // never capture its token access line, so the control page blocks. Try to
            // reclaim the port so we start our own dsh and always capture the URL.
            if (IsPortInUse(port))
            {
                AddLog($"[dsh] Port {port} is in use; reclaiming it from a stale instance...");
                bool stopped = StopPortOwner(port);
                AddLog(stopped
                    ? $"[dsh] Stopped the process holding port {port}."
                    : $"[dsh] Could not stop the process on port {port}; reusing existing DSH Web.");
                Thread.Sleep(1500);
                if (IsPortInUse(port))
                {
                    AddLog($"[dsh] Port {port} is still in use — reusing existing DSH Web.");
                    return;
                }
            }

            var node = NodeExe();
            if (!File.Exists(node) && !node.Equals("node", StringComparison.Ordinal))
            {
                AddLog($"[dsh] Node.js not found: {node}");
                return;
            }
            var bin = BinJs();
            if (!File.Exists(bin))
            {
                AddLog($"[dsh] DSH launcher not found: {bin}");
                return;
            }

            // If an admin has set a workspace path, inject it into dsh's cordis
            // config so the sandbox/cwd points there. Never allow the workspace to
            // be dsh's own install dir (this app) — that would let the AI modify
            // the launcher/dsh itself. Sanitize before applying.
            var ws = ReadWorkspacePath();
            ws = SanitizeWorkspace(ws);
            if (!string.IsNullOrWhiteSpace(ws))
                ApplyWorkspace(ws);

            // Re-apply our DSH package patches (lost on any DSH upgrade). The admin
            // default dsh keeps its settings trigger visible; per-user instances hide it.
            try
            {
                DshPatcher.ApplyAll(_root, hideSettings: false, m => AddLog("[patch] " + m));
            }
            catch (Exception ex) { AddLog($"[patch] failed: {ex.Message}"); }

            AddLog($"[dsh] Starting DSH Web on port {port} ...");
            var psi = new ProcessStartInfo
            {
                FileName = node,
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--expose-internals");
            psi.ArgumentList.Add(bin);
            psi.ArgumentList.Add("--profile");
            psi.ArgumentList.Add("web");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());
            // dsh intentionally rejects --host 0.0.0.0 (RCE exposure), so it always
            // stays on 127.0.0.1. External access is meant to go through a hardened
            // reverse proxy (see the deploy guide). When an external URL is
            // configured, tell dsh to trust that host so the proxy works.
            var extHost = ExternalHost();
            if (!string.IsNullOrEmpty(extHost))
            {
                psi.ArgumentList.Add("--trusted-host");
                psi.ArgumentList.Add(extHost);
            }
            psi.ArgumentList.Add("--no-open");
            psi.Environment["DSH_HOME"] = _dshHome;

            _proc = new Process { StartInfo = psi };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) { CaptureToken(e.Data); AddLog(e.Data); } };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AddLog("[ERR] " + e.Data); };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            AddLog($"[dsh] DSH Web started (PID {_proc.Id})");
        }
    }

    // Guard: the workspace must never be (or contain) this app's own install dir,
    // otherwise the AI could modify the TakeTopDSH / dsh code itself. Returns a
    // safe workspace, or "" (empty => dsh default root) when the value is unsafe.
    private string SanitizeWorkspace(string? ws)
    {
        if (string.IsNullOrWhiteSpace(ws)) return "";
        var target = Path.GetFullPath(ws.Trim());
        var self = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var selfPrefix = self + Path.DirectorySeparatorChar;
        // Allow the dedicated data folder <root>\WorkSpace (and anything under it);
        // keep the rest of the install tree (app code, node, .dsh) off-limits so the
        // AI can never modify the TakeTopDSH / dsh code itself.
        var dataDir = Path.Combine(self, "WorkSpace");
        var dataPrefix = dataDir + Path.DirectorySeparatorChar;
        if (target.Equals(dataDir, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
            return target;
        if (target.Equals(self, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
        {
            AddLog($"[dsh] Workspace '{target}' is the app's own directory — rejected for safety (may not modify TakeTopDSH itself).");
            return "";  // fall back to dsh default root (empty)
        }
        return target;
    }

    // Update dsh's cordis patch so its sandbox workspace root / cwd point to the
    // admin-configured workspace directory, and downgrade the sandbox to
    // workspace-write so the AI can only modify files inside the workspace root
    // (never the whole filesystem / dsh itself). Also inject persona to restrict
    // reads to workspace + docs only.
    private void ApplyWorkspace(string workspace)
    {
        try
        {
            var patchFile = Path.Combine(_dshHome, "profiles", "web", "cordis.patch.yml");
            try { Directory.CreateDirectory(Path.GetDirectoryName(patchFile)!); } catch { }

            var normalized = workspace.Replace('\\', '/');

            // Build the complete cordis.patch.yml with sandbox + persona restrictions.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Auto-generated by launcher — do not edit manually.");
            sb.AppendLine("# Sandbox: workspace-write prevents writes outside the workspace.");
            sb.AppendLine("# Persona: instructs the AI to restrict reads to workspace + docs only.");
            sb.AppendLine();
            sb.AppendLine("- id: sandbox-policy");
            sb.AppendLine("  config:");
            sb.AppendLine($"    workspaceRoot: {normalized}");
            sb.AppendLine();
            sb.AppendLine("- id: fs-sandbox");
            sb.AppendLine("  config:");
            sb.AppendLine($"    cwd: {normalized}");
            sb.AppendLine("    mode: workspace-write");
            sb.AppendLine();
            // Disable every shell/terminal tool: arbitrary commands can read any host
            // file, and there is no reliable OS-level sandbox on Windows here, so the
            // only enforcement that holds is to keep the agent on the (sandboxed) fs
            // tools. Applies to the admin default too.
            sb.AppendLine("- id: tool-bash");
            sb.AppendLine("  disabled: true");
            sb.AppendLine();
            sb.AppendLine("- id: tool-pwsh");
            sb.AppendLine("  disabled: true");
            sb.AppendLine();
            sb.AppendLine("- id: tool-bash-persistent");
            sb.AppendLine("  disabled: true");
            sb.AppendLine();
            sb.AppendLine("- id: tool-pwsh-persistent");
            sb.AppendLine("  disabled: true");
            sb.AppendLine();

            // System prompt persona: restrict file access for non-admin users.
            sb.AppendLine("- id: system-prompt");
            sb.AppendLine("  config:");
            sb.AppendLine("    persona: >-");
            sb.AppendLine("      You are a coding agent powered by the {{model}} model. Your working directory is {{cwd}}.");
            sb.AppendLine();
            sb.AppendLine("      FILE ACCESS RULES (STRICTLY ENFORCED):");
            sb.AppendLine($"      1. You may ONLY read and write files within your workspace at {{{{cwd}}}}.");
            sb.AppendLine("      2. You must NOT access, read, list, or reference any files or directories outside your workspace.");
            sb.AppendLine("      3. If a task requires accessing files outside the workspace, inform the user that access is restricted.");
            sb.AppendLine("      4. Use web search or web fetch tools for external resources (public internet) instead of local file access.");
            sb.AppendLine();
            sb.AppendLine("      SHARED EXPERIENCE:");
            sb.AppendLine("      The team's past sessions are exported as Markdown files inside your workspace, under the 'shared-sessions/' folder (one file per day, named sessions-YYYY-MM-DD.md).");
            sb.AppendLine("      Use glob/grep to search those files and read to inspect them when you want to learn from how other members handled a similar task.");

            File.WriteAllText(patchFile, sb.ToString());
            AddLog($"[dsh] Applied workspace root: {workspace} (sandbox: workspace-write)");
        }
        catch (Exception ex)
        {
            AddLog($"[dsh] Could not apply workspace path: {ex.Message}");
        }

        // The default dsh is the admin's surface: keep the "设置" (settings)
        // sidebar trigger visible so the admin can open the DSH settings page,
        // and hard-set its workspace to the configured global workspace path.
        InstanceManager.SetSettingsVisible(_root, true);
        InstanceManager.EnsureAdminWorkspace(_root, _dshHome, workspace, new DirectoryInfo(workspace).Name);
    }

    // Re-sync the admin default dsh's sandbox + workspace.json with the
    // currently-configured workspace path (call after saving the path).
    public void ApplyWorkspaceToDefault()
    {
        try
        {
            var ws = SanitizeWorkspace(ReadWorkspacePath());
            if (!string.IsNullOrWhiteSpace(ws))
                ApplyWorkspace(ws);
        }
        catch (Exception ex)
        {
            AddLog($"[dsh] Could not reapply default workspace: {ex.Message}");
        }
    }

    // Stop the process currently listening on the given port (cross-platform).
    private static bool StopPortOwner(int port)
    {
        try
        {
            var ids = new List<int>();
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains($":{port} ") && line.Contains("LISTENING"))
                    {
                        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
                            ids.Add(pid);
                    }
                }
            }
            else
            {
                // macOS / Linux: prefer `ss`, fall back to `lsof`. On minimal Linux
                // lsof is often absent, so handle both output shapes.
                var cmd = "ss";
                var args = "-ltnp";
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        // ss: users:(("node",pid=1234,fd=20)) -> 1234
                        var pidm = System.Text.RegularExpressions.Regex.Match(line, @"pid=(\d+)");
                        if (pidm.Success && int.TryParse(pidm.Groups[1].Value, out var pid))
                            ids.Add(pid);
                    }
                }
                // Fall back to lsof when ss yielded nothing.
                if (ids.Count == 0)
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "lsof",
                        Arguments = $"-i tcp:{port} -sTCP:LISTEN -t",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var lp = Process.Start(psi);
                    if (lp != null)
                    {
                        var output = lp.StandardOutput.ReadToEnd();
                        lp.WaitForExit(3000);
                        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            if (int.TryParse(line.Trim(), out var pid)) ids.Add(pid);
                    }
                }
            }
            if (ids.Count == 0) return false;
            foreach (var pid in ids.Distinct())
            {
                try { Process.GetProcessById(pid).Kill(true); return true; }
                catch { /* already gone or access denied */ }
            }
            return false;
        }
        catch { return false; }
    }

    // PIDs currently LISTENING on a loopback port.
    private static List<int> PortOwnerPids(int port)
    {
        var ids = new List<int>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in output.Split('\n'))
                    if (line.Contains($":{port} ") && line.Contains("LISTENING"))
                    {
                        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && int.TryParse(parts[^1], out var pid)) ids.Add(pid);
                    }
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ss",
                    Arguments = "-ltnp",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pidm = System.Text.RegularExpressions.Regex.Match(line, @"pid=(\d+)");
                        if (pidm.Success && int.TryParse(pidm.Groups[1].Value, out var pid)) ids.Add(pid);
                    }
                }
            }
        }
        catch { }
        return ids;
    }

    // True when the process holding the port is a node process (i.e. looks like a
    // leftover DSH from a previous run) rather than some unrelated service.
    private static bool PortHeldByNode(int port)
    {
        foreach (var pid in PortOwnerPids(port).Distinct())
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (string.Equals(Process.GetProcessById(pid).ProcessName, "node", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    var comm = File.ReadAllText("/proc/" + pid + "/comm").Trim();
                    if (comm == "node") return true;
                }
            }
            catch { }
        }
        return false;
    }

    // Pick the admin DSH port at startup: keep the configured one when free,
    // reclaim it when it's held by our own (node) leftover DSH, otherwise move to
    // the next free port and persist it. Never kills a non-DSH process, and the
    // choice is port-only — it has no effect on stored sessions/data.
    public int EnsureAdminDshPort()
    {
        var desired = DefaultPort();
        if (!IsPortInUse(desired)) return desired;
        if (PortHeldByNode(desired))
        {
            AddLog($"[dsh] port {desired} held by a stale DSH; reclaiming it");
            StopPortOwner(desired);
            for (int i = 0; i < 10 && IsPortInUse(desired); i++) Thread.Sleep(300);
            if (!IsPortInUse(desired)) return desired;
        }
        var used = new HashSet<int> { ReadLauncherPort() };
        try { foreach (var inst in _instMgr?.List() ?? new List<InstanceManager.Instance>()) used.Add(inst.DshPort); } catch { }
        for (int candidate = desired + 1; candidate <= Math.Min(desired + 200, 65535); candidate++)
        {
            if (used.Contains(candidate) || IsPortInUse(candidate)) continue;
            SaveCfgUrl("http://127.0.0.1:" + candidate);
            AddLog($"[dsh] port {desired} is in use by another process; auto-selected {candidate}");
            return candidate;
        }
        return desired;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_proc is { HasExited: false })
            {
                AddLog("[dsh] Stopping DSH Web ...");
                try { _proc.Kill(true); } catch { }
                try { _proc.WaitForExit(5000); } catch { }
            }
            _proc = null;
            AddLog("[dsh] DSH Web stopped.");
        }
    }

    // ---------- Session backup (shared experience) ----------

    // Start a background loop that periodically copies dsh session logs into
    // <admin-workspace>/sharedata/data/sessions-backup/ so the session content is
    // preserved even after a user deletes it in the dsh UI.
    public void StartSessionBackup(int intervalSec = 60)
    {
        if (intervalSec > 0) _backupIntervalSec = intervalSec;
        _ = Task.Run(async () =>
        {
            // Run one pass shortly after launch, then on the interval.
            await Task.Delay(3000, _backupCts.Token).ConfigureAwait(false);
            while (!_backupCts.IsCancellationRequested)
            {
                try { BackupSessions(); }
                catch (Exception ex) { AddLog($"[backup] error: {ex.Message}"); }
                try { await Task.Delay(_backupIntervalSec * 1000, _backupCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, _backupCts.Token);
        AddLog($"[backup] started (every {_backupIntervalSec}s)");
    }

    public void StopSessionBackup()
    {
        _backupCts.Cancel();
    }

    // Copy changed session .zstd files into the shared docs folder.
    private void BackupSessions()
    {
        var backupDir = Path.Combine(ReadWorkspacePath(), "sharedata", "data", "sessions-backup");
        Directory.CreateDirectory(backupDir);

        // Collect all sessions directories: admin root + every user instance.
        var sources = new List<(string dshHome, string label)>();
        sources.Add((_dshHome, "_admin"));
        if (_instMgr != null)
        {
            foreach (var inst in _instMgr.List())
                if (!string.IsNullOrEmpty(inst.DshHome))
                    sources.Add((inst.DshHome, $"_{inst.Id}"));
        }

        foreach (var (dshHome, label) in sources)
        {
            var sessionsRoot = Path.Combine(dshHome, "sessions");
            if (!Directory.Exists(sessionsRoot)) continue;

            var files = Directory.GetFiles(sessionsRoot, "*.zstd", SearchOption.AllDirectories);
            foreach (var src in files)
            {
                try
                {
                    var fi = new FileInfo(src);
                    var key = fi.FullName;
                    var signature = (fi.LastWriteTimeUtc, fi.Length);
                    // Skip while dsh is actively writing (recently modified / still growing).
                    if (DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromSeconds(5)) continue;
                    // Only copy if changed since last pass.
                    if (_backupSeen.TryGetValue(key, out var prev) && prev == signature) continue;

                    // Destination: <ts><label>_<sessionDir>_session.jsonl.zstd
                    var sessionId = Path.GetFileName(Path.GetDirectoryName(src) ?? "");
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var dest = Path.Combine(backupDir, $"{stamp}{label}_{sessionId}_session.jsonl.zstd");
                    File.Copy(src, dest, overwrite: true);
                    _backupSeen[key] = signature;
                    AddLog($"[backup] archived {label[1..]}/{sessionId} ({fi.Length} B)");
                }
                catch (Exception ex)
                {
                    AddLog($"[backup] skip {Path.GetFileName(src)}: {ex.Message}");
                }
            }
        }

        // Keep only the newest snapshot per session. A session's log is
        // append-only, so the newest snapshot is the COMPLETE conversation;
        // older snapshots of the same session add nothing to the shared-
        // experience export, and removing them bounds this folder's growth.
        try { DedupSessionBackups(); }
        catch (Exception ex) { AddLog($"[backup] dedup: {ex.Message}"); }

        // After mirroring .zstd snapshots, regenerate the readable per-day
        // Markdown that the AI can search ("have we done similar before").
        try { GenerateSessionMarkdown(); }
        catch (Exception ex) { AddLog($"[backup] md: {ex.Message}"); }

        // Then copy that Markdown into every instance's workspace so each user's
        // agent can read the shared experience with its normal file tools. Reads
        // are sandboxed to the workspace, so the data must live INSIDE it; the
        // loopback API path is unreachable (web_fetch blocks non-public IPs).
        try { DistributeSharedSessions(); }
        catch (Exception ex) { AddLog($"[backup] distribute: {ex.Message}"); }
    }

    // Invoke the bundled Node script to decompress sessions and write per-day MD.
    private void GenerateSessionMarkdown()
    {
        var script = Path.Combine(_root, "convert-sessions.cjs");
        var node = NodeExe();
        if (!File.Exists(script) || !File.Exists(node)) { AddLog("[backup] convert-sessions.cjs not found"); return; }
        var psi = new ProcessStartInfo
        {
            FileName = node,
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi);
        if (p == null) { AddLog("[backup] md: could not start convert script"); return; }
        var outp = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(120000);
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            AddLog($"[backup] {line.Trim()}");
        if (!string.IsNullOrWhiteSpace(err)) AddLog($"[backup] md-err: {err.Trim()}");
    }

    // Keep only the newest backup per session id. Naming is
    // <yyyyMMdd-HHmmss>_<user>_<sessionId>_session.jsonl.zstd; the leading
    // timestamp sorts lexicographically, so the max timestamp is the newest and
    // (append-only log) the most complete snapshot. Older duplicates are deleted.
    private void DedupSessionBackups()
    {
        var backupDir = Path.Combine(ReadWorkspacePath(), "sharedata", "data", "sessions-backup");
        if (!Directory.Exists(backupDir)) return;
        const string suffix = "_session.jsonl.zstd";
        var best = new Dictionary<string, (string name, string ts)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(backupDir, "*.zstd"))
        {
            var name = Path.GetFileName(f);
            var stem = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - suffix.Length) : name;
            var parts = stem.Split('_');
            var id = parts.Length >= 2 ? parts[parts.Length - 1] : stem;
            var ts = parts.Length >= 1 ? parts[0] : "";
            if (!best.TryGetValue(id, out var cur) || string.CompareOrdinal(ts, cur.ts) > 0)
                best[id] = (name, ts);
        }
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in best) keep.Add(kv.Value.name);
        var removed = 0;
        foreach (var f in Directory.GetFiles(backupDir, "*.zstd"))
        {
            if (keep.Contains(Path.GetFileName(f))) continue;
            try { File.Delete(f); removed++; } catch { }
        }
        if (removed > 0) AddLog($"[backup] deduped {removed} older session snapshot(s); kept {keep.Count}");
    }

    // Copy the per-day session Markdown into each instance's workspace (and the
    // admin/global workspace) under `<workspace>/shared-sessions/`, so every
    // user's agent can read the team's shared experience with read/glob/grep —
    // which the fs sandbox confines to the workspace. Only files that changed are
    // copied. Nothing is deleted. On Unix a member workspace is chown'd to the
    // member and chmod 700, so when the launcher cannot write it directly the
    // copy is re-run AS that member (root: `su`; non-root: passwordless sudo).
    private void DistributeSharedSessions()
    {
        var src = Path.Combine(ReadWorkspacePath(), "sharedata", "data", "sessions-md");
        if (!Directory.Exists(src)) return;
        var files = Directory.GetFiles(src, "sessions-*.md");
        if (files.Length == 0) return;

        // The per-member fallback reads this tree as a different OS user.
        if (!OperatingSystem.IsWindows())
        {
            try { OsUserManager.MakeWorldReadable(src); } catch { }
        }

        var targets = new List<(string ws, string? inst)> { (ReadWorkspacePath(), null) };
        if (_instMgr != null)
            foreach (var inst in _instMgr.List())
                if (!string.IsNullOrWhiteSpace(inst.Workspace)) targets.Add((inst.Workspace, inst.Id));

        var copied = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ws, inst) in targets)
        {
            if (!seen.Add(ws)) continue;
            var dst = Path.Combine(ws, "shared-sessions");
            try
            {
                copied += CopyMdDirect(files, dst);
            }
            catch (Exception ex)
            {
                if (!OperatingSystem.IsWindows() && inst != null &&
                    OsUserManager.RunAsInstanceUser(inst, BuildCopyScript(src, dst), out var o))
                {
                    copied += files.Length;
                    AddLog($"[backup] shared-sessions -> {ws} (copied as {inst})");
                }
                else
                {
                    AddLog($"[backup] shared-sessions -> {ws}: {ex.Message}");
                }
            }
        }
        if (copied > 0) AddLog($"[backup] distributed shared sessions ({copied} file(s))");
    }

    // Copy changed Markdown files into dst (mtime/size aware). Throws a permission
    // exception when the current user cannot write dst, which the caller turns into
    // a per-user fallback on Unix.
    private static int CopyMdDirect(string[] files, string dst)
    {
        Directory.CreateDirectory(dst);
        var copied = 0;
        foreach (var f in files)
        {
            var target = Path.Combine(dst, Path.GetFileName(f));
            var srcInfo = new FileInfo(f);
            var dstInfo = new FileInfo(target);
            if (dstInfo.Exists && dstInfo.Length == srcInfo.Length &&
                dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc) continue;
            File.Copy(f, target, overwrite: true);
            copied++;
        }
        return copied;
    }

    // POSIX copy script for the per-instance fallback (single-quoted paths).
    private static string BuildCopyScript(string src, string dst)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        return $"mkdir -p {Q(dst)} && cp -f {Q(src)}/*.md {Q(dst)}/";
    }

    // Capture the live "dsh web: ...?token=..." URL as it streams from the DSH
    // process, so the admin default instance always uses a CURRENT token (the
    // rolling log buffer could otherwise evict it and leave a stale token behind).
    private void CaptureToken(string line)
    {
        try
        {
            var idx = line.IndexOf("dsh web:", StringComparison.Ordinal);
            if (idx < 0) return;
            var url = line.Substring(idx + "dsh web:".Length).Trim();
            if (url.StartsWith("http") && url.Contains("token="))
            {
                _tokenUrl = url;
                try
                {
                    var tokenFile = Path.Combine(_dshHome, "launcher-token.txt");
                    File.WriteAllText(tokenFile, url);
                }
                catch { }
            }
        }
        catch { }
    }

    // True only when DSH printed a FRESH "dsh web:" token URL during THIS launcher
    // session. After a reboot the old value persisted in launcher-token.txt is stale
    // (DSH rotates its token), so until a fresh one is captured we must not trust the
    // file — otherwise the proxy forwards an invalid token and DSH replies
    // "authentication required".
    public bool HasFreshToken()
    {
        return !string.IsNullOrWhiteSpace(_tokenUrl);
    }

    public string? TokenUrl()
    {
        // ALWAYS re-read the token file first: DSH rotates its token and CaptureToken
        // writes the freshest one to launcher-token.txt, but the in-memory _tokenUrl
        // can lag behind (e.g. after DSH restarts). Using a stale in-memory token
        // makes DSH answer 401. The file is the reliable source of the current token.
        try
        {
            var tokenFile = Path.Combine(_dshHome, "launcher-token.txt");
            if (File.Exists(tokenFile))
            {
                var url = File.ReadAllText(tokenFile).Trim();
                if (url.StartsWith("http") && url.Contains("token="))
                {
                    _tokenUrl = url;
                    return url;
                }
            }
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(_tokenUrl)) return _tokenUrl;
        var all = _logs.ToArray();
        foreach (var line in all)
        {
            var idx = line.IndexOf("dsh web:", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var url = line.Substring(idx + "dsh web:".Length).Trim();
                if (url.StartsWith("http") && url.Contains("token=")) return url;
            }
        }
        return null;
    }

    /// <summary>
    /// Wait (poll) until the DSH web prints its "dsh web: ...token=..." line (the
    /// reliable sign it is serving HTTP), or until the timeout elapses. Returns the
    /// ready token URL, or null on timeout. Used so opening DSH / proxying to it
    /// after a launcher restart does not hit an "unable to connect" window while
    /// DSH is still starting up.
    /// </summary>
    public string? WaitForTokenUrl(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var url = TokenUrl();
            if (!string.IsNullOrWhiteSpace(url)) return url;
            // If the process died, give up early.
            if (_proc is { HasExited: true }) return null;
            Thread.Sleep(500);
        }
        return null;
    }

    public void AddLog(string line)
    {
        var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
        _logs.Enqueue(stamped);
        while (_logs.Count > _maxLog) _logs.TryDequeue(out _);
        // Persist as well: this ring only lives in memory, so a DSH that died during
        // startup left no trace anywhere (its stdout/stderr - and our [ERR] lines - went
        // here). One file per DSH home, so each instance can be diagnosed separately.
        try
        {
            var dir = Path.Combine(_dshHome, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "dsh.log"), stamped + Environment.NewLine);
        }
        catch { }
    }

    public string CurrentVersion()
    {
        var pkg = Path.Combine(_root, "node", "node_modules", "@deepseek-ai", "dsh", "package.json");
        if (!OperatingSystem.IsWindows())
        {
            pkg = Path.Combine(_root, "node", PlatformSubDir(), "lib", "node_modules", "@deepseek-ai", "dsh", "package.json");
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.GetProperty("version").GetString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    public string NpmLatest()
    {
        var node = NodeExe();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = node,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(NodeNpmCli());
            psi.ArgumentList.Add("view");
            psi.ArgumentList.Add("@deepseek-ai/dsh");
            psi.ArgumentList.Add("version");
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(30000);
            return p.ExitCode == 0 ? outp : "";
        }
        catch { return ""; }
    }

    // Install/upgrade @deepseek-ai/dsh to the given version (or "latest") into the
    // bundled node/ folder. Returns (ok, combined npm output). Callers must stop
    // the running DSH first so files aren't locked on Windows.
    //
    // `npm install` prunes packages that aren't dependencies (which would delete
    // the bundled npm itself), so we run npm from a temp copy and restore npm
    // afterwards. Lifecycle scripts are skipped: the bundled npm can't resolve
    // node-gyp, and DSH is pure JS.
    public (bool Ok, string Output) InstallLatest(string version)
    {
        var node = NodeExe();
        var nodeDir = Path.Combine(_root, "node");
        var nmDir = Path.Combine(nodeDir, "node_modules");
        var npmDir = Path.Combine(nmDir, "npm");
        var work = Path.Combine(Path.GetTempPath(), "tt-npm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var npmRun = Path.Combine(work, "npm");
            CopyDir(npmDir, npmRun);
            var psi = new ProcessStartInfo
            {
                FileName = node,
                WorkingDirectory = nodeDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(Path.Combine(npmRun, "bin", "npm-cli.js"));
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("@deepseek-ai/dsh@" + version);
            psi.ArgumentList.Add("--no-save");
            psi.ArgumentList.Add("--no-audit");
            psi.ArgumentList.Add("--no-fund");
            psi.ArgumentList.Add("--ignore-scripts");
            psi.ArgumentList.Add("--loglevel=error");
            using var p = Process.Start(psi);
            if (p == null) return (false, "failed to start npm");
            var outp = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(900000);   // up to 15 minutes
            var ok = p.HasExited && p.ExitCode == 0;
            var text = (outp + "\n" + err).Trim();
            if (!p.HasExited) text = (text + "\nnpm install timed out").Trim();
            // Restore the bundled npm if the install pruned it.
            try { if (!Directory.Exists(npmDir) && Directory.Exists(npmRun)) CopyDir(npmRun, npmDir); } catch { }
            return (ok, text);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { } }
    }

    private static void CopyDir(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), true);
    }

    private string NodeNpmCli()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(_root, "node", "node_modules", "npm", "bin", "npm-cli.js");
        return Path.Combine(_root, "node", PlatformSubDir(), "lib", "node_modules", "npm", "bin", "npm-cli.js");
    }

    public string ReadCfgUrl()
    {
        var d = ReadDshWeb();
        return d.TryGetValue("Url", out var v) && v is string s ? s : "http://127.0.0.1:46000";
    }

    public void SaveCfgUrl(string url) => WriteDshWeb(new() { ["Url"] = url });

    // Host used to reach the local DSH backend. An explicit non-loopback host in
    // the config Url is honored (e.g. [::1] on IPv6-only hosts). For the default
    // loopback we return 127.0.0.1 (NOT "localhost"): DSH binds IPv4 only, and on
    // Windows "localhost" resolves to ::1 first, so every fresh connection waited
    // for the IPv6 attempt before falling back -> ~2-4s page loads through the proxy.
    public string ReadDshHost()
    {
        try
        {
            if (Uri.TryCreate(ReadCfgUrl(), UriKind.Absolute, out var uri))
            {
                var h = uri.Host;
                if (string.IsNullOrWhiteSpace(h) ||
                    h.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    return "127.0.0.1";
                return h.Contains(':') ? "[" + h + "]" : h; // IPv6 literal needs brackets in a URL
            }
        }
        catch { }
        return "127.0.0.1";
    }

    // Launcher's own listen port; takes effect on the next start.
    public void SaveLauncherPort(int port) => WriteDshWeb(new() { ["LauncherPort"] = port });

    // Save the external reverse-proxy URL (configured on the control page).
    public void SaveExternalUrl(string? externalUrl) =>
        WriteDshWeb(new() { ["ExternalUrl"] = string.IsNullOrWhiteSpace(externalUrl) ? null : externalUrl.Trim() });

    public void SaveDefaultLanguage(string? defaultLanguage)
    {
        WriteDshWeb(new() { ["DefaultLanguage"] = string.IsNullOrWhiteSpace(defaultLanguage) ? null : defaultLanguage.Trim() });
        ApplyDshLocaleFromDefault();
    }

    // DSH shows its UI in the 'locale.preference' stored in its own settings.yaml.
    // Mirror the launcher's "缺省语言" (first entry) into it, so opening DSH uses
    // the language the admin set — and, because it is persisted, it never flickers.
    // Merges into settings.yaml so other DSH settings (welcome notice, …) survive.
    public void ApplyDshLocaleFromDefault() => WriteLocalePreference(_dshHome, DefaultLocaleCode());

    // "zh" or "en" derived from the launcher's 缺省语言 first entry.
    public string DefaultLocaleCode()
    {
        var first = (ReadDefaultLanguage() ?? "en").Split(',').FirstOrDefault()?.Split(':').LastOrDefault()?.Trim() ?? "en";
        return first.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }

    // Write locale.preference (zh/en) into a specific DSH home's settings.yaml,
    // merging so other keys survive. Static so member instances can reuse it.
    public static void WriteLocalePreference(string dshHome, string code)
    {
        try
        {
            var file = Path.Combine(dshHome, "settings.yaml");
            var text = File.Exists(file) ? File.ReadAllText(file) : "";
            if (System.Text.RegularExpressions.Regex.IsMatch(text, "(?m)^\\s*preference:\\s*.*$"))
                text = System.Text.RegularExpressions.Regex.Replace(text, "(?m)^(\\s*preference:\\s*).*$", "${1}" + code);
            else if (System.Text.RegularExpressions.Regex.IsMatch(text, "(?m)^locale:\\s*$"))
                text = System.Text.RegularExpressions.Regex.Replace(text, "(?m)^(locale:\\s*)$", "$1\n  preference: " + code);
            else
                text += (text.Length > 0 && !text.EndsWith("\n") ? "\n" : "") + "locale:\n  preference: " + code + "\n";
            Directory.CreateDirectory(dshHome);
            File.WriteAllText(file, text);
        }
        catch { }
    }

    // Workspace path the admin sets on the control page. This is the directory
    // dsh's sandbox/cwd points at (where AI works). Empty = dsh's own default.
    public string ReadWorkspacePath()
    {
        return ResolveWorkspacePath(_root);
    }

    public string DshHome => _dshHome;

    // Port the launcher itself listens on. Single source of truth: the merged
    // config (config/launcher.local.json over appsettings.json) key DshWeb.LauncherPort;
    // the LAUNCHER_PORT env var is only a fallback for advanced/container use.
    public int ReadLauncherPort()
    {
        var d = ReadDshWeb();
        if (d.TryGetValue("LauncherPort", out var v) && v != null)
        {
            if (v is long l && l > 0 && l <= 65535) return (int)l;
            if (v is string s && int.TryParse(s, out var p) && p > 0 && p <= 65535) return p;
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("LAUNCHER_PORT"), out var ep) && ep > 0 && ep <= 65535) return ep;
        return 46001;
    }

    // Resolve the effective workspace path from config without an instance: the
    // gitignored local override wins, then appsettings.json, then the portable
    // default (<install>\WorkSpace).
    public static string ResolveWorkspacePath(string root)
    {
        // Only the install-dir POINTER (config/launcher.local.json) or the portable
        // default. appsettings.json intentionally does NOT take part: it ships in the
        // repo and must never carry a machine-specific workspace path.
        return WorkspaceFrom(Path.Combine(root, "config", "launcher.local.json"))
            ?? DefaultWorkspacePath(root);
    }

    // Public wrapper: read a non-empty DshWeb.WorkspacePath from a config file.
    public static string? WorkspaceFromFile(string path) => WorkspaceFrom(path);

    private static string? WorkspaceFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.TryGetProperty("WorkspacePath", out var ws) && ws.ValueKind == JsonValueKind.String)
            {
                var v = ws.GetString();
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        }
        catch { }
        return null;
    }

    // Portable default used when the admin has not configured a workspace: a
    // dedicated `WorkSpace` folder inside the install dir (e.g.
    // D:\TakeTopDshTeam\WorkSpace), so a fresh clone runs with no configuration.
    // Only this subfolder is exposed to the AI — the rest of the install tree
    // (app code, node, .dsh) stays off-limits (see SanitizeWorkspace). Keep this
    // folder when replacing/upgrading the release; the admin page warns about that.
    public static string DefaultWorkspacePath(string root)
    {
        return Path.Combine(root, "WorkSpace");
    }

    // True when the admin has not set a workspace (the portable default is in use).
    // The admin page warns strongly in this case: a default path may be shared by
    // several clones or overwritten on upgrade, risking data loss.
    public bool IsWorkspaceDefault() => OptStr(ReadDshWeb(), "WorkspacePath") == null;

    // Address the launcher + instances bind to (127.0.0.1 loopback, or the
    // public/network interface when exposing over LAN/internet).
    public string ReadBindHost() => OptStr(ReadDshWeb(), "BindHost") ?? "127.0.0.1";

    // First port the launcher uses when auto-assigning instance ports. Defaults to
    // the launcher's own port, so member ports sit right next to it (launcher 46201,
    // admin DSH 46202 -> members 46203, 46204, ...). Override with DshWeb.InstancesStartPort.
    public int ReadInstancesStartPort()
    {
        var d = ReadDshWeb();
        if (d.TryGetValue("InstancesStartPort", out var v))
        {
            if (v is long n && n > 0 && n <= 65535) return (int)n;
            if (v is string s && int.TryParse(s, out var p) && p > 0 && p <= 65535) return p;
        }
        return ReadLauncherPort();
    }

    public void SaveWorkspacePath(string? workspacePath) =>
        WriteDshWeb(new() { ["WorkspacePath"] = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim() });

    // The install-root file is only a POINTER to the workspace: it holds
    // WorkspacePath (and nothing else) so the launcher can find the workspace even
    // before any config is loaded. Everything else lives in the workspace config
    // (<workspace>/config/launcher.json) so a program upgrade that overwrites the
    // install directory can never wipe the user's settings.
    private string BootstrapPath => Path.Combine(_root, "config", "launcher.local.json");

    // Full, user-editable config; travels with the workspace and survives upgrades.
    private string WorkspaceSettingsPath()
        => Path.Combine(ResolveWorkspacePath(_root), "config", "launcher.json");

    private Dictionary<string, object?> ReadDshWeb()
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ParseDshWeb(Path.Combine(_root, "appsettings.json"))) dict[kv.Key] = kv.Value;
        foreach (var kv in ParseDshWeb(BootstrapPath)) dict[kv.Key] = kv.Value;
        foreach (var kv in ParseDshWeb(WorkspaceSettingsPath())) dict[kv.Key] = kv.Value;
        return dict;
    }

    // One-time migration: pull legacy settings (Url/LauncherPort/…) out of the
    // install-root bootstrap into the workspace config, leaving the bootstrap with
    // only the workspace pointer.
    private void EnsureSettingsMigrated()
    {
        var boot = ParseDshWeb(BootstrapPath);
        if (boot.Count == 0) return;
        var wsPath = WorkspaceSettingsPath();
        var wsExisting = ParseDshWeb(wsPath);
        var moved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in boot)
        {
            if (string.Equals(kv.Key, "WorkspacePath", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            if (!wsExisting.ContainsKey(kv.Key)) moved[kv.Key] = kv.Value;
        }
        if (moved.Count > 0) WriteDshWebFile(wsPath, moved);
        var pointer = boot.TryGetValue("WorkspacePath", out var wp) ? Convert.ToString(wp, System.Globalization.CultureInfo.InvariantCulture) : null;
        WriteBootstrapPointer(pointer);
    }

    private static Dictionary<string, object?> ParseDshWeb(string path)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return dict;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) && web.ValueKind == JsonValueKind.Object)
                foreach (var prop in web.EnumerateObject())
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
        }
        catch { }
        return dict;
    }

    // Non-empty string override, else null.
    private static string? OptStr(Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    // Persist runtime settings. WorkspacePath goes to the install-root pointer so
    // the next launch can find the workspace; every other key is written to the
    // workspace config. Writes NEVER touch the committed appsettings.json.
    private void WriteDshWeb(Dictionary<string, object?> cfg)
    {
        if (cfg.ContainsKey("WorkspacePath"))
        {
            var ws = cfg["WorkspacePath"] == null ? null : Convert.ToString(cfg["WorkspacePath"], System.Globalization.CultureInfo.InvariantCulture);
            // 1) Remember the workspace pointer in the install dir.
            WriteBootstrapPointer(ws);
            var target = Path.Combine(string.IsNullOrWhiteSpace(ws) ? ResolveWorkspacePath(_root) : ws!, "config", "launcher.json");
            // 2) Materialize the effective settings into the workspace so it owns a
            //    complete config file from the very first save.
            var effective = ReadDshWeb();
            effective.Remove("WorkspacePath");
            if (effective.Count > 0) WriteDshWebFile(target, effective);
            // 3) Apply any remaining keys from this same call.
            cfg = cfg.Where(kv => !string.Equals(kv.Key, "WorkspacePath", StringComparison.OrdinalIgnoreCase))
                     .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (cfg.Count > 0) WriteDshWebFile(target, cfg);
            return;
        }
        WriteDshWebFile(WorkspaceSettingsPath(), cfg);
    }

    // The install-root pointer: workspace path only, so it survives the one thing
    // that must not be lost and stays trivially small.
    private void WriteBootstrapPointer(string? workspacePath)
    {
        var file = BootstrapPath;
        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); } catch { }
        var root = new JsonObject();
        var web = new JsonObject();
        if (!string.IsNullOrWhiteSpace(workspacePath)) web["WorkspacePath"] = JsonValue.Create(workspacePath);
        root["DshWeb"] = web;
        File.WriteAllText(file, root.ToJsonString(JsonWriteOptions));
    }

    private void WriteDshWebFile(string path, Dictionary<string, object?> cfg)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }
        JsonNode root;
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject(); }
            catch { root = new JsonObject(); }
        }
        else
        {
            root = new JsonObject();
        }
        var web = root["DshWeb"] as JsonObject;
        if (web == null)
        {
            web = new JsonObject();
            root["DshWeb"] = web;
        }
        foreach (var kvp in cfg)
        {
            if (kvp.Value == null)
                web.Remove(kvp.Key);
            else
                web[kvp.Key] = JsonValue.Create(Convert.ToString(kvp.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        File.WriteAllText(path, root.ToJsonString(JsonWriteOptions));
    }

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        // Keep CJK / non-ASCII as literal characters instead of \uXXXX escapes,
        // so the config file stays human-readable (中文 stays 中文, not \u4E2D\u6587).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // UI language list from appsettings.json DshWeb.DefaultLanguage, e.g.
    // "en,zh-cn". Defaults to "en,zh-cn" when unset. Empty when explicitly "".
    public string ReadDefaultLanguage()
    {
        var d = ReadDshWeb();
        return d.TryGetValue("DefaultLanguage", out var v) && v is string s ? s : "en,zh-cn";
    }

    public int DefaultPort()
    {
        var u = ReadCfgUrl();
        var last = u.Split(':').Last();
        return int.TryParse(last, out var p) ? p : 46000;
    }

    // Read the external reverse-proxy URL, if any. This is the address clients
    // use (e.g. https://192.168.2.28 or http://my.host:9090). Empty if not set.
    public string ExternalUrl()
    {
        var d = ReadDshWeb();
        return d.TryGetValue("ExternalUrl", out var v) && v is string s ? s : "";
    }

    // Authority (host[:port]) that dsh should trust when behind a proxy. Falls
    // back to the ExternalUrl host, else empty.
    public string ExternalHost()
    {
        var ext = ExternalUrl();
        if (!string.IsNullOrEmpty(ext) && Uri.TryCreate(ext, UriKind.Absolute, out var uri))
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return "";
    }

    // ---------- Server directory browsing (for the control page) ----------

    // Return the top-level browse roots for this machine: drive roots on
    // Windows (C:\ D:\ ...) and "/" on POSIX.
    public IReadOnlyList<DirectoryEntry> BrowseRoots()
    {
        var list = new List<DirectoryEntry>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    var root = drive.RootDirectory.FullName;
                    list.Add(new DirectoryEntry(root.TrimEnd('\\', '/'), root));
                }
            }
            else
            {
                list.Add(new DirectoryEntry("/", "/"));
            }
        }
        catch { }
        return list;
    }

    // List the child directories of a server path (one level). Returns the
    // current path, its parent, and the entries (skipping hidden/system).
    public BrowseResult BrowseDirectory(string? rawPath)
    {
        string current = string.IsNullOrWhiteSpace(rawPath) ? DefaultBrowsePath() : rawPath.Trim();
        if (!Directory.Exists(current))
            return new BrowseResult { Path = current, Parent = ParentOf(current), Entries = new List<DirectoryEntry>() };

        var entries = new List<DirectoryEntry>();
        try
        {
            foreach (var dir in Directory.GetDirectories(current))
            {
                try
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".") || name.StartsWith("$"))
                        continue;
                    entries.Add(new DirectoryEntry(name, dir));
                }
                catch { }
            }
            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch { }

        return new BrowseResult
        {
            Path = current,
            Parent = ParentOf(current),
            Home = DefaultBrowsePath(),
            Entries = entries
        };
    }

    private static string ParentOf(string path)
    {
        try
        {
            var parent = Directory.GetParent(path);
            if (parent != null && parent.FullName != path) return parent.FullName;
        }
        catch { }
        return "";
    }

    private static string DefaultBrowsePath()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch { return Directory.GetCurrentDirectory(); }
    }

    public record DirectoryEntry(string Name, string Path);
    public class BrowseResult
    {
        public string Path { get; set; } = "";
        public string Parent { get; set; } = "";
        public string Home { get; set; } = "";
        public IReadOnlyList<DirectoryEntry> Entries { get; set; } = new List<DirectoryEntry>();
    }
}

// JSON shape for the DshWeb config block in appsettings.json.
public class DshWeb
{
    public string? Url { get; set; }
    public string? ExternalUrl { get; set; }
    public string? WorkspacePath { get; set; }
    public string? EncryptionKey { get; set; }
    // Comma-separated UI language list, e.g. "en,zh-cn" or "zh-cn,en".
    public string? DefaultLanguage { get; set; }
}

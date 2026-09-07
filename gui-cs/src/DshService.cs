// TakeTopDSH Team — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

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

    public DshService(string root)
    {
        _root = root;
        _dshHome = Path.Combine(root, ".dsh");
    }

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
            return new { state = "running", pid = _proc.Id };
        // If our process is gone but the port is still serving, an external or
        // pre-existing dsh web instance is running — treat it as running.
        if (IsPortInUse(_currentPort))
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

    public static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect("127.0.0.1", port);
            return true;
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
            if (!File.Exists(patchFile)) return;

            var normalized = workspace.Replace('\\', '/');
            var docsDir = Path.Combine(_root, "docs").Replace('\\', '/');

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

            // System prompt persona: restrict file access for non-admin users.
            sb.AppendLine("- id: system-prompt");
            sb.AppendLine("  config:");
            sb.AppendLine("    persona: >-");
            sb.AppendLine("      You are a coding agent powered by the {{model}} model. Your working directory is {{cwd}}.");
            sb.AppendLine();
            sb.AppendLine("      FILE ACCESS RULES (STRICTLY ENFORCED):");
            sb.AppendLine($"      1. You may ONLY read and write files within your workspace at {{{{cwd}}}}.");
            sb.AppendLine($"      2. You may ONLY READ (never write, modify, or delete) files in the shared docs directory: {docsDir}");
            sb.AppendLine("      3. You must NOT access, read, list, or reference any files or directories outside your workspace and the docs directory above.");
            sb.AppendLine("      4. If a task requires accessing files outside these directories, inform the user that access is restricted.");
            sb.AppendLine("      5. Use web search or web fetch tools for external resources instead of local file access.");

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
    // docs/opencode-experience/data/sessions-backup/ so the session content is
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
        var sessionsRoot = Path.Combine(_dshHome, "sessions");
        var backupDir = Path.Combine(_root, "docs", "opencode-experience", "data", "sessions-backup");
        if (!Directory.Exists(sessionsRoot)) return;
        Directory.CreateDirectory(backupDir);

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

                // Destination: <ts>_<sessionDir>_session.jsonl.zstd (unique, historical).
                var sessionId = Path.GetFileName(Path.GetDirectoryName(src) ?? "");
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var dest = Path.Combine(backupDir, $"{stamp}_{sessionId}_session.jsonl.zstd");
                File.Copy(src, dest, overwrite: true);
                _backupSeen[key] = signature;
                AddLog($"[backup] archived {sessionId} ({fi.Length} B)");
            }
            catch (Exception ex)
            {
                // File may be locked / being written; skip this one this round.
                AddLog($"[backup] skip {Path.GetFileName(src)}: {ex.Message}");
            }
        }

        // After mirroring .zstd snapshots, regenerate the readable per-day
        // Markdown that the AI can search ("have we done similar before").
        try { GenerateSessionMarkdown(); }
        catch (Exception ex) { AddLog($"[backup] md: {ex.Message}"); }
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
                _tokenUrl = url;
        }
        catch { }
    }

    public string? TokenUrl()
    {
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
        _logs.Enqueue($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}");
        while (_logs.Count > _maxLog) _logs.TryDequeue(out _);
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

    private string NodeNpmCli()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(_root, "node", "node_modules", "npm", "bin", "npm-cli.js");
        return Path.Combine(_root, "node", PlatformSubDir(), "lib", "node_modules", "npm", "bin", "npm-cli.js");
    }

    public string ReadCfgUrl()
    {
        try
        {
            var path = Path.Combine(_root, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("DshWeb").GetProperty("Url").GetString() ?? "http://127.0.0.1:46000";
        }
        catch { return "http://127.0.0.1:46000"; }
    }

    public void SaveCfgUrl(string url)
    {
        var cfg = ReadDshWeb();
        cfg["Url"] = url;
        WriteDshWeb(cfg);
    }

    // Save the external reverse-proxy URL (configured on the control page).
    public void SaveExternalUrl(string? externalUrl)
    {
        var cfg = ReadDshWeb();
        if (string.IsNullOrWhiteSpace(externalUrl))
            cfg.Remove("ExternalUrl");
        else
            cfg["ExternalUrl"] = externalUrl.Trim();
        WriteDshWeb(cfg);
    }

    public void SaveDefaultLanguage(string? defaultLanguage)
    {
        var cfg = ReadDshWeb();
        if (string.IsNullOrWhiteSpace(defaultLanguage))
            cfg.Remove("DefaultLanguage");
        else
            cfg["DefaultLanguage"] = defaultLanguage.Trim();
        WriteDshWeb(cfg);
    }

    // Workspace path the admin sets on the control page. This is the directory
    // dsh's sandbox/cwd points at (where AI works). Empty = dsh's own default.
    public string ReadWorkspacePath()
    {
        try
        {
            var path = Path.Combine(_root, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.TryGetProperty("WorkspacePath", out var ws) &&
                ws.ValueKind == JsonValueKind.String)
                return ws.GetString() ?? "";
        }
        catch { }
        return "";
    }

    // Address the launcher + instances bind to (127.0.0.1 loopback, or the
    // public/network interface when exposing over LAN/internet).
    public string ReadBindHost()
    {
        try
        {
            var path = Path.Combine(_root, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.TryGetProperty("BindHost", out var bh) &&
                bh.ValueKind == JsonValueKind.String)
                return bh.GetString() ?? "127.0.0.1";
        }
        catch { }
        return "127.0.0.1";
    }

    // First port the launcher uses when auto-assigning instance ports.
    public int ReadInstancesStartPort()
    {
        try
        {
            var path = Path.Combine(_root, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.TryGetProperty("InstancesStartPort", out var p) &&
                p.ValueKind == JsonValueKind.Number)
                return p.GetInt32();
        }
        catch { }
        return 46000;
    }

    public void SaveWorkspacePath(string? workspacePath)
    {
        var cfg = ReadDshWeb();
        if (string.IsNullOrWhiteSpace(workspacePath))
            cfg.Remove("WorkspacePath");
        else
            cfg["WorkspacePath"] = workspacePath.Trim();
        WriteDshWeb(cfg);
    }

    // Read the whole DshWeb config block as a mutable dictionary.
    private Dictionary<string, object?> ReadDshWeb()
    {
        try
        {
            var path = Path.Combine(_root, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in web.EnumerateObject())
                {
                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.String: dict[prop.Name] = prop.Value.GetString(); break;
                        case JsonValueKind.Number: dict[prop.Name] = prop.Value.GetInt64(); break;
                        case JsonValueKind.True: dict[prop.Name] = true; break;
                        case JsonValueKind.False: dict[prop.Name] = false; break;
                        default: dict[prop.Name] = prop.Value.Clone(); break;
                    }
                }
                return dict;
            }
        }
        catch { }
        return new Dictionary<string, object?>();
    }

    // Merge the given cfg keys into appsettings.json's DshWeb block WITHOUT
    // dropping any other existing fields (Url, WorkspacePath, EncryptionKey, any
    // future key). A value of null removes that key.
    private void WriteDshWeb(Dictionary<string, object?> cfg)
    {
        var path = Path.Combine(_root, "appsettings.json");
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
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions {
            WriteIndented = true,
            // Keep CJK / non-ASCII as literal characters instead of \uXXXX escapes,
            // so the config file stays human-readable (中文 stays 中文, not \u4E2D\u6587).
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }

    // UI language list from appsettings.json DshWeb.DefaultLanguage, e.g.
    // "en,zh-cn". Defaults to "en,zh-cn" when unset. Empty when explicitly "".
    public string ReadDefaultLanguage()
    {
        try
        {
            var path = Path.Combine(_root, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.TryGetProperty("DefaultLanguage", out var dl) && dl.ValueKind == JsonValueKind.String)
                return dl.GetString() ?? "en,zh-cn";
        }
        catch { }
        return "en,zh-cn";
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
        try
        {
            var path = Path.Combine(_root, "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.TryGetProperty("ExternalUrl", out var ext) &&
                ext.ValueKind == JsonValueKind.String)
                return ext.GetString() ?? "";
        }
        catch { }
        return "";
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

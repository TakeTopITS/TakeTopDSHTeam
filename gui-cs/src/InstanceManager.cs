// TakeTopDshTeam — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This software is licensed under the Business Source License 1.1 (BSL 1.1).
// You may copy, modify, redistribute, and make non-production use; production
// use is free for an organization with up to 10 users. Use by more than 10
// users requires a commercial license. See LICENSE for the full terms and
// LICENSE-COMMERCIAL.md for commercial licensing.
//
// On the Change Date (2030-09-11) this version automatically converts to the
// Apache License, Version 2.0. THE LICENSED WORK IS PROVIDED "AS IS", WITHOUT
// WARRANTY OF ANY KIND.
//
// This software is the intellectual property of 泰顶拓鼎信息科技（上海）有限公司
// All rights reserved.
// A commercial license is also available; see LICENSE-COMMERCIAL.md.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TakeTopDshLauncher;

// Manages multiple independent dsh instances. Each instance = one dsh process
// with its own port, its own .dsh home, and its own workspace. They share the
// read-only node/ dsh code but never share state (sessions/config live in each
// instance's .dsh). The existing single-instance DshService is left untouched.
public class InstanceManager
{
    public class Instance
    {
        public string Id { get; set; } = "";          // username
        public string Name { get; set; } = "";        // display name
        public int DshPort { get; set; } = 0;         // dsh web port
        public string Workspace { get; set; } = "";   // admin-set workspace dir
        public string DshHome { get; set; } = "";     // this instance's .dsh dir
        public string? OsPassword { get; set; }       // OS user password for isolation
        public int LauncherMarker { get; set; } = 0;  // not used; dsh uses --port
        public bool Running { get; set; }

        // True while a start (OS user + process launch) is in progress. Not persisted;
        // used by the proxy to show a "starting" page instead of a "not running" error.
        [JsonIgnore] public bool Starting { get; set; }

        [JsonIgnore] public Process? Proc { get; set; }
        [JsonIgnore] public ConcurrentQueue<string> Logs { get; } = new();
        public string TokenUrl { get; set; } = "";
    }

    private readonly string _root;
    private readonly string _instancesDir;   // legacy config/instances.json (migration source)
    private readonly string _instRoot;       // instances/<id>
    private readonly string _dshTemplate;    // .dsh template root
    private readonly int _basePort;          // first port used for auto-assignment
    private readonly object _gate = new();
    private readonly List<Instance> _instances = new();
    // Static cache: OS users already created + permissions set, so the slow
    // icacls recursion over node_modules runs only once per user, not every start.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _permissionedOsUsers = new(StringComparer.OrdinalIgnoreCase);

    public InstanceManager(string root, int basePort = 46000)
    {
        _root = root;
        var ws = DshService.ResolveWorkspacePath(root);
        var wsInst = Path.Combine(ws, "instances");
        var legacyInst = Path.Combine(root, "instances");
        _instRoot = Directory.Exists(wsInst) ? wsInst : (Directory.Exists(legacyInst) ? legacyInst : wsInst);
        _instancesDir = Path.Combine(root, "config", "instances.json");
        var wsTpl = Path.Combine(ws, ".dsh");
        var legacyTpl = Path.Combine(root, ".dsh");
        _dshTemplate = Directory.Exists(wsTpl) ? wsTpl : (Directory.Exists(legacyTpl) ? legacyTpl : wsTpl);
        _basePort = basePort;
        // A fresh clone has no admin root .dsh/.credentials.yaml (it is gitignored,
        // since it holds the real API key). Ensure a placeholder template exists so
        // DSH can start and instances can seed their credentials. The admin then
        // replaces the placeholder with the real key in the DSH UI.
        EnsureAdminCredentialsTemplate();
        Load();
    }

    private void EnsureAdminCredentialsTemplate()
    {
        try
        {
            var tpl = Path.Combine(_dshTemplate, ".credentials.yaml");
            if (!File.Exists(tpl))
            {
                var y = new System.Text.StringBuilder();
                y.AppendLine("version: 1");
                y.AppendLine("refs:");
                y.AppendLine("  DEEPSEEK_API_KEY: \"ph-demo-placeholder-key-change-me\"");
                System.IO.Directory.CreateDirectory(_dshTemplate);
                File.WriteAllText(tpl, y.ToString());
            }
            // DSH refuses to start when .credentials.yaml is readable beyond its owner
            // (mode 600 required on Unix). Apply it for fresh AND pre-existing files.
            Chmod600IfUnix(tpl);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[instance] admin credential template seed failed: {ex.Message}");
        }
    }

    // Restrict a credential file to its owner (0600) on Unix/macOS. No-op on Windows.
    private static void Chmod600IfUnix(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* best effort */ }
    }

    public IReadOnlyList<Instance> List() => _instances.ToList();

    public void Load()
    {
        lock (_gate)
        {
            _instances.Clear();
            var rows = LauncherDb.LoadInstances(_root);

            // One-time migration from the legacy config/instances.json.
            if (rows.Count == 0 && File.Exists(_instancesDir))
            {
                rows = ParseLegacyInstancesJson();
                if (rows.Count > 0)
                {
                    LauncherDb.SaveInstances(_root, rows);
                    LauncherDb.ArchiveJson(_instancesDir);
                }
            }

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Id)) continue;
                var inst = new Instance
                {
                    Id = row.Id,
                    Name = row.Name,
                    DshPort = row.DshPort,
                    Workspace = row.Workspace,
                    // DshHome is ALWAYS derived from the current install root
                    // (root\instances\<id>\.dsh) so the launcher works when copied/moved
                    // to any directory — never trust an absolute path.
                    DshHome = Path.Combine(_instRoot, row.Id, ".dsh"),
                    OsPassword = row.OsPassword,
                    TokenUrl = row.TokenUrl,
                    Running = row.Running,
                };
                // Normalize workspace to a canonical native path on load so
                // legacy/mixed separator entries render/store consistently.
                if (!string.IsNullOrWhiteSpace(inst.Workspace))
                    inst.Workspace = Path.GetFullPath(inst.Workspace);
                // Re-hydrate the process handle (from a previous run) if its port is live.
                if (inst.Running && DshService.IsPortInUse(inst.DshPort))
                {
                    if (FindProcByPort(inst.DshPort, out var pid))
                    {
                        try { inst.Proc = Process.GetProcessById(pid); } catch { inst.Proc = null; }
                        if (inst.Proc == null) inst.Running = false;
                        else inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [resume] instance already running (pid {pid})");
                    }
                    else inst.Running = false;
                }
                else inst.Running = false;
                _instances.Add(inst);
            }

            // Auto-correct any instance whose port is now held by another process
            // (e.g. the environment reassigned a fixed port). Reallocate a free
            // port so the user never has to touch port config. Skip default dsh.
            RebalancePorts();
        }
    }

    // Parse the legacy config/instances.json (used only for the one-time migration).
    private List<LauncherInstanceRow> ParseLegacyInstancesJson()
    {
        var rows = new List<LauncherInstanceRow>();
        if (!File.Exists(_instancesDir)) return rows;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(_instancesDir));
            if (doc.RootElement.TryGetProperty("instances", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var iid) ? iid.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    rows.Add(new LauncherInstanceRow
                    {
                        Id = id,
                        Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        DshPort = el.TryGetProperty("dshPort", out var p) ? p.GetInt32() : 0,
                        Workspace = el.TryGetProperty("workspace", out var w) ? w.GetString() ?? "" : "",
                        OsPassword = el.TryGetProperty("osPassword", out var op) ? op.GetString() : null,
                        TokenUrl = el.TryGetProperty("tokenUrl", out var tk) ? tk.GetString() ?? "" : "",
                        Running = el.TryGetProperty("running", out var r) && r.GetBoolean(),
                    });
                }
            }
        }
        catch { /* unreadable config -> empty */ }
        return rows;
    }

    // Ensure every instance's port is free; reallocate when it was taken over by
    // a process that is not this instance's own live dsh.
    private void RebalancePorts()
    {
        try
        {
            bool changed = false;
            foreach (var inst in _instances)
            {
                if (inst.DshPort <= 0) { inst.DshPort = AllocPort(); changed = true; continue; }
                if (!DshService.IsPortInUse(inst.DshPort)) continue;
                // Port is live. Keep it only if it is our instance's own dsh;
                // otherwise the port was hijacked by something else -> reallocate.
                bool ours = inst.Proc is { HasExited: false } && FindProcByPort(inst.DshPort, out var pid) && inst.Proc.Id == pid;
                if (!ours)
                {
                    inst.DshPort = AllocPort();
                    inst.Running = false;
                    changed = true;
                }
            }
            if (changed) Save();
        }
        catch (Exception ex) { Trace.WriteLine($"[ports] rebalance failed: {ex.Message}"); }
    }

    private bool FindProcByPort(int port, out int pid)
    {
        pid = 0;
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
                using var p = Process.Start(psi);
                if (p == null) return false;
                var outLines = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in outLines.Split('\n'))
                {
                    if (line.Contains($":{port} ") && line.Contains("LISTENING"))
                    {
                        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0 && int.TryParse(parts[^1], out pid)) return pid > 0;
                    }
                }
            }
            else
            {
                // Prefer `ss` (Linux); fall back to `lsof` (macOS/BSD). On minimal
                // Linux lsof is often absent, so we must not rely on it alone.
                var lines = new List<string>();
                if (Process.Start(new ProcessStartInfo { FileName = "ss", Arguments = "-ltnp", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }) is { } ss)
                {
                    lines.AddRange(ss.StandardOutput.ReadToEnd().Split('\n'));
                    ss.WaitForExit(3000);
                }
                else if (Process.Start(new ProcessStartInfo { FileName = "lsof", Arguments = $"-iTCP:{port} -sTCP:LISTEN -t", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }) is { } lo)
                {
                    lines.AddRange(lo.StandardOutput.ReadToEnd().Split('\n'));
                    lo.WaitForExit(3000);
                }
                foreach (var line in lines)
                {
                    // ss: users:(("node",pid=1234,fd=20))  -> 1234
                    var pidm = System.Text.RegularExpressions.Regex.Match(line, @"pid=(\d+)");
                    if (pidm.Success && int.TryParse(pidm.Groups[1].Value, out pid)) return pid > 0;
                    // lsof -t: bare PID per line
                    if (int.TryParse(line.Trim(), out pid)) return pid > 0;
                }
            }
        }
        catch { }
        return false;
    }

    // Force-kill a process AND its whole child tree. Process.Kill(true) can fail
    // silently for dsh processes that run under a different OS user (the launcher
    // only owns the parent handle), so use taskkill /F /T which works cross-user
    // when the launcher runs elevated. Falls back to Kill(true) on non-Windows.
    public static void ForceKillTree(int pid)
    {
        if (pid <= 0) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /PID {pid} /T",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            else
            {
                Process.GetProcessById(pid)?.Kill(true);
            }
        }
        catch { /* best-effort */ }
    }

    // Find a free port starting at basePort (skips ports already in use by the
    // OS or already assigned to another instance).
    public int AllocPort(int? basePort = null)
    {
        var start = basePort ?? _basePort;
        HashSet<int> used = new(_instances.Select(i => i.DshPort).Where(p => p > 0));
        for (var p = start; p < start + 2000; p++)
        {
            if (used.Contains(p)) continue;
            if (!DshService.IsPortInUse(p)) return p;
        }
        throw new InvalidOperationException("no free port available");
    }

    public string NodeExe()
    {
        if (OperatingSystem.IsWindows())
        {
            var win = Path.Combine(_root, "node", "node.exe");
            return File.Exists(win) ? win : "node";
        }
        var sub = PlatformSubDir();
        var p = Path.Combine(_root, "node", sub, "bin", "node");
        return File.Exists(p) ? p : "node";
    }

    public string BinJs()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(_root, "node", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        var sub = PlatformSubDir();
        return Path.Combine(_root, "node", sub, "lib", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    }

    private static string PlatformSubDir()
    {
        if (OperatingSystem.IsWindows()) return "";
        if (OperatingSystem.IsMacOS()) return "macos-arm64";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        return arch.Equals("Arm64", StringComparison.OrdinalIgnoreCase) ? "linux-arm64" : "linux-x64";
    }

    // Root of the @deepseek-ai/dsh package. Windows node layout:
    //   node/node_modules/@deepseek-ai/dsh
    // Linux/macOS layout (unpacked by start.sh):
    //   node/<platform>/lib/node_modules/@deepseek-ai/dsh
    private string DshPkgDir() => DshPkgDirFor(_root);

    private static string DshPkgDirFor(string root) => PlatformSubDir() == ""
        ? Path.Combine(root, "node", "node_modules", "@deepseek-ai", "dsh")
        : Path.Combine(root, "node", PlatformSubDir(), "lib", "node_modules", "@deepseek-ai", "dsh");

    // Create a new instance with a fresh .dsh home + auto-allocated port.
    // If osPassword is provided, a dedicated OS user is created for isolation.
    public Instance Create(string id, string name, string workspace, string? osPassword = null, int basePort = 0)
    {
        lock (_gate)
        {
            if (_instances.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"instance '{id}' already exists");

            var port = AllocPort(basePort > 0 ? basePort : null);
            var dshHome = Path.Combine(_instRoot, id, ".dsh");
            Directory.CreateDirectory(Path.GetDirectoryName(dshHome)!);
            // Normalize the workspace to a canonical native path (backslashes on
            // Windows) so all instances display/stores the same separator form,
            // avoiding mixed "E:\a\b" vs "E:/a/b" entries in instances.json.
            if (!string.IsNullOrWhiteSpace(workspace))
                workspace = Path.GetFullPath(workspace);

            // Copy the .dsh template (config/providers) but drop sessions/storages.
            CopyDshTemplate(dshHome);

            var inst = new Instance
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                DshPort = port,
                Workspace = workspace,
                DshHome = dshHome,
                OsPassword = osPassword,
                Running = false,
            };
            _instances.Add(inst);
            // Apply workspace + sandbox mode immediately so cordis.patch.yml is correct.
            ApplyWorkspace(inst);

            // Create OS user for isolation (if password provided). If user creation
            // fails (e.g. password rejected / elevated launcher missing), keep the
            // instance so the admin can later reset the password to auto-create it.
            if (!string.IsNullOrEmpty(osPassword))
            {
                try
                {
                    // Ensure workspace directory exists before setting permissions.
                    if (!string.IsNullOrWhiteSpace(workspace))
                        Directory.CreateDirectory(workspace);

                    var osUser = OsUserManager.CreateUser(id, osPassword);
                    if (osUser != null)
                    {
                        var docsDir = Path.Combine(workspace, "..", "adminroot", "sharedata");
                        OsUserManager.SetPermissions(id, workspace, docsDir, dshHome, _root);
                        // Mark as permissioned so Start() skips the slow icacls
                        // re-traversal (Create already set full permissions).
                        _permissionedOsUsers.TryAdd(id, true);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[instance] create OS user for '{id}' failed (instance kept): {ex.Message}");
                }
            }

            Save();
            return inst;
        }
    }

    private void CopyDshTemplate(string destDshHome)
    {
        try
        {
            Directory.CreateDirectory(destDshHome);
            // Top-level yaml (settings/providers), skip sessions/storages/credentials.
            foreach (var f in Directory.GetFiles(_dshTemplate, "*.yaml", SearchOption.TopDirectoryOnly))
                File.Copy(f, Path.Combine(destDshHome, Path.GetFileName(f)), overwrite: true);
            // profiles/web config.
            var srcWeb = Path.Combine(_dshTemplate, "profiles", "web");
            var dstWeb = Path.Combine(destDshHome, "profiles", "web");
            Directory.CreateDirectory(dstWeb);
            foreach (var f in Directory.GetFiles(srcWeb, "*", SearchOption.TopDirectoryOnly))
            {
                // preserve cordis.yml / patch.yml / package.json / pnpm-workspace.yaml
                File.Copy(f, Path.Combine(dstWeb, Path.GetFileName(f)), overwrite: true);
            }
            // Share the admin's DEEPSEEK_API_KEY (and any other secret refs) across
            // every instance so users don't each re-enter the key, while generating a
            // per-instance browser-session secret (never copied from the template).
            EnsureSharedCredentials(destDshHome);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[instance] template copy failed: {ex.Message}");
        }
    }

    // Copy the `refs` block (API keys) from the .dsh template into a fresh instance's
    // .credentials.yaml, but generate a NEW browser-session secret for that instance.
    // The API key (refs/*) is inherited from the ADMIN's root template each time the
    // instance starts, so a single key set by the admin on the admin DSH is shared
    // by every user instance. Per-instance records/* (browser-session secret) are
    // preserved; only refs/* values are refreshed from the shared template.
    private void EnsureSharedCredentials(string destDshHome)
    {
        try
        {
            var destCred = Path.Combine(destDshHome, ".credentials.yaml");
            var srcCred = Path.Combine(_dshTemplate, ".credentials.yaml");
            if (!File.Exists(srcCred)) return;

            // Parse src refs as name->value pairs.
            var srcRefs = ParseRefs(File.ReadAllLines(srcCred));
            if (srcRefs.Count == 0) return;

            if (!File.Exists(destCred))
            {
                // Fresh instance: write version + refs. DSH generates its own
                // records/browser-session secret on first boot (strict format).
                var y = new System.Text.StringBuilder();
                y.AppendLine("version: 1");
                foreach (var kv in srcRefs) y.AppendLine($"{kv.Key}: \"{kv.Value}\"");
                File.WriteAllText(destCred, y.ToString());
                Chmod600IfUnix(destCred);
                return;
            }

            // Existing instance: refresh only the refs values in place, preserving
            // everything else (version, records/..., per-instance secret).
            var destLines = File.ReadAllLines(destCred).ToList();
            var inRefs = false;
            var newLines = new List<string>();
            var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ln in destLines)
            {
                var trimmed = ln.TrimStart();
                if (trimmed.StartsWith("refs:")) { inRefs = true; newLines.Add(ln); continue; }
                if (inRefs)
                {
                    // Next top-level key ends the refs block.
                    if (ln.Length > 0 && !ln.StartsWith(" ") && !ln.StartsWith("\t"))
                    {
                        inRefs = false;
                        // Append any src refs not already present in dest.
                        foreach (var kv in srcRefs)
                        {
                            if (!seenRefs.Contains(kv.Key))
                            {
                                newLines.Add($"{kv.Key}: \"{kv.Value}\"");
                                seenRefs.Add(kv.Key);
                            }
                        }
                        newLines.Add(ln);
                        continue;
                    }
                    // A refs entry "NUM: value" -> if key is in srcRefs, override value.
                    var eq = trimmed.IndexOf(':');
                    if (eq > 0)
                    {
                        var key = trimmed.Substring(0, eq).Trim().Trim('"');
                        if (srcRefs.TryGetValue(key, out var val))
                        {
                            var indent = ln.Substring(0, ln.Length - ln.TrimStart().Length);
                            newLines.Add($"{indent}{key}: \"{val}\"");
                            seenRefs.Add(key);
                            continue;
                        }
                    }
                    newLines.Add(ln);
                    continue;
                }
                newLines.Add(ln);
            }
            // If the file never had a refs block (shouldn't happen), append one.
            if (!destLines.Any(d => d.TrimStart().StartsWith("refs:")))
            {
                newLines.Add("refs:");
                foreach (var kv in srcRefs) newLines.Add($"  {kv.Key}: \"{kv.Value}\"");
            }
            File.WriteAllLines(destCred, newLines);
            Chmod600IfUnix(destCred);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[instance] shared credential seed failed: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseRefs(string[] lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inRefs = false;
        foreach (var ln in lines)
        {
            var t = ln.TrimStart();
            if (t.StartsWith("refs:")) { inRefs = true; continue; }
            if (inRefs)
            {
                if (ln.Length > 0 && !ln.StartsWith(" ") && !ln.StartsWith("\t")) break;
                var eq = t.IndexOf(':');
                if (eq > 0)
                {
                    var k = t.Substring(0, eq).Trim().Trim('"');
                    var v = t.Substring(eq + 1).Trim().Trim('"', '\'').Trim();
                    d[k] = v;
                }
            }
        }
        return d;
    }

    public void Start(Instance inst)
    {
        lock (_gate)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [start] {inst.Id} port={inst.DshPort} workspace={inst.Workspace}");
            Trace.WriteLine($"[start:{inst.Id}] beginning start sequence");

            // Sync the shared API key from the admin template BEFORE the early-return
            // below, so even an instance that is already running (e.g. auto-restored
            // on launcher start) gets the real DeepSeek key the admin configured.
            try { EnsureSharedCredentials(inst.DshHome); } catch { }

            if (inst.Proc is { HasExited: false }) return;

            // Reclaim a stale process (and its OS-user child tree) on this instance's
            // port, then start our own. Without killing the whole tree the new dsh
            // hits EADDRINUSE and exits, leaving a stale process serving the port.
            if (DshService.IsPortInUse(inst.DshPort))
            {
                if (FindProcByPort(inst.DshPort, out var stalePid))
                    ForceKillTree(stalePid);
                Thread.Sleep(1500);
            }

            var node = NodeExe();
            var bin = BinJs();
            if (!File.Exists(node) || !File.Exists(bin))
                throw new InvalidOperationException("node/dsh not found");

            // Inject this instance's workspace into its own cordis.patch.yml.
            // This also calls EnsureWorkspace to seed a blank session if needed.
            ApplyWorkspace(inst);
            Trace.WriteLine($"[start:{inst.Id}] ApplyWorkspace done in {sw.ElapsedMilliseconds}ms");

            // OS-user isolation: run dsh under a dedicated restricted OS user so
            // it can only access its own workspace. If the process exits
            // immediately, capture stderr to a diagnostic file for triage.
            Process? proc = null;
            var psi = new ProcessStartInfo
            {
                FileName = node,
                WorkingDirectory = inst.DshHome,
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
            psi.ArgumentList.Add(inst.DshPort.ToString());
            psi.ArgumentList.Add("--no-open");
            psi.Environment["DSH_HOME"] = inst.DshHome;

            if (!string.IsNullOrEmpty(inst.OsPassword))
            {
                // Ensure the dedicated OS user exists and permissions are set ONCE
                // (the icacls recursion over node_modules is slow but persistent).
                var osUserReady = _permissionedOsUsers.ContainsKey(inst.Id);
                if (!osUserReady)
                {
                    Trace.WriteLine($"[start:{inst.Id}] creating OS user + setting permissions");
                    try
                    {
                        var osUser = OsUserManager.CreateUser(inst.Id, inst.OsPassword);
                        if (osUser != null)
                        {
                            var docsDir = Path.Combine(inst.Workspace, "..", "adminroot", "sharedata");
                            if (!string.IsNullOrWhiteSpace(inst.Workspace)) Directory.CreateDirectory(inst.Workspace);
                            OsUserManager.SetPermissions(inst.Id, inst.Workspace, docsDir, inst.DshHome, _root);
                            _permissionedOsUsers.TryAdd(inst.Id, true);
                            osUserReady = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[start:{inst.Id}] OS user creation failed: {ex.Message}");
                    }
                    Trace.WriteLine($"[start:{inst.Id}] OS user setup done in {sw.ElapsedMilliseconds}ms");
                }

                if (osUserReady)
                {
                    var env = new Dictionary<string, string> { ["DSH_HOME"] = inst.DshHome };
                    proc = OsUserManager.StartAsUser(inst.Id, node,
                        ["--expose-internals", bin, "--profile", "web", "--port", inst.DshPort.ToString(), "--no-open"],
                        inst.DshHome, env, inst.OsPassword);
                }
                // If OS user creation or StartAsUser failed, fall back to launching
                // DSH directly (no OS-level isolation) so the instance can still start.
                if (proc == null)
                    Trace.WriteLine($"[start:{inst.Id}] OS user unavailable, falling back to direct launch");
            }

            if (proc == null)
            {
                proc = new Process { StartInfo = psi };
            }

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) { inst.Logs.Enqueue(e.Data); CaptureToken(inst, e.Data); } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) inst.Logs.Enqueue("[ERR] " + e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [start] dsh started (pid {proc.Id})");
            Trace.WriteLine($"[start:{inst.Id}] process started in {sw.ElapsedMilliseconds}ms");

            // If the process dies immediately (common when a restricted user lacks
            // access to node/dsh), record its full stderr for diagnosis.
            var diagPath = Path.Combine(_root, "osuser-diag.log");
            try { File.Delete(diagPath); } catch { }
            Task.Run(async () =>
            {
                try { await Task.Delay(5000); } catch { }
                if (proc is { HasExited: true })
                {
                    try
                    {
                        var err = string.Join("\n", inst.Logs.Where(l => l.StartsWith("[ERR]")));
                        File.WriteAllText(diagPath, $"[{DateTime.Now:HH:mm:ss}] dsh as OS user exited code={proc.ExitCode}\n{err}\n---tail---\n{string.Join("\n", inst.Logs.TakeLast(40))}");
                    }
                    catch { }
                }
            });

            inst.Proc = proc;
            inst.Running = true;
            Save();
        }
    }

    private void CaptureToken(Instance inst, string line)
    {
        var idx = line.IndexOf("dsh web:", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var url = line.Substring(idx + "dsh web:".Length).Trim();
            if (url.StartsWith("http") && url.Contains("token="))
            {
                inst.TokenUrl = url;
                Save(); // persist so token survives launcher restarts
            }
        }
    }

    public void Stop(Instance inst)
    {
        lock (_gate)
        {
            if (inst.Proc is { HasExited: false }) { try { inst.Proc.Kill(true); } catch { } try { inst.Proc.WaitForExit(5000); } catch { } }
            // Also force-kill the whole tree by port, in case the OS-user child
            // (which the launcher doesn't hold a handle to) is still running.
            if (DshService.IsPortInUse(inst.DshPort) && FindProcByPort(inst.DshPort, out var stopPid))
                ForceKillTree(stopPid);
            inst.Proc = null;
            inst.Running = false;
            Save();
        }
    }

    public void Delete(Instance inst)
    {
        lock (_gate)
        {
            if (inst.Proc is { HasExited: false }) { try { inst.Proc.Kill(true); } catch { } }
            if (DshService.IsPortInUse(inst.DshPort) && FindProcByPort(inst.DshPort, out var delPid))
                ForceKillTree(delPid);
            _instances.Remove(inst);
            // Remove OS user for this instance.
            OsUserManager.DeleteUser(inst.Id);
            SafeDeleteDir(Path.GetDirectoryName(inst.DshHome) ?? "");
            Save();
        }
    }

    public Instance? Get(string id) => _instances.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public string OpenUrl(Instance inst)
    {
        if (!string.IsNullOrEmpty(inst.TokenUrl)) return inst.TokenUrl;
        return $"http://127.0.0.1:{inst.DshPort}/";
    }

    /// <summary>
    /// Try to extract the DSH web auth token from the instance's credentials file
    /// when TokenUrl was not captured from stdout. Falls back to reading
    /// .credentials.yaml → records → client-connection/browser-session → payload → secret.
    /// </summary>
    public string? GetTokenFromConfig(Instance inst)
    {
        try
        {
            var credFile = Path.Combine(inst.DshHome, ".credentials.yaml");
            if (!File.Exists(credFile)) return null;
            var yaml = File.ReadAllText(credFile);
            var lines = yaml.Split('\n');
            bool inSession = false, inPayload = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimEnd();
                if (trimmed.Contains("client-connection/browser-session")) { inSession = true; inPayload = false; continue; }
                if (inSession && trimmed.TrimStart().StartsWith("payload:")) { inPayload = true; continue; }
                if (inPayload && trimmed.TrimStart().StartsWith("secret:"))
                {
                    var secret = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(secret)) return secret;
                }
                // Exit section if we hit another top-level key
                if (inPayload && line.Length > 0 && !line.StartsWith(" ") && !line.StartsWith("\t"))
                { inSession = false; inPayload = false; }
            }
        }
        catch { }
        return null;
    }

    // Inject this instance's workspace + access restrictions into cordis.patch.yml.
    private void ApplyWorkspace(Instance inst)
    {
        try
        {
            var patchFile = Path.Combine(inst.DshHome, "profiles", "web", "cordis.patch.yml");
            try { Directory.CreateDirectory(Path.GetDirectoryName(patchFile)!); } catch { }

            // Remove the "添加工作区" (add workspace) UI affordance from the
            // shared DSH client bundle. Idempotent; survives DSH re-bundles only
            // if the bundle keeps its original source, so it re-applies each start.
            EnsureClientPatch(inst);

            var norm = inst.Workspace.Replace('\\', '/');
            var nativePath = Path.GetFullPath(inst.Workspace);   // native (backslash) form DSH canonicalizes to

            // Build the complete cordis.patch.yml with sandbox + persona restrictions.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Auto-generated by launcher — do not edit manually.");
            sb.AppendLine("# Sandbox: workspace-write prevents writes outside the workspace.");
            sb.AppendLine("# Persona: instructs the AI to restrict reads to workspace + docs only.");
            sb.AppendLine();
            sb.AppendLine("- id: sandbox-policy");
            sb.AppendLine("  config:");
            sb.AppendLine($"    workspaceRoot: {norm}");
            sb.AppendLine();
            sb.AppendLine("- id: fs-sandbox");
            sb.AppendLine("  config:");
            sb.AppendLine($"    cwd: {norm}");
            sb.AppendLine("    mode: workspace-write");
            sb.AppendLine();
            // Disable every shell/terminal tool for per-user instances: arbitrary
            // shell commands can read any host file, and no reliable OS-level sandbox
            // is available on Windows here. Confining the agent to the fs tools keeps
            // reads and writes inside the workspace.
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
            // Auto-select user's workspace so they don't need to pick it manually.
            // The host directory-picker service must stay enabled — the workspace
            // controller hard-depends on it. The workspace-write sandbox fences all
            // instance writes; launcher /api/browse is admin-only.
            sb.AppendLine("- id: workspace");
            sb.AppendLine("  config:");
            sb.AppendLine($"    path: {norm}");
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

            // Pre-populate workspace.json so DSH UI auto-selects the workspace.
            // Idempotent: reuse the existing workspace record + session ids when
            // present, otherwise create a fresh workspace and seed a blank
            // session so DSH auto-opens it (without a session, DSH shows the
            // "select workspace" hero screen and disables the chat input).
            EnsureWorkspace(inst, nativePath);
        }
        catch { }
    }

    // Ensure the instance's workspace.json is valid and references at least one
    // session, so DSH auto-opens the workspace on first load (otherwise it shows
    // the "选择工作区" hero and disables the chat input). Idempotent: a valid
    // existing record is preserved; only a missing/empty one is recreated.
    private void EnsureWorkspace(Instance inst, string norm)
    {
        try
        {
            var storagesDir = Path.Combine(inst.DshHome, "storages");
            Directory.CreateDirectory(storagesDir);
            var wsFile = Path.Combine(storagesDir, "workspace.json");
            var wsName = inst.Name ?? inst.Id;
            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            // Reuse an existing valid workspace record already pointing at this path.
            if (File.Exists(wsFile))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(wsFile));
                    if (doc.RootElement.TryGetProperty("tables", out var tables) &&
                        tables.TryGetProperty("workspaces", out var wsTable))
                    {
                        foreach (var p in wsTable.EnumerateObject())
                        {
                            if (p.Value.TryGetProperty("path", out var pathEl) &&
                                p.Value.TryGetProperty("sessionIds", out var sidEl) &&
                                sidEl.GetArrayLength() > 0)
                            {
                                // Normalize both stored and requested paths to
                                // forward slashes for comparison so mixed-separator
                                // paths (E:/a\b) match canonical forms (E:/a/b).
                                var stored = (pathEl.GetString() ?? "").Replace('\\', '/');
                                var requested = norm.Replace('\\', '/');
                                if (string.Equals(stored, requested, StringComparison.OrdinalIgnoreCase))
                                    return; // already good — keep the record + its sessions
                            }
                        }
                    }
                }
                catch { /* fall through to recreate */ }
            }

            // Create a fresh workspace and seed a blank session so DSH auto-opens it.
            var wsId = Guid.NewGuid().ToString();
            var sessionId = "session-" + Guid.NewGuid().ToString();
            SeedBlankSession(inst, norm, sessionId);
            // The workspace path must be JSON-escaped: DSH stores the native path
            // (e.g. E:\WorkBuddy\ericliu) and rejects unescaped backslashes.
            var pathJson = System.Text.Json.JsonSerializer.Serialize(norm);   // -> "...\\..."
            var titleJson = System.Text.Json.JsonSerializer.Serialize(wsName);
            var wsJson = $$"""
            {
              "unit": { "name": "workspace", "version": 2 },
              "global": { "initialized": true, "workspaceIds": ["{{wsId}}"], "archivedSessionIds": [] },
              "tables": { "workspaces": { "{{wsId}}": { "path": {{pathJson}}, "title": {{titleJson}}, "sessionIds": ["{{sessionId}}"], "createdAt": "{{now}}", "updatedAt": "{{now}}" } } }
            }
            """;
            File.WriteAllText(wsFile, wsJson);
        }
        catch { }
    }

    // Write a minimal blank session (zstd-compressed header JSONL line) under the
    // instance's sessions dir, using the same path/key encoding DSH uses, so the
    // workspace owns a session and DSH auto-selects it.
    private void SeedBlankSession(Instance inst, string norm, string sessionId)
        => SeedBlankSessionAt(_root, inst.DshHome, norm, sessionId);

    // Seed a blank session under a given dsh home's sessions dir. Shared by
    // per-user instances and the admin default dsh.
    private static void SeedBlankSessionAt(string root, string dshHome, string norm, string sessionId)
    {
        try
        {
            var node = Path.Combine(root, "node", "node.exe");
            if (!File.Exists(node))
            {
                Trace.WriteLine("[workspace] node.exe not found; skipping blank-session seed");
                return;
            }
            var helper = Path.Combine(Path.GetTempPath(), "taketopds_seed.js");
            var script = @"const fs=require('fs'),path=require('path'),z=require('zlib');
function encs(s){if(!s.length)throw 0;if(s==='.')return '~002E';if(s==='..')return '~002E~002E';let o='';for(let i=0;i<s.length;i++){var c=s.charCodeAt(i),ch=String.fromCharCode(c);if(ch!=='~'&&/^[A-Za-z0-9._-]$/.test(ch))o+=ch;else o+='~'+c.toString(16).toUpperCase().padStart(4,'0');}return o;}
function keys(cwd){let r='',s=false;for(let i=0;i<cwd.length;i++){var c=cwd.charCodeAt(i),ch=String.fromCharCode(c);if(ch==='/'||ch==='\\'||ch===':'){if(!s)r+='-';s=true;}else if(ch!=='~'&&/^[A-Za-z0-9._-]$/.test(ch)){r+=ch;s=false;}else{r+='~'+c.toString(16).toUpperCase().padStart(4,'0');s=false;}}return '--'+(r.replace(/^-+/,'')||'root').slice(0,251)+'--';}
const root=process.argv[2],cwd=process.argv[3],id=process.argv[4];
const dir=path.join(root,keys(cwd),encs(id));fs.mkdirSync(dir,{recursive:true});
const rec={type:'session',version:0,id,createdAt:Date.now(),cwd,delegationDepth:0,agentPreset:'standard'};
const rec2=JSON.stringify(rec)+'\n';
fs.writeFileSync(path.join(dir,'session.jsonl.zstd'),z.zstdCompressSync(Buffer.from(rec2,'utf8')));
";
            File.WriteAllText(helper, script);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = node,
                WorkingDirectory = dshHome,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(helper);
            psi.ArgumentList.Add(Path.Combine(dshHome, "sessions"));
            psi.ArgumentList.Add(norm);
            psi.ArgumentList.Add(sessionId);
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(8000);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[workspace] seed blank session failed: {ex.Message}");
        }
    }

    // Ensure the admin default dsh's workspace.json points EXACTLY at the
    // currently-configured global workspace path, seeding a blank session so DSH
    // auto-opens it. Always replaces any prior record, so the admin's workspace
    // tracks the workspace-path field whenever it changes.
    public static void EnsureAdminWorkspace(string root, string dshHome, string workspacePath, string title)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspacePath)) return;
            var nativePath = Path.GetFullPath(workspacePath);
            var storagesDir = Path.Combine(dshHome, "storages");
            Directory.CreateDirectory(storagesDir);
            var wsFile = Path.Combine(storagesDir, "workspace.json");

            // Idempotent: if the file already has a record at this path with a
            // session, leave it (avoids re-copying). Otherwise rewrite fully.
            var hasTarget = false;
            if (File.Exists(wsFile))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(wsFile));
                    if (doc.RootElement.TryGetProperty("tables", out var tables) &&
                        tables.TryGetProperty("workspaces", out var wsTable))
                    {
                        foreach (var p in wsTable.EnumerateObject())
                        {
                            if (p.Value.TryGetProperty("path", out var pathEl) &&
                                string.Equals(pathEl.GetString(), nativePath, StringComparison.OrdinalIgnoreCase) &&
                                p.Value.TryGetProperty("sessionIds", out var sidEl) &&
                                sidEl.GetArrayLength() > 0)
                                hasTarget = true;
                        }
                    }
                }
                catch { /* rewrite below */ }
            }
            if (hasTarget) return;

            var wsId = Guid.NewGuid().ToString();
            var sessionId = "session-" + Guid.NewGuid().ToString();
            SeedBlankSessionAt(root, dshHome, nativePath, sessionId);
            var pathJson = System.Text.Json.JsonSerializer.Serialize(nativePath);
            var titleJson = System.Text.Json.JsonSerializer.Serialize(title);
            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var wsJson = $$"""
            {
              "unit": { "name": "workspace", "version": 2 },
              "global": { "initialized": true, "workspaceIds": ["{{wsId}}"], "archivedSessionIds": [] },
              "tables": { "workspaces": { "{{wsId}}": { "path": {{pathJson}}, "title": {{titleJson}}, "sessionIds": ["{{sessionId}}"], "createdAt": "{{now}}", "updatedAt": "{{now}}" } } }
            }
            """;
            File.WriteAllText(wsFile, wsJson);
            Trace.WriteLine($"[workspace] admin default dsh workspace -> {nativePath}");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[workspace] admin workspace ensure failed: {ex.Message}");
        }
    }

    // Remove the "添加工作区" (add workspace) affordance from DSH's workspace
    // picker by patching the served client bundle: (1) the workspace dropdown's
    // "添加工作区" menu entry is stripped (addEntries always []) and (2) the
    // sidebar "+" add-workspace icon button is disabled (directoryFlowAvailable
    // gated to false). Runs idempotently on every instance start so it re-applies
    // after a DSH update restores the original bundle. Only rewrites lines that
    // still carry the unpatched source.
    private void EnsureClientPatch(Instance inst)
    {
        try
        {
            // Patch the SHARED client bundle (workspace remove, fs-sandbox, bash/pwsh
            // escalation, search containment, hero preview). Keep the SHARED settings
            // trigger VISIBLE (hideSettings:false -> ApplyAll negates it to shown) so
            // the admin's default DSH (46000) can still open the settings page.
            // Normal users get the settings hidden via their own per-instance copy
            // (SetSettingsVisible below), which DSH resolves before the shared one.
            DshPatcher.ApplyAll(_root, hideSettings: false, m => Trace.WriteLine("[patch] " + m));
            // Per-user settings bundle copy (the instance's OWN settings bundle is
            // hidden; the shared/admin bundle is kept restored for admins).
            SetSettingsVisible(inst, false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[client-patch] failed: {ex.Message}");
        }
    }


    // Show or hide the DSH sidebar "设置" (settings) trigger by replacing the
    // settings footer button with `false` (React renders nothing) to hide it, or
    // restoring the original button to show it. Keeps the ConnectionIndicator.
    // Idempotent; re-applies after a DSH update restores the bundle. Shared
    // helper; used by per-user instances (hide) and the admin default dsh (show).
    // Shared-bundle variant used by the admin default dsh (DshService) — the
    // shared client bundle stays RESTORED so the admin can open DSH settings.
    public static void SetSettingsVisible(string root, bool visible)
    {
        try
        {
            var settingsGeneral = SharedSettingsPath(root);
            if (!File.Exists(settingsGeneral)) { Trace.WriteLine("[client-patch] settings-general client.js not found"); return; }
            PatchSettingsBundle(settingsGeneral, visible);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[client-patch] settings hide failed: {ex.Message}");
        }
    }

    // Absolute path of the shared settings-general client bundle.
    public static string SharedSettingsPath(string root) =>
        Path.Combine(DshPatcher.ResolvePluginDir(DshPkgDirFor(root), "dsh-client-ui-settings-general"), "lib", "client.js");

    // Per-instance variant: ensure the instance has its OWN settings bundle
    // copy, then patch it (hide for non-admin). The shared/admin bundle stays
    // restored. DSH resolves the per-instance package before the shared one.
    private void SetSettingsVisible(Instance inst, bool visible)
    {
        try
        {
            var pkgDir = Path.Combine(inst.DshHome, "profiles", "web", "node_modules", "@deepseek-ai", "dsh-client-ui-settings-general");
            var target = Path.Combine(pkgDir, "lib", "client.js");
            var sharedPkgDir = DshPatcher.ResolvePluginDir(DshPkgDir(), "dsh-client-ui-settings-general");
            if (!Directory.Exists(sharedPkgDir)) return;
            if (!File.Exists(target) || SettingsVersion(pkgDir) != SettingsVersion(sharedPkgDir))
            {
                try { if (Directory.Exists(pkgDir)) Directory.Delete(pkgDir, true); } catch { }
                Directory.CreateDirectory(pkgDir);
                CopyDir(sharedPkgDir, pkgDir);
            }
            PatchSettingsBundle(target, visible);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[client-patch] per-instance settings failed: {ex.Message}");
        }
    }

    // Read the "version" field from a package dir's package.json ("" if unknown).
    private static string SettingsVersion(string pkgDir)
    {
        try
        {
            var pj = Path.Combine(pkgDir, "package.json");
            if (!File.Exists(pj)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(pj));
            return doc.RootElement.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
        }
        catch { return ""; }
    }

    private static void PatchSettingsBundle(string settingsGeneral, bool visible)
    {
        var src = File.ReadAllText(settingsGeneral);
        var btnStart = "(0, react_jsx_runtime.jsx)(\"button\", {";
        var ciMark = "(0, react_jsx_runtime.jsx)(_deepseek_ai_dsh_client_ui_primitives.ConnectionIndicator";
        string patched = src;
        if (visible)
        {
            var hiddenMarker = "false, " + ciMark;
            var h = src.IndexOf(hiddenMarker, StringComparison.Ordinal);
            if (h >= 0)
            {
                var origBtn = "(0, react_jsx_runtime.jsx)(\"button\", {\n            ref: triggerButton,\n            type: \"button\",\n            className: clsx(SettingsRoot_module_css_default.trigger, !wide && SettingsRoot_module_css_default.rail),\n            \"aria-haspopup\": \"dialog\",\n            \"aria-expanded\": open,\n            onClick: () => {\n              setOpen(true);\n            },\n            children: renderSlot(\"settings.trigger\", { wide })\n          }), " + ciMark;
                var arrStart = src.LastIndexOf("children: [", h, StringComparison.Ordinal);
                if (arrStart >= 0)
                    patched = src.Substring(0, arrStart) + "children: [" + origBtn + src.Substring(h + hiddenMarker.Length);
            }
        }
        else
        {
            var a = src.IndexOf(btnStart, StringComparison.Ordinal);
            var b = a >= 0 ? src.IndexOf(ciMark, a, StringComparison.Ordinal) : -1;
            var closeBtn = a >= 0 && b >= 0 ? src.LastIndexOf("}), ", b - 1, StringComparison.Ordinal) : -1;
            if (a >= 0 && b >= 0 && closeBtn >= a)
            {
                var after = closeBtn + "}), ".Length;
                patched = src.Substring(0, a) + "false, " + src.Substring(after);
            }
        }
        if (!string.Equals(patched, src, StringComparison.Ordinal))
        {
            File.WriteAllText(settingsGeneral, patched);
            Trace.WriteLine(visible ? "[client-patch] settings shown" : "[client-patch] settings hidden");
        }
    }

    // Recursively copy a directory (files only; skip node_modules inside).
    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from))
        {
            if (Path.GetFileName(d) == "node_modules") continue;
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    private void SafeDeleteDir(string p)
    {
        try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }
    }

    public void Save()
    {
        lock (_gate)
        {
            var rows = _instances.Select(i => new LauncherInstanceRow
            {
                Id = i.Id,
                Name = i.Name,
                DshPort = i.DshPort,
                Workspace = i.Workspace,
                OsPassword = i.OsPassword,
                TokenUrl = i.TokenUrl,
                Running = i.Running,
            }).ToList();
            LauncherDb.SaveInstances(_root, rows);
        }
    }
}

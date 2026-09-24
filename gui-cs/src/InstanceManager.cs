// TakeTopDshTeam — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This software is the intellectual property of 泰顶拓鼎信息科技（上海）有限公司
// All rights reserved.

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
        // In-memory only: set once the OS-user launch has proven impossible in THIS
        // launcher process (see the fallback in Start). The instance then runs
        // un-isolated instead of being permanently unable to start.
        [JsonIgnore] public bool IsolationUnavailable { get; set; }
        public int LauncherMarker { get; set; } = 0;  // not used; dsh uses --port
        public bool Running { get; set; }

        // True while a start (OS user + process launch) is in progress. Not persisted;
        // used by the proxy to show a "starting" page instead of a "not running" error.
        [JsonIgnore] public bool Starting { get; set; }

        // True while WE are stopping this process (Stop / shutdown). The watchdog uses it
        // to tell a deliberate stop from a crash and then stays quiet. Not persisted.
        [JsonIgnore] public bool Stopping { get; set; }

        [JsonIgnore] public Process? Proc { get; set; }
        [JsonIgnore] public ConcurrentQueue<string> Logs { get; } = new();
        public string TokenUrl { get; set; } = "";
    }

    private readonly string _root;
    private readonly string _instancesDir;   // legacy config/instances.json (migration source)
    private readonly string _instRoot;       // instances/<id>
    private readonly string _dshTemplate;    // .dsh template root
    private readonly int _basePort;          // first port used for auto-assignment
    // Ports that must never be auto-assigned to a member (launcher + admin DSH).
    public HashSet<int> ReservedPorts { get; } = new();
    // Returns the launcher's default DSH locale ("zh"/"en"); set by Program.cs so
    // each member instance's settings.yaml follows the admin's 缺省语言.
    public Func<string>? DefaultLocaleProvider { get; set; }
    private readonly object _gate = new();

    // ---- watchdog: a member DSH that dies on its own is restarted automatically ----
    // Consecutive restart attempts per instance (reset once a process stays up a while).
    private readonly Dictionary<string, int> _wdTries = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _shuttingDown;
    // Called from the app's shutdown hook so exits during shutdown are not 'crashes'.
    public void BeginShutdown() { _shuttingDown = true; }
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
            IMark("Load: begin");
            var rows = LauncherDb.LoadInstances(_root);
            IMark("Load: after LoadInstances rows=" + rows.Count);

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
                IMark($"Load: inst {inst.Id} dbRunning={inst.Running} port={inst.DshPort}");
                var __inUse = inst.Running && DshService.IsPortInUse(inst.DshPort);
                IMark($"Load: inst {inst.Id} inUse={__inUse}");
                if (__inUse)
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
                IMark($"Load: inst {inst.Id} done running={inst.Running}");
                _instances.Add(inst);
            }

            // Auto-correct any instance whose port is now held by another process
            // (e.g. the environment reassigned a fixed port). Reallocate a free
            // port so the user never has to touch port config. Skip default dsh.
            IMark("Load: before RebalancePorts");
            RebalancePorts();
            IMark("Load: after RebalancePorts");
        }
    }

    private static void IMark(string m)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taketopds-startup.log"), $"{DateTime.Now:HH:mm:ss.fff}    [IM] {m}\n"); } catch { }
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
                IMark($"RB: {inst.Id} port={inst.DshPort}");
                if (inst.DshPort <= 0) { inst.DshPort = AllocPort(); changed = true; continue; }
                if (!DshService.IsPortInUse(inst.DshPort)) { IMark($"RB: {inst.Id} free"); continue; }
                // Port is live. Keep it only if it is our instance's own dsh;
                // otherwise the port was hijacked by something else -> reallocate.
                bool ours = inst.Proc is { HasExited: false } && FindProcByPort(inst.DshPort, out var pid) && inst.Proc.Id == pid;
                IMark($"RB: {inst.Id} live ours={ours}");
                if (!ours)
                {
                    inst.DshPort = AllocPort();
                    inst.Running = false;
                    changed = true;
                }
                IMark($"RB: {inst.Id} done port={inst.DshPort}");
            }
            if (changed) Save();
        }
        catch (Exception ex) { Trace.WriteLine($"[ports] rebalance failed: {ex.Message}"); }
    }

    // Cache of `netstat -ano` output. FindProcByPort is called once per running
    // instance during startup (Load + RebalancePorts); spawning netstat each time
    // cost several seconds, so reuse one table for a short TTL.
    private static readonly object _netstatLock = new();
    private static string _netstatCache = "";
    private static DateTime _netstatAt = DateTime.MinValue;

    private static string NetstatTable()
    {
        lock (_netstatLock)
        {
            if (_netstatCache.Length > 0 && (DateTime.UtcNow - _netstatAt).TotalSeconds < 5) return _netstatCache;
            try
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
                if (p != null)
                {
                    var o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    if (o.Length > 0) { _netstatCache = o; _netstatAt = DateTime.UtcNow; }
                }
            }
            catch { }
            return _netstatCache;
        }
    }

    private bool FindProcByPort(int port, out int pid)
    {
        pid = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var outLines = NetstatTable();
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
    // OS, ports reserved for the launcher/admin DSH, and ports already assigned
    // to another instance). A port is only accepted when it can actually be bound.
    // When the workspace ROOT moves (the user moved the folder and changed the
    // path), member instance workspaces are stored as ABSOLUTE paths; re-base the
    // ones that lived under the old root to the new one. Only rebase when the
    // folder actually exists at the new location, so we never point at an empty
    // dir (if the user didn't move the data, leave the recorded path untouched).
    public void RebaseWorkspaces(string oldRoot, string newRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot)) return;
            var o = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var n = Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(o, n, StringComparison.OrdinalIgnoreCase)) return;
            var oPrefix = o + Path.DirectorySeparatorChar;
            var changed = false;
            lock (_gate)
            {
                foreach (var inst in _instances)
                {
                    if (string.IsNullOrWhiteSpace(inst.Workspace)) continue;
                    var w = Path.GetFullPath(inst.Workspace);
                    if (!w.StartsWith(oPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(n, w.Substring(oPrefix.Length));
                    if (!Directory.Exists(target)) continue;   // data not moved there -> leave as-is
                    Trace.WriteLine($"[workspace] rebased {inst.Id}: {w} -> {target}");
                    inst.Workspace = target;
                    changed = true;
                }
                if (changed) Save();
            }
        }
        catch (Exception ex) { Trace.WriteLine($"[workspace] rebase failed: {ex.Message}"); }
    }

    public int AllocPort(int? basePort = null)
    {
        var start = basePort ?? _basePort;
        HashSet<int> used = new(_instances.Select(i => i.DshPort).Where(p => p > 0));
        foreach (var r in ReservedPorts) used.Add(r);
        for (var p = Math.Max(1, start); p <= 65535 && p < start + 2000; p++)
        {
            if (used.Contains(p)) continue;
            if (DshService.IsPortInUse(p)) continue;
            if (!DshService.IsPortFree(p)) continue;
            return p;
        }
        throw new InvalidOperationException("no free port available");
    }

    // Apply the launcher default locale to one instance's own DSH settings.yaml so
    // member DSH matches the admin's 缺省语言 (instead of a stale template value).
    private void ApplyLocale(Instance inst)
    {
        try
        {
            if (inst == null || string.IsNullOrWhiteSpace(inst.DshHome)) return;
            var code = DefaultLocaleProvider?.Invoke() ?? "en";
            DshService.WriteLocalePreference(inst.DshHome, code);
            // Product default Appearance = Light, so member DSH opens in Light too.
            DshService.WriteThemePreference(inst.DshHome, "light");
        }
        catch { }
    }

    // Re-apply the default locale to every instance (launcher start / language change).
    public void ApplyLocaleToAll()
    {
        foreach (var i in _instances.ToList()) ApplyLocale(i);
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

    // Platform-correct bundled node runtime ("node" = fall back to PATH). Static twin of
    // NodeExe() so the workspace/session seeder can use it too.
    private static string NodeExeFor(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var win = Path.Combine(root, "node", "node.exe");
            return File.Exists(win) ? win : "node";
        }
        var sub = PlatformSubDir();
        var p = Path.Combine(root, "node", sub, "bin", "node");
        return File.Exists(p) ? p : "node";
    }

    // Origins a member DSH should trust, mirroring DshService.TrustedHosts(): (a) the
    // instance's own loopback (the launcher proxies to 127.0.0.1:<instPort>), (b) the
    // launcher's loopback (a browser ON the server), and (c) the configured external URL
    // authority (the reverse proxy). Only host:port forms are emitted - that is the shape
    // DSH accepts and the shape the admin's (working) DSH already used.
    private static IReadOnlyList<string> TrustedHostsFor(string root, int instPort)
    {
        var list = new List<string>();
        void Add(string? h)
        {
            if (string.IsNullOrWhiteSpace(h)) return;
            h = h.Trim();
            if (h.Length == 0 || list.Contains(h, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(h);
        }
        try
        {
            if (instPort > 0) { Add($"127.0.0.1:{instPort}"); Add($"localhost:{instPort}"); }

            string? ext = null;
            var lp = 0;
            var ws = Path.Combine(root, "WorkSpace");
            var localCfg = Path.Combine(root, "config", "launcher.local.json");
            if (File.Exists(localCfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(localCfg));
                if (doc.RootElement.TryGetProperty("DshWeb", out var w))
                {
                    if (w.TryGetProperty("ExternalUrl", out var e)) ext = e.GetString();
                    if (w.TryGetProperty("WorkspacePath", out var wp) && !string.IsNullOrWhiteSpace(wp.GetString()))
                        ws = wp.GetString()!;
                }
            }
            var wsCfg = Path.Combine(ws, "config", "launcher.json");
            if (File.Exists(wsCfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(wsCfg));
                if (doc.RootElement.TryGetProperty("DshWeb", out var w))
                {
                    if (string.IsNullOrEmpty(ext) && w.TryGetProperty("ExternalUrl", out var e)) ext = e.GetString();
                    if (w.TryGetProperty("LauncherPort", out var p))
                    {
                        if (p.ValueKind == JsonValueKind.Number) lp = p.GetInt32();
                        else int.TryParse(p.ToString(), out lp);
                    }
                }
            }
            if (lp <= 0)
            {
                var rt = Path.Combine(root, "config", "launcher.runtime.json");
                if (File.Exists(rt))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rt));
                    if (doc.RootElement.TryGetProperty("port", out var p)) int.TryParse(p.ToString(), out lp);
                }
            }
            if (!string.IsNullOrEmpty(ext) && Uri.TryCreate(ext, UriKind.Absolute, out var uri))
                Add(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}");
            if (lp > 0) { Add($"127.0.0.1:{lp}"); Add($"localhost:{lp}"); }
        }
        catch { }
        return list;
    }

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
            // Match the admin's 缺省语言 for this new member's DSH.
            ApplyLocale(inst);

            // Create OS user for isolation (if password provided). If user creation
            // fails (e.g. password rejected / elevated launcher missing), keep the
            // instance so the admin can later reset the password to auto-create it.
            //
            // Done in the BACKGROUND: it creates an account, walks the tree with icacls
            // and on Windows performs one logon to build the profile, so it must never
            // block the HTTP request that created the instance - and a failure has to be
            // visible instead of silently swallowed. Start() re-runs it on demand if the
            // member is started before this pass ends.
            if (!string.IsNullOrEmpty(osPassword))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(workspace))
                            Directory.CreateDirectory(workspace);

                        OsUserManager.CreateUser(id, osPassword);

                        var docsDir = Path.Combine(workspace, "..", "adminroot", "sharedata");
                        OsUserManager.SetPermissions(id, workspace, docsDir, dshHome, _root);

                        // Make the OS-level isolation real (see HardenMemberAcl).
                        if (OperatingSystem.IsWindows())
                        {
                            try { LogWorkspace(_root, $"[isolation] {id}: acl -> {OsUserManager.HardenMemberAcl(id, workspace, dshHome)}"); }
                            catch (Exception aex) { LogWorkspace(_root, $"[isolation] {id}: acl hardening failed: {aex.Message}"); }
                        }

                        // Create the Windows profile ONCE here (a single logon), so the
                        // first start does not have to and so it exists before anything
                        // could create a plain directory of the same name.
                        if (OperatingSystem.IsWindows())
                        {
                            try { OsUserManager.EnsureWindowsProfile(id, osPassword); }
                            catch (Exception pex) { LogWorkspace(_root, $"[isolation] {id}: profile pre-create failed: {pex.Message}"); }
                        }

                        // Mark as permissioned so Start() skips the slow icacls
                        // re-traversal (Create already set full permissions).
                        _permissionedOsUsers.TryAdd(id, true);
                        LogWorkspace(_root, $"[isolation] {id}: OS account ready, isolated");
                    }
                    catch (Exception ex)
                    {
                        LogWorkspace(_root, $"[isolation] {id}: OS account setup failed - this member will run WITHOUT isolation: {ex.Message}");
                        try { inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [isolation] setup failed - running WITHOUT isolation: {ex.Message}"); } catch { }
                    }
                });
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
    private bool EnsureSharedCredentials(string destDshHome)
    {
        try
        {
            var destCred = Path.Combine(destDshHome, ".credentials.yaml");
            var srcCred = Path.Combine(_dshTemplate, ".credentials.yaml");
            if (!File.Exists(srcCred)) return false;

            // Parse src refs as name->value pairs.
            var srcRefs = ParseRefs(File.ReadAllLines(srcCred));
            if (srcRefs.Count == 0) return false;

            if (!File.Exists(destCred))
            {
                // Fresh instance: write version + refs. DSH generates its own
                // records/browser-session secret on first boot (strict format).
                var y = new System.Text.StringBuilder();
                y.AppendLine("version: 1");
                y.AppendLine("refs:");
                foreach (var kv in srcRefs) y.AppendLine($"  {kv.Key}: \"{kv.Value}\"");
                File.WriteAllText(destCred, y.ToString());
                Chmod600IfUnix(destCred);
                return true;
            }

            // Existing instance: refresh only the refs values in place, preserving
            // everything else (version, records/..., per-instance secret).
            var destLines = File.ReadAllLines(destCred).ToList();
            var inRefs = false;
            var newLines = new List<string>();
            var seenRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var ln in destLines)
            {
                var trimmed = ln.TrimStart();
                // Heal a malformed file: a secret key written at column 0 (e.g. a
                // stray top-level "DEEPSEEK_API_KEY:") is illegal - the DSH
                // credentials-local parser rejects it and the instance never boots.
                // Drop it; the real value lives in the indented refs: block below.
                if (ln.Length > 0 && !char.IsWhiteSpace(ln[0]))
                {
                    var c0 = ln.IndexOf(':');
                    if (c0 > 0)
                    {
                        var k0 = ln.Substring(0, c0).Trim().Trim('"');
                        if (!k0.Equals("version", StringComparison.OrdinalIgnoreCase)
                            && !k0.Equals("refs", StringComparison.OrdinalIgnoreCase)
                            && !k0.Equals("records", StringComparison.OrdinalIgnoreCase)
                            && srcRefs.ContainsKey(k0))
                        {
                            changed = true;
                            continue;
                        }
                    }
                }
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
                                changed = true;
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
                            var newLine = $"{indent}{key}: \"{val}\"";
                            if (!string.Equals(newLine, ln, StringComparison.Ordinal)) changed = true;
                            newLines.Add(newLine);
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
                changed = true;
            }
            if (changed)
            {
                File.WriteAllLines(destCred, newLines);
                Chmod600IfUnix(destCred);
            }
            return changed;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[instance] shared credential seed failed: {ex.Message}");
            return false;
        }
    }

    // ---- Shared API key propagation ----
    // Members inherit the admin's API key at instance start. If the admin changes
    // the key while instances are already running, push it out automatically: watch
    // the admin template .credentials.yaml, refresh every instance's refs, and restart
    // any running instance whose key actually changed so DSH reloads it.
    private System.IO.FileSystemWatcher? _credWatcher;
    private string? _lastSharedCredSig;

    public void StartSharedCredentialWatcher()
    {
        try
        {
            SyncSharedCredentialsToAll();   // one-time catch-up for stale instances
            var srcCred = Path.Combine(_dshTemplate, ".credentials.yaml");
            var dir = Path.GetDirectoryName(srcCred);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _credWatcher = new System.IO.FileSystemWatcher(dir, ".credentials.yaml")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size | System.IO.NotifyFilters.FileName,
            };
            System.IO.FileSystemEventHandler handler = (s, e) => { try { SyncSharedCredentialsToAll(); } catch { } };
            _credWatcher.Changed += handler;
            _credWatcher.Created += handler;
            _credWatcher.Renamed += (s, e) => { try { SyncSharedCredentialsToAll(); } catch { } };
            _credWatcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    private string SharedCredSignature()
    {
        try
        {
            var srcCred = Path.Combine(_dshTemplate, ".credentials.yaml");
            if (!File.Exists(srcCred)) return "";
            var refs = ParseRefs(File.ReadAllLines(srcCred));
            if (refs.Count == 0) return "";
            return string.Join(";", refs.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value));
        }
        catch { return ""; }
    }

    public void SyncSharedCredentialsToAll()
    {
        try
        {
            var sig = SharedCredSignature();
            if (sig.Length == 0 || sig == _lastSharedCredSig) return;
            _lastSharedCredSig = sig;
            foreach (var inst in _instances.ToList())
            {
                bool changed = false;
                try { changed = EnsureSharedCredentials(inst.DshHome); } catch { }
                if (changed && inst.Running)
                {
                    var cap = inst;
                    _ = Task.Run(() => { try { Stop(cap); Start(cap); } catch { } });
                }
            }
        }
        catch { }
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
        try { StartCore(inst); }
        catch (Exception ex)
        {
            inst.Running = false;
            try { LogWorkspace(_root, $"[start] {inst.Id}: FAILED: {ex.Message}"); } catch { }
            try { inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [start] FAILED: {ex.Message}"); } catch { }
            throw;
        }
    }

    private void StartCore(Instance inst)
    {
        lock (_gate)
        {
            inst.Stopping = false;     // a fresh start is never a deliberate stop
            var sw = System.Diagnostics.Stopwatch.StartNew();
            inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [start] {inst.Id} port={inst.DshPort} workspace={inst.Workspace}");
            Trace.WriteLine($"[start:{inst.Id}] beginning start sequence");

            // Sync the shared API key from the admin template BEFORE the early-return
            // below, so even an instance that is already running (e.g. auto-restored
            // on launcher start) gets the real DeepSeek key the admin configured.
            try { EnsureSharedCredentials(inst.DshHome); } catch { }

            // Keep this member's DSH language in sync with the launcher default,
            // even for an already-running instance (takes effect on its next start).
            ApplyLocale(inst);

            // Seed this instance's OWN workspace.json into its OWN DSH store, and do
            // it BEFORE the "already running" early return below. A DSH started by any
            // other path - boot auto-restore, an earlier visit, the console's own
            // auto-start - used to skip this entirely and then sat forever on the
            // non-selectable "Choose workspace" hero screen ("Into the Unknown"),
            // because the seeded record is what the client auto-opens.
            // Idempotent and cheap: it returns immediately once the record exists.
            try
            {
                if (!string.IsNullOrWhiteSpace(inst.Workspace))
                    EnsureAdminWorkspace(_root, inst.DshHome, inst.Workspace, inst.Name ?? inst.Id);
            }
            catch { /* best effort */ }

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
            // Same trusted origins the admin's DSH gets (DshService adds these). The DSH
            // validates the Host/Origin of its own API + WebSocket calls, so a member
            // instance without an entry for the address the browser/proxy actually uses
            // answers with an EMPTY workspace list and its pane sits on the
            // "Choose workspace" hero - which is exactly why only members were affected
            // while the admin's own DSH (which got these flags) was fine.
            foreach (var th in TrustedHostsFor(_root, inst.DshPort))
            {
                psi.ArgumentList.Add("--trusted-host");
                psi.ArgumentList.Add(th);
            }
            psi.Environment["DSH_HOME"] = inst.DshHome;

            // Set when this attempt actually used the OS-user (isolated) launcher, so
            // the early-exit watchdog below knows a direct relaunch is the remedy.
            var launchedIsolated = false;

            if (!string.IsNullOrEmpty(inst.OsPassword) && !inst.IsolationUnavailable)
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
                            // (Re)apply the ACL hardening, so members created before this
                            // change get it on their next start.
                            if (OperatingSystem.IsWindows())
                                LogWorkspace(_root, $"[isolation] {inst.Id}: acl -> {OsUserManager.HardenMemberAcl(inst.Id, inst.Workspace, inst.DshHome)}");
                            _permissionedOsUsers.TryAdd(inst.Id, true);
                            osUserReady = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWorkspace(_root, $"[isolation] {inst.Id}: OS account setup failed on start ({sw.ElapsedMilliseconds}ms) - running WITHOUT isolation: {ex.Message}");
                        try { inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [isolation] setup failed - running WITHOUT isolation: {ex.Message}"); } catch { }
                    }
                    Trace.WriteLine($"[start:{inst.Id}] OS user setup done in {sw.ElapsedMilliseconds}ms");
                }

                if (osUserReady)
                {
                    var env = new Dictionary<string, string> { ["DSH_HOME"] = inst.DshHome };
                    var osArgs = new List<string> { "--expose-internals", bin, "--profile", "web", "--port", inst.DshPort.ToString(), "--no-open" };
                    foreach (var th in TrustedHostsFor(_root, inst.DshPort))
                    {
                        osArgs.Add("--trusted-host");
                        osArgs.Add(th);
                    }
                    // The DSH's working directory must exist before the process is created: a
                    // missing .dsh made Process.Start fail with "directory name invalid" and the
                    // instance never came up. Idempotent, and it runs before either launch path.
                    try { System.IO.Directory.CreateDirectory(inst.DshHome); } catch { }
                    proc = OsUserManager.StartAsUser(inst.Id, node,
                        osArgs.ToArray(),
                        inst.DshHome, env, inst.OsPassword);
                    launchedIsolated = proc != null;
                }
                // If OS user creation or StartAsUser failed, fall back to launching
                // DSH directly (no OS-level isolation) so the instance can still start.
                if (proc == null)
                {
                    LogWorkspace(_root, $"[isolation] {inst.Id}: OS account unavailable - started WITHOUT isolation");
                    try { inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [isolation] unavailable - started WITHOUT isolation"); } catch { }
                }
            }

            if (proc == null)
            {
                proc = new Process { StartInfo = psi };
            }

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) { inst.Logs.Enqueue(e.Data); CaptureToken(inst, e.Data); } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) inst.Logs.Enqueue("[ERR] " + e.Data); };
            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                // The OS-user launch can fail at Start() for account-specific reasons
                // (e.g. the stored password no longer matches the local account, or a
                // broken profile). Historically that exception escaped the background
                // start task and only reached the in-memory ring buffer, so launcher.log
                // had no trace and the member sat on "DSH is starting up" forever.
                // Fall back to a direct (non-isolated) launch so the instance comes up.
                LogWorkspace(_root, $"[start] {inst.Id}: process start failed (isolated={launchedIsolated}): {ex.Message}");
                try { inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [start] process start failed (isolated={launchedIsolated}): {ex.Message}"); } catch { }
                if (!launchedIsolated) throw;
                inst.IsolationUnavailable = true;
                try { System.IO.Directory.CreateDirectory(inst.DshHome); } catch { }
                proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) { inst.Logs.Enqueue(e.Data); CaptureToken(inst, e.Data); } };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) inst.Logs.Enqueue("[ERR] " + e.Data); };
                proc.Start();
                launchedIsolated = false;
            }
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
                try { await Task.Delay(6000); } catch { }
                // Two failure shapes must be diagnosed: the process DIED, and the far more
                // common "alive but never started listening" (it hangs in startup, so the
                // watchdog only sees "port not listening" with no clue why).
                var alive = proc is { HasExited: false };
                var listening = alive && DshService.IsDshReady(inst.DshPort);
                if (alive && listening) return;
                try
                {
                    var err = string.Join("\n", inst.Logs.Where(l => l.StartsWith("[ERR]")));
                    var text = $"[{DateTime.Now:HH:mm:ss}] dsh {(alive ? "ALIVE but not listening" : "exited code=" + proc!.ExitCode)} isolated={launchedIsolated}\n{err}\n---tail---\n{string.Join("\n", inst.Logs.TakeLast(60))}";
                    File.WriteAllText(diagPath, text);
                    // Keep a per-instance copy too (survives the next launch, easy to read).
                    try
                    {
                        var ldir = Path.Combine(inst.DshHome, "logs");
                        Directory.CreateDirectory(ldir);
                        File.AppendAllText(Path.Combine(ldir, "dsh.log"), text + Environment.NewLine);
                    }
                    catch { }
                }
                catch { }

                // The OS-user launch can be impossible in some hosts - most notably when
                // the launcher itself runs as a session-0 service/SYSTEM task: a
                // CreateProcessWithLogonW child of a *different* user then has no usable
                // window station and node dies during DLL init with 0xC0000142
                // (STATUS_DLL_INIT_FAILED, exit code -1073741502) before it can emit any
                // stderr. Isolation is a hardening measure, not a requirement, so rather
                // than leaving the member with a permanent "starting up" spinner we
                // relaunch once WITHOUT isolation (same account as the launcher) and
                // remember that decision for the lifetime of this launcher process.
                if (!launchedIsolated) return;
                inst.IsolationUnavailable = true;
                // "Alive but never listening" is treated the same as dying: isolation is a
                // hardening measure, not a requirement, so relaunch once as the launcher's
                // own account rather than leaving the member stuck on a spinner forever.
                try { if (alive && !proc!.HasExited) proc.Kill(); } catch { }
                inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [start] OS-user launch {(alive ? "never listened" : "exited " + proc!.ExitCode)}; relaunching without isolation");
                Trace.WriteLine($"[start:{inst.Id}] isolated launch unusable ({(alive ? "no listener" : "exit " + proc!.ExitCode)}); falling back to direct launch");
                inst.Starting = true;
                try { Start(inst); }
                catch (Exception ex) { inst.Logs.Enqueue("[start] " + ex.Message); }
                finally { inst.Starting = false; }
            });

            inst.Proc = proc;
            inst.Running = true;
            Save();

        }
    }

    // ---- watchdog (poll based) ---------------------------------------------
    // Member DSHs are launched as their own OS user, where process-exit events are not
    // dependable, so instead of waiting for an event we simply verify that every
    // instance we believe is running is still listening on its port. If it is not, the
    // instance is restarted with a growing backoff (5s, 30s, 2min) and after three
    // consecutive failures we give up and write the reason into the instance log.
    System.Threading.Timer? _wdTimer;
    readonly Dictionary<string, DateTime> _wdLastRestart = new(StringComparer.OrdinalIgnoreCase);
    // Instances the watchdog has actually seen listening - only those may be restarted.
    readonly HashSet<string> _wdGuarded = new(StringComparer.OrdinalIgnoreCase);
    // Instances whose restart is in flight: the poll must not count them again while
    // the DSH is coming up (its port stays closed for ~15-20s, which would otherwise
    // look like a second, immediate failure).
    readonly HashSet<string> _wdRestarting = new(StringComparer.OrdinalIgnoreCase);

    public void StartWatchdog()
    {
        if (_wdTimer != null) return;
        _wdTimer = new System.Threading.Timer(_ => WatchdogTick(), null, 10000, 10000);
        LogWorkspace(_root, "[watchdog] started (poll every 10s)");
    }

    void WatchdogTick()
    {
        try
        {
            if (_shuttingDown) return;
            List<Instance> list;
            lock (_gate) list = _instances.ToList();
            foreach (var inst in list)
            {
                if (inst.Stopping || inst.Starting) { continue; }
                bool alive;
                try { alive = DshService.IsPortInUse(inst.DshPort); } catch { alive = true; }
                if (alive)
                {
                    // Remember that this instance is meant to be up; only then may we
                    // restart it later. A long healthy period clears the backoff.
                    _wdGuarded.Add(inst.Id);
                    _wdRestarting.Remove(inst.Id);   // it is up again
                    if (_wdLastRestart.TryGetValue(inst.Id, out var lr) && (DateTime.UtcNow - lr).TotalSeconds > 120)
                        _wdTries[inst.Id] = 0;
                    continue;
                }
                if (!_wdGuarded.Contains(inst.Id)) continue;   // stopped / never seen up
                if (_wdRestarting.Contains(inst.Id)) continue;  // a restart is already running
                var tries = _wdTries.TryGetValue(inst.Id, out var t) ? t + 1 : 1;
                _wdTries[inst.Id] = tries;
                _wdLastRestart[inst.Id] = DateTime.UtcNow;
                _wdRestarting.Add(inst.Id);
                var delay = tries <= 1 ? 5 : (tries == 2 ? 30 : 120);
                if (tries > 3)
                {
                    _wdGuarded.Remove(inst.Id);
                    _wdRestarting.Remove(inst.Id);
                    inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [watchdog] DSH is not listening (port {inst.DshPort}); giving up after 3 restarts - press Start to start it again");
                    LogWorkspace(_root, $"[watchdog] {inst.Id}: giving up after 3 restarts");
                    continue;
                }
                inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [watchdog] DSH is not listening (port {inst.DshPort}); restarting in {delay}s (attempt {tries}/3)");
                LogWorkspace(_root, $"[watchdog] {inst.Id}: port {inst.DshPort} not listening -> restart in {delay}s (attempt {tries}/3)");
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(delay * 1000); } catch { }
                    try
                    {
                        var skip = false;
                        lock (_gate) { if (_shuttingDown || inst.Stopping) skip = true; }
                        if (!skip && !DshService.IsPortInUse(inst.DshPort)) Start(inst);
                    }
                    catch (Exception ex)
                    {
                        try { inst.Logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] [watchdog] restart failed: {ex.Message}"); } catch { }
                        try { LogWorkspace(_root, $"[watchdog] {inst.Id}: restart failed: {ex.Message}"); } catch { }
                    }
                    finally { _wdRestarting.Remove(inst.Id); }
                });
            }
        }
        catch { }
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
            inst.Stopping = true;      // watchdog: this exit is intentional
            _wdGuarded.Remove(inst.Id);
            _wdRestarting.Remove(inst.Id);
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
            // Self-heal the instance's storages ACL first: this edition does not run the
            // isolation pass, and the DSH's own files there can be unreadable even for
            // administrators - the launcher then cannot read/seed anything and the member
            // reports "cannot open the DSH". Idempotent; only repairs when a read is denied.
            try
            {
                OsUserManager.RepairInstanceStorages(inst.DshHome, null);
            }
            catch { }
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
                                {
                                    // Repair the GLOBAL id list before keeping the record.
                                    // This edition writes the same storages/workspace.json
                                    // shape (see the template below), and a record whose
                                    // global.workspaceIds is empty/mismatched left the member
                                    // on DSH's "Choose a workspace" screen even though the
                                    // table entry was fine. Keep the table + sessions.
                                    try
                                    {
                                        var doc2 = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(wsFile));
                                        if (doc2?["global"] is System.Text.Json.Nodes.JsonObject g)
                                        {
                                            var arr = g["workspaceIds"] as System.Text.Json.Nodes.JsonArray;
                                            if (arr == null || arr.Count == 0 ||
                                                !string.Equals(arr[0]?.GetValue<string>() ?? "", p.Name, StringComparison.Ordinal))
                                            {
                                                g["workspaceIds"] = new System.Text.Json.Nodes.JsonArray(p.Name);
                                                File.WriteAllText(wsFile, doc2.ToJsonString());
                                            }
                                        }
                                    }
                                    catch { }
                                    return; // keep the record + its sessions
                                }
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
            // Resolve the bundled node for THIS platform: node/node.exe on Windows,
            // node/<platform>/bin/node on Linux/macOS. The old hard-coded "node.exe"
            // made this whole step a silent no-op off Windows (and on Windows whenever
            // node/ had not been copied), leaving the workspace unopenable.
            var node = NodeExeFor(root);
            if (string.IsNullOrEmpty(node) || (node != "node" && !File.Exists(node)))
            {
                LogWorkspace(root, "[seed] node runtime not found; skipping blank-session seed");
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
    public static bool EnsureAdminWorkspace(string root, string dshHome, string workspacePath, string title)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                LogWorkspace(root, "[seed] skipped: no workspace configured for this instance");
                return false;
            }
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
            // The record alone is NOT enough: if the blank session it points at is gone
            // (e.g. it was seeded while the bundled node runtime was missing - that step
            // used to be skipped silently), DSH still shows the "Choose workspace" hero.
            // The session tree is owned by the member's OS account, so the launcher may
            // not be able to read it at all. Directory.EnumerateFiles(..., AllDirectories)
            // aborts on the first unreadable subdir and a bare `catch {}` then reported
            // "no session" for a perfectly healthy instance - re-seeding AND restarting
            // it on every visit ("DSH takes ages to open"). null (unreadable) must be
            // treated as "assume valid", only a readable-but-empty store is stale.
            var hasRealSession = HasSessionFile(Path.Combine(dshHome, "sessions")) != false;
            if (hasTarget && hasRealSession)
            {
                LogWorkspace(root, $"ok   {dshHome} -> {nativePath} (record already valid)");
                return false;
            }
            if (hasTarget)
                LogWorkspace(root, $"stale {dshHome}: record present but no session on disk -> re-seeding");

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
            Trace.WriteLine($"[workspace] seeded workspace -> {nativePath}");
            LogWorkspace(root, $"NEW  {dshHome} -> {nativePath} (workspace record seeded)");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[workspace] workspace ensure failed: {ex.Message}");
            LogWorkspace(root, $"FAIL {dshHome}: {ex.Message}");
            return false;
        }
    }

    // ACL-tolerant probe for a real session file under a DSH "sessions" tree.
    // Returns true  -> at least one session file found;
    //         false -> the tree was readable (at least partly) but held none;
    //         null  -> nothing under it could be read at all (member-owned ACL),
    //                  i.e. "unknown" and the caller must not assume "stale".
    // A hand-rolled walk is used because Directory.EnumerateFiles(AllDirectories)
    // throws as soon as ONE subdirectory denies access, which would hide every
    // session and make the caller re-seed + restart the instance on every visit.
    private static bool? HasSessionFile(string sessionsDir)
    {
        try { if (!Directory.Exists(sessionsDir)) return false; }
        catch { return null; }
        var anyReadable = false;
        var sawDenied = false;
        var stack = new Stack<string>();
        stack.Push(sessionsDir);
        var guard = 0;
        while (stack.Count > 0 && guard++ < 50000)
        {
            var dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); anyReadable = true; }
            catch { sawDenied = true; files = Array.Empty<string>(); }
            foreach (var f in files)
                if (Path.GetFileName(f).StartsWith("session.jsonl", StringComparison.OrdinalIgnoreCase))
                    return true;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); anyReadable = true; }
            catch { sawDenied = true; subs = Array.Empty<string>(); }
            foreach (var s in subs) stack.Push(s);
        }
        // Any unreadable part means the view is incomplete: report "unknown" so the
        // caller never mistakes a permission problem for a missing session.
        if (sawDenied) return null;
        return anyReadable ? false : (bool?)null;
    }

    // Repair attempts are rate-limited per instance so a DSH that keeps rewriting
    // (or dropping) its own workspace record can never cause a repair/restart loop.
    private static readonly ConcurrentDictionary<string, DateTime> _wsSeedAt =
        new(StringComparer.OrdinalIgnoreCase);

    // Called on the DSH proxy path for a member. The folder existing is NOT enough:
    // DSH resolves the workspace from its own storages/workspace.json RECORD, and a
    // missing/invalid record leaves it on the non-selectable "Choose workspace" hero
    // ("Into the Unknown") no matter how many times the user retries. This repairs
    // the record, and - only when it actually had to write - restarts that instance
    // once so the already-running DSH picks it up.
    public bool EnsureInstanceWorkspaceSeeded(Instance inst)
    {
        try
        {
            if (inst == null || string.IsNullOrWhiteSpace(inst.Workspace)) return false;
            var now = DateTime.UtcNow;
            if (_wsSeedAt.TryGetValue(inst.Id, out var last) &&
                (now - last) < TimeSpan.FromMinutes(5)) return false;
            _wsSeedAt[inst.Id] = now;

            var wrote = EnsureAdminWorkspace(_root, inst.DshHome, inst.Workspace, inst.Name ?? inst.Id);
            if (!wrote) return false;

            LogWorkspace(_root, $"[proxy-seed] {inst.Id}: workspace record was missing -> seeded");
            if (inst.Proc is { HasExited: false })
            {
                LogWorkspace(_root, $"[proxy-seed] {inst.Id}: DSH running -> restarting once to load it");
                Task.Run(() =>
                {
                    try { Stop(inst); Thread.Sleep(500); Start(inst); }
                    catch (Exception ex) { LogWorkspace(_root, $"[proxy-seed] {inst.Id}: restart failed: {ex.Message}"); }
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            LogWorkspace(_root, $"[proxy-seed] {inst.Id}: failed: {ex.Message}");
            return false;
        }
    }

    // Append one line to <root>\logs\launcher.log (best effort, size-capped) so the
    // workspace self-heal can be diagnosed later from the filesystem alone - the
    // in-memory log and Trace output are both invisible in a headless install.
    public static void LogWorkspace(string root, string msg)
    {
        try
        {
            var file = Path.Combine(root, "logs", "launcher.log");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var fi = new FileInfo(file);
            if (fi.Exists && fi.Length > 4 * 1024 * 1024) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { /* best effort */ }
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

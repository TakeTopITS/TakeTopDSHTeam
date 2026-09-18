// TakeTopDshTeam — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This software is the intellectual property of 泰顶拓鼎信息科技（上海）有限公司
// (TaiDingTuoDing Information Technology (Shanghai) Co., Ltd.). All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TakeTopDshLauncher;

/// <summary>
/// Cross-platform OS user management for dsh instance isolation.
/// Creates dedicated OS users per instance, sets directory permissions,
/// and launches dsh processes under those users.
/// </summary>
public static class OsUserManager
{
    // Windows: "dsh-{username}" (max 20 chars for samAccountName)
    // Linux/macOS: "dsh_{username}" (max 32; Linux usernames cap ~32 and macOS
    // short names are even shorter — truncating keeps useradd/dscl happy).
    private static string OsUser(string id) => OperatingSystem.IsWindows()
        ? $"dsh-{id}"[..Math.Min(id.Length + 4, 20)]
        : $"dsh_{id}"[..Math.Min(id.Length + 4, 32)];

    // ──────────────────────── CREATE ────────────────────────

    /// <summary>
    /// Create a dedicated OS user for the given instance id.
    /// Returns the OS username created, or null on failure.
    /// </summary>
    public static string? CreateUser(string id, string password)
    {
        var user = OsUser(id);
        try
        {
            if (OperatingSystem.IsWindows())
                return CreateWindowsUser(user, password);
            else if (OperatingSystem.IsLinux())
                return CreateLinuxUser(user, password);
            else if (OperatingSystem.IsMacOS())
                return CreateMacUser(user, password);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[osuser] create {user} failed: {ex.Message}");
            // Re-throw a clear message so the caller can surface WHY it failed —
            // most commonly the launcher is NOT running with Administrator rights,
            // which `net user`/`icacls` (Windows) or `useradd`/`dscl` (Unix) require.
            throw new InvalidOperationException(
                $"cannot create OS user '{user}' for instance isolation: {ex.Message}. " +
                "Ensure the launcher is running with Administrator privileges (start.bat / sudo).", ex);
        }
        return null;
    }

    // ──────────────────────── DELETE ────────────────────────

    public static bool DeleteUser(string id)
    {
        var user = OsUser(id);
        try
        {
            if (OperatingSystem.IsWindows())
                return DeleteWindowsUser(user);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return DeleteUnixUser(user);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[osuser] delete {user} failed: {ex.Message}");
        }
        return false;
    }

    // ──────────────────────── PERMISSIONS ────────────────────

    /// <summary>
    /// Set up filesystem permissions so the OS user can only access:
    /// 1. The workspace directory (read/write)
    /// 2. The docs directory (read-only)
    /// 3. The instance .dsh directory (read/write)
    /// 4. The dsh installation (read-only, for running the app)
    /// </summary>
    public static bool SetPermissions(string id, string workspace, string docsDir, string dshHome, string root)
    {
        var user = OsUser(id);
        try
        {
            if (OperatingSystem.IsWindows())
                return SetWindowsPermissions(user, workspace, docsDir, dshHome, root);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return SetUnixPermissions(user, workspace, docsDir, dshHome, root);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[osuser] permissions {user} failed: {ex.Message}");
        }
        return false;
    }

    // ──────────────────────── START AS USER ─────────────────

    /// <summary>
    /// Launch a process under the dedicated OS user.
    /// </summary>
    public static Process? StartAsUser(string id, string exe, string[] args, string workingDir, Dictionary<string, string>? env = null, string? password = null)
    {
        var user = OsUser(id);
        try
        {
            if (OperatingSystem.IsWindows())
                return StartWindowsAsUser(user, exe, args, workingDir, env, password);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return StartUnixAsUser(user, exe, args, workingDir, env);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[osuser] start-as-user {user} failed: {ex.Message}");
        }
        return null;
    }

    // ──────────────────── WINDOWS IMPLEMENTATION ──────────────
    //
    // Account changes go through netapi32, NOT net.exe. `net user` refuses a password
    // longer than 14 characters on the command line and quietly switches to an
    // interactive prompt - with no console attached that prompt never returns, so the
    // call hangs until the caller's 30s timeout kills it and the account is silently
    // never created. NetUserAdd/NetUserSetInfo have no length limit, never prompt, and
    // are ~40ms.

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_comment;
        public uint usri1_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1003
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOCALGROUP_MEMBERS_INFO_3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? lgrmi3_domainandname;
    }

    private const uint USER_PRIV_USER = 1;          // ordinary user (not admin)
    private const uint UF_SCRIPT = 0x0001;
    private const uint UF_DONT_EXPIRE_PASSWD = 0x00010000;
    private const int NERR_Success = 0;
    private const int NERR_UserNotFound = 2221;
    private const int NERR_UserExists = 2224;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(string? servername, uint level, IntPtr buf, out uint parmErr);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? servername, string username, uint level, IntPtr buf, out uint parmErr);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(string? servername, string username);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? servername, string username, uint level, out IntPtr bufptr);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? servername, string groupname, uint level, IntPtr buf, uint totalentries);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private static IntPtr AllocStruct<T>(T value) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, p, false);
        return p;
    }

    /// <summary>
    /// Cut a member's PRIVATE trees off from the directories' inherited "everybody may
    /// read" ACEs and re-grant them to just that member plus the service. Without this the
    /// account-level isolation is only cosmetic: the workspaces inherit
    /// BUILTIN\Users:(RX) / Authenticated Users:(M) from the drive root, so every member
    /// can read - and write - every other member's files (verified 2026-09-18). Shared
    /// trees are deliberately left alone: members are meant to read those.
    /// </summary>
    [SupportedOSPlatform("windows")]
    /// <summary>
    /// The instance's own DSH writes some files with an ACL that keeps EVERYONE but its own
    /// OS account (plus an internal SID) out - not even SYSTEM/Administrators. The launcher
    /// then cannot read the workspace record or seed a session, the watchdog keeps restarting
    /// the instance and the member reports "cannot open the DSH". Take ownership of whatever
    /// we cannot read and grant the same accounts the workspace hardening uses.
    /// Scope: &lt;dshHome&gt;\storages only - that is what the launcher needs.
    /// </summary>
    public static string RepairInstanceStorages(string dshHome, string? memberUser)
    {
        if (!OperatingSystem.IsWindows()) return "n/a";
        if (string.IsNullOrWhiteSpace(dshHome)) return "nothing-to-do";
        var storages = Path.Combine(dshHome, "storages");
        if (!Directory.Exists(storages)) return "nothing-to-do";
        try
        {
            foreach (var f in Directory.EnumerateFiles(storages, "*", SearchOption.AllDirectories).Take(300))
                using (var s = File.OpenRead(f)) { }
            // The DSH re-creates profiles\node_modules on every boot; an unwritable one made
            // it die with "EPERM: mkdir ...\@deepseek-ai" and the instance never listened.
            var nm = Path.Combine(dshHome, "profiles", "node_modules");
            if (Directory.Exists(nm))
            {
                var probe = Path.Combine(nm, ".tt-access-probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            // The member-owned "sessions" tree must be listable by the launcher: the
            // workspace self-heal decides "healthy record vs stale" by looking for a
            // session file, and an unreadable tree made it re-seed + restart the
            // instance on every visit. Probe it here so a broken ACL triggers the
            // repair below.
            var sessions = Path.Combine(dshHome, "sessions");
            if (Directory.Exists(sessions))
            {
                var probe = Path.Combine(sessions, ".tt-access-probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            return "ok";
        }
        catch { }
        var names = new List<string> { "SYSTEM", "Administrators" };
        try
        {
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name ?? "";
            if (me.Length > 0 && !me.ToLowerInvariant().Contains("\\dsh-")) names.Add(me);
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(memberUser)) names.Add(memberUser);
        var inher = string.Join(" ", names.Select(n => "\"" + n + ":(OI)(CI)F\""));
        var flat = string.Join(" ", names.Select(n => "\"" + n + ":F\""));
        RunCmd("takeown", $"/f \"{storages}\" /r /d y", throwOnError: false);
        RunCmd("icacls", $"\"{storages}\" /grant {inher} /T /C /Q", throwOnError: false);
        RunCmd("icacls", $"\"{storages}\" /grant {flat} /T /C /Q", throwOnError: false);
        // The session tree is owned by the member account and can deny the launcher /
        // Administrators, which made the workspace self-heal re-seed + restart the
        // instance on every visit. Heal it too. Deep is safe here: unlike
        // profiles\node_modules there are no junctions inside "sessions".
        var sessionsDir = Path.Combine(dshHome, "sessions");
        if (Directory.Exists(sessionsDir))
        {
            RunCmd("takeown", $"/f \"{sessionsDir}\" /r /d y", throwOnError: false);
            RunCmd("icacls", $"\"{sessionsDir}\" /grant {inher} /T /C /Q", throwOnError: false);
            RunCmd("icacls", $"\"{sessionsDir}\" /grant {flat} /T /C /Q", throwOnError: false);
        }
        // ...then the directories the DSH itself writes into, non-recursively: the entries
        // of profiles\node_modules are junctions into the shared install and a /T walk
        // would follow them.
        foreach (var dir in new[] { dshHome, Path.Combine(dshHome, "profiles"), Path.Combine(dshHome, "profiles", "node_modules") })
        {
            if (!Directory.Exists(dir)) continue;
            RunCmd("takeown", $"/f \"{dir}\"", throwOnError: false);
            RunCmd("icacls", $"\"{dir}\" /grant {inher}", throwOnError: false);
            RunCmd("icacls", $"\"{dir}\" /grant {flat}", throwOnError: false);
        }
        return "dsh=repaired";
    }

    public static string HardenMemberAcl(string id, string workspace, string dshHome)
    {
        var user = OsUser(id);
        var parts = new List<string>();
        var names = new List<string> { "SYSTEM", "Administrators", user };
        try
        {
            // The account running the launcher must be able to read member files - that is
            // what serves preview / download. When the launcher is NOT elevated the token is
            // UAC-filtered, i.e. NOT a member of "Administrators", so the group ACE alone
            // leaves every workspace unreadable (HTTP 500). Member accounts (dsh-*) are
            // deliberately never added: members stay isolated.
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().Name ?? "";
            if (me.Length > 0 && !me.ToLowerInvariant().Contains("\\dsh-")
                && !names.Any(n => n.Equals(me, StringComparison.OrdinalIgnoreCase)))
                names.Add(me);
        }
        catch { }
        var grant = string.Join(" ", names.Select(n => "\"" + n + ":(OI)(CI)F\""));
        // The SAME accounts without the inheritance flags, granted recursively. (OI)(CI)
        // only describe inheritance for a CONTAINER, so a recursive /grant that carries
        // them can leave a FILE with an empty DACL - and an empty DACL denies everyone,
        // including Administrators, which made such a file impossible to preview or
        // download (the API answered HTTP 500). This flat pass gives every file and
        // folder an explicit ACE and REPAIRS the files left unreadable by older builds.
        var grantFlat = string.Join(" ", names.Select(n => "\"" + n + ":F\""));
        try
        {
            if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
            {
                // /T so the files already inside lose the inherited broad ACEs too.
                RunCmd("icacls", $"\"{workspace}\" /inheritance:r /T /C /Q", throwOnError: false);
                RunCmd("icacls", $"\"{workspace}\" /grant {grant} /T /C /Q", throwOnError: false);
                RunCmd("icacls", $"\"{workspace}\" /grant {grantFlat} /T /C /Q", throwOnError: false);
                parts.Add("workspace=hardened");
            }
            if (!string.IsNullOrWhiteSpace(dshHome) && Directory.Exists(dshHome))
            {
                // Self-heal first: the DSH's own narrow ACLs (see RepairInstanceStorages)
                // made it die with "EPERM: mkdir ...\profiles\node_modules\@deepseek-ai"
                // and the instance never listened - the "cannot open the DSH" reports.
                // This runs on every start via the isolation pass, unlike EnsureWorkspace
                // which is only reached when the profile patch is written.
                var healed = RepairInstanceStorages(dshHome, user);
                if (healed != "ok" && healed != "nothing-to-do" && healed != "n/a") parts.Add(healed);
                // No /T: .dsh\profiles\node_modules holds junctions into the shared tree
                // and /T would walk tens of thousands of files; children inherit from here.
                RunCmd("icacls", $"\"{dshHome}\" /inheritance:r /Q", throwOnError: false);
                RunCmd("icacls", $"\"{dshHome}\" /grant {grant} /Q", throwOnError: false);
                parts.Add(".dsh=hardened");
                var instDir = Path.GetDirectoryName(dshHome);
                if (!string.IsNullOrWhiteSpace(instDir) && Directory.Exists(instDir))
                {
                    RunCmd("icacls", $"\"{instDir}\" /inheritance:r /Q", throwOnError: false);
                    RunCmd("icacls", $"\"{instDir}\" /grant {grant} /Q", throwOnError: false);
                    parts.Add("instanceDir=hardened");
                }
            }
        }
        catch (Exception ex) { parts.Add("EX " + ex.Message); }
        return parts.Count == 0 ? "nothing-to-do" : string.Join(" ", parts);
    }

    [SupportedOSPlatform("windows")]
    private static int SetWindowsPassword(string user, string password)
    {
        var p = AllocStruct(new USER_INFO_1003 { usri1003_password = password });
        try { return NetUserSetInfo(null, user, 1003, p, out _); }
        finally { Marshal.FreeHGlobal(p); }
    }

    [SupportedOSPlatform("windows")]
    private static string CreateWindowsUser(string user, string password)
    {
        // If the account already exists, DO NOT delete it: deleting destroys its
        // profile (and the whole point of the user is to carry a stable profile).
        // Just make sure the password and group membership are correct.
        var info = new USER_INFO_1
        {
            usri1_name = user,
            usri1_password = password,
            usri1_priv = USER_PRIV_USER,
            usri1_home_dir = null,
            usri1_comment = "TakeTopDSH Team restricted instance user",
            usri1_flags = UF_SCRIPT | UF_DONT_EXPIRE_PASSWD,
            usri1_script_path = null,
        };
        var p = AllocStruct(info);
        int rc;
        try { rc = NetUserAdd(null, 1, p, out _); }
        finally { Marshal.FreeHGlobal(p); }

        if (rc == NERR_UserExists) rc = SetWindowsPassword(user, password);
        if (rc != NERR_Success) throw new Exception($"NetUserAdd('{user}') failed: {rc}");

        // Add to the local "Users" group (never Administrators). Already-a-member is
        // not an error, so the result is deliberately ignored.
        var pm = AllocStruct(new LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = user });
        try { NetLocalGroupAddMembers(null, "Users", 3, pm, 1); }
        finally { Marshal.FreeHGlobal(pm); }

        return user;
    }

    [SupportedOSPlatform("windows")]
    private static bool DeleteWindowsUser(string user)
    {
        var rc = NetUserDel(null, user);
        return rc == NERR_Success || rc == NERR_UserNotFound;
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsUserExists(string user)
    {
        var rc = NetUserGetInfo(null, user, 0, out var buf);
        if (buf != IntPtr.Zero) NetApiBufferFree(buf);
        return rc == NERR_Success;
    }

    [SupportedOSPlatform("windows")]
    private static bool SetWindowsPermissions(string user, string workspace, string docsDir, string dshHome, string root)
    {
        // 1. Grant full control on workspace
        RunCmd("icacls", $"\"{workspace}\" /grant {user}:(OI)(CI)F /T /Q", throwOnError: false);

        // 2. Grant read-only on docs
        RunCmd("icacls", $"\"{docsDir}\" /grant {user}:(OI)(CI)R /T /Q", throwOnError: false);

        // 3. Grant full control on instance .dsh directory. Do NOT use /T here:
        //    .dsh/profiles/node_modules holds junctions into the shared node_modules
        //    tree, and icacls /T follows them, traversing tens of thousands of files
        //    on every start (the "60s+ 启动阻塞"). Grant the dir itself and let
        //    children inherit; junction targets take their ACL from node/ and root
        //    which are granted read access (steps 4-5).
        RunCmd("icacls", $"\"{dshHome}\" /grant {user}:(OI)(CI)F /Q", throwOnError: false);

        // 4. Grant read-only on dsh installation (node/ + gui-cs/src/wwwroot/).
        //    These live under `root` which is granted (OI)(CI)R in step 5 below, so
        //    child files inherit read access. Granting the top-level dir with
        //    /T would recursively traverse the enormous node_modules tree on every
        //    instance start (seen as a 60s+ "启动阻塞"), so we only set the ACE on
        //    the directory itself and rely on inheritance.
        var nodeDir = Path.Combine(root, "node");
        var wwwroot = Path.Combine(root, "gui-cs", "src", "wwwroot");
        if (Directory.Exists(nodeDir))
            RunCmd("icacls", $"\"{nodeDir}\" /grant {user}:(OI)(CI)R /Q", throwOnError: false);
        if (Directory.Exists(wwwroot))
            RunCmd("icacls", $"\"{wwwroot}\" /grant {user}:(OI)(CI)R /Q", throwOnError: false);

        // 5. Grant read on the root directory itself (top-level inheritance only,
        //    NOT /T recursion — the top-level root contains the huge node_modules
        //    tree; /T there is extremely slow. Child objects inherit (OI)(CI)R).
        RunCmd("icacls", $"\"{root}\" /grant {user}:(OI)(CI)R /Q", throwOnError: false);

        // 6. Specifically deny write on docs (belt and suspenders)
        RunCmd("icacls", $"\"{docsDir}\" /deny {user}:(OI)(CI)WD /T /Q", throwOnError: false);

        // 7. Do NOT hand-create "C:\Users\<user>" any more. Doing that (with a made-up
        //    AppData\Local\Temp and no registry hive) made Windows treat the name as
        //    taken and put the REAL profile in "C:\Users\<user>.<MACHINE>", so the
        //    profile paths we handed the child never matched where the profile really
        //    lived. The real profile is created once, properly, by EnsureWindowsProfile
        //    (a single logon-with-profile) when the member is created.

        return true;
    }

    // Resolve the REAL profile directory Windows assigned to this OS user - either
    // "C:\Users\<user>" or, when a plain directory of that name already exists,
    // "C:\Users\<user>.<MACHINE>". Read from the registry so we never have to guess.
    [SupportedOSPlatform("windows")]
    public static string? WindowsProfilePath(string user)
    {
        try
        {
            var r = RunCmd("reg", "query \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\" /s /v ProfileImagePath", throwOnError: false);
            if (r.ExitCode != 0) return null;
            string? fallback = null;
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.Trim();
                var i = line.IndexOf("ProfileImagePath", StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                var rest = line.Substring(i + "ProfileImagePath".Length).Trim();
                var sp = rest.IndexOfAny(new[] { ' ', '\t' });
                if (sp >= 0)
                {
                    rest = rest.Substring(sp).Trim();
                    if (rest.StartsWith("REG_", StringComparison.OrdinalIgnoreCase))
                    {
                        sp = rest.IndexOfAny(new[] { ' ', '\t' });
                        if (sp < 0) continue;
                        rest = rest.Substring(sp).Trim();
                    }
                }
                if (rest.Length == 0) continue;
                var leaf = Path.GetFileName(rest.TrimEnd('\\'));
                if (string.Equals(leaf, user, StringComparison.OrdinalIgnoreCase)) return rest;
                if (leaf.StartsWith(user + ".", StringComparison.OrdinalIgnoreCase)) fallback ??= rest;
            }
            return fallback;
        }
        catch { return null; }
    }

    /// <summary>
    /// Make sure this OS user has a real Windows profile (registry hive + home dir).
    /// If it does not exist yet, one logon-with-profile is enough to create it
    /// (measured at well under a second); after that every start simply reuses it.
    /// Returns the profile path, or null when it could not be created.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? EnsureWindowsProfile(string id, string password)
    {
        var user = OsUser(id);
        var have = WindowsProfilePath(user);
        if (have != null) return have;
        try
        {
            var sys = Environment.SystemDirectory;
            var proc = StartWindowsAsUser(user, Path.Combine(sys, "cmd.exe"),
                new[] { "/c", "exit" }, sys, new Dictionary<string, string>(), password);
            if (proc != null)
            {
                Trace.WriteLine($"[osuser] creating profile for {user}");
                proc.Start();
                proc.WaitForExit(60000);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[osuser] profile create for {user} failed: {ex.Message}");
        }
        return WindowsProfilePath(user);
    }

    [SupportedOSPlatform("windows")]
    private static Process? StartWindowsAsUser(string user, string exe, string[] args, string workingDir, Dictionary<string, string>? env, string? password = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            Trace.WriteLine($"[osuser] no password for {user}, cannot start as user");
            return null;
        }

        Trace.WriteLine($"[osuser] starting as {user}: {exe}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir,
            Domain = ".",
            UserName = user,
        };

        // Build SecureString from password
        var securePwd = new System.Security.SecureString();
        foreach (char c in password) securePwd.AppendChar(c);
        psi.Password = securePwd;

        // Load the target user's profile for the child (CreateProcessWithLogonW
        // LOGON_WITH_PROFILE). Without it the child gets a token but NO profile
        // hive, and node.exe can die during DLL initialization with 0xC0000142
        // (STATUS_DLL_INIT_FAILED, exit code -1073741502) before it can write
        // anything to stderr. The profile is created on first use and then reused.
        psi.LoadUserProfile = true;

        // Point the child at its REAL profile (resolved from the registry) instead of a
        // guessed "C:\Users\<user>". When there is no profile yet - or it cannot be
        // resolved - fall back to the instance's own .dsh dir, which that user already
        // owns, so the child still gets a usable HOME/TEMP. node/dsh need these to
        // bootstrap; without them the process exits immediately.
        var profile = WindowsProfilePath(user);
        var home = profile ?? workingDir;
        var temp = profile != null ? Path.Combine(profile, "AppData", "Local", "Temp")
                                   : Path.Combine(workingDir, "tmp");
        try { Directory.CreateDirectory(temp); } catch { }
        var root = Path.GetPathRoot(home) ?? "C:\\";
        psi.Environment["USERPROFILE"] = home;
        psi.Environment["HOMEDRIVE"] = root.TrimEnd('\\');
        psi.Environment["HOMEPATH"] = home.Length >= root.Length ? home.Substring(root.Length - 1) : "\\";
        psi.Environment["TEMP"] = temp;
        psi.Environment["TMP"] = temp;
        psi.Environment["APPDATA"] = profile != null ? Path.Combine(profile, "AppData", "Roaming") : home;
        psi.Environment["LOCALAPPDATA"] = profile != null ? Path.Combine(profile, "AppData", "Local") : home;
        if (env != null)
        {
            foreach (var kvp in env)
                psi.Environment[kvp.Key] = kvp.Value;
        }

        try
        {
            // Return an UNSTARTED process. The caller wires stdout/stderr handlers
            // and BeginOutputReadLine() BEFORE Start(), so the early "dsh web:" banner
            // (which carries the auth token) is not lost to the pipe. If we started
            // here the token line would be emitted before the caller reads output.
            var proc = new Process { StartInfo = psi };
            Trace.WriteLine($"[osuser] prepared as {user} (not started)");
            return proc;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[osuser] failed to prepare as {user}: {ex.Message}");
            return null;
        }
    }

    // ──────────────────── LINUX/macOS IMPLEMENTATION ──────────

    [UnsupportedOSPlatform("windows")]
    private static string CreateLinuxUser(string user, string password)
    {
        // If the account already exists, DO NOT delete it: `userdel -r` also removes its
        // home directory, and that home IS this user's profile (DSH state, the node module
        // links, the member's own files). Windows keeps its account for exactly the same
        // reason. Only refresh the password (and make sure the shell is usable).
        var exists = RunBash($"id -u {user} 2>/dev/null", throwOnError: false).ExitCode == 0;
        if (!exists)
        {
            var result = RunBash($"useradd -m -s /bin/bash {user}");
            if (result.ExitCode != 0 && !result.Output.Contains("already exists"))
                throw new Exception($"useradd failed: {result.Output}");
        }
        else
        {
            RunBash($"usermod -s /bin/bash {user}", throwOnError: false);
        }
        // Set password via chpasswd, single-quote-escaping both parts so a password
        // containing a quote, space, $, etc. cannot break out of the command.
        RunBash($"echo '{ShellQuote(user)}:{ShellQuote(password)}' | chpasswd");
        return user;
    }

    [UnsupportedOSPlatform("windows")]
    private static string CreateMacUser(string user, string password)
    {
        // Same rule as Linux: keep an existing account (and its home = its profile) instead
        // of deleting and recreating it. Recreating also handed out a NEW random UniqueID on
        // every start, which left the existing files owned by a no-longer-existing uid.
        var exists = RunBash($"id -u {user} 2>/dev/null", throwOnError: false).ExitCode == 0;
        if (!exists)
        {
            var uid = 501 + new Random().Next(1000, 9000);
            RunBash($"sudo dscl . -create /Users/{user}");
            RunBash($"sudo dscl . -create /Users/{user} UserShell /bin/bash");
            RunBash($"sudo dscl . -create /Users/{user} RealName \"{user}\"");
            RunBash($"sudo dscl . -create /Users/{user} UniqueID {uid}");
            RunBash($"sudo dscl . -create /Users/{user} PrimaryGroupID 20");
            RunBash($"sudo dscl . -create /Users/{user} NFSHomeDirectory /Users/{user}");
        }
        else
        {
            RunBash($"sudo dscl . -create /Users/{user} UserShell /bin/bash", throwOnError: false);
        }
        // (Re)create the home only when missing; createhomedir is a no-op if it exists.
        RunBash($"sudo createhomedir -c -u {user}", throwOnError: false);
        // dscl takes the password as a plain positional arg; wrap in double quotes
        // and escape any embedded double quotes / backslashes so spaces don't split.
        RunBash($"sudo dscl . -passwd /Users/{user} \"{ShellQuoteDbl(password)}\"");
        return user;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool DeleteUnixUser(string user)
    {
        // macOS has no `userdel`; use dscl there. Linux uses `userdel -r`.
        if (OperatingSystem.IsMacOS())
        {
            RunBash($"sudo dscl . -delete /Users/{user} 2>/dev/null || true", throwOnError: false);
            RunBash($"sudo rm -rf /Users/{user} 2>/dev/null || true", throwOnError: false);
            return true;
        }
        return RunBash($"userdel -r {user} 2>/dev/null || true").ExitCode == 0;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool SetUnixPermissions(string user, string workspace, string docsDir, string dshHome, string root)
    {
        // 1. Set ownership of workspace to the user. Use owner-only form (no
        //    group) — on macOS there is no same-named group (CreateMacUser uses
        //    PrimaryGroupID 20 = staff), so "{user}:{user}" would fail.
        RunBash($"chown -R {user} \"{workspace}\"");

        // 2. Set ownership of instance .dsh directory
        RunBash($"chown -R {user} \"{dshHome}\"");

        // 3. Grant read-only access to docs
        RunBash($"chmod -R o+rX \"{docsDir}\"");

        // 4. Grant read-only access to dsh installation
        var nodeDir = Path.Combine(root, "node");
        var wwwroot = Path.Combine(root, "gui-cs", "src", "wwwroot");
        if (Directory.Exists(nodeDir))
            RunBash($"chmod -R o+rX \"{nodeDir}\"");
        if (Directory.Exists(wwwroot))
            RunBash($"chmod -R o+rX \"{wwwroot}\"");

        // 5. Set workspace permissions (owner full, others nothing)
        RunBash($"chmod -R 700 \"{workspace}\"");

        // 6. Set .dsh permissions: OWNER ONLY. It holds this member's DSH state
        //    (storages/workspace.json, sessions/); with 755 every other member's account
        //    on the box could read it. The launcher runs as root, so it is unaffected.
        RunBash($"chmod -R 700 \"{dshHome}\"");

        // 6b. The folder that CONTAINS .dsh must stay traversable (the member's DSH walks
        //     into it) but need not be listable: 711 = traverse only, no directory listing.
        var instDir = Path.GetDirectoryName(dshHome);
        if (!string.IsNullOrWhiteSpace(instDir) && Directory.Exists(instDir))
            RunBash($"chmod 711 \"{instDir}\"", throwOnError: false);

        return true;
    }

    [UnsupportedOSPlatform("windows")]
    private static Process? StartUnixAsUser(string user, string exe, string[] args, string workingDir, Dictionary<string, string>? env)
    {
        var argStr = string.Join(" ", args.Select(a => $"\"{a}\""));
        var envStr = env != null ? string.Join(" ", env.Select(e => $"export {e.Key}=\"{e.Value}\";")) : "";

        var psi = new ProcessStartInfo
        {
            FileName = "su",
            Arguments = $"- {user} -c \"cd '{workingDir}' && {envStr} '{exe}' {argStr}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir,
        };

        // Return an UNSTARTED process, mirroring StartWindowsAsUser. The caller
        // (InstanceManager.Start) wires stdout/stderr handlers and calls Start()
        // BEFORE BeginOutputReadLine(), so the early "dsh web:" banner that carries
        // the auth token is not lost to the pipe. Calling Start() here too would
        // throw (process already started) on Linux/macOS.
        var proc = new Process { StartInfo = psi };
        return proc;
    }

    // ──────────────────── RUN AS INSTANCE USER ───────────────

    /// <summary>
    /// Run a bash script as an instance's OS user. Needed because on Unix each
    /// instance workspace is chown'd to that user and chmod 700, so a launcher
    /// running as a DIFFERENT user (e.g. a non-root systemd service) cannot write
    /// into it. Root uses `su`; a non-root launcher with passwordless sudo uses
    /// `sudo -n -u`. Returns true on exit 0.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static bool RunAsInstanceUser(string id, string script, out string output)
    {
        output = "";
        var user = OsUser(id);
        var quoted = script.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var uid = RunBash("id -u", throwOnError: false).Output.Trim();
        string cmd, args;
        if (uid == "0")
        {
            cmd = "su";
            args = $"- {user} -c \"{quoted}\"";
        }
        else
        {
            cmd = "sudo";
            args = $"-n -u {user} bash -c \"{quoted}\"";
        }
        try
        {
            var r = RunCmd(cmd, args, throwOnError: false);
            output = r.Output.Trim();
            return r.ExitCode == 0;
        }
        catch (Exception ex) { output = ex.Message; return false; }
    }

    /// <summary>
    /// Make a file tree readable (o+rX) by other OS users on Unix, so a
    /// per-instance copy run as that user can read the shared session Markdown.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static void MakeWorldReadable(string path)
    {
        try { RunBash($"chmod -R o+rX \"{path}\"", throwOnError: false); } catch { }
    }

    // ──────────────────── HELPERS ────────────────────────────
    // Escape a value for safe interpolation inside SINGLE quotes in a POSIX shell.
    // The outer quotes are supplied by the caller ('...'); this returns the inner
    // text with every ' replaced by the canonical '\'' idiom, so a value containing
    // a quote, space, $, etc. cannot break out of the command.
    private static string ShellQuote(string value) => value.Replace("'", "'\\''");

    // Escape a value for safe interpolation inside DOUBLE quotes (caller supplies
    // the outer "..."). Backslash and double quote are escaped. Used for the
    // `dscl -passwd` positional password.
    private static string ShellQuoteDbl(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private record CmdResult(int ExitCode, string Output);

    private static CmdResult RunCmd(string cmd, string args, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var r = ReadOutput(p, 30000);
        if (r.ExitCode != 0 && throwOnError)
            throw new Exception($"{cmd} {args} failed (exit {r.ExitCode}): {r.Output}");
        return r;
    }

    private static CmdResult RunBash(string script, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var r = ReadOutput(p, 30000);
        if (r.ExitCode != 0 && throwOnError)
            throw new Exception($"bash '{script}' failed (exit {r.ExitCode}): {r.Output}");
        return r;
    }

    /// <summary>
    /// Read a process's stdout+stderr concurrently and wait with a hard timeout.
    /// Never blocks the caller indefinitely: a slow icacls/useradd/child takes no
    /// more than the timeout before it is killed. Reading both streams on background
    /// async tasks avoids the classic ReadToEnd() pipe deadlock and the
    /// "ReadToEnd before WaitForExit" trap that made the timeout useless.
    /// </summary>
    private static CmdResult ReadOutput(Process p, int timeoutMs)
    {
        var sb = new System.Text.StringBuilder();
        Task t1 = Task.Run(() => { try { sb.Append(p.StandardOutput.ReadToEnd()); } catch { } });
        Task t2 = Task.Run(() => { try { sb.Append(p.StandardError.ReadToEnd()); } catch { } });
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(2000);
        }
        try { Task.WaitAll(new[] { t1, t2 }, 2000); } catch { }
        return new CmdResult(p.ExitCode, sb.ToString());
    }
}

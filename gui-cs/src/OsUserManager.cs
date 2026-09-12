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
// (TakeTop Information Technology (Shanghai) Co., Ltd.). All rights reserved.
// A commercial license is also available; see LICENSE-COMMERCIAL.md.

using System.Diagnostics;
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

    [SupportedOSPlatform("windows")]
    private static string CreateWindowsUser(string user, string password)
    {
        // If the OS user already exists, DO NOT delete it: deleting destroys its
        // profile, and the next CreateProcessWithLogonW then reloads a fresh
        // profile (~60-95s per start). Keep the profile, just ensure the password
        // and group membership are correct.
        var exists = RunCmd("net", $"user {user}", throwOnError: false).ExitCode == 0;
        if (!exists)
        {
            // Create user with no expiration, password never expires
            var result = RunCmd("net", $"user {user} {password} /add /expires:never /passwordchg:no");
            if (result.ExitCode != 0) throw new Exception($"net user failed: {result.Output}");
        }
        else
        {
            // Refresh password (harmless if unchanged) so creds stay in sync.
            RunCmd("net", $"user {user} {password}", throwOnError: false);
        }
        // Add to "Users" group (not Administrators)
        RunCmd("net", $"localgroup Users {user} /add", throwOnError: false);
        return user;
    }

    [SupportedOSPlatform("windows")]
    private static bool DeleteWindowsUser(string user)
    {
        return RunCmd("net", $"user {user} /delete", throwOnError: false).ExitCode == 0;
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

        // 7. Ensure the restricted user has a profile + temp area (node/dsh need
        //    USERPROFILE/TEMP; a freshly-created local user has no profile until
        //    first logon, which breaks node bootstrap). Profile dirs are small,
        //    so /T is fine here.
        var profile = $"C:\\Users\\{user}";
        var temp = Path.Combine(profile, "AppData", "Local", "Temp");
        try
        {
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(Path.Combine(profile, "AppData", "Roaming"));
            RunCmd("icacls", $"\"{profile}\" /grant {user}:(OI)(CI)F /T /Q", throwOnError: false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[osuser] profile setup for {user} failed: {ex.Message}");
        }

        return true;
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

        // Set environment variables (the target user's profile/temp — node/dsh
        // needs these to bootstrap; without them the process exits immediately).
        psi.Environment["USERPROFILE"] = $"C:\\Users\\{user}";
        psi.Environment["HOMEDRIVE"] = "C:";
        psi.Environment["HOMEPATH"] = $"\\Users\\{user}";
        psi.Environment["TEMP"] = $"C:\\Users\\{user}\\AppData\\Local\\Temp";
        psi.Environment["TMP"] = $"C:\\Users\\{user}\\AppData\\Local\\Temp";
        psi.Environment["APPDATA"] = $"C:\\Users\\{user}\\AppData\\Roaming";
        psi.Environment["LOCALAPPDATA"] = $"C:\\Users\\{user}\\AppData\\Local";
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
        // Delete if exists
        RunBash($"userdel -r {user} 2>/dev/null || true");
        // Create user with home directory
        var result = RunBash($"useradd -m -s /bin/bash {user}");
        if (result.ExitCode != 0 && !result.Output.Contains("already exists"))
            throw new Exception($"useradd failed: {result.Output}");
        // Set password via chpasswd, single-quote-escaping both parts so a password
        // containing a quote, space, $, etc. cannot break out of the command.
        RunBash($"echo '{ShellQuote(user)}:{ShellQuote(password)}' | chpasswd");
        return user;
    }

    [UnsupportedOSPlatform("windows")]
    private static string CreateMacUser(string user, string password)
    {
        // macOS uses dscl
        RunBash($"sudo dscl . -delete /Users/{user} 2>/dev/null || true");
        var uid = 501 + new Random().Next(1000, 9000);
        RunBash($"sudo dscl . -create /Users/{user}");
        RunBash($"sudo dscl . -create /Users/{user} UserShell /bin/bash");
        RunBash($"sudo dscl . -create /Users/{user} RealName \"{user}\"");
        RunBash($"sudo dscl . -create /Users/{user} UniqueID {uid}");
        RunBash($"sudo dscl . -create /Users/{user} PrimaryGroupID 20");
        RunBash($"sudo dscl . -create /Users/{user} NFSHomeDirectory /Users/{user}");
        RunBash($"sudo createhomedir -c -u {user}");
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

        // 6. Set .dsh permissions (owner full, others read)
        RunBash($"chmod -R 755 \"{dshHome}\"");

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

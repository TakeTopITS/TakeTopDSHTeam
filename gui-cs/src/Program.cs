// TakeTopDSH Team — multi-user DeepSeek Harness platform
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
using System.IO.Compression;
using System.Net.WebSockets;
using TakeTopDshLauncher;

// Compute the project root: walk up from the app base dir looking for the root.
var root = FindRoot(AppContext.BaseDirectory);

// Standalone patch mode: run `TakeTopDshLauncher --apply-patch` after a DSH
// upgrade to re-apply every launcher patch (removed "添加工作区", sandbox read
// containment, bash/pwsh escalation disabled, search containment, settings). This
// keeps our changes working even after npm replaces the DSH package.
if (args.Any(a => a.Equals("--apply-patch", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("Applying DSH patches (standalone)...");
    DshPatcher.ApplyAll(root, hideSettings: false, m => Console.WriteLine(m));
    Console.WriteLine("Done. All DSH patches re-applied.");
    Environment.Exit(0);
}

var dsh = new DshService(root);
// Consolidate the previous launcher DB (config/launcher.db) into the single
// central business database before any store reads it.
LauncherDb.MigrateUsersAndInstances(root);
// Unix: keep the central DB / admin data folders private for other OS users.
LauncherDb.HardenUnixPermissions(root);
var instMgr = new InstanceManager(root, dsh.ReadInstancesStartPort());
dsh.SetInstanceManager(instMgr);
var auth = new AuthService(root);
// Repair orphan instances (instances without a matching user account).
auth.RepairOrphanInstances(instMgr.List());

// Serve the UI: prefer a wwwroot next to the executable, else the source one.
var wwwroot = new[] {
    Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    Path.Combine(root, "gui-cs", "src", "wwwroot"),
    Path.Combine(root, "wwwroot"),
}.FirstOrDefault(d => System.IO.Directory.Exists(d));

var launcherPort = Environment.GetEnvironmentVariable("LAUNCHER_PORT") ?? "46001";
var bindHost = dsh.ReadBindHost();   // 127.0.0.1 by default; public IP/LAN when exposed
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = wwwroot,
});

// Bind the launcher to the configured host. Loopback (127.0.0.1) is the safe
// default; setting BindHost in appsettings exposes it on that interface.
builder.WebHost.UseUrls($"http://{bindHost}:{launcherPort}");

var app = builder.Build();

// Enable WebSocket handling so the DSH's /api/remote.mux upgrade is recognized
// (the proxy bridges it to the instance's WebSocket). Must run before app.Use.
app.UseWebSockets();

// ---- Auth middleware: all /api/* (except login) require a valid session ----
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/"))
    {
        if (path == "/api/login" || path == "/api/auth/me" || path == "/api/languages")
        {
            await next();
            return;
        }
        var token = ctx.Request.Cookies["tt_session"];
        var user = auth.ValidateSession(token);
        // Accept ?launcher_token too (workbench iframes send no session cookie).
        if (user == null)
        {
            var lt = ctx.Request.Query["launcher_token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(lt)) user = auth.ValidateSession(lt);
        }
        if (user == null)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"unauthorized\"}");
            return;
        }
        ctx.Items["username"] = user;
        ctx.Items["isAdmin"] = auth.Find(user)?.Admin == true;
        await next();
        return;
    }
    await next();
});

// ---- Workspace-not-set guard (always on, no toggle) ----
// While the admin still uses the portable DEFAULT workspace, refuse data-creating
// operations: a default directory can be shared by other clones/versions or be
// overwritten on an upgrade, which would lose data. Setting the workspace itself
// (/api/workspace) and read/login endpoints stay allowed so the admin can fix it.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var method = ctx.Request.Method;
    var mutating =
        (HttpMethods.IsPost(method) && (path == "/api/instances" || path == "/api/start" || path == "/api/tasks" || path == "/api/feedback")) ||
        (HttpMethods.IsPut(method) && path == "/api/tasks") ||
        (HttpMethods.IsDelete(method) && path == "/api/tasks");
    if (mutating && dsh.IsWorkspaceDefault())
    {
        ctx.Response.StatusCode = 409;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"workspace_not_set\",\"message\":\"请先设置工作区目录（当前使用默认路径，升级或其它副本可能覆盖数据）\"}");
        return;
    }
    await next();
});

// --- API ---
app.MapPost("/api/login", (LoginRequest req, HttpContext ctx) =>
{
    req = req with { Username = req.Username?.ToLowerInvariant() };
    if (req.Username == null || req.Password == null || !auth.Verify(req.Username, req.Password))
        return Results.Json(new { ok = false, error = "用户名或密码错误" }, statusCode: 401);
    var user = auth.Find(req.Username)!;
    var token = auth.CreateSession(user.Username);
    ctx.Response.Cookies.Append("tt_session", token, new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/",
    });
    return Results.Json(new { ok = true, username = user.Username, admin = user.Admin, instanceId = user.InstanceId });
});
app.MapPost("/api/logout", (HttpContext ctx) =>
{
    var token = ctx.Request.Cookies["tt_session"];
    auth.DestroySession(token);
    ctx.Response.Cookies.Delete("tt_inst");
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    // Accept cookie, middleware-resolved user, or the URL ?launcher_token (which
    // the workbench iframes send since SameSite=Lax hides the session cookie).
    string? user = (string?)ctx.Items["username"];
    if (string.IsNullOrEmpty(user))
    {
        var token = ctx.Request.Cookies["tt_session"];
        user = auth.ValidateSession(token);
    }
    if (string.IsNullOrEmpty(user))
    {
        var lt = ctx.Request.Query["launcher_token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(lt)) user = auth.ValidateSession(lt);
    }
    if (user == null) return Results.Json(new { ok = false }, statusCode: 401);
    var u = auth.Find(user);
    return Results.Ok(new { ok = true, username = user, admin = u?.Admin == true, instanceId = u?.InstanceId ?? "" });
});
app.MapGet("/api/status", () => dsh.Status());
// The launcher's own app version (single source: the csproj <Version>).
app.MapGet("/api/version", () => new
{
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
    informational = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "1.0.0",
    product = "TakeTopDSH Team",
});
app.MapGet("/api/logs", (int? from) => new { logs = dsh.Logs(from ?? 0), total = dsh.TotalLogCount, from = from ?? 0 });
app.MapGet("/api/token", () => new { url = dsh.TokenUrl() });
// Public: language list for the login page (available before authentication).
app.MapGet("/api/languages", () =>
{
    var langRaw = dsh.ReadDefaultLanguage();
    return new { languages = SplitLanguages(langRaw) };
});
app.MapGet("/api/config", () =>
{
    var langRaw = dsh.ReadDefaultLanguage();
    return new
    {
        root = root,
        url = dsh.ReadCfgUrl(),
        externalUrl = dsh.ExternalUrl(),
        version = dsh.CurrentVersion(),
        defaultLanguage = langRaw,
        languages = SplitLanguages(langRaw),
    };
});
// Workspace configuration (set by an admin on the control page).
app.MapGet("/api/workspace", () => new { workspace = dsh.ReadWorkspacePath(), isDefault = dsh.IsWorkspaceDefault() });
app.MapPost("/api/workspace", (WorkspaceRequest req, HttpContext ctx) =>
{
    if (!(bool)ctx.Items["isAdmin"]!)
        return Results.Json(new { ok = false, error = "仅管理员可修改" }, statusCode: 403);
    dsh.SaveWorkspacePath(req.Workspace);
    // Reapply the admin default dsh workspace so it matches the new path now.
    dsh.ApplyWorkspaceToDefault();
    return Results.Ok(new { ok = true, workspace = dsh.ReadWorkspacePath() });
});
// Server directory browsing: roots when no path, else a one-level listing. Admin only.
// When ?inst=<id> is provided and no path, the default browse path is the instance's workspace.
app.MapGet("/api/browse", ([Microsoft.AspNetCore.Mvc.FromQuery] string? path, [Microsoft.AspNetCore.Mvc.FromQuery] string? inst, HttpContext ctx) =>
{
    if (!(bool)ctx.Items["isAdmin"]!)
        return Results.Json(new { ok = false, error = "仅管理员可浏览" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(path))
    {
        // If an instance is specified, default to its workspace directory.
        if (!string.IsNullOrWhiteSpace(inst))
        {
            var instance = instMgr.Get(inst);
            if (instance != null && !string.IsNullOrWhiteSpace(instance.Workspace))
            {
                var ws = instance.Workspace;
                if (Directory.Exists(ws))
                {
                    var r0 = dsh.BrowseDirectory(ws);
                    return (object)new { path = r0.Path, parent = r0.Parent, home = r0.Home, entries = r0.Entries };
                }
            }
        }
        return (object)new { roots = dsh.BrowseRoots() };
    }
    var r = dsh.BrowseDirectory(path);
    return (object)new { path = r.Path, parent = r.Parent, home = r.Home, entries = r.Entries };
});
app.MapPost("/api/start", (StartRequest req) =>
{
    var port = req.Port ?? dsh.DefaultPort();
    dsh.Start(port);
    return new { ok = true };
});
app.MapPost("/api/stop", () => { dsh.Stop(); return new { ok = true }; });
app.MapPost("/api/config", (ConfigRequest req, HttpContext ctx) =>
{
    if (req.Url != null)
    {
        // Reject URL pointing to the launcher port to prevent self-proxy loops.
        if (Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) &&
            uri.Port.ToString() == launcherPort)
        {
            return Results.Json(new { ok = false, error = $"Cannot set DSH URL to launcher port {launcherPort}; use 46000 instead." }, statusCode: 400);
        }
        dsh.SaveCfgUrl(req.Url);
    }
    if (req.ExternalUrl != null) dsh.SaveExternalUrl(req.ExternalUrl);
    if (req.DefaultLanguage != null) dsh.SaveDefaultLanguage(req.DefaultLanguage);
    return Results.Json(new { ok = true, url = dsh.ReadCfgUrl(), externalUrl = dsh.ExternalUrl(), defaultLanguage = dsh.ReadDefaultLanguage() });
});
app.MapGet("/api/update", () => new { current = dsh.CurrentVersion(), latest = dsh.NpmLatest() });

// Upgrade the bundled DSH to the latest npm version. Admin-only and disruptive:
// stops the default DSH + all running instances, npm-installs the new version,
// re-applies our DSH patches, then restarts whatever was running.
app.MapPost("/api/update/apply", async (HttpContext ctx) =>
{
    if (ctx.Items["isAdmin"] as bool? != true)
        return Results.Json(new { ok = false, error = "需要管理员权限" }, statusCode: 403);
    var from = dsh.CurrentVersion();
    var latest = dsh.NpmLatest();
    if (string.IsNullOrWhiteSpace(latest))
        return Results.Json(new { ok = false, error = "无法获取最新版本（请检查网络）" }, statusCode: 502);
    if (latest == from)
        return Results.Json(new { ok = true, from, to = from, message = "已是最新版本" });
    var wasRunning = dsh.IsRunning;
    var restart = new List<string>();
    try { if (dsh.IsRunning) dsh.Stop(); } catch { }
    foreach (var inst in instMgr.List())
        if (inst.Running) { try { instMgr.Stop(inst); restart.Add(inst.Id); } catch { } }
    dsh.AddLog($"[update] upgrading @deepseek-ai/dsh {from} -> {latest} ...");
    var (ok, output) = dsh.InstallLatest(latest);
    foreach (var line in (output ?? "").Split('\n'))
        if (!string.IsNullOrWhiteSpace(line)) dsh.AddLog("[update] " + line.Trim());
    if (ok)
    {
        try { DshPatcher.ApplyAll(root, hideSettings: false, m => dsh.AddLog("[patch] " + m)); }
        catch (Exception ex) { dsh.AddLog("[patch] error: " + ex.Message); }
    }
    else dsh.AddLog("[update] upgrade FAILED");
    var to = dsh.CurrentVersion();
    // Always bring DSH + the previously-running instances back up, even if the
    // install failed (otherwise we'd leave the platform down).
    dsh.AddLog($"[update] now at {to}; restarting DSH ...");
    if (wasRunning) { try { dsh.Start(dsh.DefaultPort()); } catch (Exception ex) { dsh.AddLog("[update] restart error: " + ex.Message); } }
    foreach (var id in restart) { try { var t = instMgr.Get(id); if (t != null) instMgr.Start(t); } catch { } }
    return Results.Json(new { ok, from, to, error = ok ? null : "升级失败", log = output }, statusCode: ok ? 200 : 500);
});

// ---- Instance manager API (multi-user, each instance = user) ----
app.MapGet("/api/instances", (HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    var isAdmin = (bool)ctx.Items["isAdmin"]!;
    IEnumerable<InstanceManager.Instance> src = instMgr.List();
    if (!isAdmin)
    {
        // Non-admin sees only their own bound instance.
        var bound = auth.Find(username)?.InstanceId ?? username;
        src = src.Where(i => string.Equals(i.Id, bound, StringComparison.OrdinalIgnoreCase));
    }
    var list = src.Select(i => new
    {
        id = i.Id, name = i.Name, dshPort = i.DshPort,
        workspace = i.Workspace, running = i.Running,
    });
    return new { instances = list };
});
// Member directory for task collaboration: any authenticated user may list ALL
// members (id + name) so they can assign tasks to / reference one another.
app.MapGet("/api/members", (HttpContext ctx) =>
{
    if (ctx.Items["username"] == null) return Results.Json(new { ok = false }, statusCode: 401);
    // Include the admin pseudo-member plus every instance.
    var list = new List<object> { new { id = "admin", name = "Admin", admin = true } };
    foreach (var i in instMgr.List())
        list.Add(new { id = i.Id, name = i.Name, admin = false });
    return Results.Ok(new { ok = true, members = list });
});
app.MapPost("/api/instances", (CreateInstanceRequest req, HttpContext ctx) =>
{
    if (!(bool)ctx.Items["isAdmin"]!)
        return Results.Json(new { ok = false, error = "仅管理员可创建" }, statusCode: 403);
    try
    {
        // Auto-generate workspace: {global_workspace}/{username} if not provided.
        var userId = (req.Id ?? "").ToLowerInvariant();
        var ws = req.Workspace ?? "";
        if (string.IsNullOrWhiteSpace(ws) && !string.IsNullOrWhiteSpace(userId))
        {
            var globalWs = dsh.ReadWorkspacePath();
            if (!string.IsNullOrWhiteSpace(globalWs))
            {
                ws = Path.Combine(globalWs, userId);
                Directory.CreateDirectory(ws);  // ensure the sub-directory exists
            }
        }
        var inst = instMgr.Create(userId, req.Name ?? userId, ws, req.Password);
        // Bind the instance to a user account of the same username.
        // If account creation fails (e.g. bad password), keep the instance —
        // the admin can later reset the password to auto-create the account.
        var accountErr = auth.CreateUser(userId, req.Password ?? "", inst.Id);
        return Results.Ok(new { ok = true, id = inst.Id, dshPort = inst.DshPort, accountCreated = accountErr == null, accountError = accountErr });
    }
    catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
});
app.MapPost("/api/instances/{id}/start", (string id, HttpContext ctx) =>
{
    var inst = instMgr.Get(id);
    // Robustness: if the caller passed an empty/unknown id but is a normal user
    // (launcher "我的实例" Start), fall back to their bound instance so an odd
    // front-end id never yields a misleading "instance not found".
    if (inst == null && !(bool)ctx.Items["isAdmin"]!)
    {
        var un = (string)ctx.Items["username"]!;
        inst = instMgr.Get(un);
    }
    if (inst == null) return Results.NotFound(new { ok = false, error = "instance not found" });
    if (!CanAccess(ctx, inst.Id))
        return Results.Json(new { ok = false, error = "无权操作" }, statusCode: 403);
    if (inst.Proc is { HasExited: false })
        return Results.Ok(new { ok = true, running = true, dshPort = inst.DshPort });

    // Defer the slow part (creating the OS user, granting icacls permissions, and
    // launching dsh under a fresh user profile) to a background task so the HTTP
    // request returns immediately instead of blocking ~60s on a first boot. The
    // UI polls instance state and shows "启动中" until running becomes true.
    inst.Starting = true;
    _ = Task.Run(() =>
    {
        try { instMgr.Start(inst); }
        catch (Exception ex) { inst.Logs.Enqueue("[start] " + ex.Message); }
        finally { inst.Starting = false; }
    });
    return Results.Ok(new { ok = true, accepted = true, dshPort = inst.DshPort });
});
app.MapPost("/api/instances/{id}/stop", (string id, HttpContext ctx) =>
{
    var inst = instMgr.Get(id);
    // Robustness: fall back to the caller's bound instance for normal users.
    if (inst == null && !(bool)ctx.Items["isAdmin"]!)
    {
        var un = (string)ctx.Items["username"]!;
        inst = instMgr.Get(un);
    }
    if (inst == null) return Results.NotFound(new { ok = false });
    if (!CanAccess(ctx, inst.Id)) return Results.Json(new { ok = false, error = "无权操作" }, statusCode: 403);
    instMgr.Stop(inst);
    return Results.Ok(new { ok = true, running = false });
});
app.MapGet("/api/instances/{id}/open", (string id, HttpContext ctx) =>
{
    var inst = instMgr.Get(id);
    if (inst == null) return Results.NotFound(new { ok = false });
    if (!CanAccess(ctx, inst.Id)) return Results.Json(new { ok = false, error = "无权操作" }, statusCode: 403);
    return Results.Ok(new { ok = true, url = instMgr.OpenUrl(inst) });
});
// Return the caller's session token so the JS "打开" button can embed it in the
// navigation URL — bypasses the browser's cookie-suppression on cross-page jumps.
app.MapGet("/api/session-token", (HttpContext ctx) =>
{
    var token = ctx.Request.Cookies["tt_session"];
    var user = auth.ValidateSession(token);
    if (user == null) return Results.Unauthorized();
    return Results.Ok(new { token });
});
app.MapDelete("/api/instances/{id}", (string id, HttpContext ctx) =>
{
    if (!(bool)ctx.Items["isAdmin"]!)
        return Results.Json(new { ok = false, error = "仅管理员可删除" }, statusCode: 403);
    var inst = instMgr.Get(id);
    if (inst == null) return Results.NotFound(new { ok = false });
    instMgr.Delete(inst);
    // Also delete the associated user account.
    auth.DeleteUser(id, (string)ctx.Items["username"]!);
    return Results.Ok(new { ok = true });
});

// ---- Password management ----
// User changes own password (must verify old password).
app.MapPost("/api/change-password", (ChangePasswordRequest req, HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    var err = auth.ChangePasswordWithOld(username, req.OldPassword ?? "", req.NewPassword ?? "");
    if (err != null) return Results.BadRequest(new { ok = false, error = err });
    return Results.Ok(new { ok = true });
});
// Admin resets any user's password.
app.MapPost("/api/admin/reset-password", (ResetPasswordRequest req, HttpContext ctx) =>
{
    if (!(bool)ctx.Items["isAdmin"]!)
        return Results.Json(new { ok = false, error = "仅管理员可操作" }, statusCode: 403);
    if (string.IsNullOrEmpty(req.Username))
        return Results.BadRequest(new { ok = false, error = "用户名不能为空" });
    var uname = req.Username.ToLowerInvariant();
    var err = auth.ResetPassword(uname, req.NewPassword ?? "", uname);
    if (err != null) return Results.BadRequest(new { ok = false, error = err });
    return Results.Ok(new { ok = true });
});

// Non-admin may only access their own bound instance.
bool CanAccess(HttpContext ctx, string instanceId)
{
    if ((bool)ctx.Items["isAdmin"]!) return true;
    var username = (string)ctx.Items["username"]!;
    var bound = auth.Find(username)?.InstanceId ?? username;
    return string.Equals(bound, instanceId, StringComparison.OrdinalIgnoreCase);
}

// ================= File manager (per-user workspace) =================
// Each authenticated user manages files under their OWN workspace root:
//   - normal user  -> instMgr.Get(username).Workspace
//   - admin        -> the global workspace path (dsh.ReadWorkspacePath())
// Every path is relative (forward slashes) and MUST resolve inside the root;
// anything resolving outside the root is refused.

string? ResolveUserWorkspace(string username)
{
    var u = auth.Find(username);
    if (u?.Admin == true) return dsh.ReadWorkspacePath();
    var inst = instMgr.Get(username);
    return inst == null ? null : inst.Workspace;
}

// Resolve the workspace a file-manager request should operate on. Supports an
// optional ?inst=<id> target (used by the /work split page): admins may view any
// user's workspace; non-admins are confined to their own. `allowCrossMemberRead`
// additionally lets any authenticated user READ (list/preview/download) files
// from another member's workspace — required so collaborators can view each
// other's task/feedback attachments. Write operations are never cross-member.
string? ResolveFileWorkspace(HttpContext ctx, bool allowCrossMemberRead = false)
{
    var username = (string)ctx.Items["username"]!;
    var instParam = ctx.Request.Query["inst"].FirstOrDefault();
    if (!string.IsNullOrEmpty(instParam))
    {
        // "admin" always resolves to the global workspace (admin is not in instances.json).
        if (string.Equals(instParam, "admin", StringComparison.OrdinalIgnoreCase))
            return dsh.ReadWorkspacePath();
        // Admin (or the user themself) may view any instance workspace.
        if (string.Equals(instParam, username, StringComparison.OrdinalIgnoreCase))
        {
            var t = instMgr.Get(instParam);
            return t == null ? null : t.Workspace;
        }
        // Non-admin requesting another member's workspace: allowed only for reads.
        var caller = auth.Find(username);
        if (caller?.Admin != true && !allowCrossMemberRead) return null;
        var inst = instMgr.Get(instParam);
        return inst == null ? null : inst.Workspace;
    }
    return ResolveUserWorkspace(username);
}

// Resolve a user-safe relative path under `root`. Throws ArgumentException when
// the resolved absolute path escapes the root (path-traversal guard).
string SafeResolve(string root, string? rel)
{
    var baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var abs = Path.GetFullPath(Path.Combine(baseRoot, rel ?? ""));
    var boundary = baseRoot + Path.DirectorySeparatorChar;
    if (!abs.Equals(baseRoot, StringComparison.OrdinalIgnoreCase) &&
        !abs.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("path escapes the permitted workspace");
    return abs;
}

// Find a free sibling name like "name (1).ext" — used when unzip's target
// folder name is already taken by an existing file or directory.
static string SuggestName(string parentDir, string name)
{
    var ext = Path.GetExtension(name);
    var stem = Path.GetFileNameWithoutExtension(name);
    if (string.IsNullOrEmpty(stem)) stem = name;
    for (var i = 1; i < 100000; i++)
    {
        var cand = stem + " (" + i + ")" + ext;
        if (!File.Exists(Path.Combine(parentDir, cand)) && !Directory.Exists(Path.Combine(parentDir, cand)))
            return cand;
    }
    return stem + " (" + Guid.NewGuid().ToString("N")[..6] + ")" + ext;
}

// Return top-level entries (dirs first, then files, both sorted) for a listing.
static IReadOnlyList<object> BuildFileEntries(string absDir)
{
    var entries = new List<object>();
    foreach (var d in Directory.GetDirectories(absDir))
        entries.Add(new FileEntryInfo(Path.GetFileName(d), "dir", 0, Directory.GetLastWriteTimeUtc(d)));
    foreach (var f in Directory.GetFiles(absDir))
    {
        var fi = new FileInfo(f);
        entries.Add(new FileEntryInfo(fi.Name, "file", fi.Length, fi.LastWriteTimeUtc));
    }
    return entries
        .Cast<FileEntryInfo>()
        .OrderBy(e => e.Type == "dir" ? 0 : 1)
        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .Select(e => new { name = e.Name, type = e.Type, size = e.Size, mtime = e.Mtime.ToString("o") })
        .ToList();
}

// Health: returns the resolved workspace root (REQUIRED for the UI to know where it is).
app.MapGet("/api/files/root", (HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx, allowCrossMemberRead: true);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    return Results.Ok(new { ok = true, root = ws });
});

// List a directory (or the workspace root when `path` is empty).
app.MapGet("/api/files/list", (string? path, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx, allowCrossMemberRead: true);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var absDir = SafeResolve(ws, path);
        if (!Directory.Exists(absDir)) return Results.Json(new { ok = false, error = "目录不存在" }, statusCode: 404);
        var entries = BuildFileEntries(absDir);
        return Results.Ok(new { ok = true, root = ws, path = path ?? "", entries });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Upload one or more files into a target directory.
app.MapPost("/api/files/upload", (HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var targetRel = ctx.Request.Form["path"].ToString() ?? "";
        var targetDir = SafeResolve(ws, targetRel);
        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
        // Optional per-file rename map (original name -> new name) chosen by the
        // user when a same-name file already exists in the target folder.
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var renamesRaw = ctx.Request.Form["renames"].ToString();
        if (!string.IsNullOrWhiteSpace(renamesRaw))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(renamesRaw);
                if (parsed != null) foreach (var kv in parsed) if (kv.Value != null) renames[kv.Key] = kv.Value;
            }
            catch { }
        }
        // Detect conflicts first; nothing is written until every name is resolved.
        var conflicts = new List<object>();
        var plan = new List<(IFormFile file, string dest)>();
        foreach (var f in ctx.Request.Form.Files)
        {
            var origName = Path.GetFileName(f.FileName);
            if (string.IsNullOrWhiteSpace(origName)) continue;
            var destName = (renames.TryGetValue(origName, out var nn) && !string.IsNullOrWhiteSpace(nn)) ? nn.Trim() : origName;
            if (destName.IndexOfAny(new[] { '/', '\\' }) >= 0 || destName is "." or "..")
                return Results.Json(new { ok = false, error = "名称非法: " + destName }, statusCode: 400);
            var dest = Path.Combine(targetDir, destName);
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                conflicts.Add(new { name = origName, target = destName, suggestion = SuggestName(targetDir, destName) });
                continue;
            }
            plan.Add((f, dest));
        }
        if (conflicts.Count > 0)
            return Results.Json(new { ok = false, conflict = true, conflicts }, statusCode: 409);
        var saved = new List<string>();
        foreach (var p in plan)
        {
            using var stream = System.IO.File.Create(p.dest);
            p.file.CopyTo(stream);
            saved.Add(Path.GetFileName(p.dest));
        }
        return Results.Ok(new { ok = true, saved });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Delete a file or directory (recursive for dirs).
app.MapPost("/api/files/delete", (FileOpRequest req, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var abs = SafeResolve(ws, req.Path);
        if (File.Exists(abs)) File.Delete(abs);
        else if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
        else return Results.Json(new { ok = false, error = "不存在" }, statusCode: 404);
        return Results.Ok(new { ok = true });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Rename a file/directory within the same parent.
app.MapPost("/api/files/rename", (FileRenameRequest req, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var abs = SafeResolve(ws, req.Path);
        var absNew = SafeResolve(ws, req.NewPath);
        if (!File.Exists(abs) && !Directory.Exists(abs)) return Results.Json(new { ok = false, error = "不存在" }, statusCode: 404);
        if (File.Exists(absNew) || Directory.Exists(absNew)) return Results.Json(new { ok = false, error = "目标已存在" }, statusCode: 409);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(absNew)!);
        if (File.Exists(abs)) File.Move(abs, absNew);
        else Directory.Move(abs, absNew);
        return Results.Ok(new { ok = true });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Move a file/directory into a target directory. If the destination name is
// already taken, return a conflict + a suggested free name so the UI can ask the
// user to rename (NewName overrides the destination base name).
app.MapPost("/api/files/move", (FileMoveRequest req, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var src = SafeResolve(ws, req.Path);
        var dst = SafeResolve(ws, req.ToPath);
        if (!File.Exists(src) && !Directory.Exists(src)) return Results.Json(new { ok = false, error = "源不存在" }, statusCode: 404);
        // ToPath may itself be a directory (drop onto a folder); otherwise it is a
        // full destination path and we use its directory.
        var targetDir = Directory.Exists(dst) ? dst : Path.GetDirectoryName(dst)!;
        var newName = string.IsNullOrWhiteSpace(req.NewName) ? Path.GetFileName(src) : req.NewName!.Trim();
        if (newName.IndexOfAny(new[] { '/', '\\' }) >= 0 || newName is "." or "..")
            return Results.Json(new { ok = false, error = "名称非法" }, statusCode: 400);
        var finalDst = Path.Combine(targetDir, newName);
        // Moving onto itself (same folder it already lives in) is a no-op, not a conflict.
        if (string.Equals(finalDst.TrimEnd(Path.DirectorySeparatorChar), src.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, error = "源和目标相同" }, statusCode: 400);
        // Refuse to move a directory into its own subtree.
        if (Directory.Exists(src) &&
            finalDst.StartsWith(src.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, error = "不能移动到自身子目录" }, statusCode: 400);
        if (File.Exists(finalDst) || Directory.Exists(finalDst))
            return Results.Json(new { ok = false, conflict = true, target = newName, suggestion = SuggestName(targetDir, newName) }, statusCode: 409);
        System.IO.Directory.CreateDirectory(targetDir);
        if (File.Exists(src)) File.Move(src, finalDst);
        else Directory.Move(src, finalDst);
        return Results.Ok(new { ok = true });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Create a directory (recursive). If the name is already taken, return a
// conflict + a suggested free name so the UI can ask the user to rename.
app.MapPost("/api/files/mkdir", (FileOpRequest req, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var abs = SafeResolve(ws, req.Path);
        if (File.Exists(abs) || Directory.Exists(abs))
        {
            var parent = Path.GetDirectoryName(abs)!;
            var nm = Path.GetFileName(abs);
            return Results.Json(new { ok = false, conflict = true, target = nm, suggestion = SuggestName(parent, nm) }, statusCode: 409);
        }
        Directory.CreateDirectory(abs);
        return Results.Ok(new { ok = true });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Compress a file or directory to <name>.zip in the same folder.
app.MapPost("/api/files/zip", (FileOpRequest req, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var src = SafeResolve(ws, req.Path);
        if (!File.Exists(src) && !Directory.Exists(src)) return Results.Json(new { ok = false, error = "不存在" }, statusCode: 404);
        var zip = src + ".zip";
        if (File.Exists(zip)) return Results.Json(new { ok = false, error = "zip 已存在" }, statusCode: 409);
        if (Directory.Exists(src))
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(src, zip);
        }
        else
        {
            using var za = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
            za.CreateEntryFromFile(src, Path.GetFileName(src));
        }
        return Results.Ok(new { ok = true, name = Path.GetFileName(zip) });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Extract a zip archive into a SAME-NAME folder placed NEXT TO the archive
// (i.e. the new folder is created in the same directory as the .zip). If that
// folder name is already taken (by a file OR a folder), return a conflict plus a
// suggested free name so the UI can ask the user to rename the target folder.
app.MapPost("/api/files/unzip", (FileOpRequest req, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var abs = SafeResolve(ws, req.Path);
        if (!File.Exists(abs) || !abs.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, error = "不是 zip 文件" }, statusCode: 400);
        var parent = Path.GetDirectoryName(abs)!;                 // same directory as the .zip
        var baseName = Path.GetFileNameWithoutExtension(abs);
        var targetName = string.IsNullOrWhiteSpace(req.TargetName) ? baseName : req.TargetName!.Trim();
        if (targetName.IndexOfAny(new[] { '/', '\\' }) >= 0 || targetName is "." or "..")
            return Results.Json(new { ok = false, error = "名称非法" }, statusCode: 400);
        var target = Path.Combine(parent, targetName);
        if (File.Exists(target) || Directory.Exists(target))
            return Results.Json(new { ok = false, conflict = true, target = targetName, suggestion = SuggestName(parent, targetName) }, statusCode: 409);
        Directory.CreateDirectory(target);
        System.IO.Compression.ZipFile.ExtractToDirectory(abs, target);
        return Results.Ok(new { ok = true, name = targetName });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (System.IO.InvalidDataException ex) { return Results.Json(new { ok = false, error = "解压失败: " + ex.Message }, statusCode: 400); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Read a file's text content for the viewer (limit size to avoid huge loads).
app.MapPost("/api/files/read", (FileOpRequest req, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx, allowCrossMemberRead: true);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var abs = SafeResolve(ws, req.Path);
        if (!File.Exists(abs)) return Results.Json(new { ok = false, error = "文件不存在" }, statusCode: 404);
        var fi = new FileInfo(abs);
        const long max = 4L * 1024 * 1024;   // 4 MB cap
        if (fi.Length > max) return Results.Json(new { ok = false, error = "文件过大，无法预览（>4MB）" }, statusCode: 413);
        var ext = Path.GetExtension(abs).ToLowerInvariant();
        var bytes = File.ReadAllBytes(abs);
        var isImage = (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico");
        if (isImage)
        {
            var mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
                ".bmp" => "image/bmp", ".webp" => "image/webp", ".svg" => "image/svg+xml", ".ico" => "image/x-icon", _ => "application/octet-stream" };
            var dataUri = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
            return Results.Json(new { ok = true, type = "image", ext = ext, name = Path.GetFileName(abs), dataUri });
        }
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return Results.Json(new { ok = true, type = "text", ext = ext, name = Path.GetFileName(abs), content = text });
    }
    catch (ArgumentException ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// Download a file's raw bytes as an attachment (for files the viewer cannot
// preview). Browsers then hand the downloaded file to the OS default app.
app.MapGet("/api/files/download", (string? path, string? inline, HttpContext ctx) =>
{
    var ws = ResolveFileWorkspace(ctx, allowCrossMemberRead: true);
    if (string.IsNullOrWhiteSpace(ws)) return Results.Json(new { ok = false, error = "未绑定工作区" }, statusCode: 400);
    try
    {
        var abs = SafeResolve(ws, path);
        // Legacy/back-compat: task-content images stored the path relative to
        // TaskData (e.g. "Doc/img.png"); the file actually lives under TaskData/.
        // If the requested path doesn't exist, retry under <workspace>/TaskData.
        if (!File.Exists(abs))
        {
            var cleaned = (path ?? "").TrimStart('/', '\\');
            var relUnder = cleaned.StartsWith("TaskData", StringComparison.OrdinalIgnoreCase)
                ? cleaned
                : "TaskData/" + cleaned;
            var cand = SafeResolve(ws, relUnder);
            if (File.Exists(cand)) abs = cand;
        }
        if (!File.Exists(abs)) return Results.NotFound();
        var bytes = File.ReadAllBytes(abs);
        var name = Path.GetFileName(abs);
        var mime = MimeOf(Path.GetExtension(abs));
        ctx.Response.Headers.ContentType = mime;
        // RFC 5987 filename* for non-ASCII names; the plain `filename` fallback
        // must be ASCII-only (HTTP headers cannot carry non-ASCII bytes), so use
        // a safe placeholder there and put the real (possibly CJK) name in
        // filename*=UTF-8''...
        var asciiFallback = "download" + Path.GetExtension(name);
        var fileNameStar = Uri.EscapeDataString(name);
        ctx.Response.Headers.ContentType = mime;
        ctx.Response.Headers.ContentDisposition = (inline == "true" || inline == "1")
            ? "inline; filename=\"" + asciiFallback + "\"; filename*=UTF-8''" + fileNameStar
            : "attachment; filename=\"" + asciiFallback + "\"; filename*=UTF-8''" + fileNameStar;
        return Results.File(bytes, mime, null, enableRangeProcessing: true);
    }
    catch (ArgumentException) { return Results.Json(new { ok = false, error = "路径越界" }, statusCode: 403); }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

static string MimeOf(string ext)
{
    return ext.ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" or ".csv" or ".ini" or ".json" or ".yml" or ".yaml" or ".xml" or ".html" or ".htm" or ".js" or ".css" or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".c" or ".h" or ".cpp" or ".cs" or ".go" or ".rs" or ".rb" or ".php" or ".sh" or ".sql" or ".bat" or ".ps1" => "text/plain; charset=utf-8",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".webp" => "image/webp", ".svg" => "image/svg+xml", ".ico" => "image/x-icon",
        ".pdf" => "application/pdf", ".zip" => "application/zip", ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint", ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".mp4" => "video/mp4", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".exe" or ".dll" => "application/octet-stream",
        _ => "application/octet-stream"
    };
}

// Serve the "DSH is starting up" spinner page (auto-refreshes every 3s). Used when a
// proxied DSH (the default one or a per-user instance) isn't ready yet, so users see
// a friendly wait page instead of a raw "connection refused" proxy error.
static async Task WriteDshStarting(HttpContext ctx)
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/html; charset=utf-8";
    var lang = "en";
    if (ctx.Request.Cookies.TryGetValue("tt_lang", out var lv) && !string.IsNullOrEmpty(lv)) lang = lv;
    var isZh = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    var msg = isZh ? "DSH 正在启动中，请稍候..." : "DSH is starting up, please wait...";
    var sub = isZh
        ? "首次打开 DSH 需要稍等片刻，加载完成后，下次即可即时打开。"
        : "The first time you open DSH it may take a moment to start; after that it opens instantly.";
    var hint = isZh ? "页面每 3 秒自动刷新。" : "Page will auto-refresh every 3 seconds.";
    var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<style>body{margin:0;display:flex;justify-content:center;align-items:center;height:100vh;font-family:system-ui,sans-serif;background:#f8f9fa}" +
        ".box{text-align:center;color:#555}" +
        ".spinner{width:48px;height:48px;border:5px solid #e0e0e0;border-top-color:#4a90d9;border-radius:50%;animation:spin 1s linear infinite;margin:0 auto 20px}" +
        "@keyframes spin{to{transform:rotate(360deg)}}" +
        "p{margin:8px 0;font-size:15px}.hint{font-size:13px;color:#999;margin-top:12px}" +
        "</style></head><body>" +
        "<div class=\"box\"><div class=\"spinner\"></div>" +
        "<p>" + msg + "</p>" +
        "<p class=\"hint\">" + sub + "</p>" +
        "<p class=\"hint\">" + hint + "</p>" +
        "</div><script>setTimeout(function(){location.reload()},3000)</script>" +
        "</body></html>";
    await ctx.Response.WriteAsync(html);
}

// ================= Tasks (任务分配) =================
// Tasks are stored per-user in SQLite under <workspace>/TaskData/tasks-<instId>.db
// (legacy XML is auto-imported on first use). Related files are uploaded into
// <workspace>/TaskData/Doc. Status stored in English
// (pending|processing|done|cancelled); the UI localizes the labels.

static List<LanguageItem> SplitLanguages(string? raw)
{
    var result = new List<LanguageItem>();
    if (string.IsNullOrWhiteSpace(raw)) return result;
    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var colon = part.IndexOf(':');
        string label, code;
        if (colon >= 0)
        {
            label = part[..colon].Trim();
            code = part[(colon + 1)..].Trim();
        }
        else
        {
            label = part;
            code = part;
        }
        if (string.IsNullOrWhiteSpace(code)) continue;
        var codeKey = code.ToLowerInvariant();
        if (result.Any(x => x.Code.Equals(codeKey, StringComparison.OrdinalIgnoreCase))) continue;
        result.Add(new LanguageItem(label, codeKey));
    }
    return result;
}

// ================= Tasks (任务分配) =================
// Tasks are stored per-user in SQLite under <workspace>/TaskData/tasks-<instId>.db
// (legacy XML is auto-imported on first use). Related files are uploaded into
// <workspace>/TaskData/Doc. Status stored in English
// (pending|processing|done|cancelled); the UI localizes the labels.

// On-demand task/feedback cache: the SQLite rows are read lazily once and mutated
// in memory; changes are flushed back to the database on a short debounce instead
// of writing on every request. The launcher is a single process, so a single
// in-process cache is safe.
var taskCache = new TaskDataCache();
ScheduleTaskFlush();

List<TaskRecord> ReadTaskList(string instId)
{
    return taskCache.GetTasks(instId, () => LoadTasksFromDisk(instId));
}

void WriteTaskList(string instId, List<TaskRecord> tasks)
{
    taskCache.SetTasks(instId, tasks);
}

// GET /api/tasks?inst=<id>&scope=assignment&page=N&pageSize=M  -> the tasks for a
// member. Admin may read any member (and "admin" for its own); a normal user may
// only read its own. Supports backend pagination (page/pageSize); omitting them
// returns the full list (backwards compatible).
app.MapGet("/api/tasks", (string? inst, string? scope, int? page, int? pageSize, HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    var isAdmin = (bool)ctx.Items["isAdmin"]!;
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    Console.WriteLine($"[tasks] user={username} admin={isAdmin} inst={inst} scope={scope} page={page} pageSize={pageSize} ip={ctx.Connection.RemoteIpAddress}");
    try
    {
        List<(TaskRecord Task, string Member)> all;
        if (string.IsNullOrWhiteSpace(inst))
        {
            all = new List<(TaskRecord Task, string Member)>();
            foreach (var m in instMgr.List())
            {
                foreach (var t in ReadTaskList(m.Id))
                    all.Add((t, m.Id));
            }
            foreach (var t in ReadTaskList("admin"))
                if (!all.Any(x => x.Task.Seq == t.Seq && x.Member == "admin"))
                    all.Add((t, "admin"));
            // "All" overview: non-admins only see tasks they created (not tasks
            // created by others, even if assigned to them).
            if (!isAdmin) all = all.Where(x => string.Equals(x.Task.CreatedBy, username, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else if (string.Equals(scope, "assignment", StringComparison.OrdinalIgnoreCase))
        {
            all = ReadTaskList(inst)
                .Where(t => string.Equals(t.CreatedBy, username, StringComparison.OrdinalIgnoreCase))
                .Select(t => (t, inst))
                .ToList();
        }
        else
        {
            all = ReadTaskList(inst).Select(t => (t, inst)).ToList();
        }

        var ordered = all.OrderByDescending(x => x.Task.Seq).ToList();
        var total = ordered.Count;
        // Apply pagination only when page/pageSize are supplied.
        int? pagedTotal = null; List<(TaskRecord Task, string Member)> slice = ordered;
        if (page.HasValue || pageSize.HasValue)
        {
            var ps = pageSize.GetValueOrDefault(25);
            if (ps < 1) ps = 25;
            var pg = page.GetValueOrDefault(1);
            if (pg < 1) pg = 1;
            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)ps));
            if (pg > pages) pg = pages;
            var skip = (pg - 1) * ps;
            slice = ordered.Skip(skip).Take(ps).ToList();
            pagedTotal = total;
            pageSize = ps;
            page = pg;
        }

        var result = slice.Select(x => new {
            seq = x.Task.Seq, name = x.Task.Name, type = x.Task.Type, content = x.Task.Content,
            status = x.Task.Status, assignedAt = x.Task.AssignedAt, createdBy = x.Task.CreatedBy,
            files = x.Task.Files, member = x.Member
        });
        if (pagedTotal.HasValue)
        {
            Console.WriteLine($"[tasks] user={username} -> returning {slice.Count()} of {pagedTotal}");
            return Results.Ok(new { ok = true, tasks = result, total = pagedTotal, page = page, pageSize = pageSize });
        }
        Console.WriteLine($"[tasks] user={username} -> returning {slice.Count()} of {total}");
        return Results.Ok(new { ok = true, tasks = result, total = total });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// POST /api/tasks  -> add a task  { inst, type, content, status, files[] }
app.MapPost("/api/tasks", (TaskUpsertRequest req, HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    if (string.IsNullOrWhiteSpace(req.Inst)) return Results.Json(new { ok = false, error = "缺少成员" }, statusCode: 400);
    try
    {
        var list = ReadTaskList(req.Inst);
        var seq = (list.Count == 0 ? 0 : list.Max(t => t.Seq)) + 1;
        list.Add(new TaskRecord { Seq = seq, Name = req.Name ?? "", Type = req.Type ?? "", Content = req.Content ?? "", Status = req.Status ?? "pending", AssignedAt = string.IsNullOrWhiteSpace(req.AssignedAt) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm") : req.AssignedAt, CreatedBy = username, Files = req.Files ?? new List<string>() });
        WriteTaskList(req.Inst, list);
        return Results.Ok(new { ok = true, seq });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// PUT /api/tasks  -> edit a task  { inst, seq, type, content, status, files[] }
app.MapPut("/api/tasks", (TaskUpsertRequest req, HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    var isAdmin = (bool)ctx.Items["isAdmin"]!;
    if (string.IsNullOrWhiteSpace(req.Inst)) return Results.Json(new { ok = false, error = "缺少成员" }, statusCode: 400);
    try
    {
        var list = ReadTaskList(req.Inst);
        var t = list.FirstOrDefault(x => x.Seq == req.Seq);
        if (t == null) return Results.Json(new { ok = false, error = "任务不存在" }, statusCode: 404);
        if (!isAdmin && !string.Equals(t.CreatedBy, username, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, error = "仅可编辑自己建立的任务" }, statusCode: 403);
        t.Name = req.Name ?? t.Name;
        t.Type = req.Type ?? t.Type;
        t.Content = req.Content ?? t.Content;
        t.Status = req.Status ?? t.Status;
        t.AssignedAt = req.AssignedAt ?? t.AssignedAt;
        if (req.Files != null) t.Files = req.Files;
        WriteTaskList(req.Inst, list);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// DELETE /api/tasks?inst=<id>&seq=<n>  -> delete a task
app.MapDelete("/api/tasks", (string? inst, int? seq, HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    var isAdmin = (bool)ctx.Items["isAdmin"]!;
    if (string.IsNullOrWhiteSpace(inst)) return Results.Json(new { ok = false, error = "缺少成员" }, statusCode: 400);
    try
    {
        var list = ReadTaskList(inst);
        var t = list.FirstOrDefault(x => x.Seq == seq);
        if (t == null) return Results.Json(new { ok = false, error = "任务不存在" }, statusCode: 404);
        if (!isAdmin && !string.Equals(t.CreatedBy, username, StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, error = "仅可删除自己建立的任务" }, statusCode: 403);
        list.RemoveAll(x => x.Seq == seq);
        WriteTaskList(inst, list);
        // Also drop this task's feedback so orphaned entries don't attach to a
        // future task that reuses the same seq number.
        try
        {
            if (seq.HasValue)
            {
                var fb = ReadFeedback(inst);
                if (fb.Remove(seq.Value)) WriteFeedback(inst, fb);
            }
        }
        catch { }
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

string TaskWorkspace(string instId)
{
    // Admin can assign tasks to itself: its "workspace" is the global workspace.
    if (string.Equals(instId, "admin", StringComparison.OrdinalIgnoreCase))
        return dsh.ReadWorkspacePath();
    var inst = instMgr.Get(instId);
    return inst?.Workspace ?? "";
}

string TaskDataDir(string instId)
{
    var ws = TaskWorkspace(instId);
    if (string.IsNullOrWhiteSpace(ws)) throw new ArgumentException("成员工作区未配置", nameof(instId));
    var dir = System.IO.Path.Combine(ws, "TaskData");
    Directory.CreateDirectory(dir);
    Directory.CreateDirectory(System.IO.Path.Combine(dir, "Doc"));
    return dir;
}

// Tasks/feedback live in the single central database (see LauncherDb), keyed by
// `owner`. The paths below point at the LEGACY per-member stores and are used
// only once, to import existing data on first access.
string TaskDbPath(string instId)
{
    return System.IO.Path.Combine(TaskDataDir(instId), $"tasks-{instId}.db");
}

string TaskDocPath(string instId)
{
    return System.IO.Path.Combine(TaskDataDir(instId), $"tasks-{instId}.xml");
}

// ===== Task feedback (one per task per user per day; the same-day entry is editable) =====
string FeedbackDocPath(string instId)
{
    return System.IO.Path.Combine(TaskDataDir(instId), $"feedback-{instId}.xml");
}

// Legacy paths may be unavailable (member workspace not configured); treat that
// as "no legacy data" rather than failing the request.
string SafeTaskDbPath(string instId) { try { return TaskDbPath(instId); } catch { return ""; } }
string SafeTaskDocPath(string instId) { try { return TaskDocPath(instId); } catch { return ""; } }
string SafeFeedbackDocPath(string instId) { try { return FeedbackDocPath(instId); } catch { return ""; } }

Dictionary<int, List<FeedbackEntry>> ReadFeedback(string instId)
{
    return taskCache.GetFeedback(instId, () => LoadFeedbackFromDisk(instId));
}

void WriteFeedback(string instId, Dictionary<int, List<FeedbackEntry>> dict)
{
    taskCache.SetFeedback(instId, dict);
}

// ---- Disk serializers (used by the cache to load lazily and flush) ----
// Backed by the single central database; `instId` is the row `owner`. The legacy
// per-member paths are passed only so LauncherDb can import old data once.
List<TaskRecord> LoadTasksFromDisk(string instId)
    => LauncherDb.LoadTasks(root, instId, SafeTaskDbPath(instId), SafeTaskDocPath(instId), SafeFeedbackDocPath(instId));

void WriteTasksToDisk(string instId, List<TaskRecord> tasks)
    => LauncherDb.SaveTasks(root, instId, tasks);

Dictionary<int, List<FeedbackEntry>> LoadFeedbackFromDisk(string instId)
    => LauncherDb.LoadFeedback(root, instId, SafeTaskDbPath(instId), SafeTaskDocPath(instId), SafeFeedbackDocPath(instId));

void WriteFeedbackToDisk(string instId, Dictionary<int, List<FeedbackEntry>> dict)
    => LauncherDb.SaveFeedback(root, instId, dict);

// Debounced flush hook: every FLUSH_INTERVAL the cache writes its dirty entries to disk.
void ScheduleTaskFlush()
{
    TaskCleanupHolder.Timer = new System.Threading.Timer(_ =>
    {
        try { taskCache.Flush(WriteTasksToDisk, WriteFeedbackToDisk); }
        catch (Exception ex) { Console.WriteLine("task flush: " + ex.Message); }
    }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
}

// GET /api/feedback?inst=<id>&seq=<n> -> the feedback entries for a task (any user may read; scoped like tasks)
app.MapGet("/api/feedback", (string? inst, int? seq, HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    var isAdmin = (bool)ctx.Items["isAdmin"]!;
    var target = string.IsNullOrWhiteSpace(inst) ? username : inst;
    try
    {
    var dict = ReadFeedback(target);
    var list = seq.HasValue && dict.TryGetValue(seq.Value, out var l) ? l : new List<FeedbackEntry>();
    // Staleness guard: because task seq numbers are reused after a delete, a
    // freshly created task can inherit orphaned feedback left by a previous
    // task that occupied the same seq. Only show feedback dated on/after the
    // task's assignedAt time; anything earlier is orphaned and dropped.
    if (seq.HasValue)
    {
        var assignedAt = ReadTaskList(target).FirstOrDefault(x => x.Seq == seq.Value)?.AssignedAt;
        if (!string.IsNullOrWhiteSpace(assignedAt) && DateTime.TryParse(assignedAt, out var start))
            list = list.Where(e => DateTime.TryParse(e.Time ?? e.Date, out var et) && et >= start).ToList();
    }
    list = list.OrderByDescending(e => e.Time).ToList();
    // Keep the wire format stable for the UI: "files" stays a comma-separated
    // string even though it is stored normalized in feedback_files.
    var entries = list.Select(e => new
    {
        date = e.Date,
        by = e.By,
        content = e.Content,
        time = e.Time,
        files = string.Join(",", e.Files ?? new List<string>()),
    });
    return Results.Ok(new { ok = true, entries });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});

// GET /api/sessions?date=2026-09-09 -> markdown content of that day's sessions (all users)
// GET /api/sessions -> list of available dates
// Any authenticated user may read (experience data is shared).
app.MapGet("/api/sessions", (string? date, HttpContext ctx) =>
{
    if (ctx.Items["username"] == null) return Results.Json(new { ok = false }, statusCode: 401);
    var mdDir = Path.Combine(dsh.ReadWorkspacePath(), "sharedata", "data", "sessions-md");
    if (!Directory.Exists(mdDir)) return Results.Ok(new { ok = true, dates = Array.Empty<string>(), content = "" });
    if (!string.IsNullOrWhiteSpace(date))
    {
        var file = Path.Combine(mdDir, $"sessions-{date}.md");
        if (!System.IO.File.Exists(file)) return Results.Ok(new { ok = true, content = "" });
        var content = System.IO.File.ReadAllText(file);
        return Results.Ok(new { ok = true, content });
    }
    var dates = Directory.GetFiles(mdDir, "sessions-*.md")
        .Select(Path.GetFileName)
        .Select(n => n![9..^3]) // extract YYYY-MM-DD from sessions-YYYY-MM-DD.md
        .OrderBy(d => d)
        .ToList();
    return Results.Ok(new { ok = true, dates });
});

// POST /api/feedback (multipart: inst, seq, content, files...) -> upsert today's entry
app.MapPost("/api/feedback", async (HttpContext ctx) =>
{
    var username = (string)ctx.Items["username"]!;
    var isAdmin = (bool)ctx.Items["isAdmin"]!;
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { ok = false, error = "expected multipart form" });
    var form = await ctx.Request.ReadFormAsync();
    var inst = form["inst"].FirstOrDefault() ?? "";
    var seqStr = form["seq"].FirstOrDefault() ?? "0";
    var content = form["content"].FirstOrDefault() ?? "";
    if (!int.TryParse(seqStr, out var seq)) seq = 0;
    var target = string.IsNullOrWhiteSpace(inst) ? username : inst;
    if (string.IsNullOrWhiteSpace(content)) return Results.Json(new { ok = false, error = "反馈内容不能为空" }, statusCode: 400);
    try
    {
        var dict = ReadFeedback(target);
        if (!dict.TryGetValue(seq, out var list)) { list = new List<FeedbackEntry>(); dict[seq] = list; }
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var entry = list.FirstOrDefault(e => e.Date == today && string.Equals(e.By, username, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            entry = new FeedbackEntry { Date = today, By = username };
            list.Add(entry);
        }
        entry.Content = content;
        entry.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        // Save uploaded files
        var ws = ResolveUserWorkspace(target);
        if (ws != null && form.Files.Count > 0)
        {
            var fbDir = System.IO.Path.Combine(ws, "TaskData", "Feedback", seq.ToString());
            Directory.CreateDirectory(fbDir);
            var savedNames = new List<string>();
            // Preserve existing files
            savedNames.AddRange(entry.Files ?? new List<string>());
            foreach (var file in form.Files)
            {
                var safeName = System.IO.Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "file_" + DateTime.Now.Ticks;
                var dest = System.IO.Path.Combine(fbDir, safeName);
                using var fs = new FileStream(dest, FileMode.Create);
                await file.CopyToAsync(fs);
                if (!savedNames.Contains(safeName)) savedNames.Add(safeName);
            }
            entry.Files = savedNames;
        }
        // keep newest first for display
        list.Sort((a, b) => string.CompareOrdinal(b.Time, a.Time));
        WriteFeedback(target, dict);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
});


// own instance's loopback port (127.0.0.1), so only ONE public port is exposed
// and instance ports never leave the machine. Launcher REST /api/* (except the
// DSH-owned paths below) is still served by this app; DSH owns /api/remote.mux
// (WebSocket), /api/session.export, and its static assets.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    var token = ctx.Request.Cookies["tt_session"];
    var user = auth.ValidateSession(token);
    // Always capture the launcher_token from the URL (even if cookie auth already
    // succeeded) so the proxy can re-inject it on DSH 303 redirects. This avoids
    // the token being stripped mid-flight, which made the first "打开" click flash
    // to the login page.
    var launcherToken = ctx.Request.Query["launcher_token"].FirstOrDefault();
    if (user == null && !string.IsNullOrEmpty(launcherToken))
        user = auth.ValidateSession(launcherToken);

    // If we only authenticated via the URL launcher_token (no session cookie yet),
    // set the tt_session cookie on this response so the SPA's own API calls
    // (fetch /api/auth/me, /api/files/*, etc.) authenticate too — otherwise e.g.
    // the /work left pane (/fm) shows the login shell because its internal fetches
    // carry no cookie.
    if (user != null && string.IsNullOrWhiteSpace(token) && !string.IsNullOrEmpty(launcherToken))
    {
        try
        {
            ctx.Response.Cookies.Append("tt_session", launcherToken, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/",
            });
        }
        catch { }
    }

    // Management page lives under /admin — never proxied. Any authenticated user
    // may visit it (they need it to change their password / open their own DSH);
    // admin-only cards/actions are gated inside the page by role.
    if (path == "/admin" || path == "/admin/" || path.StartsWith("/admin/"))
    {
        if (user == null) { ctx.Response.Redirect("/"); return; }
        // Serve the launcher control page (index.html) for /admin. Never cache it:
        // some browsers kept the old JS (the "打开" button that failed to navigate),
        // so force a fresh fetch on every load.
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
        var adminIndex = wwwroot is null ? null : Path.Combine(wwwroot, "index.html");
        if (adminIndex is null || !System.IO.File.Exists(adminIndex))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("not found");
            return;
        }
        await ctx.Response.SendFileAsync(adminIndex);
        return;
    }

    // Workspace split page (/work) and the standalone file-manager pane (/fm).
    // Both belong to the launcher (never proxied). Any authenticated user may load
    // them; the file manager is scoped per-user on the server (ResolveUserWorkspace).
    if (path == "/work" || path == "/work/" || path.StartsWith("/work/") ||
        path == "/fm" || path == "/fm/" || path.StartsWith("/fm/") ||
        path == "/tasks" || path == "/tasks/" || path.StartsWith("/tasks/"))
    {
        if (user == null) { ctx.Response.Redirect("/"); return; }
        ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
        var baseName = path.StartsWith("/fm", StringComparison.OrdinalIgnoreCase) ? "index.html"
            : path.StartsWith("/tasks", StringComparison.OrdinalIgnoreCase) ? "tasks.html"
            : "work.html";
        var file = wwwroot is null ? null : Path.Combine(wwwroot, baseName);
        if (file is null || !System.IO.File.Exists(file))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("not found");
            return;
        }
        await ctx.Response.SendFileAsync(file);
        return;
    }

    // The launcher's OWN REST endpoints stay local (whitelist). Everything under
    // /api/* NOT in this whitelist belongs to the proxied DSH instance (settings,
    // session, credentials, dynamicCordisRunner, agentPresets, modelCatalog, etc.)
    // and MUST be forwarded to the instance — otherwise the DSH client 404s on
    // its API calls and cannot initialize (shows "选择工作区"/blank).
    bool launcherOwned =
        path == "/favicon.ico" ||
        path.StartsWith("/vendor") ||
        path.StartsWith("/api/login") ||
        path.StartsWith("/api/languages") ||
        path.StartsWith("/api/logout") ||
        path.StartsWith("/api/auth/me") ||
        path.StartsWith("/api/status") ||
        path.StartsWith("/api/version") ||
        path.StartsWith("/api/logs") ||
        path.StartsWith("/api/token") ||
        path.StartsWith("/api/config") ||
        path.StartsWith("/api/workspace") ||
        path.StartsWith("/api/browse") ||
        path.StartsWith("/api/files") ||
        path.StartsWith("/api/start") ||
        path.StartsWith("/api/stop") ||
        path.StartsWith("/api/update") ||
        path.StartsWith("/api/instances") ||
        path.StartsWith("/api/members") ||
        path.StartsWith("/api/change-password") ||
        path.StartsWith("/api/admin/") ||
        path.StartsWith("/api/tasks") ||
        path.StartsWith("/api/feedback") ||
        path.StartsWith("/api/session-token");
    if (launcherOwned) { await next(); return; }

    // DSH-owned requests are proxied to the logged-in user's own instance.
    // Not logged in → show the login shell at the root.
    if (user == null)
    {
        if (path == "/" || path == "") { await next(); return; }
        ctx.Response.StatusCode = 401;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"unauthorized\"}");
        return;
    }

    // Resolve the proxy target: non-admin → their bound instance; admin → default dsh.
    try
    {
        int proxyPort;
        string? proxyToken = null;
        string? workspaceId = null;
        bool isAdmin = auth.Find(user)?.Admin == true;
        // Allow routing to a specific instance via ?inst=<id> (admin opening a
        // user's DSH through the launcher so it stays same-origin + injectable).
        // When ?inst= is present, persist it in a cookie so ALL subsequent sub-
        // requests (plugin bundles, API calls, etc.) route to the same instance.
        // Without this, DSH SPA sub-requests lose the ?inst= param and get
        // routed to the admin's default instance, causing 404s on plugin bundles.
        var reqInstance = ctx.Request.Query["inst"].FirstOrDefault()
            ?? ctx.Request.Query["instance"].FirstOrDefault();
        // Referer fallback: sub-requests (plugin bundles, API calls) created inside
        // a DSH iframe that was opened with ?inst=send a Referer of the parent
        // page, e.g. http://127.0.0.1:46001/?inst=ericliu. Parse it so routing
        // survives even if the tt_inst cookie isn't sent. This never touches the
        // upstream URL, so DSH's exact-match serveBundle still finds the bundle.
        if (string.IsNullOrEmpty(reqInstance))
        {
            var referer = ctx.Request.Headers["Referer"].ToString();
            if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            {
                var rInst = ExtractQueryParam(refUri.Query, "inst");
                if (!string.IsNullOrEmpty(rInst)) reqInstance = rInst;
            }
        }
        if (!string.IsNullOrEmpty(reqInstance))
        {
            // Persist the instance id in a cookie so ALL subsequent sub-
            // requests (plugin bundles, API calls, etc.) route to the same instance.
            ctx.Response.Cookies.Append("tt_inst", reqInstance, new CookieOptions
            {
                Path = "/",
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(30)
            });
        }
        else if (isAdmin)
        {
            var isRootNav = path == "/" || path == "";
            if (isRootNav)
            {
                // Root navigation without ?inst= → admin's own DSH.
                // Clear any stale tt_inst cookie so sub-requests don't route
                // to a previously-opened user instance.
                ctx.Response.Cookies.Delete("tt_inst");
            }
            else if (ctx.Request.Cookies.TryGetValue("tt_inst", out var savedInst) && !string.IsNullOrEmpty(savedInst))
            {
                // Sub-request (plugin bundle, API call) without ?inst= →
                // use the cookie so it routes to the same instance.
                reqInstance = savedInst;
            }
        }
        if (!string.IsNullOrEmpty(reqInstance))
        {
            var inst2 = instMgr.Get(reqInstance);
            if (inst2 == null || (!inst2.Running && !inst2.Starting))
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("实例未运行，请先在控制台启动。");
                return;
            }
            proxyPort = inst2.DshPort;
            // The instance's DSH may still be starting up. Show the "starting" spinner
            // (auto-refresh) instead of a raw proxy/connection error.
            if (!DshService.IsDshReady(proxyPort)) { await WriteDshStarting(ctx); return; }
            workspaceId = ReadWorkspaceId(inst2.DshHome);
            var t2 = inst2.TokenUrl;
            if (!string.IsNullOrEmpty(t2) && t2.IndexOf("token=", StringComparison.Ordinal) >= 0)
                proxyToken = t2.Substring(t2.IndexOf("token=", StringComparison.Ordinal) + 6);
            if (string.IsNullOrEmpty(proxyToken)) proxyToken = instMgr.GetTokenFromConfig(inst2);
        }
        else if (isAdmin)
        {
            proxyPort = dsh.DefaultPort();
            // The admin's default DSH also needs its workspace id embedded into the
            // injected auto-open script, otherwise the DSH UI stays on "选择工作区"
            // (the instance branches below already do this). Read it from the
            // launcher's own .dsh/storages/workspace.json.
            workspaceId = ReadWorkspaceId(Path.Combine(root, ".dsh"));
            // Check for token URL (may have been captured from a previous DSH
            // process or from a prior request in this session).
            var tu = dsh.TokenUrl();
            // Require BOTH: (a) the DSH web service actually responds over HTTP (not
            // merely that the TCP port is open — a stale/orphaned dsh right after a
            // reboot can hold 46000 while ours is still starting, proxying to it shows
            // "site not found"), AND (b) a FRESH token for THIS session has been
            // captured (the value persisted in launcher-token.txt is stale after
            // reboot — DSH rotates its token, so using it makes DSH answer
            // "authentication required"). Until both hold we show the spinner.
            if (string.IsNullOrEmpty(tu) || !DshService.IsDshReady(proxyPort) || !dsh.HasFreshToken())
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                var isRunning = DshService.IsDshReady(proxyPort);
                // Read language from cookie (tt_lang=zh-cn or en).
                var lang = "en";
                if (ctx.Request.Cookies.TryGetValue("tt_lang", out var lv) && !string.IsNullOrEmpty(lv))
                    lang = lv;
                var isZh = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                var msg = isRunning
                    ? (isZh ? "正在连接 DSH..." : "Connecting to DSH...")
                    : (isZh ? "DSH 正在启动中，请稍候..." : "DSH is starting up, please wait...");
                var sub = isZh
                    ? "首次打开 DSH 需要稍等片刻，加载完成后，下次即可即时打开。"
                    : "The first time you open DSH it may take a moment to start; after that it opens instantly.";
                var hint = isZh ? "页面每 3 秒自动刷新。" : "Page will auto-refresh every 3 seconds.";
                var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                    "<style>body{margin:0;display:flex;justify-content:center;align-items:center;height:100vh;font-family:system-ui,sans-serif;background:#f8f9fa}" +
                    ".box{text-align:center;color:#555}" +
                    ".spinner{width:48px;height:48px;border:5px solid #e0e0e0;border-top-color:#4a90d9;border-radius:50%;animation:spin 1s linear infinite;margin:0 auto 20px}" +
                    "@keyframes spin{to{transform:rotate(360deg)}}" +
                    "p{margin:8px 0;font-size:15px}.hint{font-size:13px;color:#999;margin-top:12px}" +
                    "</style></head><body>" +
                    "<div class=\"box\"><div class=\"spinner\"></div>" +
                    "<p>" + msg + "</p>" +
                    "<p class=\"hint\">" + sub + "</p>" +
                    "<p class=\"hint\">" + hint + "</p>" +
                    "</div><script>setTimeout(function(){location.reload()},3000)</script>" +
                    "</body></html>";
                await ctx.Response.WriteAsync(html);
                return;
            }
            if (tu.IndexOf("token=", StringComparison.Ordinal) > 0)
                proxyToken = tu.Substring(tu.IndexOf("token=", StringComparison.Ordinal) + 6);
        }
        else
        {
            var inst = instMgr.Get(user);
            if (inst == null || !inst.Running)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("实例未运行，请先在控制台启动。");
                return;
            }
            proxyPort = inst.DshPort;
            workspaceId = ReadWorkspaceId(inst.DshHome);
            var t = inst.TokenUrl;
            if (!string.IsNullOrEmpty(t) && t.IndexOf("token=", StringComparison.Ordinal) >= 0)
                proxyToken = t.Substring(t.IndexOf("token=", StringComparison.Ordinal) + 6);
            // Fallback: read token from credentials file when TokenUrl not captured
            if (string.IsNullOrEmpty(proxyToken))
            {
                var fallbackSecret = instMgr.GetTokenFromConfig(inst);
                if (!string.IsNullOrEmpty(fallbackSecret)) proxyToken = fallbackSecret;
            }
        }
        await ProxyToPort(ctx, proxyPort, path, proxyToken, root, launcherToken, workspaceId);
    }
    catch (Exception ex)
    {
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsync("proxy error: " + ex.Message); }
    }
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

// Open the launcher control page in the default browser once the server is up,
// and auto-start the dsh web process so it is ready to use immediately.
_ = Task.Run(async () =>
{
    await Task.Delay(500);
    // Re-apply all DSH package patches on every launcher start, so a restart
    // after a DSH upgrade keeps every feature working. The admin default dsh
    // keeps its settings visible; per-user instances hide it.
    DshPatcher.ApplyAll(root, hideSettings: false, m => Console.WriteLine("[patch] " + m));
    // Start the dsh web process on the configured port (background; the
    // control page's "Open DSH Web" button waits for its token on demand).
    // Retry up to 3 times with delays since DSH may need a moment after
    // patching to be ready.
    var cfgPort = dsh.DefaultPort();
    for (var attempt = 1; attempt <= 3; attempt++)
    {
        // "Running" must mean the HTTP service actually answers — a live process can
        // still be loading or stuck without ever binding the port.
        if (dsh.IsRunning && DshService.IsDshReady(cfgPort)) break;
        Console.WriteLine($"[auto-start] Attempt {attempt}/3 to start DSH on port {cfgPort}...");
        try { dsh.Start(cfgPort); } catch (Exception ex) { Console.WriteLine($"[auto-start] DSH start failed (attempt {attempt}): {ex.Message}"); }
        // Give DSH time to bind the port and answer HTTP before declaring success.
        for (var w = 0; w < 15; w++)
        {
            await Task.Delay(1000);
            if (dsh.IsRunning && DshService.IsDshReady(cfgPort)) break;
        }
    }
    if (dsh.IsRunning && DshService.IsDshReady(cfgPort))
        Console.WriteLine($"[auto-start] DSH is running on port {cfgPort}");
    else
        Console.WriteLine($"[auto-start] DSH failed to become ready after 3 attempts on port {cfgPort}");
    // Auto-restore any instance persisted as running so its dsh is actually up
    // (otherwise the card shows "运行中" but the port is dead and "打开" fails).
    foreach (var inst in instMgr.List())
        if (inst.Running)
        {
            try { instMgr.Start(inst); } catch { }
        }
    // Background: periodically mirror dsh sessions into docs/ (shared experience).
    dsh.StartSessionBackup();
    if (Environment.GetEnvironmentVariable("DSH_OPEN_BROWSER") != "0")
    {
        await Task.Delay(1200);
        OpenBrowser($"http://127.0.0.1:{launcherPort}");
    }
});

app.Run();

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
    catch { /* ignore */ }
}

// Reverse-proxy a request to an instance's loopback port, preserving method,
// path/query, headers, body, and upgrading WebSocket connections.
static async Task ProxyToPort(HttpContext ctx, int port, string path, string? token = null, string? root = null, string? launcherToken = null, string? workspaceId = null)
{
    try
    {
        var target = $"http://127.0.0.1:{port}";
        var basePath = path == "/" || path == "" ? "/" : path;

        // Strip our own launcher_token from the query string so it doesn't leak
        // to the upstream DSH instance.
        var rawQs = ctx.Request.QueryString.Value ?? "";
        var qs = System.Text.RegularExpressions.Regex.Replace(rawQs, @"[?&]launcher_token=[^&]*", "");
        if (qs.StartsWith("?")) qs = qs.Substring(1);
        if (qs.StartsWith("&")) qs = qs.Substring(1);

        // Build the upstream URL. On ANY root navigation we attach the (fresh) DSH
        // auth token so DSH re-issues a CURRENT dsh-auth cookie to the browser —
        // including the admin "Open DSH" case whose URL carries ?launcher_token.
        // Without the token, DSH never issues dsh-auth and the first page load
        // shows "dsh web authentication required" (only a manual refresh, once the
        // browser has obtained dsh-auth, succeeds). Sub-requests (isRootNav=false)
        // are not injected; they rely on the cookie obtained from the root load.
        // The token-driven 303 loop is broken by the redirectToken rewrite below.
        var hasLauncherToken = ctx.Request.Query.ContainsKey("launcher_token");
        var isRootNav = path == "/" || path == "";
        // Inject the DSH auth token ONLY on a root navigation when the browser does
        // NOT yet have a dsh-auth cookie. This seeds DSH's auth cookie on first load
        // (so we never see "authentication required"). Once the browser holds
        // dsh-auth we must NOT inject again — otherwise DSH answers 303 -> "/" every
        // time and the browser hits an infinite redirect ("page isn't redirecting
        // properly"). Sub-requests rely on the cookie and never inject.
        var hasDshAuthCookie = ctx.Request.Headers["Cookie"].ToString().Contains("dsh-auth");
        var injectTokenForAuth = !string.IsNullOrEmpty(token) &&
            isRootNav && !hasDshAuthCookie &&
            qs.IndexOf("token=", StringComparison.Ordinal) < 0;
        string BuildUpstream(bool inject)
        {
            var u = target + basePath;
            if (inject)
            {
                var body = qs.TrimStart('?');
                u += (body.Length == 0 ? "?" : "?") + (body.Length == 0 ? "" : body + "&") + "token=" + token;
            }
            else
            {
                u += (string.IsNullOrEmpty(qs) ? "" : "?" + qs);
            }
            return u;
        }
        var upstream = BuildUpstream(injectTokenForAuth);

        // WebSocket upgrade (DSH /api/remote.mux). Forward the auth cookie(s) and/or
        // the instance token so the upstream DSH accepts the connection; without the
        // dsh-auth cookie the DSH returns 401 and the SPA cannot initialize.
        if (ctx.WebSockets.IsWebSocketRequest)
        {
            using var wsClient = new ClientWebSocket();
            var cleanQs = System.Text.RegularExpressions.Regex.Replace(rawQs, @"[?&]launcher_token=[^&]*", "");
            if (cleanQs.StartsWith("?") || cleanQs.StartsWith("&")) cleanQs = cleanQs.Substring(1);
            var targetWs = target.Replace("http://", "ws://") + basePath + (string.IsNullOrEmpty(cleanQs) ? "" : "?" + cleanQs);
            var cookieStr = ctx.Request.Headers["Cookie"].ToString();
            if (!string.IsNullOrWhiteSpace(cookieStr))
                wsClient.Options.SetRequestHeader("Cookie", cookieStr);
            // Forward the ORIGINAL Host header so the upstream DSH computes the SAME
            // cookie authority it used when issuing dsh-auth (via the launcher Host).
            // ClientWebSocket otherwise sends its own authority (DSH's loopback port),
            // and DSH then looks for the cookie under a DIFFERENT name -> 401, and the
            // SPA's workspace feed (mux) never loads.
            var origHost = ctx.Request.Headers["Host"].ToString();
            if (!string.IsNullOrWhiteSpace(origHost))
                wsClient.Options.SetRequestHeader("Host", origHost);
            // Temporarily connected; retries and upgrade errors are handled below.
            await wsClient.ConnectAsync(new Uri(targetWs), ctx.RequestAborted);
            using var serverWs = await ctx.WebSockets.AcceptWebSocketAsync();
            var cts = new CancellationTokenSource();
            Task pumpToServer = Pump(wsClient, serverWs, cts.Token);
            Task pumpToClient = Pump(serverWs, wsClient, cts.Token);
            await Task.WhenAny(pumpToServer, pumpToClient);
            cts.Cancel();
            return;
        }

        // Send (and, for a root navigation that DSH rejects, retry once with the
        // instance token attached). The retry recovers from a stale dsh-auth cookie:
        // DSH accepts the token, re-issues a fresh cookie via a 303, and the browser
        // follows to the rewritten launcher_token URL.
        byte[] bodyBytesRaw = Array.Empty<byte>();
        if (ctx.Request.Body != null && ctx.Request.ContentLength > 0)
        {
            var buf = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buf);
            bodyBytesRaw = buf.ToArray();
        }

        HttpResponseMessage resp = await SendAsync(upstream);
        if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
        {
            if (isRootNav && !string.IsNullOrEmpty(token) && !injectTokenForAuth && qs.IndexOf("token=", StringComparison.Ordinal) < 0)
            {
                resp.Dispose();
                resp = await SendAsync(BuildUpstream(true));
            }
        }

        async Task<HttpResponseMessage> SendAsync(string url)
        {
            using var h2 = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            using var inv = new HttpMessageInvoker(h2);
            using var r2 = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), url);
            if (bodyBytesRaw.Length > 0)
                r2.Content = new ByteArrayContent(bodyBytesRaw);
            foreach (var h in ctx.Request.Headers)
            {
                if (h.Key.StartsWith("Connection") || h.Key.StartsWith("Upgrade")) continue;
                // Forward the original Host so the upstream DSH sees a SAME-ORIGIN
                // request: DSH's isTrustedApiRequest compares `Origin.host` with the
                // `Host` header, and dropping Host made every cross-port API call
                // reject as "forbidden" (breaking session.create from the browser).
                // It also keeps the dsh-auth cookie authority consistent (the cookie
                // name is derived from the Host).
                if (h.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
                {
                    var hv = h.Value.ToString();
                    try { r2.Headers.Host = hv; } catch { r2.Headers.TryAddWithoutValidation("Host", (IEnumerable<string>)h.Value); }
                    continue;
                }
                if (!r2.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value))
                    r2.Content?.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value);
            }
            return await inv.SendAsync(r2, ctx.RequestAborted);
        }

        ctx.Response.StatusCode = (int)resp.StatusCode;
        // DSH returns 401 on a root navigation when its auth isn't established yet
        // (right after a reboot) but succeeds on the next load once the browser has
        // dsh-auth. Instead of showing DSH's raw "authentication required" message,
        // serve a friendly launcher spinner page that auto-reloads until DSH accepts
        // — the user never sees the error. Refresh: 1 is a browser-native backstop.
        if (isRootNav && (int)resp.StatusCode == 401)
        {
            var aUn = System.Text.Encoding.UTF8;
            var aCode = "en"; string? aLv = null;
            if (ctx.Request.Cookies.TryGetValue("tt_lang", out aLv) && !string.IsNullOrEmpty(aLv))
                aCode = aLv;
            var aZh = aCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var aText = aZh ? "正在加载工作区&hellip;" : "Loading workspaces&hellip;";
            var authSpinner = "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<style>body{margin:0;display:flex;justify-content:center;align-items:center;height:100vh;font-family:system-ui,sans-serif;background:#fff}" +
                ".box{text-align:center;color:#888}.sp{width:42px;height:42px;border:4px solid #e5e7eb;border-top-color:#4a90d9;border-radius:50%;animation:spin 1s linear infinite;margin:0 auto 16px}" +
                "@keyframes spin{to{transform:rotate(360deg)}}p{margin:4px 0;font-size:14px}" +
                "</style></head><body><div class=\"box\"><div class=\"sp\"></div><p>" + aText + "</p>" +
                "<script>setTimeout(function(){location.reload()},1500)</script></div></body></html>";
            var spBytes = System.Text.Encoding.UTF8.GetBytes(authSpinner);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength = spBytes.Length;
            ctx.Response.Headers.Remove("Content-Encoding");
            ctx.Response.Headers["Refresh"] = "1";
            await ctx.Response.Body.WriteAsync(spBytes, ctx.RequestAborted);
            return;
        }
        // Copy headers but never Transfer-Encoding / Content-Length — let ASP.NET
        // frame the body itself (CopyToAsync re-chunks correctly).

        // Collect Set-Cookie headers to rewrite after copying other headers.
        // DSH sets SameSite=Strict on its dsh-auth cookie; browsers may not send
        // Strict cookies during redirect chains. We rewrite them to SameSite=Lax
        // via ASP.NET Core's cookie API (which properly controls the attribute).
        var dshCookies = new List<(string Name, string Value, string Attributes)>();
        string rawSetCookieHeader = "";

        foreach (var h in resp.Headers)
        {
            if (IsHopByHop(h.Key)) continue;
            if (string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                rawSetCookieHeader = string.Join(", ", h.Value);
                // Parse each Set-Cookie value
                foreach (var sc in h.Value)
                    ParseSetCookie(sc, dshCookies);
                continue;  // Don't copy raw Set-Cookie — we handle it below
            }
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }
        foreach (var h in resp.Content.Headers)
        {
            if (IsHopByHop(h.Key) || string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                rawSetCookieHeader = string.Join(", ", h.Value);
                foreach (var sc in h.Value)
                    ParseSetCookie(sc, dshCookies);
                continue;
            }
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }

        // Re-issue each DSH cookie with SameSite=Lax via ASP.NET Core's cookie
        // infrastructure — this bypasses ASP.NET Core's Set-Cookie interception
        // and ensures the browser receives SameSite=Lax.
        foreach (var (name, value, _) in dshCookies)
        {
            ctx.Response.Cookies.Append(name, value, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromDays(30),
            });
        }

        // Rewrite 303/302 Location to carry the launcher_token so the browser's
        // redirect still authenticates (Firefox doesn't send tt_session cookie on
        // the redirect either). When the incoming request had no launcher_token
        // (a fresh root load that we just re-authed with the token), use the
        // attached session cookie token as the launcher_token so the redirect's
        // target can authenticate again — this breaks the token→303→/→token loop.
        string? redirectToken = launcherToken;
        if (string.IsNullOrEmpty(redirectToken))
            redirectToken = ctx.Request.Cookies["tt_session"];   // valid launcher session token
        if (!string.IsNullOrEmpty(redirectToken) && (resp.StatusCode == System.Net.HttpStatusCode.Redirect || resp.StatusCode == System.Net.HttpStatusCode.SeeOther) && ctx.Response.Headers.ContainsKey("Location"))
        {
            var loc = ctx.Response.Headers["Location"].ToString();
            // Keep the ?inst= routing param through the DSH 303 so we stay on the
            // same instance after the token->redirect dance (admin opening a user).
            var reqInst = ctx.Request.Query["inst"].FirstOrDefault();
            if (!string.IsNullOrEmpty(reqInst))
            {
                var pre = loc.Contains('?') ? '&' : '?';
                loc += pre + "inst=" + Uri.EscapeDataString(reqInst);
            }
            if (loc.StartsWith("/") && !loc.Contains("launcher_token="))
            {
                var sep = loc.Contains('?') ? '&' : '?';
                ctx.Response.Headers["Location"] = loc + sep + "launcher_token=" + Uri.EscapeDataString(redirectToken);
            }
        }

        ctx.Response.Headers["x-proxied-to"] = target;

        // --- HTML response interception: inject auto-open workspace script ---
        var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
        var enc = string.Join(" ", resp.Content.Headers.ContentEncoding);
        if (ct.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);

            // Decompress if gzip/deflate so we can modify the HTML text.
            if (enc.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream(bodyBytes);
                using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                gz.CopyTo(outMs);
                bodyBytes = outMs.ToArray();
            }
            else if (enc.Contains("deflate", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream(bodyBytes);
                using var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                def.CopyTo(outMs);
                bodyBytes = outMs.ToArray();
            }

            var html = System.Text.Encoding.UTF8.GetString(bodyBytes);

            // Inject auto-open script before </head> (or </body> as fallback).
            // Auto-select a WORKSPACE-ATTACHED session so the chat input is enabled.
            // We call DSH's OWN session.create with the workspaceId (embedded below) so
            // the session is fully-initialized (projcache-backed) and binds its
            // workspace chip -> composer not inert. A hand-seeded blank session never
            // resolves the workspace, so it stays inactive. The workspaceId comes from
            // the instance's workspace.json (read server-side). __WSID__ is replaced
            // at runtime (workspaceId may be null -> empty string).
            var wsIdJs = workspaceId == null ? "" : workspaceId;
            const string autoOpenScript = @"
<script>
(function(){
  var MAX_RETRIES = 100;
  var tries = 0;
  var WORKSPACE_ID = '__WSID__';

  function rpc(method, args) {
    return fetch('/api/' + method, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ type: 'client-request', rpcId: crypto.randomUUID(), method: method, payload: { args: args } })
    }).then(function(r){ return r.json(); });
  }

    // Apply the launcher-selected language to DSH via its settings RPC.
  // DSH locale ids: 'en', 'zh'. Map launcher codes (en, zh-cn) accordingly.
  function applyLocale() {
    try {
      var m = document.cookie.match(/(?:^|;\s*)tt_lang=([^;]+)/);
      if (!m) return;
      var code = decodeURIComponent(m[1]).toLowerCase();
      var dshLocale = (code === 'zh' || code === 'zh-cn' || code === 'cn') ? 'zh' : (code === 'en' || code === 'en-us') ? 'en' : code;
      if (!dshLocale) return;
      // Try settings/update; ignore failures (best effort).
      rpc('settings/update', { ns: 'locale', patch: { preference: dshLocale } }).then(function(){ /* ok */ }).catch(function(){});
    } catch(e) { }
  }

  function doOpen() {
    applyLocale();
    if (!WORKSPACE_ID) return; // no workspace configured; nothing to auto-open
    var marker = 'dsh.launcher.autoopened';
    var stored;
    try { stored = JSON.parse(localStorage.getItem('dsh.sessions.current') || '{}'); } catch(e) { stored = {}; }


    // If a session is already current and the composer is enabled, leave the app alone.
    var editor = document.querySelector('[contenteditable=""true""], [role=""textbox""], textarea');
    if (stored.sessionId && editor && !editor.disabled) return;

    // Create a NATIVE (fully-initialized) workspace session via DSH's own RPC.
    rpc('session/create', { request: { workspaceId: WORKSPACE_ID } }).then(function(cr){
      var sessionId = cr && cr.result && cr.result.ok && cr.result.value ? cr.result.value.sessionId : null;
      if (!sessionId) { console.warn('[launcher] session/create failed:', JSON.stringify(cr)); return null; }
      return { sessionId: sessionId };
    }).then(function(res){
      if (!res || !res.sessionId) return;
      if (stored.sessionId === res.sessionId) return;
      localStorage.setItem('dsh.sessions.current', JSON.stringify({ sessionId: res.sessionId }));
      if (localStorage.getItem(marker) === res.sessionId) return; // already pivoted once
      localStorage.setItem(marker, res.sessionId);
      location.reload();
    }).catch(function(err){
      console.warn('[launcher] auto-open error:', err);
    });
  }

  var iv = setInterval(function() {
    tries++;
    if (tries > MAX_RETRIES) { clearInterval(iv); return; }
    if (window.__ModuleLoader__) {
      clearInterval(iv);
      setTimeout(doOpen, 900);
    }
  }, 300);
})();
</script>";

            // Inject before </head> if present, otherwise before </body>.
            // Only inject the auto-open workspace script. We deliberately do NOT
            // patch <script> src URLs to carry ?inst=: DSH's serveBundle does an
            // exact string match on pathname+search, so appending &inst=<id> would
            // break the combo-URL lookup (404). Sub-request routing is instead
            // handled server-side via the tt_inst cookie (set below) plus a
            // Referer fallback (see ProxyToPort caller).
            // Runtime watch: DSH sometimes shows "authentication required" (rendered
            // by the SPA after an auth API fails on the first load after a reboot),
            // but succeeds on the next reload once the browser has dsh-auth. Watching
            // for that text and auto-reloading removes the manual-refresh step.
            const string authReloadScript = @"
<script>
(function(){
  var RELOADED=false;
  function scan(){
    if (RELOADED) return;
    var t = document.body ? document.body.innerText : '';
    if (t && (t.indexOf('authentication required')>=0 || t.indexOf('Authentication Required')>=0 || t.indexOf('dsh web authentication')>=0)) {
      RELOADED=true;
      setTimeout(function(){ location.reload(); }, 800);
    }
  }
  scan(); setInterval(scan, 500);
})();
</script>";
            // Accept a file dropped from the file-manager pane into the DSH composer:
            // read the transferred path/name and insert it at the caret. We listen on
            // the DSH document (same-origin via the launcher proxy) and insert into
            // whichever contenteditable / text control is focused.
            const string fmDropScript = @"
<script>
(function(){
  function isEditable(el){
    if (!el) return false;
    if (el.isContentEditable) return true;
    if (el.tagName==='TEXTAREA') return true;
    if (el.tagName==='INPUT' && /text|search/i.test(el.type||'')) return true;
    // detect common rich-text editor root markers
    if (el.getAttribute && (el.getAttribute('contenteditable')==='true')) return true;
    if (el.hasAttribute && (el.hasAttribute('data-lexical-editor') || el.hasAttribute('data-slate-editor') || el.hasAttribute('data-placeholder'))) return true;
    return false;
  }
  function findComposer(){
    var act = document.activeElement;
    if (isEditable(act)) return act;
    // search deep for the composer editor root (often a nested contenteditable)
    var nodes = document.querySelectorAll('[contenteditable=""true""], [contenteditable], [data-lexical-editor], [data-slate-editor], [data-placeholder], [role=""textbox""], textarea, input[type=""text""]');
    // prefer a non-empty contenteditable near the bottom of the window (composer)
    var best = null;
    for (var i=0;i<nodes.length;i++){ if (isEditable(nodes[i])) { best = nodes[i]; } }
    if (best) return best;
    // last resort: any focused-editable descendant
    for (var i=0;i<nodes.length;i++){ if (nodes[i].isContentEditable) return nodes[i]; }
    return nodes[0] || null;
  }
  function insertAtCaret(el, text){
    try {
      if (el.isContentEditable){
        el.focus();
        // Use the well-supported editing command so ProseMirror-style editors
        // (DSH's composer) recognize the inserted text, then fire input/change.
        var ok = false;
        try { ok = document.execCommand('insertText', false, text); } catch(err){ ok = false; }
        if (!ok){
          var sel = window.getSelection();
          if (!sel.rangeCount){ sel = window.getSelection(); }
          var r = sel.rangeCount ? sel.getRangeAt(0) : document.createRange();
          r.collapse(false);
          var t = document.createTextNode(text);
          r.insertNode(t);
        }
        el.dispatchEvent(new Event('input', {bubbles:true}));
        el.dispatchEvent(new Event('change', {bubbles:true}));
        try { document.dispatchEvent(new Event('input', {bubbles:true})); } catch(err){}
      } else {
        var s = el.selectionStart, e = el.selectionEnd;
        el.value = el.value.slice(0,s) + text + el.value.slice(e);
        el.selectionStart = el.selectionEnd = s + text.length;
        el.focus();
        el.dispatchEvent(new Event('input', {bubbles:true}));
        el.dispatchEvent(new Event('change', {bubbles:true}));
      }
    } catch(err){}
  }
  document.addEventListener('drop', function(ev){
    var p='', n='';
    try { p = ev.dataTransfer.getData('text/plain') || ''; } catch(err){}
    try { n = ev.dataTransfer.getData('application/x-tt-fm-name') || ''; } catch(err){}
    if (!p && !n) return;
    ev.preventDefault(); ev.stopPropagation();
    var comp = findComposer();
    if (!comp) return;
    // Drag a file: transfer its workspace-relative path (excluding the workspace
    // dir name), falling back to the bare file name for task-drag drops.
    var txt = p || n;
    insertAtCaret(comp, txt);
  }, true);
  document.addEventListener('dragover', function(ev){ ev.preventDefault(); }, true);
})();
</script>";
            var autoOpenInjected = autoOpenScript.Replace("__WSID__", wsIdJs ?? "") + authReloadScript + fmDropScript;
            if (html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase) >= 0)
                html = html.Replace("</head>", autoOpenInjected + "</head>", StringComparison.OrdinalIgnoreCase);
            else if (html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase) >= 0)
                html = html.Replace("</body>", autoOpenInjected + "</body>", StringComparison.OrdinalIgnoreCase);

            var modifiedBytes = System.Text.Encoding.UTF8.GetBytes(html);
            // Strip Content-Encoding since we decompressed and send uncompressed.
            ctx.Response.ContentLength = modifiedBytes.Length;
            ctx.Response.Headers.Remove("Content-Encoding");
            await ctx.Response.Body.WriteAsync(modifiedBytes, ctx.RequestAborted);
        }
        else
        {
            await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }
    catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException)
    {
        // best effort
    }
    catch (Exception ex)
    {
        if (!ctx.Response.HasStarted)
        {
            // Connection failures (target DSH down/still starting) → friendly spinner.
            if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.Sockets.SocketException)
            {
                await WriteDshStarting(ctx);
                return;
            }
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync($"proxied request failed: {ex.Message}");
        }
    }
}

static bool IsHopByHop(string name)
{
    switch (name.ToLowerInvariant())
    {
        case "transfer-encoding":
        case "connection":
        case "keep-alive":
        case "upgrade":
        case "proxy-connection":
        case "trailer":
            return true;
        default:
            return false;
    }
}

static async Task Pump(System.Net.WebSockets.WebSocket from, System.Net.WebSockets.WebSocket to, CancellationToken ct)
{
    var buffer = new byte[16 * 1024];
    try
    {
        while (!ct.IsCancellationRequested && from.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var result = await from.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
            {
                await to.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "close", ct);
                return;
            }
            // Send ONLY the bytes actually received (result.Count); sending the whole
            // 16 KiB buffer would corrupt the upstream frame and the DSH would never
            // answer (workspace list stays empty → "选择工作区").
            await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }
    catch { /* pump ended */ }
}

static string FindRoot(string baseDir)
{
    var dir = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    for (var i = 0; i < 12; i++)
    {
        // The true project root contains both appsettings.json and the node/ dir.
        if (File.Exists(Path.Combine(dir, "appsettings.json")) && Directory.Exists(Path.Combine(dir, "node")))
            return dir;
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
    return baseDir;
}

// Read the first workspaceId from an instance's storages/workspace.json.
// Used to embed the workspace id into the auto-open script so it can create a
// native (workspace-attached) session via DSH's own session.create RPC.
static string? ReadWorkspaceId(string? dshHome)
{
    try
    {
        if (string.IsNullOrWhiteSpace(dshHome)) return null;
        var wsFile = Path.Combine(dshHome, "storages", "workspace.json");
        if (!File.Exists(wsFile)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(wsFile));
        if (doc.RootElement.TryGetProperty("tables", out var tables) &&
            tables.TryGetProperty("workspaces", out var wsTable) &&
            wsTable.EnumerateObject().FirstOrDefault().Value.TryGetProperty("path", out _))
            return wsTable.EnumerateObject().FirstOrDefault().Name;
    }
    catch { /* best effort */ }
    return null;
}

// Parse a raw Set-Cookie header value into (name, value, attributes).
// "dsh-auth-xxx=v1.abc; Max-Age=2592000; Path=/; HttpOnly; SameSite=Strict"
// → ("dsh-auth-xxx", "v1.abc", "Max-Age=2592000; Path=/; HttpOnly; SameSite=Strict")
static void ParseSetCookie(string raw, List<(string Name, string Value, string Attributes)> results)
{
    if (string.IsNullOrWhiteSpace(raw)) return;
    var parts = raw.Split(';', 2, StringSplitOptions.TrimEntries);
    var nameValue = parts[0];
    var attrs = parts.Length > 1 ? parts[1] : "";
    var eqIdx = nameValue.IndexOf('=');
    if (eqIdx < 0) return;
    var name = nameValue.Substring(0, eqIdx).Trim();
    var value = nameValue.Substring(eqIdx + 1).Trim();
    results.Add((name, value, attrs));
}

// Pull a single query parameter out of a raw "?a=1&b=2" string. Avoids a
// dependency on System.Web.HttpUtility (not guaranteed in .NET Core).
static string? ExtractQueryParam(string query, string key)
{
    if (string.IsNullOrEmpty(query)) return null;
    var q = query.StartsWith('?') ? query.Substring(1) : query;
    foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        if (string.Equals(pair, key, StringComparison.OrdinalIgnoreCase)) return "";
        var eq = pair.IndexOf('=');
        if (eq > 0 && string.Equals(pair.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
        {
            var val = pair.Substring(eq + 1);
            return Uri.UnescapeDataString(val.Replace('+', ' '));
        }
    }
    return null;
}

record StartRequest(int? Port);
record ConfigRequest(string? Url, string? ExternalUrl, string? DefaultLanguage);
record WorkspaceRequest(string? Workspace);
record LoginRequest(string? Username, string? Password);
record CreateInstanceRequest(string? Id, string? Name, string? Workspace, string? Password);
record ChangePasswordRequest(string? OldPassword, string? NewPassword);
record ResetPasswordRequest(string? Username, string? NewPassword);
record FileOpRequest(string? Path = null, string? TargetName = null);
record FileRenameRequest(string? Path = null, string? NewPath = null);
record FileMoveRequest(string? Path = null, string? ToPath = null, string? NewName = null);
record FileEntryInfo(string Name, string Type, long Size, DateTime Mtime);
// A configured UI language: display label + the code actually applied.
public record LanguageItem(string Label, string Code);

record TaskRecord
{
    public int Seq { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Content { get; set; }
    public string? Status { get; set; }
    public string? AssignedAt { get; set; }
    public string? CreatedBy { get; set; }
    public List<string> Files { get; set; } = new();
}

record TaskUpsertRequest(string? Inst, int Seq, string? Name, string? Type, string? Content, string? Status, string? AssignedAt, List<string>? Files);

record FeedbackEntry
{
    public string? Date { get; set; }
    public string? By { get; set; }
    public string? Content { get; set; }
    public string? Time { get; set; }
    public List<string> Files { get; set; } = new(); // feedback attachment filenames
}

record FeedbackRequest(string? Inst, int Seq, string? Content);

// In-process cache for task & feedback data. Loads once (lazy), mutates in memory,
// and flushes dirty entries back to SQLite on a short debounce. The launcher is a
// single process so a single cache is shared safely (guarded by a lock).
class TaskDataCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<TaskRecord>> _tasks = new();
    private readonly Dictionary<string, Dictionary<int, List<FeedbackEntry>>> _fb = new();
    private readonly HashSet<string> _dirtyTasks = new();
    private readonly HashSet<string> _dirtyFb = new();

    public List<TaskRecord> GetTasks(string instId, Func<List<TaskRecord>> loader)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(instId, out var list)) return list;
            list = (loader?.Invoke() ?? new List<TaskRecord>());
            _tasks[instId] = list;
            return list;
        }
    }

    public void SetTasks(string instId, List<TaskRecord> tasks)
    {
        lock (_lock)
        {
            _tasks[instId] = tasks;
            _dirtyTasks.Add(instId);
        }
    }

    public Dictionary<int, List<FeedbackEntry>> GetFeedback(string instId, Func<Dictionary<int, List<FeedbackEntry>>> loader)
    {
        lock (_lock)
        {
            if (_fb.TryGetValue(instId, out var dict)) return dict;
            dict = (loader?.Invoke() ?? new Dictionary<int, List<FeedbackEntry>>());
            _fb[instId] = dict;
            return dict;
        }
    }

    public void SetFeedback(string instId, Dictionary<int, List<FeedbackEntry>> dict)
    {
        lock (_lock)
        {
            _fb[instId] = dict;
            _dirtyFb.Add(instId);
        }
    }

    public void Flush(Action<string, List<TaskRecord>> writeTasks, Action<string, Dictionary<int, List<FeedbackEntry>>> writeFb)
    {
        List<(string Id, List<TaskRecord> List)> toWriteTasks;
        List<(string Id, Dictionary<int, List<FeedbackEntry>> Dict)> toWriteFb;
        lock (_lock)
        {
            toWriteTasks = _dirtyTasks.Select(id => (id, _tasks[id])).ToList();
            toWriteFb = _dirtyFb.Select(id => (id, _fb[id])).ToList();
            _dirtyTasks.Clear();
            _dirtyFb.Clear();
        }
        foreach (var (id, list) in toWriteTasks) writeTasks(id, list);
        foreach (var (id, dict) in toWriteFb) writeFb(id, dict);
    }
}

static class TaskCleanupHolder
{
    public static System.Threading.Timer? Timer;
}

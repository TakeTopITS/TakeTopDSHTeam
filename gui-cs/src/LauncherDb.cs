// TakeTopDshTeam 鈥?multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 娉伴《鎷撻紟淇℃伅绉戞妧锛堜笂娴凤級鏈夐檺鍏徃
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
// This software is the intellectual property of 娉伴《鎷撻紟淇℃伅绉戞妧锛堜笂娴凤級鏈夐檺鍏徃
// (TakeTop Information Technology (Shanghai) Co., Ltd.). All rights reserved.
// A commercial license is also available; see LICENSE-COMMERCIAL.md.

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TakeTopDshLauncher;

// A persisted launcher account (mirrors AuthService.User minus derived state).
public sealed class LauncherUserRow
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = 100000;
    public bool Admin { get; set; }
    public string InstanceId { get; set; } = "";
}

// A persisted instance definition (mirrors InstanceManager.Instance's stored fields).
public sealed class LauncherInstanceRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int DshPort { get; set; }
    public string Workspace { get; set; } = "";
    public string? OsPassword { get; set; }
    public string TokenUrl { get; set; } = "";
    public bool Running { get; set; }
}

// The single business database for the whole deployment (the SQLite form of a
// "one PostgreSQL database"): users, instances and every member's tasks/feedback
// live in ONE file, distinguished by the `owner` column on the task tables.
//
// Location: <admin workspace>\database\taketopDSHTeam.db (falls back to
// <install>\database\... when no workspace is configured). The launcher is the
// ONLY process that opens this file; every member accesses it through the
// launcher HTTP API, never by opening the file directly. That keeps a single
// writer (safe for SQLite) and lets cross-member statistics be one SQL query,
// while staying portable and zero-install.
public static class LauncherDb
{
    public const string DbFileName = "taketopDSHTeam.db";

    // <workspace>/database/taketopDSHTeam.db, or <install>/database/... when the
    // admin has not configured a workspace yet.
    public static string PathFor(string root)
    {
        var ws = ReadWorkspacePath(root);
        var baseDir = string.IsNullOrWhiteSpace(ws) ? root : ws;
        return System.IO.Path.Combine(baseDir, "database", DbFileName);
    }

    private     static string ReadWorkspacePath(string root)
    {
        return WorkspaceFrom(System.IO.Path.Combine(root, "config", "launcher.local.json"))
            ?? WorkspaceFrom(System.IO.Path.Combine(root, "appsettings.json"))
            ?? DshService.DefaultWorkspacePath(root);
    }

    // Read a non-empty DshWeb.WorkspacePath from a config file, else null. The
    // gitignored launcher.local.json takes precedence over appsettings.json, so the
    // DB path follows the workspace the admin set in the UI.
    static string? WorkspaceFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("DshWeb", out var web) &&
                web.TryGetProperty("WorkspacePath", out var ws) &&
                ws.ValueKind == JsonValueKind.String)
            {
                var v = ws.GetString();
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        }
        catch { }
        return null;
    }

    private static string ConnString(string dbPath, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        // Pooling off for the one-shot legacy reads so the file handle is released
        // immediately and the file can be renamed aside right after import.
        Pooling = !readOnly,
    }.ToString();

    private static void Exec(SqliteConnection conn, string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
    }

    private static SqliteConnection Open(string dbPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection(ConnString(dbPath, false));
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA synchronous=NORMAL;");
        Exec(conn, "PRAGMA busy_timeout=5000;");
        EnsureSchema(conn);
        return conn;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var conn = new SqliteConnection(ConnString(dbPath, true));
        conn.Open();
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        Exec(conn, @"
CREATE TABLE IF NOT EXISTS users(
  username      TEXT PRIMARY KEY,
  password_hash TEXT NOT NULL DEFAULT '',
  salt          TEXT NOT NULL DEFAULT '',
  iterations    INTEGER NOT NULL DEFAULT 100000,
  admin         INTEGER NOT NULL DEFAULT 0,
  instance_id   TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS instances(
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL DEFAULT '',
  dsh_port    INTEGER NOT NULL DEFAULT 0,
  workspace   TEXT NOT NULL DEFAULT '',
  os_password TEXT,
  token_url   TEXT NOT NULL DEFAULT '',
  running     INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS tasks(
  owner       TEXT NOT NULL,
  seq         INTEGER NOT NULL,
  name        TEXT NOT NULL DEFAULT '',
  type        TEXT NOT NULL DEFAULT '',
  content     TEXT NOT NULL DEFAULT '',
  status      TEXT NOT NULL DEFAULT 'pending',
  assigned_at TEXT NOT NULL DEFAULT '',
  created_by  TEXT NOT NULL DEFAULT '',
  parent_task INTEGER NOT NULL DEFAULT 0,
  parent_owner TEXT NOT NULL DEFAULT '',
  PRIMARY KEY(owner, seq)
);
CREATE TABLE IF NOT EXISTS task_files(
  owner TEXT NOT NULL,
  seq   INTEGER NOT NULL,
  idx   INTEGER NOT NULL,
  rel   TEXT NOT NULL,
  PRIMARY KEY(owner, seq, idx)
);
CREATE TABLE IF NOT EXISTS feedback(
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  owner   TEXT NOT NULL,
  seq     INTEGER NOT NULL,
  date    TEXT NOT NULL DEFAULT '',
  by_user TEXT NOT NULL DEFAULT '',
  content TEXT NOT NULL DEFAULT '',
  time    TEXT NOT NULL DEFAULT '',
  files   TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_feedback_owner ON feedback(owner, seq);
CREATE TABLE IF NOT EXISTS feedback_files(
  fid  INTEGER NOT NULL,
  idx  INTEGER NOT NULL,
  name TEXT NOT NULL,
  PRIMARY KEY(fid, idx)
);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL DEFAULT '');
");
        // Migrations for databases created before the column existed. Each is
        // guarded by a pragma_table_info check so it is safe to run every open.
        try
        {
            if (Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name='parent_task';") == 0)
                Exec(conn, "ALTER TABLE tasks ADD COLUMN parent_task INTEGER NOT NULL DEFAULT 0;");
            if (Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name='parent_owner';") == 0)
                Exec(conn, "ALTER TABLE tasks ADD COLUMN parent_owner TEXT NOT NULL DEFAULT '';");
        }
        catch { }
    }

    // ================= Users =================

    public static List<LauncherUserRow> LoadUsers(string root)
    {
        using var conn = Open(PathFor(root));
        return ReadUsers(conn);
    }

    public static void SaveUsers(string root, List<LauncherUserRow> users)
    {
        using var conn = Open(PathFor(root));
        WriteUsers(conn, users);
    }

    private static List<LauncherUserRow> ReadUsers(SqliteConnection conn)
    {
        var list = new List<LauncherUserRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username,password_hash,salt,iterations,admin,instance_id FROM users;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var username = r.GetString(0);
            if (string.IsNullOrWhiteSpace(username)) continue; // skip orphans from past bugs
            list.Add(new LauncherUserRow
            {
                Username = username,
                PasswordHash = r.GetString(1),
                Salt = r.GetString(2),
                Iterations = r.GetInt32(3),
                Admin = r.GetInt32(4) != 0,
                InstanceId = r.GetString(5),
            });
        }
        return list;
    }

    private static void WriteUsers(SqliteConnection conn, List<LauncherUserRow> users)
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM users;"; del.ExecuteNonQuery(); }
        foreach (var u in users ?? new List<LauncherUserRow>())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO users(username,password_hash,salt,iterations,admin,instance_id) VALUES($u,$p,$s,$i,$a,$id);";
            ins.Parameters.AddWithValue("$u", u.Username ?? "");
            ins.Parameters.AddWithValue("$p", u.PasswordHash ?? "");
            ins.Parameters.AddWithValue("$s", u.Salt ?? "");
            ins.Parameters.AddWithValue("$i", u.Iterations == 0 ? 100000 : u.Iterations);
            ins.Parameters.AddWithValue("$a", u.Admin ? 1 : 0);
            ins.Parameters.AddWithValue("$id", u.InstanceId ?? "");
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ================= Instances =================

    public static List<LauncherInstanceRow> LoadInstances(string root)
    {
        using var conn = Open(PathFor(root));
        return ReadInstances(conn);
    }

    public static void SaveInstances(string root, List<LauncherInstanceRow> instances)
    {
        using var conn = Open(PathFor(root));
        WriteInstances(conn, instances);
    }

    private static List<LauncherInstanceRow> ReadInstances(SqliteConnection conn)
    {
        var list = new List<LauncherInstanceRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,dsh_port,workspace,os_password,token_url,running FROM instances;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetString(0);
            if (string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new LauncherInstanceRow
            {
                Id = id,
                Name = r.GetString(1),
                DshPort = r.GetInt32(2),
                Workspace = r.GetString(3),
                OsPassword = r.IsDBNull(4) ? null : r.GetString(4),
                TokenUrl = r.GetString(5),
                Running = r.GetInt32(6) != 0,
            });
        }
        return list;
    }

    private static void WriteInstances(SqliteConnection conn, List<LauncherInstanceRow> instances)
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM instances;"; del.ExecuteNonQuery(); }
        foreach (var it in instances ?? new List<LauncherInstanceRow>())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO instances(id,name,dsh_port,workspace,os_password,token_url,running) VALUES($id,$n,$p,$w,$op,$tk,$r);";
            ins.Parameters.AddWithValue("$id", it.Id ?? "");
            ins.Parameters.AddWithValue("$n", it.Name ?? "");
            ins.Parameters.AddWithValue("$p", it.DshPort);
            ins.Parameters.AddWithValue("$w", it.Workspace ?? "");
            ins.Parameters.AddWithValue("$op", (object?)it.OsPassword ?? DBNull.Value);
            ins.Parameters.AddWithValue("$tk", it.TokenUrl ?? "");
            ins.Parameters.AddWithValue("$r", it.Running ? 1 : 0);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ================= Tasks (owner-scoped) =================

    internal static List<TaskRecord> LoadTasks(string root, string owner, string legacyDbPath, string legacyTasksXmlPath, string legacyFeedbackXmlPath)
    {
        using var conn = Open(PathFor(root));
        EnsureLegacyImported(conn, owner, legacyDbPath, legacyTasksXmlPath, legacyFeedbackXmlPath);
        return ReadTasks(conn, owner);
    }

    internal static void SaveTasks(string root, string owner, List<TaskRecord> tasks)
    {
        using var conn = Open(PathFor(root));
        WriteTasks(conn, owner, tasks);
    }

    private static List<TaskRecord> ReadTasks(SqliteConnection conn, string owner)
    {
        var list = new List<TaskRecord>();
        var filesBySeq = new Dictionary<int, List<string>>();
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.CommandText = "SELECT seq, rel FROM task_files WHERE owner=$o ORDER BY seq, idx;";
            fcmd.Parameters.AddWithValue("$o", owner);
            using var fr = fcmd.ExecuteReader();
            while (fr.Read())
            {
                var s = fr.GetInt32(0);
                if (!filesBySeq.TryGetValue(s, out var fl)) { fl = new List<string>(); filesBySeq[s] = fl; }
                fl.Add(fr.GetString(1));
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT seq,name,type,content,status,assigned_at,created_by,parent_task,parent_owner FROM tasks WHERE owner=$o ORDER BY seq DESC;";
            cmd.Parameters.AddWithValue("$o", owner);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var seq = r.GetInt32(0);
                list.Add(new TaskRecord
                {
                    Seq = seq,
                    Name = r.GetString(1),
                    Type = r.GetString(2),
                    Content = r.GetString(3),
                    Status = r.GetString(4),
                    AssignedAt = r.GetString(5),
                    CreatedBy = r.GetString(6),
                    ParentTask = r.FieldCount > 7 && !r.IsDBNull(7) ? r.GetInt32(7) : 0,
                    ParentOwner = r.FieldCount > 8 && !r.IsDBNull(8) ? r.GetString(8) : "",
                    Files = filesBySeq.TryGetValue(seq, out var fl) ? fl : new List<string>(),
                });
            }
        }
        return list;
    }

    private static void WriteTasks(SqliteConnection conn, string owner, List<TaskRecord> tasks)
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tasks WHERE owner=$o; DELETE FROM task_files WHERE owner=$o;";
            del.Parameters.AddWithValue("$o", owner);
            del.ExecuteNonQuery();
        }
        foreach (var t in tasks ?? new List<TaskRecord>())
        {
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO tasks(owner,seq,name,type,content,status,assigned_at,created_by,parent_task,parent_owner) VALUES($o,$seq,$name,$type,$content,$status,$at,$by,$parent,$parentOwner);";
                ins.Parameters.AddWithValue("$o", owner);
                ins.Parameters.AddWithValue("$seq", t.Seq);
                ins.Parameters.AddWithValue("$name", t.Name ?? "");
                ins.Parameters.AddWithValue("$type", t.Type ?? "");
                ins.Parameters.AddWithValue("$content", t.Content ?? "");
                ins.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(t.Status) ? "pending" : t.Status!);
                ins.Parameters.AddWithValue("$at", t.AssignedAt ?? "");
                ins.Parameters.AddWithValue("$by", t.CreatedBy ?? "");
                ins.Parameters.AddWithValue("$parent", t.ParentTask);
                ins.Parameters.AddWithValue("$parentOwner", t.ParentOwner ?? "");
                ins.ExecuteNonQuery();
            }
            var idx = 0;
            foreach (var f in t.Files ?? new List<string>())
            {
                using var inf = conn.CreateCommand();
                inf.Transaction = tx;
                inf.CommandText = "INSERT INTO task_files(owner,seq,idx,rel) VALUES($o,$seq,$idx,$rel);";
                inf.Parameters.AddWithValue("$o", owner);
                inf.Parameters.AddWithValue("$seq", t.Seq);
                inf.Parameters.AddWithValue("$idx", idx++);
                inf.Parameters.AddWithValue("$rel", f ?? "");
                inf.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    // ================= Feedback (owner-scoped) =================

    internal static Dictionary<int, List<FeedbackEntry>> LoadFeedback(string root, string owner, string legacyDbPath, string legacyTasksXmlPath, string legacyFeedbackXmlPath)
    {
        using var conn = Open(PathFor(root));
        EnsureLegacyImported(conn, owner, legacyDbPath, legacyTasksXmlPath, legacyFeedbackXmlPath);
        return ReadFeedback(conn, owner);
    }

    internal static void SaveFeedback(string root, string owner, Dictionary<int, List<FeedbackEntry>> dict)
    {
        using var conn = Open(PathFor(root));
        WriteFeedback(conn, owner, dict);
    }

    private static Dictionary<int, List<FeedbackEntry>> ReadFeedback(SqliteConnection conn, string owner)
    {
        var dict = new Dictionary<int, List<FeedbackEntry>>();
        var filesByFid = new Dictionary<long, List<string>>();
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.CommandText = "SELECT ff.fid, ff.name FROM feedback_files ff JOIN feedback f ON f.id=ff.fid WHERE f.owner=$o ORDER BY ff.fid, ff.idx;";
            fcmd.Parameters.AddWithValue("$o", owner);
            using var fr = fcmd.ExecuteReader();
            while (fr.Read())
            {
                var fid = fr.GetInt64(0);
                if (!filesByFid.TryGetValue(fid, out var fl)) { fl = new List<string>(); filesByFid[fid] = fl; }
                fl.Add(fr.GetString(1));
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id,seq,date,by_user,content,time,files FROM feedback WHERE owner=$o ORDER BY seq, time;";
            cmd.Parameters.AddWithValue("$o", owner);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fid = r.GetInt64(0);
                var seq = r.GetInt32(1);
                if (!filesByFid.TryGetValue(fid, out var files))
                {
                    var legacy = r.GetString(6);
                    files = string.IsNullOrWhiteSpace(legacy)
                        ? new List<string>()
                        : legacy.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                }
                if (!dict.TryGetValue(seq, out var list)) { list = new List<FeedbackEntry>(); dict[seq] = list; }
                list.Add(new FeedbackEntry
                {
                    Date = r.GetString(2),
                    By = r.GetString(3),
                    Content = r.GetString(4),
                    Time = r.GetString(5),
                    Files = files,
                });
            }
        }
        return dict;
    }

    private static void WriteFeedback(SqliteConnection conn, string owner, Dictionary<int, List<FeedbackEntry>> dict)
    {
        // Rebuild this owner's feedback: capture the row ids first so their
        // normalized feedback_files rows can be removed too.
        var ids = new List<long>();
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT id FROM feedback WHERE owner=$o;";
            q.Parameters.AddWithValue("$o", owner);
            using var r = q.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        using var tx = conn.BeginTransaction();
        foreach (var fid in ids)
        {
            using var d = conn.CreateCommand();
            d.Transaction = tx;
            d.CommandText = "DELETE FROM feedback_files WHERE fid=$f;";
            d.Parameters.AddWithValue("$f", fid);
            d.ExecuteNonQuery();
        }
        using (var del = conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM feedback WHERE owner=$o;"; del.Parameters.AddWithValue("$o", owner); del.ExecuteNonQuery(); }

        foreach (var kv in dict ?? new Dictionary<int, List<FeedbackEntry>>())
        {
            foreach (var e in kv.Value ?? new List<FeedbackEntry>())
            {
                var files = e.Files ?? new List<string>();
                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO feedback(owner,seq,date,by_user,content,time,files) VALUES($o,$seq,$date,$by,$content,$time,$files);";
                    ins.Parameters.AddWithValue("$o", owner);
                    ins.Parameters.AddWithValue("$seq", kv.Key);
                    ins.Parameters.AddWithValue("$date", e.Date ?? "");
                    ins.Parameters.AddWithValue("$by", e.By ?? "");
                    ins.Parameters.AddWithValue("$content", e.Content ?? "");
                    ins.Parameters.AddWithValue("$time", e.Time ?? "");
                    ins.Parameters.AddWithValue("$files", string.Join(",", files));
                    ins.ExecuteNonQuery();
                }
                long fid;
                using (var idc = conn.CreateCommand()) { idc.Transaction = tx; idc.CommandText = "SELECT last_insert_rowid();"; fid = (long)idc.ExecuteScalar()!; }
                var idx = 0;
                foreach (var name in files)
                {
                    using var inf = conn.CreateCommand();
                    inf.Transaction = tx;
                    inf.CommandText = "INSERT INTO feedback_files(fid,idx,name) VALUES($f,$idx,$name);";
                    inf.Parameters.AddWithValue("$f", fid);
                    inf.Parameters.AddWithValue("$idx", idx++);
                    inf.Parameters.AddWithValue("$name", name ?? "");
                    inf.ExecuteNonQuery();
                }
            }
        }
        tx.Commit();
    }

    // ================= One-time legacy import =================

    // Import a member's pre-central task/feedback data exactly once, then archive
    // the source. Tasks and feedback usually share ONE legacy file, so both are
    // imported here and the file is archived only after both are handled.
    private static void EnsureLegacyImported(SqliteConnection conn, string owner, string legacyDbPath, string legacyTasksXml, string legacyFeedbackXml)
    {
        if (!File.Exists(legacyDbPath) && !File.Exists(legacyTasksXml) && !File.Exists(legacyFeedbackXml)) return;
        var key = "legacy_imported:" + owner;
        if (MetaHas(conn, key)) return;

        if (Scalar(conn, "SELECT COUNT(*) FROM tasks WHERE owner=$o;", c => c.Parameters.AddWithValue("$o", owner)) == 0)
        {
            var tasks = ReadLegacyTasks(legacyDbPath, legacyTasksXml);
            if (tasks.Count > 0) WriteTasks(conn, owner, tasks);
        }
        if (Scalar(conn, "SELECT COUNT(*) FROM feedback WHERE owner=$o;", c => c.Parameters.AddWithValue("$o", owner)) == 0)
        {
            var fb = ReadLegacyFeedback(legacyDbPath, legacyFeedbackXml);
            if (fb.Count > 0) WriteFeedback(conn, owner, fb);
        }
        MetaSet(conn, key);
        ArchiveLegacy(legacyDbPath, legacyTasksXml, legacyFeedbackXml);
    }

    private static bool MetaHas(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM meta WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() != null;
    }

    private static void MetaSet(SqliteConnection conn, string key)
        => Exec(conn, "INSERT OR REPLACE INTO meta(key,value) VALUES($k,'1');", c => c.Parameters.AddWithValue("$k", key));

    // ================= One-time migration from the old stores =================

    // Copy users + instances from the previous config/launcher.db into the central
    // database (only when the central tables are still empty), then archive it.
    public static void MigrateUsersAndInstances(string root)
    {
        var central = PathFor(root);
        var old = System.IO.Path.Combine(root, "config", "launcher.db");
        if (!File.Exists(old)) return;
        if (string.Equals(System.IO.Path.GetFullPath(old), System.IO.Path.GetFullPath(central), StringComparison.OrdinalIgnoreCase)) return;

        using (var conn = Open(central))
        {
            if (Scalar(conn, "SELECT COUNT(*) FROM users;") == 0)
            {
                var users = ReadUsersFromFile(old);
                if (users.Count > 0) WriteUsers(conn, users);
            }
            if (Scalar(conn, "SELECT COUNT(*) FROM instances;") == 0)
            {
                var instances = ReadInstancesFromFile(old);
                if (instances.Count > 0) WriteInstances(conn, instances);
            }
        }
        ArchiveFile(old);
        ArchiveFile(old + "-wal");
        ArchiveFile(old + "-shm");
    }

    // On Unix, keep the sensitive folders of the admin workspace private (0700) so
    // per-member OS users cannot read the central database or the admin's
    // task/experience data at the OS level. The in-app sandbox already fences DSH
    // tool access; this closes the OS-level gap. No-op on Windows (ACLs are used).
    public static void HardenUnixPermissions(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var dbDir = System.IO.Path.GetDirectoryName(PathFor(root));
            if (string.IsNullOrEmpty(dbDir)) return;
            Directory.CreateDirectory(dbDir);
            Chmod700(dbDir);
            var ws = System.IO.Path.GetDirectoryName(dbDir);
            if (!string.IsNullOrEmpty(ws))
                foreach (var sub in new[] { "TaskData", "sharedata" })
                {
                    var d = System.IO.Path.Combine(ws, sub);
                    if (Directory.Exists(d)) Chmod700(d);
                }
        }
        catch (Exception ex) { Console.WriteLine("[perms] harden failed: " + ex.Message); }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Chmod700(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception ex) { Console.WriteLine("[perms] chmod 700 " + path + ": " + ex.Message); }
    }

    private static List<LauncherUserRow> ReadUsersFromFile(string dbPath)
    {
        try { using var c = OpenReadOnly(dbPath); return ReadUsers(c); }
        catch (Exception ex) { Console.WriteLine("[launcherdb] legacy users read: " + ex.Message); return new List<LauncherUserRow>(); }
    }

    private static List<LauncherInstanceRow> ReadInstancesFromFile(string dbPath)
    {
        try { using var c = OpenReadOnly(dbPath); return ReadInstances(c); }
        catch (Exception ex) { Console.WriteLine("[launcherdb] legacy instances read: " + ex.Message); return new List<LauncherInstanceRow>(); }
    }

    // ================= Legacy per-member readers (import only) =================

    private static List<TaskRecord> ReadLegacyTasks(string dbPath, string xmlPath)
    {
        if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
        {
            var fromDb = ReadLegacyTasksDb(dbPath);
            if (fromDb.Count > 0) return fromDb;
        }
        if (!string.IsNullOrWhiteSpace(xmlPath) && File.Exists(xmlPath)) return LegacyXml.ReadTasks(xmlPath);
        return new List<TaskRecord>();
    }

    private static List<TaskRecord> ReadLegacyTasksDb(string dbPath)
    {
        var list = new List<TaskRecord>();
        var filesBySeq = new Dictionary<int, List<string>>();
        try
        {
            using var conn = OpenReadOnly(dbPath);
            using (var fcmd = conn.CreateCommand())
            {
                fcmd.CommandText = "SELECT seq, rel FROM task_files ORDER BY seq, idx;";
                using var fr = fcmd.ExecuteReader();
                while (fr.Read())
                {
                    var s = fr.GetInt32(0);
                    if (!filesBySeq.TryGetValue(s, out var fl)) { fl = new List<string>(); filesBySeq[s] = fl; }
                    fl.Add(fr.GetString(1));
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT seq,name,type,content,status,assigned_at,created_by FROM tasks ORDER BY seq DESC;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var seq = r.GetInt32(0);
                    list.Add(new TaskRecord
                    {
                        Seq = seq,
                        Name = r.GetString(1),
                        Type = r.GetString(2),
                        Content = r.GetString(3),
                        Status = r.GetString(4),
                        AssignedAt = r.GetString(5),
                        CreatedBy = r.GetString(6),
                        Files = filesBySeq.TryGetValue(seq, out var fl) ? fl : new List<string>(),
                    });
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] legacy tasks db read: " + ex.Message); }
        return list;
    }

    private static Dictionary<int, List<FeedbackEntry>> ReadLegacyFeedback(string dbPath, string xmlPath)
    {
        if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
        {
            var fromDb = ReadLegacyFeedbackDb(dbPath);
            if (fromDb.Count > 0) return fromDb;
        }
        if (!string.IsNullOrWhiteSpace(xmlPath) && File.Exists(xmlPath)) return LegacyXml.ReadFeedback(xmlPath);
        return new Dictionary<int, List<FeedbackEntry>>();
    }

    private static Dictionary<int, List<FeedbackEntry>> ReadLegacyFeedbackDb(string dbPath)
    {
        var dict = new Dictionary<int, List<FeedbackEntry>>();
        try
        {
            using var conn = OpenReadOnly(dbPath);
            var filesByFid = new Dictionary<long, List<string>>();
            using (var fcmd = conn.CreateCommand())
            {
                fcmd.CommandText = "SELECT fid, name FROM feedback_files ORDER BY fid, idx;";
                try
                {
                    using var fr = fcmd.ExecuteReader();
                    while (fr.Read())
                    {
                        var fid = fr.GetInt64(0);
                        if (!filesByFid.TryGetValue(fid, out var fl)) { fl = new List<string>(); filesByFid[fid] = fl; }
                        fl.Add(fr.GetString(1));
                    }
                }
                catch { }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id,seq,date,by_user,content,time,files FROM feedback ORDER BY seq, time;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var fid = r.GetInt64(0);
                    var seq = r.GetInt32(1);
                    if (!filesByFid.TryGetValue(fid, out var files))
                    {
                        var legacy = r.GetString(6);
                        files = string.IsNullOrWhiteSpace(legacy)
                            ? new List<string>()
                            : legacy.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    }
                    if (!dict.TryGetValue(seq, out var list)) { list = new List<FeedbackEntry>(); dict[seq] = list; }
                    list.Add(new FeedbackEntry
                    {
                        Date = r.GetString(2),
                        By = r.GetString(3),
                        Content = r.GetString(4),
                        Time = r.GetString(5),
                        Files = files,
                    });
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] legacy feedback db read: " + ex.Message); }
        return dict;
    }

    // Rename legacy files aside (keep them; never delete) after a successful import.
    private static void ArchiveLegacy(string dbPath, string tasksXml, string feedbackXml)
    {
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            ArchiveFile(dbPath);
            ArchiveFile(dbPath + "-wal");
            ArchiveFile(dbPath + "-shm");
        }
        ArchiveFile(tasksXml);
        ArchiveFile(feedbackXml);
    }

    private static void ArchiveFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var bak = path + ".bak";
            if (File.Exists(bak)) bak = path + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
            File.Move(path, bak);
            Console.WriteLine($"[launcherdb] migrated {path} -> {System.IO.Path.GetFileName(bak)}");
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] migrate archive: " + ex.Message); }
    }

    // Keep a legacy JSON file beside the new DB (renamed, never deleted).
    public static void ArchiveJson(string jsonPath) => ArchiveFile(jsonPath);
}

// Readers for the ancient XML format, used only when importing pre-SQLite data.
static class LegacyXml
{
    public static List<TaskRecord> ReadTasks(string docPath)
    {
        var list = new List<TaskRecord>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(docPath);
            var root = doc.Root;
            if (root == null) return list;
            foreach (var t in root.Elements("Task"))
            {
                var files = t.Element("Files")?.Elements("File")
                    .Select(f => f.Value.Trim()).Where(f => f.Length > 0).ToList() ?? new List<string>();
                list.Add(new TaskRecord
                {
                    Seq = int.TryParse(t.Element("Seq")?.Value, out var s) ? s : (list.Count + 1),
                    Name = t.Element("Name")?.Value ?? "",
                    Type = t.Element("Type")?.Value ?? "",
                    Content = t.Element("Content")?.Value ?? "",
                    Status = t.Element("Status")?.Value ?? "pending",
                    AssignedAt = t.Element("AssignedAt")?.Value ?? "",
                    CreatedBy = t.Element("CreatedBy")?.Value ?? "",
                    Files = files,
                });
            }
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] legacy tasks xml read: " + ex.Message); }
        return list.OrderByDescending(t => t.Seq).ToList();
    }

    public static Dictionary<int, List<FeedbackEntry>> ReadFeedback(string fp)
    {
        var dict = new Dictionary<int, List<FeedbackEntry>>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(fp);
            var root = doc.Root;
            if (root == null) return dict;
            foreach (var t in root.Elements("Task"))
            {
                if (!int.TryParse(t.Attribute("seq")?.Value, out var seq)) continue;
                var list = new List<FeedbackEntry>();
                foreach (var e in t.Elements("Entry"))
                {
                    list.Add(new FeedbackEntry
                    {
                        Date = e.Element("Date")?.Value ?? "",
                        By = e.Element("By")?.Value ?? "",
                        Content = e.Element("Content")?.Value ?? "",
                        Time = e.Element("Time")?.Value ?? "",
                        Files = (e.Element("Files")?.Value ?? "")
                            .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    });
                }
                dict[seq] = list;
            }
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] legacy feedback xml read: " + ex.Message); }
        return dict;
    }
}

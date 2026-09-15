// TakeTopDshTeam 鈥?multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 娉伴《鎷撻紟淇℃伅绉戞妧锛堜笂娴凤級鏈夐檺鍏徃
// EMail: service@taketopits.com
//
// This software is the intellectual property of 娉伴《鎷撻紟淇℃伅绉戞妧锛堜笂娴凤級鏈夐檺鍏徃
// (TaiDingTuoDing Information Technology (Shanghai) Co., Ltd.). All rights reserved.

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
        // Only the install-dir pointer (or the portable default). appsettings.json is
        // deliberately excluded so a repo-shipped template can never pick the workspace.
        return DshService.ResolveWorkspacePath(root);
    }

    // ---- Pre-upgrade check / upgrade (used by start scripts before launching) ----
    // True when the DB exists but is still in the pre-`taketop_` naming (tables
    // without the prefix). A missing DB (fresh install) is NOT an upgrade case.
    public static bool NeedsUpgrade(string root)
    {
        try
        {
            var dbPath = PathFor(root);
            if (!File.Exists(dbPath)) return false;
            using var conn = OpenReadOnly(dbPath);
            bool TableExists(string t) =>
                Scalar(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{t}';") > 0;
            if (TableExists("users") && !TableExists("taketop_users")) return true;
            if (TableExists("tasks") && !TableExists("taketop_tasks")) return true;
            if (TableExists("feedback") && !TableExists("taketop_feedback")) return true;
            // A different program version wrote this DB: back it up before touching it.
            if (!string.Equals(ScalarStr(conn, $"SELECT taketop_value FROM taketop_meta WHERE taketop_key='{AppVersionKey}';"), AppVersion, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch { return false; }
    }

    // Back up the DB into <dbdir>/backups/, then run the in-place schema upgrade
    // (opening the DB triggers EnsureSchema's rename migration). Returns the backup path.
    public static string? UpgradeDatabase(string root)
    {
        var dbPath = PathFor(root);
        if (!File.Exists(dbPath)) return null;
        string? bak = null;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(dbPath)!;
            var bakDir = System.IO.Path.Combine(dir, "backups");
            System.IO.Directory.CreateDirectory(bakDir);
            bak = System.IO.Path.Combine(bakDir, $"taketopDSHTeam-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            try { File.Copy(dbPath, bak, overwrite: false); } catch { bak = null; }
        }
        catch { bak = null; }
        using (var conn = Open(dbPath)) { }
        return bak;
    }

    // ---- Legacy → `taketop_` rename migration ----
    // Older databases use un-prefixed table/column names. Rename them IN PLACE
    // (SQLite RENAME keeps the data) before the CREATE TABLE IF NOT EXISTS block,
    // so a fresh DB just creates the new schema and an existing one is upgraded
    // without data loss.
    private static void MigrateLegacyToTaketop(SqliteConnection conn)
    {
        var tables = new (string From, string To)[]
        {
            ("users", "taketop_users"),
            ("instances", "taketop_instances"),
            ("tasks", "taketop_tasks"),
            ("task_files", "taketop_task_files"),
            ("feedback", "taketop_feedback"),
            ("feedback_files", "taketop_feedback_files"),
            ("meta", "taketop_meta"),
        };
        var renamed = new List<string>();
        foreach (var (from, to) in tables)
        {
            if (!TableExists(conn, from) || TableExists(conn, to)) continue;
            Exec(conn, $"ALTER TABLE {from} RENAME TO {to};");
            renamed.Add(to);
        }
        foreach (var (_, table) in tables)
        {
            if (!TableExists(conn, table)) continue;
            foreach (var col in TableColumns(conn, table))
            {
                if (col.StartsWith("taketop_", StringComparison.OrdinalIgnoreCase)) continue;
                var target = "taketop_" + col;
                if (ColumnExists(conn, table, target)) continue;
                Exec(conn, $"ALTER TABLE {table} RENAME COLUMN {col} TO {target};");
            }
        }
        // Indexes follow a renamed table but keep their old names; drop them and
        // let the CREATE INDEX statements below recreate them with new names.
        foreach (var ix in new[] { "ix_tasks_owner_seq", "ix_feedback_owner" })
            Exec(conn, $"DROP INDEX IF EXISTS {ix};");
        if (renamed.Count > 0)
            Console.WriteLine("[launcherdb] renamed legacy tables -> " + string.Join(", ", renamed));
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", table);
        var v = cmd.ExecuteScalar();
        return v != null && v is not DBNull && Convert.ToInt64(v) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$c;";
        cmd.Parameters.AddWithValue("$c", column);
        var v = cmd.ExecuteScalar();
        return v != null && v is not DBNull && Convert.ToInt64(v) > 0;
    }

    private static List<string> TableColumns(SqliteConnection conn, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(0));
        return cols;
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
        MigrateLegacyToTaketop(conn);
        Exec(conn, @"
CREATE TABLE IF NOT EXISTS taketop_users(
  taketop_username      TEXT PRIMARY KEY,
  taketop_password_hash TEXT NOT NULL DEFAULT '',
  taketop_salt          TEXT NOT NULL DEFAULT '',
  taketop_iterations    INTEGER NOT NULL DEFAULT 100000,
  taketop_admin         INTEGER NOT NULL DEFAULT 0,
  taketop_instance_id   TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS taketop_instances(
  taketop_id          TEXT PRIMARY KEY,
  taketop_name        TEXT NOT NULL DEFAULT '',
  taketop_dsh_port    INTEGER NOT NULL DEFAULT 0,
  taketop_workspace   TEXT NOT NULL DEFAULT '',
  taketop_os_password TEXT,
  taketop_token_url   TEXT NOT NULL DEFAULT '',
  taketop_running     INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS taketop_tasks(
  taketop_uid         TEXT NOT NULL,
  taketop_owner       TEXT NOT NULL,
  taketop_seq         INTEGER NOT NULL DEFAULT 0,
  taketop_name        TEXT NOT NULL DEFAULT '',
  taketop_type        TEXT NOT NULL DEFAULT '',
  taketop_content     TEXT NOT NULL DEFAULT '',
  taketop_status      TEXT NOT NULL DEFAULT 'pending',
  taketop_assigned_at TEXT NOT NULL DEFAULT '',
  taketop_created_by  TEXT NOT NULL DEFAULT '',
  taketop_parent_uid  TEXT NOT NULL DEFAULT '',
  PRIMARY KEY(taketop_uid)
);
CREATE UNIQUE INDEX IF NOT EXISTS taketop_ix_tasks_owner_seq ON taketop_tasks(taketop_owner, taketop_seq);
CREATE TABLE IF NOT EXISTS taketop_task_files(
  taketop_uid   TEXT NOT NULL,
  taketop_idx   INTEGER NOT NULL,
  taketop_rel   TEXT NOT NULL,
  PRIMARY KEY(taketop_uid, taketop_idx)
);
CREATE TABLE IF NOT EXISTS taketop_feedback(
  taketop_uid      TEXT PRIMARY KEY,
  taketop_owner    TEXT NOT NULL,
  taketop_task_uid TEXT NOT NULL DEFAULT '',
  taketop_date     TEXT NOT NULL DEFAULT '',
  taketop_by_user  TEXT NOT NULL DEFAULT '',
  taketop_content  TEXT NOT NULL DEFAULT '',
  taketop_time     TEXT NOT NULL DEFAULT '',
  taketop_files    TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS taketop_feedback_files(
  taketop_feedback_uid TEXT NOT NULL,
  taketop_idx          INTEGER NOT NULL,
  taketop_name         TEXT NOT NULL,
  PRIMARY KEY(taketop_feedback_uid, taketop_idx)
);
CREATE TABLE IF NOT EXISTS taketop_meta(taketop_key TEXT PRIMARY KEY, taketop_value TEXT NOT NULL DEFAULT '');
");
        // Migrations for databases created before the column existed. Each is
        // guarded by a pragma_table_info check so it is safe to run every open.
        try
        {
            // Databases created before task ids moved to a random string: add the
            // legacy parent columns (if an even older schema) and rebuild the task
            // tables keyed by uid. Fresh databases already have `uid` and skip this.
            // Ensure feedback.task_uid exists BEFORE the task migration so the
            // migration can fill it in on the same pass (older databases have only
            // the per-owner seq column).
            if (Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('taketop_feedback') WHERE name='taketop_task_uid';") == 0)
                Exec(conn, "ALTER TABLE taketop_feedback ADD COLUMN taketop_task_uid TEXT NOT NULL DEFAULT '';");
            if (Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('taketop_tasks') WHERE name='taketop_uid';") == 0)
            {
                // Take a consistent snapshot of the whole database before the
                // destructive rebuild (tasks/task_files are dropped and recreated),
                // so an interrupted upgrade can always be recovered by hand.
                BackupBeforeMigration(conn);
                if (Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('taketop_tasks') WHERE name='taketop_parent_task';") == 0)
                    Exec(conn, "ALTER TABLE taketop_tasks ADD COLUMN taketop_parent_task INTEGER NOT NULL DEFAULT 0;");
                if (Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('taketop_tasks') WHERE name='taketop_parent_owner';") == 0)
                    Exec(conn, "ALTER TABLE taketop_tasks ADD COLUMN taketop_parent_owner TEXT NOT NULL DEFAULT '';");
                MigrateTasksToUid(conn);
            }
            // Back-fill feedback.task_uid for rows imported before the column
            // existed (works while the legacy `seq` column is still present).
            try
            {
                Exec(conn, @"UPDATE taketop_feedback SET taketop_task_uid = (SELECT t.taketop_uid FROM taketop_tasks t WHERE t.taketop_owner=taketop_feedback.taketop_owner AND t.taketop_seq=taketop_feedback.taketop_seq)
                             WHERE (taketop_task_uid IS NULL OR taketop_task_uid='')
                               AND EXISTS (SELECT 1 FROM taketop_tasks t WHERE t.taketop_owner=taketop_feedback.taketop_owner AND t.taketop_seq=taketop_feedback.taketop_seq);");
            }
            catch { }
            // Move feedback off the old INTEGER AUTOINCREMENT id onto a uid primary
            // key and repoint feedback_files at feedback.uid. Also drops the legacy
            // `seq` column in the same rebuild.
            MigrateFeedbackToUid(conn);
            // Created here (not in the schema script) because on an old database the
            // feedback table exists without task_uid until the ALTER above runs.
            Exec(conn, "CREATE INDEX IF NOT EXISTS taketop_ix_feedback_owner ON taketop_feedback(taketop_owner, taketop_task_uid);");
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] migrate: " + ex.Message); }
        // Remember which program version wrote this database, so the next build
        // can detect an upgrade and back the DB up BEFORE touching the data.
        WriteAppVersion(conn);
    }

    // The launcher's own version (from the assembly), used to detect a program
    // upgrade so the database can be backed up before the new build touches it.
    public static string AppVersion
    {
        get
        {
            try { return typeof(LauncherDb).Assembly.GetName().Version?.ToString() ?? "0.0.0"; }
            catch { return "0.0.0"; }
        }
    }

    private const string AppVersionKey = "app_version";

    private static string? ScalarStr(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    // The version recorded in the DB (null when written by an older build).
    public static string? ReadAppVersion(string root)
    {
        try
        {
            var dbPath = PathFor(root);
            if (!File.Exists(dbPath)) return null;
            using var conn = OpenReadOnly(dbPath);
            return ScalarStr(conn, $"SELECT taketop_value FROM taketop_meta WHERE taketop_key='{AppVersionKey}';");
        }
        catch { return null; }
    }

    private static void WriteAppVersion(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO taketop_meta(taketop_key,taketop_value) VALUES($k,$v);";
            cmd.Parameters.AddWithValue("$k", AppVersionKey);
            cmd.Parameters.AddWithValue("$v", AppVersion);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Best-effort consistent snapshot taken right before a destructive schema
    // migration. VACUUM INTO includes any data still sitting in the WAL, unlike a
    // plain file copy, and never overwrites an existing file.
    private static void BackupBeforeMigration(SqliteConnection conn)
    {
        try
        {
            var dbPath = conn.DataSource;
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return;
            var dir = System.IO.Path.GetDirectoryName(dbPath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            var name = System.IO.Path.GetFileNameWithoutExtension(dbPath);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var bak = System.IO.Path.Combine(dir!, name + ".preupgrade-" + stamp + ".db");
            Exec(conn, "VACUUM INTO '" + bak.Replace("'", "''") + "';");
            Console.WriteLine("[launcherdb] pre-migration backup -> " + bak);
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] pre-migration backup failed: " + ex.Message); }
    }

    // Move feedback from the legacy INTEGER AUTOINCREMENT `id` primary key onto a
    // uid (32-hex) primary key, and repoint feedback_files from `fid` (the old
    // numeric id) to `feedback_uid`. Also drops the legacy `seq` column. Safe to
    // call on every open: it no-ops once `feedback.uid` exists.
    private static void MigrateFeedbackToUid(SqliteConnection conn)
    {
        if (Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('taketop_feedback') WHERE name='taketop_uid';") > 0) return;
        BackupBeforeMigration(conn);

        // Old attachment rows are keyed by the numeric feedback id.
        var filesByFid = new Dictionary<long, List<(int Idx, string Name)>>();
        try
        {
            using var fcmd = conn.CreateCommand();
            fcmd.CommandText = "SELECT taketop_fid, taketop_idx, taketop_name FROM taketop_feedback_files ORDER BY taketop_fid, taketop_idx;";
            using var fr = fcmd.ExecuteReader();
            while (fr.Read())
            {
                var fid = fr.GetInt64(0);
                if (!filesByFid.TryGetValue(fid, out var fl)) { fl = new List<(int, string)>(); filesByFid[fid] = fl; }
                fl.Add((fr.GetInt32(1), fr.GetString(2)));
            }
        }
        catch { }

        var rows = new List<(long Id, string Owner, string TaskUid, string Date, string By, string Content, string Time, string Files)>();
        using (var cmd = conn.CreateCommand())
        {
            // task_uid was added above (if missing); `seq`, if still present, is dropped.
            cmd.CommandText = "SELECT taketop_id, taketop_owner, COALESCE(taketop_task_uid,''), taketop_date, taketop_by_user, taketop_content, taketop_time, COALESCE(taketop_files,'') FROM taketop_feedback;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
        }

        using var tx = conn.BeginTransaction();
        ExecTx(conn, tx, "DROP TABLE IF EXISTS taketop_feedback_new;");
        ExecTx(conn, tx, @"CREATE TABLE taketop_feedback_new(
  taketop_uid      TEXT PRIMARY KEY,
  taketop_owner    TEXT NOT NULL,
  taketop_task_uid TEXT NOT NULL DEFAULT '',
  taketop_date     TEXT NOT NULL DEFAULT '',
  taketop_by_user  TEXT NOT NULL DEFAULT '',
  taketop_content  TEXT NOT NULL DEFAULT '',
  taketop_time     TEXT NOT NULL DEFAULT '',
  taketop_files    TEXT NOT NULL DEFAULT '');");
        ExecTx(conn, tx, "DROP TABLE IF EXISTS taketop_feedback_files_new;");
        ExecTx(conn, tx, @"CREATE TABLE taketop_feedback_files_new(
  taketop_feedback_uid TEXT NOT NULL,
  taketop_idx          INTEGER NOT NULL,
  taketop_name         TEXT NOT NULL,
  PRIMARY KEY(taketop_feedback_uid, taketop_idx));");

        var idToUid = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            var uid = NewUid();
            idToUid[row.Id] = uid;
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO taketop_feedback_new(taketop_uid,taketop_owner,taketop_task_uid,taketop_date,taketop_by_user,taketop_content,taketop_time,taketop_files) VALUES($u,$o,$t,$d,$b,$c,$tm,$f);";
                ins.Parameters.AddWithValue("$u", uid);
                ins.Parameters.AddWithValue("$o", row.Owner);
                ins.Parameters.AddWithValue("$t", row.TaskUid);
                ins.Parameters.AddWithValue("$d", row.Date);
                ins.Parameters.AddWithValue("$b", row.By);
                ins.Parameters.AddWithValue("$c", row.Content);
                ins.Parameters.AddWithValue("$tm", row.Time);
                ins.Parameters.AddWithValue("$f", row.Files);
                ins.ExecuteNonQuery();
            }
            if (filesByFid.TryGetValue(row.Id, out var fl))
            {
                foreach (var (idx, name) in fl)
                {
                    using var inf = conn.CreateCommand();
                    inf.Transaction = tx;
                    inf.CommandText = "INSERT INTO taketop_feedback_files_new(taketop_feedback_uid,taketop_idx,taketop_name) VALUES($u,$i,$n);";
                    inf.Parameters.AddWithValue("$u", uid);
                    inf.Parameters.AddWithValue("$i", idx);
                    inf.Parameters.AddWithValue("$n", name);
                    inf.ExecuteNonQuery();
                }
            }
        }
        ExecTx(conn, tx, "DROP TABLE taketop_feedback;");
        ExecTx(conn, tx, "ALTER TABLE taketop_feedback_new RENAME TO taketop_feedback;");
        ExecTx(conn, tx, "DROP TABLE taketop_feedback_files;");
        ExecTx(conn, tx, "ALTER TABLE taketop_feedback_files_new RENAME TO taketop_feedback_files;");
        tx.Commit();
        Console.WriteLine("[launcherdb] migrated feedback to uid primary key");
    }

    // Execute a statement on an explicit transaction (Microsoft.Data.Sqlite requires
    // the transaction to be attached to every command while one is pending).
    private static void ExecTx(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // A new globally-unique id: 16 random bytes as 32 lowercase hex chars. Fixed
    // length, no meaning, effectively collision-free (so it can serve as a primary
    // key and survive a database-engine change, e.g. to PostgreSQL).
    public static string NewUid()
    {
        Span<byte> buf = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    // Task ids use the same generator; kept as a named alias for call-site clarity.
    public static string NewTaskUid() => NewUid();

    // One-time rebuild of the legacy (owner, seq)-keyed task tables into the
    // uid-keyed schema, mapping old parent links and feedback to the new ids.
    private static void MigrateTasksToUid(SqliteConnection conn)
    {
        var rows = new List<(string Owner, int Seq, string Name, string Type, string Content, string Status, string At, string By, int PT, string PO)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT taketop_owner,taketop_seq,taketop_name,taketop_type,taketop_content,taketop_status,taketop_assigned_at,taketop_created_by,taketop_parent_task,taketop_parent_owner FROM taketop_tasks;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetString(6), r.GetString(7), r.IsDBNull(8) ? 0 : r.GetInt32(8), r.IsDBNull(9) ? "" : r.GetString(9)));
        }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in rows) map[t.Owner + "#" + t.Seq] = NewTaskUid();

        var files = new List<(string Owner, int Seq, int Idx, string Rel)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT taketop_owner,taketop_seq,taketop_idx,taketop_rel FROM taketop_task_files;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) files.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)));
        }

        var fbs = new List<(long Id, string Owner, int Seq)>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT taketop_id,taketop_owner,taketop_seq FROM taketop_feedback;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) fbs.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        }
        catch { }

        Exec(conn, "DROP TABLE IF EXISTS taketop_tasks_uid_new;");
        Exec(conn, @"CREATE TABLE taketop_tasks_uid_new(
  taketop_uid         TEXT NOT NULL,
  taketop_owner       TEXT NOT NULL,
  taketop_seq         INTEGER NOT NULL DEFAULT 0,
  taketop_name        TEXT NOT NULL DEFAULT '',
  taketop_type        TEXT NOT NULL DEFAULT '',
  taketop_content     TEXT NOT NULL DEFAULT '',
  taketop_status      TEXT NOT NULL DEFAULT 'pending',
  taketop_assigned_at TEXT NOT NULL DEFAULT '',
  taketop_created_by  TEXT NOT NULL DEFAULT '',
  taketop_parent_uid  TEXT NOT NULL DEFAULT '',
  PRIMARY KEY(taketop_uid));");
        foreach (var t in rows)
        {
            var uid = map[t.Owner + "#" + t.Seq];
            var parentUid = (t.PT > 0 && map.TryGetValue(t.PO + "#" + t.PT, out var p)) ? p : "";
            Exec(conn, "INSERT INTO taketop_tasks_uid_new(taketop_uid,taketop_owner,taketop_seq,taketop_name,taketop_type,taketop_content,taketop_status,taketop_assigned_at,taketop_created_by,taketop_parent_uid) VALUES($u,$o,$s,$n,$ty,$c,$st,$a,$b,$p);", c =>
            {
                c.Parameters.AddWithValue("$u", uid);
                c.Parameters.AddWithValue("$o", t.Owner);
                c.Parameters.AddWithValue("$s", t.Seq);
                c.Parameters.AddWithValue("$n", t.Name);
                c.Parameters.AddWithValue("$ty", t.Type);
                c.Parameters.AddWithValue("$c", t.Content);
                c.Parameters.AddWithValue("$st", string.IsNullOrWhiteSpace(t.Status) ? "pending" : t.Status);
                c.Parameters.AddWithValue("$a", t.At);
                c.Parameters.AddWithValue("$b", t.By);
                c.Parameters.AddWithValue("$p", parentUid);
            });
        }
        Exec(conn, "DROP TABLE taketop_tasks;");
        Exec(conn, "ALTER TABLE taketop_tasks_uid_new RENAME TO taketop_tasks;");
        Exec(conn, "CREATE UNIQUE INDEX IF NOT EXISTS taketop_ix_tasks_owner_seq ON taketop_tasks(taketop_owner, taketop_seq);");

        Exec(conn, "DROP TABLE IF EXISTS taketop_task_files_uid_new;");
        Exec(conn, "CREATE TABLE taketop_task_files_uid_new(taketop_uid TEXT NOT NULL, taketop_idx INTEGER NOT NULL, taketop_rel TEXT NOT NULL, PRIMARY KEY(taketop_uid, taketop_idx));");
        foreach (var f in files)
        {
            if (!map.TryGetValue(f.Owner + "#" + f.Seq, out var uid)) continue;
            Exec(conn, "INSERT INTO taketop_task_files_uid_new(taketop_uid,taketop_idx,taketop_rel) VALUES($u,$i,$r);", c =>
            {
                c.Parameters.AddWithValue("$u", uid);
                c.Parameters.AddWithValue("$i", f.Idx);
                c.Parameters.AddWithValue("$r", f.Rel);
            });
        }
        Exec(conn, "DROP TABLE taketop_task_files;");
        Exec(conn, "ALTER TABLE taketop_task_files_uid_new RENAME TO taketop_task_files;");

        foreach (var fb in fbs)
        {
            if (!map.TryGetValue(fb.Owner + "#" + fb.Seq, out var uid)) continue;
            Exec(conn, "UPDATE taketop_feedback SET taketop_task_uid=$u WHERE taketop_id=$id;", c =>
            {
                c.Parameters.AddWithValue("$u", uid);
                c.Parameters.AddWithValue("$id", fb.Id);
            });
        }
        Console.WriteLine($"[launcherdb] migrated {rows.Count} tasks to uid primary keys");
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
        cmd.CommandText = "SELECT taketop_username,taketop_password_hash,taketop_salt,taketop_iterations,taketop_admin,taketop_instance_id FROM taketop_users;";
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
        using (var del = conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM taketop_users;"; del.ExecuteNonQuery(); }
        foreach (var u in users ?? new List<LauncherUserRow>())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO taketop_users(taketop_username,taketop_password_hash,taketop_salt,taketop_iterations,taketop_admin,taketop_instance_id) VALUES($u,$p,$s,$i,$a,$id);";
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
        cmd.CommandText = "SELECT taketop_id,taketop_name,taketop_dsh_port,taketop_workspace,taketop_os_password,taketop_token_url,taketop_running FROM taketop_instances;";
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
        using (var del = conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM taketop_instances;"; del.ExecuteNonQuery(); }
        foreach (var it in instances ?? new List<LauncherInstanceRow>())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO taketop_instances(taketop_id,taketop_name,taketop_dsh_port,taketop_workspace,taketop_os_password,taketop_token_url,taketop_running) VALUES($id,$n,$p,$w,$op,$tk,$r);";
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
        var filesByUid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.CommandText = "SELECT tf.taketop_uid, tf.taketop_rel FROM taketop_task_files tf JOIN taketop_tasks t ON t.taketop_uid = tf.taketop_uid WHERE t.taketop_owner=$o ORDER BY tf.taketop_uid, tf.taketop_idx;";
            fcmd.Parameters.AddWithValue("$o", owner);
            using var fr = fcmd.ExecuteReader();
            while (fr.Read())
            {
                var u = fr.GetString(0);
                if (!filesByUid.TryGetValue(u, out var fl)) { fl = new List<string>(); filesByUid[u] = fl; }
                fl.Add(fr.GetString(1));
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT taketop_uid,taketop_seq,taketop_name,taketop_type,taketop_content,taketop_status,taketop_assigned_at,taketop_created_by,taketop_parent_uid FROM taketop_tasks WHERE taketop_owner=$o ORDER BY taketop_seq DESC;";
            cmd.Parameters.AddWithValue("$o", owner);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var uid = r.GetString(0);
                list.Add(new TaskRecord
                {
                    Uid = uid,
                    Seq = r.GetInt32(1),
                    Name = r.GetString(2),
                    Type = r.GetString(3),
                    Content = r.GetString(4),
                    Status = r.GetString(5),
                    AssignedAt = r.GetString(6),
                    CreatedBy = r.GetString(7),
                    ParentUid = r.FieldCount > 8 && !r.IsDBNull(8) ? r.GetString(8) : "",
                    Files = filesByUid.TryGetValue(uid, out var fl) ? fl : new List<string>(),
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
            // Remove this owner's tasks and their files (files are keyed by task uid).
            del.CommandText = "DELETE FROM taketop_task_files WHERE taketop_uid IN (SELECT taketop_uid FROM taketop_tasks WHERE taketop_owner=$o); DELETE FROM taketop_tasks WHERE taketop_owner=$o;";
            del.Parameters.AddWithValue("$o", owner);
            del.ExecuteNonQuery();
        }
        foreach (var t in tasks ?? new List<TaskRecord>())
        {
            if (string.IsNullOrWhiteSpace(t.Uid)) t.Uid = NewTaskUid();
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO taketop_tasks(taketop_uid,taketop_owner,taketop_seq,taketop_name,taketop_type,taketop_content,taketop_status,taketop_assigned_at,taketop_created_by,taketop_parent_uid) VALUES($uid,$o,$seq,$name,$type,$content,$status,$at,$by,$parentUid);";
                ins.Parameters.AddWithValue("$uid", t.Uid);
                ins.Parameters.AddWithValue("$o", owner);
                ins.Parameters.AddWithValue("$seq", t.Seq);
                ins.Parameters.AddWithValue("$name", t.Name ?? "");
                ins.Parameters.AddWithValue("$type", t.Type ?? "");
                ins.Parameters.AddWithValue("$content", t.Content ?? "");
                ins.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(t.Status) ? "pending" : t.Status!);
                ins.Parameters.AddWithValue("$at", t.AssignedAt ?? "");
                ins.Parameters.AddWithValue("$by", t.CreatedBy ?? "");
                ins.Parameters.AddWithValue("$parentUid", t.ParentUid ?? "");
                ins.ExecuteNonQuery();
            }
            var idx = 0;
            foreach (var f in t.Files ?? new List<string>())
            {
                using var inf = conn.CreateCommand();
                inf.Transaction = tx;
                inf.CommandText = "INSERT INTO taketop_task_files(taketop_uid,taketop_idx,taketop_rel) VALUES($uid,$idx,$rel);";
                inf.Parameters.AddWithValue("$uid", t.Uid);
                inf.Parameters.AddWithValue("$idx", idx++);
                inf.Parameters.AddWithValue("$rel", f ?? "");
                inf.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    // ================= Feedback (owner-scoped) =================

    internal static Dictionary<string, List<FeedbackEntry>> LoadFeedback(string root, string owner, string legacyDbPath, string legacyTasksXmlPath, string legacyFeedbackXmlPath)
    {
        using var conn = Open(PathFor(root));
        EnsureLegacyImported(conn, owner, legacyDbPath, legacyTasksXmlPath, legacyFeedbackXmlPath);
        return ReadFeedback(conn, owner);
    }

    internal static void SaveFeedback(string root, string owner, Dictionary<string, List<FeedbackEntry>> dict)
    {
        using var conn = Open(PathFor(root));
        WriteFeedback(conn, owner, dict);
    }

    private static Dictionary<string, List<FeedbackEntry>> ReadFeedback(SqliteConnection conn, string owner)
    {
        var dict = new Dictionary<string, List<FeedbackEntry>>(StringComparer.OrdinalIgnoreCase);
        var filesByUid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.CommandText = "SELECT ff.taketop_feedback_uid, ff.taketop_name FROM taketop_feedback_files ff JOIN taketop_feedback f ON f.taketop_uid=ff.taketop_feedback_uid WHERE f.taketop_owner=$o ORDER BY ff.taketop_feedback_uid, ff.taketop_idx;";
            fcmd.Parameters.AddWithValue("$o", owner);
            using var fr = fcmd.ExecuteReader();
            while (fr.Read())
            {
                var fuid = fr.GetString(0);
                if (!filesByUid.TryGetValue(fuid, out var fl)) { fl = new List<string>(); filesByUid[fuid] = fl; }
                fl.Add(fr.GetString(1));
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT taketop_uid,taketop_task_uid,taketop_date,taketop_by_user,taketop_content,taketop_time,taketop_files FROM taketop_feedback WHERE taketop_owner=$o ORDER BY taketop_task_uid, taketop_time;";
            cmd.Parameters.AddWithValue("$o", owner);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fuid = r.IsDBNull(0) ? "" : r.GetString(0);
                var uid = r.IsDBNull(1) ? "" : r.GetString(1); // the task uid this feedback belongs to
                if (string.IsNullOrWhiteSpace(uid)) continue; // orphaned: task no longer exists
                if (!filesByUid.TryGetValue(fuid, out var files))
                {
                    var legacy = r.GetString(6);
                    files = string.IsNullOrWhiteSpace(legacy)
                        ? new List<string>()
                        : legacy.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                }
                if (!dict.TryGetValue(uid, out var list)) { list = new List<FeedbackEntry>(); dict[uid] = list; }
                list.Add(new FeedbackEntry
                {
                    Uid = fuid,
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

    private static void WriteFeedback(SqliteConnection conn, string owner, Dictionary<string, List<FeedbackEntry>> dict)
    {
        using var tx = conn.BeginTransaction();
        // Remove this owner's feedback and their normalized attachment rows. Each
        // entry keeps its uid (generated once and carried in memory), so a re-save
        // rewrites the same feedback_uid links instead of minting new ids.
        using (var df = conn.CreateCommand())
        {
            df.Transaction = tx;
            df.CommandText = "DELETE FROM taketop_feedback_files WHERE taketop_feedback_uid IN (SELECT taketop_uid FROM taketop_feedback WHERE taketop_owner=$o);";
            df.Parameters.AddWithValue("$o", owner);
            df.ExecuteNonQuery();
        }
        using (var del = conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM taketop_feedback WHERE taketop_owner=$o;"; del.Parameters.AddWithValue("$o", owner); del.ExecuteNonQuery(); }

        foreach (var kv in dict ?? new Dictionary<string, List<FeedbackEntry>>())
        {
            foreach (var e in kv.Value ?? new List<FeedbackEntry>())
            {
                var files = e.Files ?? new List<string>();
                var fuid = string.IsNullOrWhiteSpace(e.Uid) ? NewUid() : e.Uid!;
                e.Uid = fuid;
                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO taketop_feedback(taketop_uid,taketop_owner,taketop_task_uid,taketop_date,taketop_by_user,taketop_content,taketop_time,taketop_files) VALUES($fuid,$o,$uid,$date,$by,$content,$time,$files);";
                    ins.Parameters.AddWithValue("$fuid", fuid);
                    ins.Parameters.AddWithValue("$o", owner);
                    ins.Parameters.AddWithValue("$uid", kv.Key ?? "");
                    ins.Parameters.AddWithValue("$date", e.Date ?? "");
                    ins.Parameters.AddWithValue("$by", e.By ?? "");
                    ins.Parameters.AddWithValue("$content", e.Content ?? "");
                    ins.Parameters.AddWithValue("$time", e.Time ?? "");
                    ins.Parameters.AddWithValue("$files", string.Join(",", files));
                    ins.ExecuteNonQuery();
                }
                var idx = 0;
                foreach (var name in files)
                {
                    using var inf = conn.CreateCommand();
                    inf.Transaction = tx;
                    inf.CommandText = "INSERT INTO taketop_feedback_files(taketop_feedback_uid,taketop_idx,taketop_name) VALUES($f,$idx,$name);";
                    inf.Parameters.AddWithValue("$f", fuid);
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

        if (Scalar(conn, "SELECT COUNT(*) FROM taketop_tasks WHERE taketop_owner=$o;", c => c.Parameters.AddWithValue("$o", owner)) == 0)
        {
            var tasks = ReadLegacyTasks(legacyDbPath, legacyTasksXml);
            if (tasks.Count > 0) WriteTasks(conn, owner, tasks);
        }
        if (Scalar(conn, "SELECT COUNT(*) FROM taketop_feedback WHERE taketop_owner=$o;", c => c.Parameters.AddWithValue("$o", owner)) == 0)
        {
            var fb = ReadLegacyFeedback(legacyDbPath, legacyFeedbackXml);
            if (fb.Count > 0)
            {
                // Legacy feedback is keyed by the old per-owner seq; map it onto the
                // newly-generated task uids before writing.
                var seqToUid = ReadTasks(conn, owner).ToDictionary(t => t.Seq, t => t.Uid);
                var byUid = new Dictionary<string, List<FeedbackEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in fb)
                    if (seqToUid.TryGetValue(kv.Key, out var uid)) byUid[uid] = kv.Value;
                if (byUid.Count > 0) WriteFeedback(conn, owner, byUid);
            }
        }
        MetaSet(conn, key);
        ArchiveLegacy(legacyDbPath, legacyTasksXml, legacyFeedbackXml);
    }

    private static bool MetaHas(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM taketop_meta WHERE taketop_key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() != null;
    }

    private static void MetaSet(SqliteConnection conn, string key)
        => Exec(conn, "INSERT OR REPLACE INTO taketop_meta(taketop_key,taketop_value) VALUES($k,'1');", c => c.Parameters.AddWithValue("$k", key));

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
            if (Scalar(conn, "SELECT COUNT(*) FROM taketop_users;") == 0)
            {
                var users = ReadUsersFromFile(old);
                if (users.Count > 0) WriteUsers(conn, users);
            }
            if (Scalar(conn, "SELECT COUNT(*) FROM taketop_instances;") == 0)
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
                fcmd.CommandText = "SELECT taketop_seq, taketop_rel FROM taketop_task_files ORDER BY taketop_seq, taketop_idx;";
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
                cmd.CommandText = "SELECT taketop_seq,taketop_name,taketop_type,taketop_content,taketop_status,taketop_assigned_at,taketop_created_by FROM taketop_tasks ORDER BY taketop_seq DESC;";
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
                fcmd.CommandText = "SELECT taketop_fid, taketop_name FROM taketop_feedback_files ORDER BY taketop_fid, taketop_idx;";
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
                cmd.CommandText = "SELECT taketop_id,taketop_seq,taketop_date,taketop_by_user,taketop_content,taketop_time,taketop_files FROM taketop_feedback ORDER BY taketop_seq, taketop_time;";
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

// TakeTopDSH Team — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License for more
// details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.
//
// This software is the intellectual property of 泰顶拓鼎信息科技（上海）有限公司
// (TakeTop Information Technology (Shanghai) Co., Ltd.). All rights reserved.
// A commercial license is also available; see LICENSE-COMMERCIAL.md.

using Microsoft.Data.Sqlite;

// SQLite-backed persistence for tasks & feedback, replacing the legacy XML files.
// One database per member at <workspace>/TaskData/tasks-<instId>.db. The launcher
// is the only writer (single process), so rewriting the whole table inside a
// transaction on each flush is safe and fast at this scale. Connections are pooled
// by Microsoft.Data.Sqlite and native SQLite is bundled per RID, so there is no
// external server or install (unlike PostgreSQL).
static class SqliteStore
{
    private static string ConnString(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    private static void Exec(SqliteConnection conn, string sql, Action<SqliteCommand>? bind = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection(ConnString(dbPath));
        conn.Open();
        // WAL: many readers + one writer; NORMAL is durable enough and fast;
        // busy_timeout avoids transient SQLITE_BUSY if a reader overlaps a write.
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA synchronous=NORMAL;");
        Exec(conn, "PRAGMA busy_timeout=5000;");
        EnsureSchema(conn);
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        Exec(conn, @"
CREATE TABLE IF NOT EXISTS tasks(
  seq         INTEGER PRIMARY KEY,
  name        TEXT NOT NULL DEFAULT '',
  type        TEXT NOT NULL DEFAULT '',
  content     TEXT NOT NULL DEFAULT '',
  status      TEXT NOT NULL DEFAULT 'pending',
  assigned_at TEXT NOT NULL DEFAULT '',
  created_by  TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS task_files(
  seq INTEGER NOT NULL,
  idx INTEGER NOT NULL,
  rel TEXT NOT NULL,
  PRIMARY KEY(seq, idx)
);
CREATE TABLE IF NOT EXISTS feedback(
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  seq     INTEGER NOT NULL,
  date    TEXT NOT NULL DEFAULT '',
  by_user TEXT NOT NULL DEFAULT '',
  content TEXT NOT NULL DEFAULT '',
  time    TEXT NOT NULL DEFAULT '',
  files   TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_feedback_seq ON feedback(seq);
CREATE TABLE IF NOT EXISTS feedback_files(
  fid  INTEGER NOT NULL,
  idx  INTEGER NOT NULL,
  name TEXT NOT NULL,
  PRIMARY KEY(fid, idx)
);
CREATE INDEX IF NOT EXISTS ix_feedback_files_fid ON feedback_files(fid);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL DEFAULT '');
");
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

    // ================= Tasks =================

    public static List<TaskRecord> LoadTasks(string dbPath, string xmlPath)
    {
        using var conn = Open(dbPath);
        // One-time migration from the legacy XML (marks itself so a copy of the
        // XML left on disk can never overwrite post-migration data).
        if (File.Exists(xmlPath) && !MetaHas(conn, "legacy_tasks_imported"))
        {
            var legacy = LegacyXml.ReadTasks(xmlPath);
            SaveTasks(conn, legacy);
            MetaSet(conn, "legacy_tasks_imported");
            ArchiveXml(xmlPath);
            return legacy;
        }

        var list = new List<TaskRecord>();
        var filesBySeq = new Dictionary<int, List<string>>();
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
        return list;
    }

    public static void SaveTasks(string dbPath, List<TaskRecord> tasks)
    {
        using var conn = Open(dbPath);
        SaveTasks(conn, tasks);
    }

    private static void SaveTasks(SqliteConnection conn, List<TaskRecord> tasks)
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM tasks; DELETE FROM task_files;";
            del.ExecuteNonQuery();
        }
        foreach (var t in tasks ?? new List<TaskRecord>())
        {
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT INTO tasks(seq,name,type,content,status,assigned_at,created_by) " +
                    "VALUES($seq,$name,$type,$content,$status,$at,$by);";
                ins.Parameters.AddWithValue("$seq", t.Seq);
                ins.Parameters.AddWithValue("$name", t.Name ?? "");
                ins.Parameters.AddWithValue("$type", t.Type ?? "");
                ins.Parameters.AddWithValue("$content", t.Content ?? "");
                ins.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(t.Status) ? "pending" : t.Status!);
                ins.Parameters.AddWithValue("$at", t.AssignedAt ?? "");
                ins.Parameters.AddWithValue("$by", t.CreatedBy ?? "");
                ins.ExecuteNonQuery();
            }
            var idx = 0;
            foreach (var f in t.Files ?? new List<string>())
            {
                using var inf = conn.CreateCommand();
                inf.Transaction = tx;
                inf.CommandText = "INSERT INTO task_files(seq,idx,rel) VALUES($seq,$idx,$rel);";
                inf.Parameters.AddWithValue("$seq", t.Seq);
                inf.Parameters.AddWithValue("$idx", idx++);
                inf.Parameters.AddWithValue("$rel", f ?? "");
                inf.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    // ================= Feedback =================

    public static Dictionary<int, List<FeedbackEntry>> LoadFeedback(string dbPath, string xmlPath)
    {
        using var conn = Open(dbPath);
        if (File.Exists(xmlPath) && !MetaHas(conn, "legacy_feedback_imported"))
        {
            var legacy = LegacyXml.ReadFeedback(xmlPath);
            SaveFeedback(conn, legacy);
            MetaSet(conn, "legacy_feedback_imported");
            ArchiveXml(xmlPath);
            return legacy;
        }

        var dict = new Dictionary<int, List<FeedbackEntry>>();
        NormalizeFeedbackFiles(conn);

        // Normalized per-file rows (one row per attachment).
        var filesByFid = new Dictionary<long, List<string>>();
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.CommandText = "SELECT fid, name FROM feedback_files ORDER BY fid, idx;";
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
            cmd.CommandText = "SELECT id,seq,date,by_user,content,time,files FROM feedback ORDER BY seq, time;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fid = r.GetInt64(0);
                var seq = r.GetInt32(1);
                // Prefer feedback_files; fall back to the legacy comma column for
                // any row not normalized yet (self-heals until the next save).
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

    public static void SaveFeedback(string dbPath, Dictionary<int, List<FeedbackEntry>> dict)
    {
        using var conn = Open(dbPath);
        SaveFeedback(conn, dict);
    }

    private static void SaveFeedback(SqliteConnection conn, Dictionary<int, List<FeedbackEntry>> dict)
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM feedback; DELETE FROM feedback_files;";
            del.ExecuteNonQuery();
        }
        foreach (var kv in dict ?? new Dictionary<int, List<FeedbackEntry>>())
        {
            foreach (var e in kv.Value ?? new List<FeedbackEntry>())
            {
                var files = e.Files ?? new List<string>();
                using (var ins = conn.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText =
                        "INSERT INTO feedback(seq,date,by_user,content,time,files) " +
                        "VALUES($seq,$date,$by,$content,$time,$files);";
                    ins.Parameters.AddWithValue("$seq", kv.Key);
                    ins.Parameters.AddWithValue("$date", e.Date ?? "");
                    ins.Parameters.AddWithValue("$by", e.By ?? "");
                    ins.Parameters.AddWithValue("$content", e.Content ?? "");
                    ins.Parameters.AddWithValue("$time", e.Time ?? "");
                    // Dual-write the legacy comma column too, so an older build (or a
                    // rollback) can still read the attachment names.
                    ins.Parameters.AddWithValue("$files", string.Join(",", files));
                    ins.ExecuteNonQuery();
                }
                long fid;
                using (var idc = conn.CreateCommand())
                {
                    idc.Transaction = tx;
                    idc.CommandText = "SELECT last_insert_rowid();";
                    fid = (long)idc.ExecuteScalar()!;
                }
                var idx = 0;
                foreach (var name in files)
                {
                    using var inf = conn.CreateCommand();
                    inf.Transaction = tx;
                    inf.CommandText = "INSERT INTO feedback_files(fid,idx,name) VALUES($fid,$idx,$name);";
                    inf.Parameters.AddWithValue("$fid", fid);
                    inf.Parameters.AddWithValue("$idx", idx++);
                    inf.Parameters.AddWithValue("$name", name ?? "");
                    inf.ExecuteNonQuery();
                }
            }
        }
        tx.Commit();
    }

    // One-time normalization: split the legacy comma-separated feedback.files
    // column into feedback_files rows for databases created before that table
    // existed. Rows that already have feedback_files entries are left untouched
    // (the comma column is lossy for names containing a comma). Idempotent.
    private static void NormalizeFeedbackFiles(SqliteConnection conn)
    {
        if (MetaHas(conn, "feedback_files_normalized")) return;

        var already = new HashSet<long>();
        using (var have = conn.CreateCommand())
        {
            have.CommandText = "SELECT DISTINCT fid FROM feedback_files;";
            using var hr = have.ExecuteReader();
            while (hr.Read()) already.Add(hr.GetInt64(0));
        }

        var rows = new List<(long Fid, string Files)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, files FROM feedback WHERE files <> '';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
        }
        foreach (var (fid, files) in rows)
        {
            if (already.Contains(fid)) continue;
            var idx = 0;
            foreach (var name in files.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                Exec(conn, "INSERT OR IGNORE INTO feedback_files(fid,idx,name) VALUES($fid,$idx,$name);",
                    c =>
                    {
                        c.Parameters.AddWithValue("$fid", fid);
                        c.Parameters.AddWithValue("$idx", idx);
                        c.Parameters.AddWithValue("$name", name);
                    });
                idx++;
            }
        }
        MetaSet(conn, "feedback_files_normalized");
    }

    // Keep the old XML beside the new DB (renamed, never deleted) so a rollback
    // is possible if needed.
    private static void ArchiveXml(string xmlPath)
    {
        try
        {
            var bak = xmlPath + ".bak";
            if (File.Exists(bak)) bak = xmlPath + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
            File.Move(xmlPath, bak);
            Console.WriteLine($"[sqlite] migrated {xmlPath} -> {Path.GetFileName(bak)}");
        }
        catch (Exception ex) { Console.WriteLine("[sqlite] migrate archive: " + ex.Message); }
    }
}

// Readers for the legacy XML format, used only once to import existing data.
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
        catch (Exception ex) { Console.WriteLine("[sqlite] legacy tasks read: " + ex.Message); }
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
        catch (Exception ex) { Console.WriteLine("[sqlite] legacy feedback read: " + ex.Message); }
        return dict;
    }
}

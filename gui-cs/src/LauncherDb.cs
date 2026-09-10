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

// Central SQLite database for launcher-global data: accounts and instance
// definitions, replacing config/users.json and config/instances.json.
//
// The launcher is the ONLY writer of this file (a single process), so rewriting a
// table inside a transaction on each change is safe. DSH itself never reads this
// database — it only consumes the ports/workspaces/credentials the launcher
// derives from these rows — so switching the storage backend does not affect DSH.
public static class LauncherDb
{
    public static string PathFor(string root) => System.IO.Path.Combine(root, "config", "launcher.db");

    private static string ConnString(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection(ConnString(dbPath));
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA synchronous=NORMAL;");
        Exec(conn, "PRAGMA busy_timeout=5000;");
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
");
        return conn;
    }

    // ================= Users =================

    public static List<LauncherUserRow> LoadUsers(string dbPath)
    {
        var list = new List<LauncherUserRow>();
        using var conn = Open(dbPath);
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

    public static void SaveUsers(string dbPath, List<LauncherUserRow> users)
    {
        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM users;";
            del.ExecuteNonQuery();
        }
        foreach (var u in users ?? new List<LauncherUserRow>())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT OR REPLACE INTO users(username,password_hash,salt,iterations,admin,instance_id) " +
                "VALUES($u,$p,$s,$i,$a,$id);";
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

    public static List<LauncherInstanceRow> LoadInstances(string dbPath)
    {
        var list = new List<LauncherInstanceRow>();
        using var conn = Open(dbPath);
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

    public static void SaveInstances(string dbPath, List<LauncherInstanceRow> instances)
    {
        using var conn = Open(dbPath);
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM instances;";
            del.ExecuteNonQuery();
        }
        foreach (var it in instances ?? new List<LauncherInstanceRow>())
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText =
                "INSERT OR REPLACE INTO instances(id,name,dsh_port,workspace,os_password,token_url,running) " +
                "VALUES($id,$n,$p,$w,$op,$tk,$r);";
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

    // Keep the legacy JSON beside the new DB (renamed, never deleted) for rollback.
    public static void ArchiveJson(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath)) return;
            var bak = jsonPath + ".bak";
            if (File.Exists(bak)) bak = jsonPath + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
            File.Move(jsonPath, bak);
            Console.WriteLine($"[launcherdb] migrated {jsonPath} -> {Path.GetFileName(bak)}");
        }
        catch (Exception ex) { Console.WriteLine("[launcherdb] migrate archive: " + ex.Message); }
    }
}

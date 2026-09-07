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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TakeTopDshLauncher;

// Authentication for the launcher control page: user accounts, PBKDF2 hashed
// passwords, simple server-side sessions keyed by a cookie token.
// Accounts: admin (pre-provisioned) + per-user accounts bound to an instance.
public class AuthService
{
    public class User
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";    // Base64(PBKDF2)
        public string Salt { get; set; } = "";            // Base64
        public int Iterations { get; set; } = 100000;
        public bool Admin { get; set; }
        public string InstanceId { get; set; } = "";      // bound instance ("" for admin)
    }

    private readonly string _usersFile;
    private readonly List<User> _users = new();
    private readonly Dictionary<string, string> _sessions = new(); // token -> username
    private readonly object _gate = new();
    private const string AdminUser = "admin";
    private const string AdminPass = "admin123";

    public AuthService(string root)
    {
        _usersFile = Path.Combine(root, "config", "users.json");
        Load();
        // Ensure a default admin account exists.
        if (!_users.Any(u => u.Username.Equals(AdminUser, StringComparison.OrdinalIgnoreCase)))
        {
            var salt = GenerateSalt();
            _users.Add(new User
            {
                Username = AdminUser,
                Salt = Convert.ToBase64String(salt),
                Iterations = 100000,
                PasswordHash = Convert.ToBase64String(PBKDF2(AdminPass, salt, 100000)),
                Admin = true,
                InstanceId = "",
            });
            Save();
        }
    }

    // Repair orphan instances: if an instance exists without a matching user
    // account, auto-create one with a default password so the user can log in
    // and reset their password.
    public void RepairOrphanInstances(IReadOnlyList<InstanceManager.Instance> instances)
    {
        lock (_gate)
        {
            bool changed = false;
            foreach (var inst in instances)
            {
                if (string.IsNullOrWhiteSpace(inst.Id)) continue;
                if (_users.Any(u => u.Username.Equals(inst.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                // Instance has no user account — create one with a default password.
                var salt = GenerateSalt();
                _users.Add(new User
                {
                    Username = inst.Id,
                    Salt = Convert.ToBase64String(salt),
                    Iterations = 100000,
                    PasswordHash = Convert.ToBase64String(PBKDF2("123456", salt, 100000)),
                    Admin = false,
                    InstanceId = inst.Id,
                });
                changed = true;
            }
            if (changed) Save();
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            _users.Clear();
            if (File.Exists(_usersFile))
            {
                try
                {
                    var doc = JsonDocument.Parse(File.ReadAllText(_usersFile));
                    if (doc.RootElement.TryGetProperty("users", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            _users.Add(new User
                            {
                                Username = (el.TryGetProperty("Username", out var uP) ? uP.GetString() : null)
                                           ?? (el.TryGetProperty("username", out var uL) ? uL.GetString() : null)
                                           ?? "",
                                PasswordHash = (el.TryGetProperty("PasswordHash", out var pH) ? pH.GetString() : null)
                                               ?? (el.TryGetProperty("passwordHash", out var pL) ? pL.GetString() : null)
                                               ?? "",
                                Salt = (el.TryGetProperty("Salt", out var sH) ? sH.GetString() : null)
                                       ?? (el.TryGetProperty("salt", out var sL) ? sL.GetString() : null)
                                       ?? "",
                                Iterations = (el.TryGetProperty("Iterations", out var iH) && iH.GetInt32() != 0) ? iH.GetInt32()
                                             : (el.TryGetProperty("iterations", out var iL) ? iL.GetInt32() : 100000),
                                Admin = (el.TryGetProperty("Admin", out var aH) && aH.GetBoolean())
                                        || (el.TryGetProperty("admin", out var aL) && aL.GetBoolean()),
                                InstanceId = (el.TryGetProperty("InstanceId", out var iiH) ? iiH.GetString() : null)
                                             ?? (el.TryGetProperty("instanceId", out var iiL) ? iiL.GetString() : null)
                                             ?? "",
                            });
                            // Skip entries with empty username (orphan from past bugs).
                            if (string.IsNullOrWhiteSpace(_users.Last().Username))
                                _users.RemoveAt(_users.Count - 1);
                        }
                    }
                }
                catch { }
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_usersFile)!);
            var obj = new { users = _users.Select(u => new { u.Username, u.PasswordHash, u.Salt, u.Iterations, u.Admin, u.InstanceId }) };
            File.WriteAllText(_usersFile, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public User? Find(string username) => _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<User> List() => _users.ToList();

    // Validate account username: letters (a-z A-Z), digits allowed but not
    // purely digits, and no CJK/Chinese characters. Keep ASCII letters + optional
    // dots/underscores/dashes; no Chinese.
    public static bool IsValidUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        if (Regex.IsMatch(username, @"[\u4e00-\u9fff]")) return false;  // Chinese chars
        if (!Regex.IsMatch(username, @"^[A-Za-z][A-Za-z0-9._-]*$")) return false; // must start with letter
        return true;
    }

    public string? CreateUser(string username, string password, string instanceId, bool admin = false)
    {
        username = username.ToLowerInvariant();
        lock (_gate)
        {
            if (!IsValidUsername(username))
                return "账号须以字母开头，仅含字母/数字/._-，不含汉字或纯数字";
            if (string.IsNullOrEmpty(password) || password.Length < 6)
                return "密码至少6位";
            if (_users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                return "账号已存在";
            var salt = GenerateSalt();
            _users.Add(new User
            {
                Username = username,
                Salt = Convert.ToBase64String(salt),
                Iterations = 100000,
                PasswordHash = Convert.ToBase64String(PBKDF2(password, salt, 100000)),
                Admin = admin,
                InstanceId = instanceId ?? username,
            });
            Save();
            return null;
        }
    }

    public bool DeleteUser(string username, string? actorUsername)
    {
        lock (_gate)
        {
            if (string.Equals(username, AdminUser, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(username, actorUsername, StringComparison.OrdinalIgnoreCase)) return false;
            var u = Find(username);
            if (u == null) return false;
            _users.Remove(u);
            Save();
            return true;
        }
    }

    public bool Verify(string username, string password)
    {
        var u = Find(username);
        if (u == null) return false;
        var hash = PBKDF2(password, Convert.FromBase64String(u.Salt), u.Iterations);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(u.PasswordHash));
    }

    public bool ChangePassword(string username, string newPassword)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) return false;
            var salt = GenerateSalt();
            u.Salt = Convert.ToBase64String(salt);
            u.Iterations = 100000;
            u.PasswordHash = Convert.ToBase64String(PBKDF2(newPassword, salt, 100000));
            Save();
            return true;
        }
    }

    // User changes own password: must verify old password first.
    public string? ChangePasswordWithOld(string username, string oldPassword, string newPassword)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) return "用户不存在";
            if (!Verify(username, oldPassword)) return "旧密码错误";
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6) return "新密码至少6位";
            var salt = GenerateSalt();
            u.Salt = Convert.ToBase64String(salt);
            u.Iterations = 100000;
            u.PasswordHash = Convert.ToBase64String(PBKDF2(newPassword, salt, 100000));
            Save();
            return null;
        }
    }

    // Admin resets any user's password (no old password needed).
    // If the user account doesn't exist, auto-creates it (repairs orphan instance).
    public string? ResetPassword(string username, string newPassword, string instanceId = "")
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6) return "新密码至少6位";
            var u = Find(username);
            if (u == null)
            {
                // Auto-create missing user account (bind to instance).
                var salt2 = GenerateSalt();
                _users.Add(new User
                {
                    Username = username,
                    Salt = Convert.ToBase64String(salt2),
                    Iterations = 100000,
                    PasswordHash = Convert.ToBase64String(PBKDF2(newPassword, salt2, 100000)),
                    Admin = false,
                    InstanceId = string.IsNullOrEmpty(instanceId) ? username : instanceId,
                });
                Save();
                return null;
            }
            var salt = GenerateSalt();
            u.Salt = Convert.ToBase64String(salt);
            u.Iterations = 100000;
            u.PasswordHash = Convert.ToBase64String(PBKDF2(newPassword, salt, 100000));
            Save();
            return null;
        }
    }

    // --- Session (cookie token -> username) ---
    public string CreateSession(string username)
    {
        username = username.ToLowerInvariant();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (_gate) { _sessions[token] = username; }
        return token;
    }

    public string? ValidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_gate) { return _sessions.TryGetValue(token, out var u) ? u : null; }
    }

    public void DestroySession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (_gate) { _sessions.Remove(token); }
    }

    private static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(16);

    private static byte[] PBKDF2(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}

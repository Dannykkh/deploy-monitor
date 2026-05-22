using System;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DeployMonitor.Web.Auth
{
    public class SqliteUserStore
    {
        private readonly string _connectionString;

        public SqliteUserStore(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public bool ValidateCredentials(string username, string password)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT password_hash FROM users WHERE username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            var hash = cmd.ExecuteScalar() as string;
            if (hash == null) return false;
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }

        public bool ChangePassword(string username, string oldPassword, string newPassword)
        {
            var ok = ChangeCredentials(username, oldPassword, null, newPassword, out _, out _);
            return ok;
        }

        public bool ChangeCredentials(
            string currentUsername,
            string oldPassword,
            string? newUsername,
            string? newPassword,
            out string error,
            out string updatedUsername)
        {
            error = "";
            updatedUsername = currentUsername;

            if (!ValidateCredentials(currentUsername, oldPassword))
            {
                error = "현재 비밀번호가 올바르지 않습니다.";
                return false;
            }

            var candidateUsername = string.IsNullOrWhiteSpace(newUsername)
                ? currentUsername
                : newUsername.Trim();
            var hasUsernameChange = !string.Equals(candidateUsername, currentUsername, StringComparison.OrdinalIgnoreCase);
            var hasPasswordChange = !string.IsNullOrWhiteSpace(newPassword);

            if (!hasUsernameChange && !hasPasswordChange)
            {
                error = "변경할 항목이 없습니다.";
                return false;
            }

            if (hasUsernameChange && !IsValidUsername(candidateUsername))
            {
                error = "아이디는 3~40자, 영문/숫자/.-_ 만 사용할 수 있습니다.";
                return false;
            }

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                if (hasUsernameChange && UserExists(conn, candidateUsername))
                {
                    error = "이미 사용 중인 아이디입니다.";
                    tx.Rollback();
                    return false;
                }

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (hasUsernameChange && hasPasswordChange)
                {
                    cmd.CommandText = "UPDATE users SET username = @nu, password_hash = @h WHERE username = @u";
                    cmd.Parameters.AddWithValue("@nu", candidateUsername);
                    cmd.Parameters.AddWithValue("@h", BCrypt.Net.BCrypt.HashPassword(newPassword!, 12));
                    cmd.Parameters.AddWithValue("@u", currentUsername);
                }
                else if (hasUsernameChange)
                {
                    cmd.CommandText = "UPDATE users SET username = @nu WHERE username = @u";
                    cmd.Parameters.AddWithValue("@nu", candidateUsername);
                    cmd.Parameters.AddWithValue("@u", currentUsername);
                }
                else
                {
                    cmd.CommandText = "UPDATE users SET password_hash = @h WHERE username = @u";
                    cmd.Parameters.AddWithValue("@h", BCrypt.Net.BCrypt.HashPassword(newPassword!, 12));
                    cmd.Parameters.AddWithValue("@u", currentUsername);
                }

                var affected = cmd.ExecuteNonQuery();
                if (affected <= 0)
                {
                    error = "계정 정보를 업데이트하지 못했습니다.";
                    tx.Rollback();
                    return false;
                }

                tx.Commit();
                updatedUsername = candidateUsername;
                return true;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                error = "계정 정보 변경 중 오류가 발생했습니다.";
                return false;
            }
        }

        private static bool IsValidUsername(string username)
        {
            return Regex.IsMatch(username ?? "", @"^[a-zA-Z0-9._-]{3,40}$");
        }

        private static bool UserExists(SqliteConnection conn, string username)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            var count = (long)(cmd.ExecuteScalar() ?? 0L);
            return count > 0;
        }

        public void UpdateLastLogin(string username)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET last_login = datetime('now') WHERE username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            cmd.ExecuteNonQuery();
        }
    }
}

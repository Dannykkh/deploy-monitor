using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace DeployMonitor.Web.Data
{
    public static class SqliteDbInitializer
    {
        public static string Initialize()
        {
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deploy-monitor.db");
            var connectionString = $"Data Source={dbPath}";

            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS users (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    username      TEXT NOT NULL UNIQUE,
                    password_hash TEXT NOT NULL,
                    created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                    last_login    TEXT
                );

                CREATE TABLE IF NOT EXISTS deploy_history (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_name TEXT NOT NULL,
                    commit_hash  TEXT,
                    status       TEXT NOT NULL,
                    started_at   TEXT NOT NULL,
                    finished_at  TEXT,
                    duration_sec REAL,
                    log_summary  TEXT,
                    trigger_type TEXT NOT NULL DEFAULT 'auto'
                );
            ";
            cmd.ExecuteNonQuery();

            // Seed admin account if not exists
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = 'admin'";
            var count = (long)checkCmd.ExecuteScalar()!;

            if (count == 0)
            {
                var (initialPassword, fromEnvironment) = ResolveInitialAdminPassword();

                using var insertCmd = conn.CreateCommand();
                insertCmd.CommandText = "INSERT INTO users (username, password_hash) VALUES ('admin', @h)";
                insertCmd.Parameters.AddWithValue("@h", BCrypt.Net.BCrypt.HashPassword(initialPassword, 12));
                insertCmd.ExecuteNonQuery();

                if (!fromEnvironment)
                    WriteInitialPasswordFile(initialPassword);
            }

            return dbPath;
        }

        private static (string password, bool fromEnvironment) ResolveInitialAdminPassword()
        {
            var envPassword = Environment.GetEnvironmentVariable("DEPLOY_MONITOR_ADMIN_PASSWORD");
            if (!string.IsNullOrWhiteSpace(envPassword))
                return (envPassword.Trim(), true);

            return ("admin1234", false);
        }

        private static void WriteInitialPasswordFile(string password)
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "admin-initial-password.txt");
                var content = string.Join(Environment.NewLine, new[]
                {
                    "Deploy Monitor initial admin credential",
                    $"created_at={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    "username=admin",
                    $"password={password}",
                    "note=로그인 후 즉시 비밀번호를 변경하세요."
                });
                File.WriteAllText(path, content);
            }
            catch
            {
                // 파일 기록 실패 시에도 DB 초기화는 계속 진행
            }
        }
    }
}

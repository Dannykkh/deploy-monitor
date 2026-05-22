using System;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace DeployMonitor.Views
{
    public partial class AccountWindow : Window
    {
        public AccountWindow()
        {
            InitializeComponent();
            LoadCurrentUsername();
        }

        private string GetDbPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deploy-monitor.db");
        }

        private void LoadCurrentUsername()
        {
            try
            {
                var dbPath = GetDbPath();
                if (!File.Exists(dbPath)) return;

                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT username FROM users LIMIT 1";
                var result = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrEmpty(result))
                    UsernameBox.Text = result;
            }
            catch
            {
                // DB가 아직 없을 수 있음
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var username = UsernameBox.Text?.Trim();
            var password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username))
            {
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                StatusText.Text = "아이디를 입력하세요.";
                return;
            }

            if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            {
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                StatusText.Text = "비밀번호는 4자 이상이어야 합니다.";
                return;
            }

            try
            {
                var dbPath = GetDbPath();
                if (!File.Exists(dbPath))
                {
                    StatusText.Foreground = System.Windows.Media.Brushes.Red;
                    StatusText.Text = "DB 파일이 없습니다. 프로그램을 먼저 실행해주세요.";
                    return;
                }

                var hash = BCrypt.Net.BCrypt.HashPassword(password, 12);

                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();

                // 기존 사용자가 있는지 확인
                using var countCmd = conn.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM users";
                var count = (long)countCmd.ExecuteScalar()!;

                if (count == 0)
                {
                    // 사용자가 없으면 새로 추가
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = "INSERT INTO users (username, password_hash) VALUES (@u, @h)";
                    insertCmd.Parameters.AddWithValue("@u", username);
                    insertCmd.Parameters.AddWithValue("@h", hash);
                    insertCmd.ExecuteNonQuery();
                }
                else
                {
                    // 기존 사용자 업데이트 (첫 번째 사용자)
                    using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = "UPDATE users SET username = @u, password_hash = @h WHERE id = (SELECT id FROM users LIMIT 1)";
                    updateCmd.Parameters.AddWithValue("@u", username);
                    updateCmd.Parameters.AddWithValue("@h", hash);
                    updateCmd.ExecuteNonQuery();
                }

                StatusText.Foreground = System.Windows.Media.Brushes.Green;
                StatusText.Text = "계정이 변경되었습니다.";
            }
            catch (Exception ex)
            {
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                StatusText.Text = $"오류: {ex.Message}";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}

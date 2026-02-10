using System;
using System.IO;
using System.Text.Json;

namespace DeployMonitor.Models
{
    /// <summary>
    /// 앱 설정 (settings.json으로 저장/로드)
    /// </summary>
    public class AppSettings
    {
        /// <summary>Bonobo bare repo 폴더 경로</summary>
        public string RepositoryFolder { get; set; } = @"C:\Bonobo.Git.Server\App_Data\Repositories";

        /// <summary>작업 복사본(deploy) 폴더 경로</summary>
        public string DeployFolder { get; set; } = @"D:\deploy";

        /// <summary>폴링 주기 (초)</summary>
        public int IntervalSeconds { get; set; } = 30;

        /// <summary>기본 감시 브랜치</summary>
        public string DefaultBranch { get; set; } = "master";

        /// <summary>프로그램 시작 시 자동 감시 여부</summary>
        public bool AutoStart { get; set; } = false;

        // --- 저장/로드 ---

        private static string GetSettingsPath()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, "settings.json");
        }

        public static AppSettings Load()
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return new AppSettings();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            var path = GetSettingsPath();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(path, json);
        }
    }
}

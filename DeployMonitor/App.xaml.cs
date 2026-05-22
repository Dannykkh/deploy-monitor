using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DeployMonitor.Models;
using Hardcodet.Wpf.TaskbarNotification;
using DeployMonitor.ViewModels;
using DeployMonitor.Web;

namespace DeployMonitor
{
    public partial class App : Application
    {
        private TaskbarIcon? _trayIcon;
        private MainViewModel? _mainViewModel;
        private WebServerHost? _webServer;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Git safe.directory 설정 (다른 사용자 소유 저장소 접근 허용)
            ConfigureGitSafeDirectory();

            // 글로벌 예외 핸들러 (크래시 로그)
            DispatcherUnhandledException += (_, args) =>
            {
                LogCrash("DispatcherUnhandled", args.Exception);
                args.Handled = true; // 크래시 방지
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                    LogCrash("AppDomain", ex);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogCrash("UnobservedTask", args.Exception);
                args.SetObserved();
            };

            // MainViewModel 생성 (앱 전체에서 공유)
            _mainViewModel = new MainViewModel();

            // 웹 대시보드 서버 시작 (백그라운드)
            _ = Task.Run(async () =>
            {
                try
                {
                    var settings = AppSettings.Load();
                    _webServer = new WebServerHost();
                    await _webServer.StartAsync(_mainViewModel, settings.WebPort, settings.WebListenAnyIP);
                }
                catch (Exception ex)
                {
                    LogCrash("WebServer", ex);
                }
            });

            InitializeTrayIcon();
            ShowMainWindow();
        }

        private static void LogCrash(string source, Exception ex)
        {
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                var msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\n\n";
                File.AppendAllText(logPath, msg);
            }
            catch { }
        }

        /// <summary>시스템 트레이 아이콘 초기화</summary>
        private void InitializeTrayIcon()
        {
            try
            {
                _trayIcon = new TaskbarIcon
                {
                    ToolTipText = "Git Deploy Monitor",
                    Visibility = Visibility.Visible
                };

                // 아이콘 설정
                try
                {
                    var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
                    if (File.Exists(iconPath))
                        _trayIcon.Icon = new Icon(iconPath);
                    else
                        _trayIcon.Icon = SystemIcons.Application;
                }
                catch
                {
                    _trayIcon.Icon = SystemIcons.Application;
                }

            // 더블클릭 → 창 열기
            _trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();

            // 우클릭 컨텍스트 메뉴
            var menu = new System.Windows.Controls.ContextMenu();

            var openItem = new System.Windows.Controls.MenuItem { Header = "열기" };
            openItem.Click += (_, _) => ShowMainWindow();
            menu.Items.Add(openItem);

            var watchItem = new System.Windows.Controls.MenuItem { Header = "감시 시작/중지" };
            watchItem.Click += (_, _) =>
            {
                var mainWindow = MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.ToggleWatch();
                    return;
                }

                if (_mainViewModel?.StartStopCommand.CanExecute(null) == true)
                    _mainViewModel.StartStopCommand.Execute(null);
            };
            menu.Items.Add(watchItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "종료" };
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenu = menu;
            }
            catch (Exception ex)
            {
                LogCrash("InitializeTrayIcon", ex);
            }
        }

        /// <summary>메인 창 표시</summary>
        private void ShowMainWindow()
        {
            if (MainWindow == null)
            {
                MainWindow = new MainWindow(_mainViewModel!);
            }
            MainWindow.Show();
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        }

        /// <summary>트레이 풍선 알림 표시</summary>
        public void ShowBalloon(string title, string message, bool isSuccess)
        {
            var icon = isSuccess ? BalloonIcon.Info : BalloonIcon.Error;
            _trayIcon?.ShowBalloonTip(title, message, icon);
        }

        /// <summary>앱 종료</summary>
        private async void ExitApplication()
        {
            // 웹 서버 종료
            if (_webServer != null)
            {
                try { await _webServer.StopAsync(); } catch { }
            }

            var mainWindow = MainWindow as MainWindow;
            mainWindow?.PrepareExit();

            _mainViewModel?.Dispose();
            _trayIcon?.Dispose();
            _trayIcon = null;

            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _trayIcon?.Dispose();
            base.OnExit(e);
        }

        /// <summary>Git safe.directory 설정 (다른 사용자 소유 저장소 접근 허용)</summary>
        private static void ConfigureGitSafeDirectory()
        {
            // 백그라운드에서 실행 (UI 블로킹 방지)
            Task.Run(() =>
            {
                try
                {
                    var settings = AppSettings.Load();
                    var configured = GetConfiguredSafeDirectories();

                    // 기존 와일드카드 설정 제거 (과도한 권한)
                    if (configured.Contains("*"))
                    {
                        RunGit("config --global --unset-all safe.directory \"*\"");
                        configured.Remove("*");
                    }

                    var candidates = CollectSafeDirectories(settings);
                    if (candidates.Count == 0) return;

                    foreach (var path in candidates)
                    {
                        if (configured.Contains(path)) continue;
                        RunGit($"config --global --add safe.directory \"{path}\"");
                    }
                }
                catch
                {
                    // Git이 설치되지 않은 경우 무시
                }
            });
        }

        private static HashSet<string> CollectSafeDirectories(AppSettings settings)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDirectoryIfExists(result, settings.RepositoryFolder);
            AddDirectoryIfExists(result, settings.DeployFolder);
            AddFirstLevelSubdirectories(result, settings.RepositoryFolder);
            AddFirstLevelSubdirectories(result, settings.DeployFolder);
            return result;
        }

        private static void AddFirstLevelSubdirectories(HashSet<string> result, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return;

            try
            {
                var fullRoot = Path.GetFullPath(rootPath);
                if (!Directory.Exists(fullRoot)) return;

                foreach (var dir in Directory.GetDirectories(fullRoot))
                    AddDirectoryIfExists(result, dir);
            }
            catch
            {
                // 디렉터리 열거 실패는 무시
            }
        }

        private static void AddDirectoryIfExists(HashSet<string> result, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath))
                    result.Add(fullPath);
            }
            catch
            {
                // 경로 정규화 실패는 무시
            }
        }

        private static HashSet<string> GetConfiguredSafeDirectories()
        {
            var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var (exitCode, output, _) = RunGit("config --global --get-all safe.directory");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return configured;

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                configured.Add(line.Trim());

            return configured;
        }

        private static (int exitCode, string output, string error) RunGit(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, "", "Failed to start git process.");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return (process.ExitCode, output, error);
        }
    }
}

using System;
using System.Drawing;
using System.IO;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace DeployMonitor
{
    public partial class App : Application
    {
        private TaskbarIcon? _trayIcon;

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

            InitializeTrayIcon();
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
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Git Deploy Monitor",
                Visibility = Visibility.Visible
            };

            // 아이콘 설정
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                _trayIcon.Icon = new Icon(iconPath);
            }
            else
            {
                // 기본 아이콘 (앱 아이콘 사용)
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
                mainWindow?.ToggleWatch();
            };
            menu.Items.Add(watchItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "종료" };
            exitItem.Click += (_, _) => ExitApplication();
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenu = menu;
        }

        /// <summary>메인 창 표시</summary>
        private void ShowMainWindow()
        {
            if (MainWindow == null)
            {
                MainWindow = new MainWindow();
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
        private void ExitApplication()
        {
            var mainWindow = MainWindow as MainWindow;
            mainWindow?.ExitApplication();

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
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "config --global --add safe.directory \"*\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(5000);
            }
            catch
            {
                // Git이 설치되지 않은 경우 무시
            }
        }
    }
}

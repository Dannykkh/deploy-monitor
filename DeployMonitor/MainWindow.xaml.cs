using System.ComponentModel;
using System.Windows;
using DeployMonitor.ViewModels;

namespace DeployMonitor
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _isExiting;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // 로그 자동 스크롤
            _viewModel.WatchLogs.CollectionChanged += (_, _) =>
            {
                if (WatchLogListBox.Items.Count > 0)
                    WatchLogListBox.ScrollIntoView(WatchLogListBox.Items[^1]);
            };
            _viewModel.DeployLogs.CollectionChanged += (_, _) =>
            {
                if (DeployLogListBox.Items.Count > 0)
                    DeployLogListBox.ScrollIntoView(DeployLogListBox.Items[^1]);
            };

            // 배포 완료 시 트레이 알림
            _viewModel.DeployFinished += OnDeployFinished;
        }

        /// <summary>배포 완료 알림</summary>
        private void OnDeployFinished(string projectName, bool success)
        {
            var app = Application.Current as App;
            if (app == null) return;

            var title = success ? "배포 완료" : "배포 실패";
            var message = success
                ? $"{projectName} 배포가 완료되었습니다."
                : $"{projectName} 배포에 실패했습니다.";

            app.ShowBalloon(title, message, success);
        }

        /// <summary>닫기 버튼 → 트레이로 최소화</summary>
        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
                var app = Application.Current as App;
                app?.ShowBalloon("Git Deploy Monitor", "트레이로 최소화되었습니다.", true);
            }
        }

        /// <summary>실제 종료 (트레이 메뉴에서 호출)</summary>
        public void ExitApplication()
        {
            _isExiting = true;
            _viewModel.Dispose();
            Close();
        }

        /// <summary>감시 시작/중지 (트레이 메뉴에서 호출)</summary>
        public void ToggleWatch()
        {
            if (_viewModel.StartStopCommand.CanExecute(null))
                _viewModel.StartStopCommand.Execute(null);
        }

        /// <summary>감시 중 여부</summary>
        public bool IsWatching => _viewModel.IsWatching;

    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeployMonitor.Models;
using DeployMonitor.ViewModels;
using DeployMonitor.Views;

namespace DeployMonitor
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _isExiting;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            // 로그 자동 스크롤
            _viewModel.WatchLogs.CollectionChanged += (_, _) =>
            {
                try
                {
                    if (WatchLogListBox?.Items.Count > 0)
                        WatchLogListBox.ScrollIntoView(WatchLogListBox.Items[^1]);
                }
                catch { }
            };
            _viewModel.DeployLogs.CollectionChanged += (_, _) =>
            {
                try
                {
                    if (DeployLogListBox?.Items.Count > 0)
                        DeployLogListBox.ScrollIntoView(DeployLogListBox.Items[^1]);
                }
                catch { }
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

        /// <summary>종료 준비 (App에서 호출, Dispose는 App이 관리)</summary>
        public void PrepareExit()
        {
            _isExiting = true;
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

        /// <summary>탭 변경 시 스크롤을 맨 아래로</summary>
        private void LogTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is not TabControl) return;

            // 약간의 지연 후 스크롤 (UI 렌더링 완료 대기)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                try
                {
                    if (WatchLogListBox?.Items.Count > 0)
                        WatchLogListBox.ScrollIntoView(WatchLogListBox.Items[^1]);
                    if (DeployLogListBox?.Items.Count > 0)
                        DeployLogListBox.ScrollIntoView(DeployLogListBox.Items[^1]);
                }
                catch { }
            });
        }

        /// <summary>계정 버튼 클릭</summary>
        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new AccountWindow { Owner = this };
            win.ShowDialog();
        }

        /// <summary>Whitelist 버튼 클릭</summary>
        private void WhitelistButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new WhitelistWindow(
                _viewModel.GlobalExitedOkContainers,
                value => _viewModel.GlobalExitedOkContainers = value)
            {
                Owner = this
            };
            win.ShowDialog();
        }

        /// <summary>프로젝트 행 더블클릭 시 상세 로그 표시</summary>
        private void ProjectGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProjectGrid.SelectedItem is not ProjectInfo project) return;

            var log = project.LastDeploymentLog;
            if (string.IsNullOrWhiteSpace(log))
            {
                MessageBox.Show("배포 로그가 없습니다.", project.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 로그 표시 창
            var logWindow = new Window
            {
                Title = $"[{project.Name}] 배포 로그",
                Width = 800,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var textBox = new TextBox
            {
                Text = log,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                Margin = new Thickness(8)
            };

            logWindow.Content = textBox;
            logWindow.Show();
        }
    }
}

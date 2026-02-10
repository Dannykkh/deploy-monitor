using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DeployMonitor.Models;
using DeployMonitor.Services;

namespace DeployMonitor.ViewModels
{
    /// <summary>
    /// 메인 뷰모델 - 프로젝트 목록, 로그, 감시 제어
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly RepoScanner _scanner = new();
        private readonly CommitWatcher _watcher = new();
        private readonly DeployRunner _runner = new();
        private AppSettings _settings;

        private string _repoFolder = "";
        private string _deployFolder = "";
        private int _intervalSeconds = 30;
        private string _defaultBranch = "master";
        private bool _isWatching;
        private string _watchButtonText = "● 감시 시작";

        // 컬렉션 동기화용 락 객체
        private readonly object _watchLogsLock = new();
        private readonly object _deployLogsLock = new();

        public MainViewModel()
        {
            _settings = AppSettings.Load();
            _repoFolder = _settings.RepositoryFolder;
            _deployFolder = _settings.DeployFolder;
            _intervalSeconds = _settings.IntervalSeconds;
            _defaultBranch = _settings.DefaultBranch;

            // 컬렉션 스레드 동기화 활성화
            BindingOperations.EnableCollectionSynchronization(WatchLogs, _watchLogsLock);
            BindingOperations.EnableCollectionSynchronization(DeployLogs, _deployLogsLock);

            // 이벤트 연결
            _scanner.DebugLog += AddWatchLog;
            _watcher.CommitDetected += OnCommitDetected;
            _watcher.LogMessage += AddWatchLog;
            _runner.LogMessage += AddDeployLog;
            _runner.DeployCompleted += OnDeployCompleted;

            // 커맨드 초기화
            StartStopCommand = new RelayCommand(ToggleWatch);
            RefreshCommand = new RelayCommand(ScanProjects);
            BrowseRepoCommand = new RelayCommand(BrowseRepoFolder);
            BrowseDeployCommand = new RelayCommand(BrowseDeployFolder);
            ManualDeployCommand = new RelayCommand(ManualDeploy);

            // 프로그램 로드 후 자동으로 감시 시작 (StartWatch에 스캔 포함)
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ToggleWatch);
        }

        // --- 프로퍼티 ---

        public ObservableCollection<ProjectInfo> Projects { get; } = new();
        public ObservableCollection<string> WatchLogs { get; } = new();
        public ObservableCollection<string> DeployLogs { get; } = new();

        public string RepoFolder
        {
            get => _repoFolder;
            set { if (SetField(ref _repoFolder, value)) AutoSave(); }
        }

        public string DeployFolder
        {
            get => _deployFolder;
            set { if (SetField(ref _deployFolder, value)) AutoSave(); }
        }

        public int IntervalSeconds
        {
            get => _intervalSeconds;
            set { if (SetField(ref _intervalSeconds, value)) AutoSave(); }
        }

        public string DefaultBranch
        {
            get => _defaultBranch;
            set { if (SetField(ref _defaultBranch, value)) AutoSave(); }
        }

        public bool IsWatching
        {
            get => _isWatching;
            private set
            {
                if (SetField(ref _isWatching, value))
                {
                    WatchButtonText = value ? "■ 감시 중지" : "● 감시 시작";
                }
            }
        }

        public string WatchButtonText
        {
            get => _watchButtonText;
            private set => SetField(ref _watchButtonText, value);
        }

        // --- 커맨드 ---

        public ICommand StartStopCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand BrowseRepoCommand { get; }
        public ICommand BrowseDeployCommand { get; }
        public ICommand ManualDeployCommand { get; }

        // --- 메서드 ---

        /// <summary>프로젝트 목록 새로고침</summary>
        private async void ScanProjects()
        {
            var wasWatching = IsWatching;
            if (wasWatching) _watcher.Stop();

            try
            {
                Projects.Clear();
                AddWatchLog("프로젝트 스캔 중...");

                // 백그라운드에서 스캔 실행 (UI 블로킹 방지)
                var repoFolder = RepoFolder;
                var deployFolder = DeployFolder;
                var branch = DefaultBranch;
                var list = await System.Threading.Tasks.Task.Run(() =>
                    _scanner.Scan(repoFolder, deployFolder, branch));

                foreach (var p in list)
                    Projects.Add(p);

                AddWatchLog($"deploy.bat 있는 프로젝트 {list.Count}개 발견");

                if (wasWatching) StartWatchInternal();
            }
            catch (Exception ex)
            {
                AddWatchLog($"스캔 오류: {ex.Message}");
            }
        }

        /// <summary>감시 시작/중지 토글</summary>
        private void ToggleWatch()
        {
            if (IsWatching)
                StopWatch();
            else
                StartWatch();
        }

        private async void StartWatch()
        {
            if (string.IsNullOrWhiteSpace(RepoFolder) || string.IsNullOrWhiteSpace(DeployFolder))
            {
                AddWatchLog("저장소 폴더와 배포 폴더를 먼저 설정하세요.");
                return;
            }

            try
            {
                // 감시 시작 전 프로젝트 목록 새로고침 (비동기)
                Projects.Clear();
                AddWatchLog("프로젝트 스캔 중...");

                var repoFolder = RepoFolder;
                var deployFolder = DeployFolder;
                var branch = DefaultBranch;
                var list = await System.Threading.Tasks.Task.Run(() =>
                    _scanner.Scan(repoFolder, deployFolder, branch));

                foreach (var p in list)
                    Projects.Add(p);

                AddWatchLog($"deploy.bat 있는 프로젝트 {list.Count}개 발견");

                StartWatchInternal();
            }
            catch (Exception ex)
            {
                AddWatchLog($"감시 시작 오류: {ex.Message}");
            }
        }

        private void StartWatchInternal()
        {
            if (Projects.Count == 0)
                AddWatchLog("deploy.bat 있는 프로젝트가 없습니다. 저장소에 deploy.bat을 추가하세요.");
            else
                AddWatchLog($"감시 대상: {Projects.Count}개");

            var projectList = new System.Collections.Generic.List<ProjectInfo>(Projects);
            _watcher.Start(projectList, IntervalSeconds);
            IsWatching = true;
        }

        private void StopWatch()
        {
            _watcher.Stop();
            IsWatching = false;
        }

        /// <summary>설정 자동 저장 (프로퍼티 변경 시 호출)</summary>
        private void AutoSave()
        {
            _settings.RepositoryFolder = RepoFolder;
            _settings.DeployFolder = DeployFolder;
            _settings.IntervalSeconds = IntervalSeconds;
            _settings.DefaultBranch = DefaultBranch;
            _settings.Save();
        }

        /// <summary>저장소 폴더 찾기</summary>
        private void BrowseRepoFolder()
        {
            var path = BrowseFolder("Bonobo bare repo 폴더 선택");
            if (!string.IsNullOrEmpty(path))
            {
                RepoFolder = path;
                ScanProjects();
            }
        }

        /// <summary>배포 폴더 찾기</summary>
        private void BrowseDeployFolder()
        {
            var path = BrowseFolder("배포(deploy) 폴더 선택");
            if (!string.IsNullOrEmpty(path))
            {
                DeployFolder = path;
                ScanProjects();
            }
        }

        /// <summary>수동 배포</summary>
        private void ManualDeploy(object? parameter)
        {
            if (parameter is ProjectInfo project && project.HasDeployBat)
            {
                AddDeployLog($"[{project.Name}] 수동 배포 시작");
                _runner.Enqueue(project);
            }
        }

        /// <summary>폴더 선택 다이얼로그</summary>
        private static string? BrowseFolder(string description)
        {
            // WPF에는 FolderBrowserDialog가 없으므로 OpenFileDialog 트릭 또는 WindowsAPICodePack 사용
            // 여기서는 Microsoft.Win32.OpenFolderDialog (.NET 8) 사용 불가 시 대체
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true
            };

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? dialog.SelectedPath
                : null;
        }

        // --- 이벤트 핸들러 ---

        /// <summary>새 커밋 감지 시 UI 업데이트 후 배포 큐에 추가</summary>
        private void OnCommitDetected(ProjectInfo project, string newHash)
        {
            // bare repo에 deploy.bat이 있는지만 확인 (복사는 DeployRunner에서 clone/pull로 처리)
            var hasDeployBat = RepoScanner.HasDeployBatInRepo(
                project.BareRepoPath,
                project.Branch,
                project.Name,
                out _);

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!hasDeployBat)
                {
                    project.HasDeployBat = false;
                    project.Status = ProjectStatus.NotConfigured;
                    project.LastMessage = "deploy.bat 없음";
                    AddWatchLog($"[{project.Name}] deploy.bat 없음 (목록에서 제거)");

                    var existing = Projects.FirstOrDefault(p => p.Name == project.Name);
                    if (existing != null)
                        Projects.Remove(existing);
                    return;
                }

                if (Projects.All(p => p.Name != project.Name))
                    Projects.Add(project);

                project.HasDeployBat = true;
                project.LastCommitHash = newHash;
                _runner.Enqueue(project);
            });
        }

        /// <summary>배포 완료 이벤트</summary>
        private void OnDeployCompleted(string projectName, bool success)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                DeployFinished?.Invoke(projectName, success);
            });
        }

        /// <summary>배포 완료 알림 (UI에서 구독)</summary>
        public event Action<string, bool>? DeployFinished;

        // 로그 버퍼 (스레드 안전) - 감시/배포 분리
        private readonly ConcurrentQueue<string> _watchLogBuffer = new();
        private readonly ConcurrentQueue<string> _deployLogBuffer = new();
        private int _watchLogFlushPending;
        private int _deployLogFlushPending;

        /// <summary>감시 로그 추가</summary>
        public void AddWatchLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            _watchLogBuffer.Enqueue($"[{timestamp}] {message}");

            if (Interlocked.CompareExchange(ref _watchLogFlushPending, 1, 0) != 0) return;

            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, FlushWatchLogBuffer);
        }

        /// <summary>배포 로그 추가</summary>
        public void AddDeployLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            _deployLogBuffer.Enqueue($"[{timestamp}] {message}");

            if (Interlocked.CompareExchange(ref _deployLogFlushPending, 1, 0) != 0) return;

            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, FlushDeployLogBuffer);
        }

        private void FlushWatchLogBuffer()
        {
            var items = new List<string>();
            while (_watchLogBuffer.TryDequeue(out var entry))
                items.Add(entry);

            lock (_watchLogsLock)
            {
                foreach (var item in items)
                    WatchLogs.Add(item);

                // 500줄 초과 시 최근 400줄만 유지
                if (WatchLogs.Count > 500)
                {
                    var recent = WatchLogs.TakeLast(400).ToList();
                    WatchLogs.Clear();
                    foreach (var item in recent)
                        WatchLogs.Add(item);
                }
            }

            Interlocked.Exchange(ref _watchLogFlushPending, 0);

            if (!_watchLogBuffer.IsEmpty)
            {
                if (Interlocked.CompareExchange(ref _watchLogFlushPending, 1, 0) == 0)
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, FlushWatchLogBuffer);
            }
        }

        private void FlushDeployLogBuffer()
        {
            var items = new List<string>();
            while (_deployLogBuffer.TryDequeue(out var entry))
                items.Add(entry);

            lock (_deployLogsLock)
            {
                foreach (var item in items)
                    DeployLogs.Add(item);

                // 500줄 초과 시 최근 400줄만 유지
                if (DeployLogs.Count > 500)
                {
                    var recent = DeployLogs.TakeLast(400).ToList();
                    DeployLogs.Clear();
                    foreach (var item in recent)
                        DeployLogs.Add(item);
                }
            }

            Interlocked.Exchange(ref _deployLogFlushPending, 0);

            if (!_deployLogBuffer.IsEmpty)
            {
                if (Interlocked.CompareExchange(ref _deployLogFlushPending, 1, 0) == 0)
                    Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, FlushDeployLogBuffer);
            }
        }

        // --- INotifyPropertyChanged ---

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _runner.Dispose();
        }
    }
}

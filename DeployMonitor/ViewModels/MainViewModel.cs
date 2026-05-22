using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DeployMonitor.Models;
using DeployMonitor.Services;
using DeployMonitor.Web.Data;

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
        private readonly SystemMonitorService _monitor = new();
        private AppSettings _settings;

        private string _repoFolder = "";
        private string _deployFolder = "";
        private int _intervalSeconds = 30;
        private string _defaultBranch = "master";
        private string _globalExitedOkContainers = "";
        private bool _isWatching;
        private string _watchButtonText = "● 감시 시작";

        private string _cpuUsage = "CPU: --%";
        private string _memUsage = "MEM: --%";
        private string _gpuUsage = "GPU: --%";

        // Raw system metrics for API
        private double _rawCpu;
        private double _rawMem;
        private double _rawGpu = -1;

        // 컬렉션 동기화용 락 객체
        private readonly object _watchLogsLock = new();
        private readonly object _deployLogsLock = new();
        private readonly object _projectsLock = new();

        public MainViewModel()
        {
            _settings = AppSettings.Load();
            _repoFolder = _settings.RepositoryFolder;
            _deployFolder = _settings.DeployFolder;
            _intervalSeconds = _settings.IntervalSeconds;
            _defaultBranch = _settings.DefaultBranch;
            _globalExitedOkContainers = _settings.GlobalExitedOkContainers;
            _runner.GlobalExitedOkContainers = _globalExitedOkContainers;

            // 컬렉션 스레드 동기화 활성화
            BindingOperations.EnableCollectionSynchronization(WatchLogs, _watchLogsLock);
            BindingOperations.EnableCollectionSynchronization(DeployLogs, _deployLogsLock);
            BindingOperations.EnableCollectionSynchronization(Projects, _projectsLock);

            // 이벤트 연결
            _scanner.DebugLog += AddWatchLog;
            _watcher.CommitDetected += OnCommitDetected;
            _watcher.LogMessage += AddWatchLog;
            _watcher.NewProjectFound += OnNewProjectFound;
            _runner.LogMessage += AddDeployLog;
            _runner.DeployCompleted += OnDeployCompleted;
            _runner.DeployHistoryEvent += OnDeployHistory;

            // 커맨드 초기화
            StartStopCommand = new RelayCommand(ToggleWatch);
            RefreshCommand = new RelayCommand(ScanProjects);
            BrowseRepoCommand = new RelayCommand(BrowseRepoFolder);
            BrowseDeployCommand = new RelayCommand(BrowseDeployFolder);
            ManualDeployCommand = new RelayCommand(ManualDeploy);

            // 시스템 모니터 시작
            _monitor.UsageUpdated += OnUsageUpdated;
            _monitor.Start();

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

        public string GlobalExitedOkContainers
        {
            get => _globalExitedOkContainers;
            set
            {
                if (SetField(ref _globalExitedOkContainers, value))
                {
                    _runner.GlobalExitedOkContainers = value ?? "";
                    AutoSave();
                }
            }
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

        public string CpuUsage
        {
            get => _cpuUsage;
            private set => SetField(ref _cpuUsage, value);
        }

        public string MemUsage
        {
            get => _memUsage;
            private set => SetField(ref _memUsage, value);
        }

        public string GpuUsage
        {
            get => _gpuUsage;
            private set => SetField(ref _gpuUsage, value);
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
                lock (_projectsLock) Projects.Clear();
                AddWatchLog("프로젝트 스캔 중...");

                // 백그라운드에서 스캔 실행 (UI 블로킹 방지)
                var repoFolder = RepoFolder;
                var deployFolder = DeployFolder;
                var branch = DefaultBranch;
                var list = await System.Threading.Tasks.Task.Run(() =>
                    _scanner.Scan(repoFolder, deployFolder, branch));

                lock (_projectsLock)
                {
                    foreach (var p in list)
                        Projects.Add(p);
                }

                AddWatchLog($"deploy.bat 있는 프로젝트 {list.Count}개 발견");
                await SyncProjectRuntimeStatusesAsync(list);

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
                lock (_projectsLock) Projects.Clear();
                AddWatchLog("프로젝트 스캔 중...");

                var repoFolder = RepoFolder;
                var deployFolder = DeployFolder;
                var branch = DefaultBranch;
                var list = await System.Threading.Tasks.Task.Run(() =>
                    _scanner.Scan(repoFolder, deployFolder, branch));

                lock (_projectsLock)
                {
                    foreach (var p in list)
                        Projects.Add(p);
                }

                AddWatchLog($"deploy.bat 있는 프로젝트 {list.Count}개 발견");
                await SyncProjectRuntimeStatusesAsync(list);

                StartWatchInternal();
            }
            catch (Exception ex)
            {
                AddWatchLog($"감시 시작 오류: {ex.Message}");
            }
        }

        private void StartWatchInternal()
        {
            List<ProjectInfo> projectList;
            lock (_projectsLock)
            {
                projectList = new List<ProjectInfo>(Projects);
            }

            if (projectList.Count == 0)
                AddWatchLog("deploy.bat 있는 프로젝트가 없습니다. 저장소에 deploy.bat을 추가하세요.");
            else
                AddWatchLog($"감시 대상: {projectList.Count}개");

            _watcher.Start(projectList, IntervalSeconds, RepoFolder, DeployFolder, DefaultBranch);
            IsWatching = true;
        }

        /// <summary>스캔 직후 Docker 실행 상태를 반영한다.</summary>
        private async Task SyncProjectRuntimeStatusesAsync(IReadOnlyList<ProjectInfo> list)
        {
            if (list.Count == 0)
                return;

            AddWatchLog("Docker 상태 동기화 중...");

            foreach (var project in list)
            {
                await _runner.RefreshProjectStatusFromDockerAsync(project);
            }

            var successCount = list.Count(p => p.Status == ProjectStatus.Success);
            var errorCount = list.Count(p => p.Status == ProjectStatus.Error);
            var idleCount = list.Count(p => p.Status == ProjectStatus.Idle);
            AddWatchLog($"상태 반영 완료: 정상 {successCount}개, 오류 {errorCount}개, 대기 {idleCount}개");
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
            _settings.GlobalExitedOkContainers = GlobalExitedOkContainers;
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
                _runner.Enqueue(project, "manual");
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

        /// <summary>새 프로젝트 발견 이벤트</summary>
        private void OnNewProjectFound(ProjectInfo project)
        {
            bool added = false;
            lock (_projectsLock)
            {
                if (Projects.All(p => p.Name != project.Name))
                {
                    Projects.Add(project);
                    added = true;
                }
            }

            if (added)
            {
                AddWatchLog($"[{project.Name}] 새 프로젝트 추가됨");
                _ = _runner.RefreshProjectStatusFromDockerAsync(project);
            }
        }

        /// <summary>시스템 사용량 갱신</summary>
        private void OnUsageUpdated(double cpu, double mem, double gpu)
        {
            _rawCpu = cpu;
            _rawMem = mem;
            _rawGpu = gpu;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                CpuUsage = $"CPU: {cpu:F0}%";
                MemUsage = $"MEM: {mem:F0}%";
                GpuUsage = gpu >= 0 ? $"GPU: {gpu:F0}%" : "GPU: N/A";
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

        // --- Deploy History ---

        /// <summary>배포 이력 저장소 (웹서버 시작 시 설정됨)</summary>
        public DeployHistoryStore? DeployHistoryStore { get; set; }

        private void OnDeployHistory(string projectName, bool success, string? commitHash,
            DateTime startedAt, string? logSummary, string triggerType)
        {
            try
            {
                DeployHistoryStore?.Save(projectName, commitHash,
                    success ? "Success" : "Error",
                    startedAt, DateTime.Now, logSummary, triggerType);
            }
            catch { }
        }

        // --- Web API Methods ---

        /// <summary>프로젝트 스냅샷 (API용)</summary>
        public List<object> GetProjectsSnapshot()
        {
            List<ProjectInfo> snapshot;
            lock (_projectsLock)
            {
                snapshot = new List<ProjectInfo>(Projects);
            }

            return snapshot.Select(p => (object)new
            {
                name = p.Name,
                status = (int)p.Status,
                statusDisplay = p.StatusDisplay,
                hasDeployBat = p.HasDeployBat,
                branch = p.Branch,
                lastCommitHash = p.LastCommitHash,
                lastCommitDetectedTime = p.LastCommitDetectedTime,
                lastDeployTime = p.LastDeployTime,
                lastMessage = p.LastMessage
            }).ToList();
        }

        /// <summary>시스템 메트릭 (API용)</summary>
        public (double cpu, double mem, double gpu) GetSystemMetrics()
        {
            return (_rawCpu, _rawMem, _rawGpu);
        }

        /// <summary>감시 로그 스냅샷 (API용)</summary>
        public List<string> GetWatchLogSnapshot(int last = 100)
        {
            lock (_watchLogsLock)
            {
                return WatchLogs.TakeLast(last).ToList();
            }
        }

        /// <summary>배포 로그 스냅샷 (API용)</summary>
        public List<string> GetDeployLogSnapshot(int last = 100)
        {
            lock (_deployLogsLock)
            {
                return DeployLogs.TakeLast(last).ToList();
            }
        }

        /// <summary>프로젝트 조회 (API용)</summary>
        public ProjectInfo? GetProject(string projectName)
        {
            lock (_projectsLock)
            {
                return Projects.FirstOrDefault(p =>
                    string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>프로젝트별 배포 로그 (API용)</summary>
        public string? GetProjectLog(string projectName)
        {
            ProjectInfo? project;
            lock (_projectsLock)
            {
                project = Projects.FirstOrDefault(p =>
                    string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            }
            return project?.LastDeploymentLog;
        }

        /// <summary>감시 시작 (API용)</summary>
        public Task ApiStartWatch()
        {
            var tcs = new TaskCompletionSource();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!IsWatching) StartWatch();
                tcs.TrySetResult();
            });
            return tcs.Task;
        }

        /// <summary>감시 중지 (API용)</summary>
        public Task ApiStopWatch()
        {
            var tcs = new TaskCompletionSource();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (IsWatching) StopWatch();
                tcs.TrySetResult();
            });
            return tcs.Task;
        }

        /// <summary>수동 배포 (API용)</summary>
        public Task<bool> ApiManualDeploy(string projectName)
        {
            ProjectInfo? project;
            lock (_projectsLock)
            {
                project = Projects.FirstOrDefault(p =>
                    string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            }

            if (project == null || !project.HasDeployBat)
                return Task.FromResult(false);

            AddDeployLog($"[{project.Name}] 수동 배포 시작 (웹)");
            _runner.Enqueue(project, "manual");
            return Task.FromResult(true);
        }

        /// <summary>프로젝트 새로고침 (API용)</summary>
        public Task ApiScanProjects()
        {
            var tcs = new TaskCompletionSource();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ScanProjects();
                tcs.TrySetResult();
            });
            return tcs.Task;
        }

        /// <summary>설정 변경 (API용)</summary>
        public void ApiUpdateSettings(string? repoFolder, string? deployFolder, int? intervalSeconds, string? defaultBranch, string? globalExitedOkContainers)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (repoFolder != null) RepoFolder = repoFolder;
                if (deployFolder != null) DeployFolder = deployFolder;
                if (intervalSeconds.HasValue) IntervalSeconds = intervalSeconds.Value;
                if (defaultBranch != null) DefaultBranch = defaultBranch;
                if (globalExitedOkContainers != null) GlobalExitedOkContainers = globalExitedOkContainers;
            });
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
            _monitor.Dispose();
            _watcher.Dispose();
            _runner.Dispose();
        }
    }
}

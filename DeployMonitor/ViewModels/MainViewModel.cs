using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
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

        public MainViewModel()
        {
            _settings = AppSettings.Load();
            _repoFolder = _settings.RepositoryFolder;
            _deployFolder = _settings.DeployFolder;
            _intervalSeconds = _settings.IntervalSeconds;
            _defaultBranch = _settings.DefaultBranch;

            // 이벤트 연결
            _watcher.CommitDetected += OnCommitDetected;
            _watcher.LogMessage += AddLog;
            _runner.LogMessage += AddLog;
            _runner.DeployCompleted += OnDeployCompleted;

            // 커맨드 초기화
            StartStopCommand = new RelayCommand(ToggleWatch);
            RefreshCommand = new RelayCommand(ScanProjects);
            BrowseRepoCommand = new RelayCommand(BrowseRepoFolder);
            BrowseDeployCommand = new RelayCommand(BrowseDeployFolder);
            ManualDeployCommand = new RelayCommand(ManualDeploy);

            // 초기 스캔
            ScanProjects();

            // 자동 시작
            if (_settings.AutoStart)
                ToggleWatch();
        }

        // --- 프로퍼티 ---

        public ObservableCollection<ProjectInfo> Projects { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();

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
        private void ScanProjects()
        {
            var wasWatching = IsWatching;
            if (wasWatching) _watcher.Stop();

            Projects.Clear();
            var list = _scanner.Scan(RepoFolder, DeployFolder, DefaultBranch);
            foreach (var p in list)
                Projects.Add(p);

            AddLog($"deploy.bat 있는 프로젝트 {list.Count}개 발견");

            if (wasWatching) StartWatch();
        }

        /// <summary>감시 시작/중지 토글</summary>
        private void ToggleWatch()
        {
            if (IsWatching)
                StopWatch();
            else
                StartWatch();
        }

        private void StartWatch()
        {
            if (string.IsNullOrWhiteSpace(RepoFolder) || string.IsNullOrWhiteSpace(DeployFolder))
            {
                AddLog("저장소 폴더와 배포 폴더를 먼저 설정하세요.");
                return;
            }

            // 감시 시작 전 프로젝트 목록 새로고침
            ScanProjects();

            if (Projects.Count == 0)
                AddLog("deploy.bat 있는 프로젝트가 없습니다. 저장소에 deploy.bat을 추가하세요.");
            else
                AddLog($"감시 대상: {Projects.Count}개");

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
                AddLog($"[{project.Name}] 수동 배포 시작");
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
            var deployPath = project.DeployPath ?? "";
            var updated = false;
            var hasDeployBat = !string.IsNullOrWhiteSpace(deployPath)
                && RepoScanner.SyncDeployBatFromRepo(
                    project.BareRepoPath,
                    deployPath,
                    project.Branch,
                    project.Name,
                    out updated,
                    out _);

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!hasDeployBat)
                {
                    project.HasDeployBat = false;
                    project.Status = ProjectStatus.NotConfigured;
                    project.LastMessage = "deploy.bat 없음";
                    AddLog($"[{project.Name}] deploy.bat 없음 (목록에서 제거)");

                    var existing = Projects.FirstOrDefault(p => p.Name == project.Name);
                    if (existing != null)
                        Projects.Remove(existing);
                    return;
                }

                if (updated)
                    AddLog($"[{project.Name}] deploy.bat 업데이트됨");

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

        /// <summary>로그 flush 완료 후 스크롤 요청</summary>
        public event Action? ScrollToEndRequested;

        // 로그 버퍼 (스레드 안전)
        private readonly ConcurrentQueue<string> _logBuffer = new();
        private int _logFlushPending;

        /// <summary>로그 추가 (스레드 안전, 버퍼링 후 단일 flush)</summary>
        public void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logBuffer.Enqueue($"[{timestamp}] {message}");

            // 이미 flush 예약됐으면 건너뜀
            if (Interlocked.CompareExchange(ref _logFlushPending, 1, 0) != 0) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(DispatcherPriority.Background, FlushLogBuffer);
        }

        /// <summary>버퍼에 쌓인 로그를 한꺼번에 UI에 추가</summary>
        private void FlushLogBuffer()
        {
            // 먼저 버퍼를 로컬 리스트로 모두 드레인
            var items = new List<string>();
            while (_logBuffer.TryDequeue(out var entry))
                items.Add(entry);

            // 한꺼번에 추가
            foreach (var item in items)
                LogEntries.Add(item);

            // 최대 500개 유지
            while (LogEntries.Count > 500)
                LogEntries.RemoveAt(0);

            // 모든 추가 완료 후 스크롤 (레이아웃 충돌 방지)
            ScrollToEndRequested?.Invoke();

            // flush 플래그 해제
            Interlocked.Exchange(ref _logFlushPending, 0);

            // flush 중 새 항목 도착 시 재예약
            if (!_logBuffer.IsEmpty)
            {
                if (Interlocked.CompareExchange(ref _logFlushPending, 1, 0) == 0)
                {
                    Application.Current?.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background, FlushLogBuffer);
                }
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

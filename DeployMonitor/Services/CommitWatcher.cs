using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeployMonitor.Models;

namespace DeployMonitor.Services
{
    /// <summary>
    /// bare repo 커밋 변경 감지 (FileSystemWatcher + 폴링 백업)
    /// </summary>
    public class CommitWatcher : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = new();
        private Timer? _pollingTimer;
        private List<ProjectInfo> _projects = new();
        private bool _isRunning;

        // 스레드 안전한 해시 추적 (UI 바인딩 프로퍼티 대신 사용)
        private readonly ConcurrentDictionary<string, string> _knownHashes = new();

        // 새 프로젝트 스캔용
        private string _repoFolder = "";
        private string _deployFolder = "";
        private string _defaultBranch = "master";

        /// <summary>새 커밋이 감지되면 발생 (ProjectInfo, newHash)</summary>
        public event Action<ProjectInfo, string>? CommitDetected;

        /// <summary>로그 메시지 발생</summary>
        public event Action<string>? LogMessage;

        /// <summary>새 프로젝트 발견 시 발생</summary>
        public event Action<ProjectInfo>? NewProjectFound;

        /// <summary>감시 시작</summary>
        public void Start(List<ProjectInfo> projects, int intervalSeconds,
            string repoFolder = "", string deployFolder = "", string defaultBranch = "master")
        {
            if (_isRunning) return;
            _isRunning = true;
            _projects = projects;
            _repoFolder = repoFolder;
            _deployFolder = deployFolder;
            _defaultBranch = defaultBranch;
            _knownHashes.Clear();

            // 현재 해시를 기록 (백그라운드에서 실행)
            Task.Run(() =>
            {
                foreach (var project in projects)
                {
                    if (!project.HasDeployBat) continue;
                    var hash = RepoScanner.ReadCommitHash(project.BareRepoPath, project.Branch);
                    if (!string.IsNullOrEmpty(hash))
                        _knownHashes[project.Name] = hash;
                }
            });

            // FileSystemWatcher 설정 (가벼운 작업이므로 UI 스레드 OK)
            foreach (var project in projects)
            {
                if (!project.HasDeployBat) continue;
                SetupWatcher(project);
            }

            // 백업 폴링 타이머
            var interval = TimeSpan.FromSeconds(intervalSeconds);
            _pollingTimer = new Timer(PollAll, null, interval, interval);

            LogMessage?.Invoke("감시를 시작했습니다.");
        }

        /// <summary>감시 중지</summary>
        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            _pollingTimer?.Dispose();
            _pollingTimer = null;

            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            _watchers.Clear();

            LogMessage?.Invoke("감시를 중지했습니다.");
        }

        public bool IsRunning => _isRunning;

        /// <summary>FileSystemWatcher 설정</summary>
        private void SetupWatcher(ProjectInfo project)
        {
            var refsDir = Path.Combine(project.BareRepoPath, "refs", "heads");
            if (!Directory.Exists(refsDir))
            {
                refsDir = project.BareRepoPath;
            }

            try
            {
                var watcher = new FileSystemWatcher(refsDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                var proj = project;
                watcher.Changed += (_, e) => OnFileChanged(proj, e.FullPath);
                watcher.Created += (_, e) => OnFileChanged(proj, e.FullPath);

                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[{project.Name}] FileSystemWatcher 설정 실패: {ex.Message}");
            }

            // packed-refs도 감시
            try
            {
                var packedRefsDir = project.BareRepoPath;
                var packedWatcher = new FileSystemWatcher(packedRefsDir, "packed-refs")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                var proj = project;
                packedWatcher.Changed += (_, _) => CheckProject(proj);

                _watchers.Add(packedWatcher);
            }
            catch
            {
                // packed-refs 감시 실패는 무시 (폴링으로 보완)
            }
        }

        /// <summary>파일 변경 이벤트 핸들러</summary>
        private void OnFileChanged(ProjectInfo project, string changedPath)
        {
            var branchFile = Path.GetFileName(changedPath);
            if (branchFile == project.Branch || changedPath.Contains("packed-refs"))
            {
                // 파일 쓰기 완료 대기 (짧은 지연)
                Thread.Sleep(200);
                CheckProject(project);
            }
        }

        /// <summary>모든 프로젝트 폴링 확인</summary>
        private void PollAll(object? state)
        {
            if (!_isRunning) return;

            // 기존 프로젝트 커밋 확인
            foreach (var project in _projects)
            {
                if (!project.HasDeployBat) continue;
                if (project.Status == ProjectStatus.Deploying) continue;

                CheckProject(project);
            }

            // 새 프로젝트 스캔
            ScanForNewProjects();
        }

        /// <summary>새 프로젝트 스캔</summary>
        private void ScanForNewProjects()
        {
            if (string.IsNullOrEmpty(_repoFolder) || !Directory.Exists(_repoFolder)) return;

            try
            {
                var dirs = Directory.GetDirectories(_repoFolder);
                foreach (var dir in dirs)
                {
                    var dirName = Path.GetFileName(dir);

                    // bare repo 확인
                    var headPath = Path.Combine(dir, "HEAD");
                    if (!File.Exists(headPath)) continue;

                    // 프로젝트명
                    var projectName = dirName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                        ? dirName[..^4]
                        : dirName;

                    // 이미 감시 중인지 확인
                    if (_projects.Exists(p => p.Name == projectName)) continue;

                    // deploy.bat 존재 확인
                    if (!RepoScanner.HasDeployBatInRepo(dir, _defaultBranch, projectName, out _)) continue;

                    // 새 프로젝트 발견
                    var deployPath = Path.Combine(_deployFolder, projectName);
                    var commitHash = RepoScanner.ReadCommitHash(dir, _defaultBranch);

                    var newProject = new ProjectInfo
                    {
                        Name = projectName,
                        BareRepoPath = dir,
                        DeployPath = deployPath,
                        HasDeployBat = true,
                        Branch = _defaultBranch,
                        LastCommitHash = commitHash,
                        Status = ProjectStatus.Idle
                    };

                    _projects.Add(newProject);
                    _knownHashes[projectName] = commitHash;
                    SetupWatcher(newProject);

                    LogMessage?.Invoke($"[{projectName}] 새 프로젝트 발견");
                    NewProjectFound?.Invoke(newProject);
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"새 프로젝트 스캔 오류: {ex.Message}");
            }
        }

        /// <summary>프로젝트의 커밋 해시 변경 확인 (스레드 안전)</summary>
        private void CheckProject(ProjectInfo project)
        {
            if (project.Status == ProjectStatus.Deploying) return;

            try
            {
                var currentHash = RepoScanner.ReadCommitHash(project.BareRepoPath, project.Branch);
                if (string.IsNullOrEmpty(currentHash)) return;

                var knownHash = _knownHashes.GetOrAdd(project.Name, "");

                // 최초 실행 시 해시만 기록
                if (string.IsNullOrEmpty(knownHash))
                {
                    _knownHashes[project.Name] = currentHash;
                    return;
                }

                // 해시가 다르면 새 커밋
                if (currentHash != knownHash)
                {
                    _knownHashes[project.Name] = currentHash;

                    var shortHash = currentHash.Length >= 7 ? currentHash[..7] : currentHash;
                    LogMessage?.Invoke($"[{project.Name}] 새 커밋 감지 ({shortHash})");

                    // UI 프로퍼티는 이벤트 구독자가 Dispatcher에서 변경
                    CommitDetected?.Invoke(project, currentHash);
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[{project.Name}] 커밋 확인 오류: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

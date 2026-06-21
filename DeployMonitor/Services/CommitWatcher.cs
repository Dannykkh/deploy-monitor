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

        // 폴링 재진입 방지 (Timer는 이전 콜백 완료를 기다리지 않고 새 스레드풀 스레드에서 또 실행함)
        private int _pollRunning;

        // deploy.bat 없는 repo의 마지막 확인 커밋 해시 캐시.
        // 같은 커밋에서 git ls-tree 재실행을 막아 폴링 부하를 낮춘다.
        // 커밋이 바뀌면(해시 변경) 자동으로 재확인되므로 나중에 deploy.bat을 추가해도 감지된다.
        private readonly ConcurrentDictionary<string, string> _noDeployBatHashes = new();

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
            _noDeployBatHashes.Clear();
            Interlocked.Exchange(ref _pollRunning, 0);

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
                    // git gc/repack 등 버스트 시 내부 버퍼 오버플로(이벤트 유실)를 줄인다.
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };

                var proj = project;
                watcher.Changed += (_, e) => OnFileChanged(proj, e.FullPath);
                watcher.Created += (_, e) => OnFileChanged(proj, e.FullPath);
                watcher.Error += OnWatcherError;

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
                packedWatcher.Changed += (_, e) => OnFileChanged(proj, e.FullPath);
                packedWatcher.Error += OnWatcherError;

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
            var normalizedPath = changedPath.Replace('\\', '/');
            var normalizedBranch = project.Branch.Replace('\\', '/').Trim('/');

            // branch가 feature/x 형태여도 refs/heads/feature/x 변경을 즉시 감지
            var isBranchRefChanged =
                normalizedPath.EndsWith($"/refs/heads/{normalizedBranch}", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith($"/{normalizedBranch}", StringComparison.OrdinalIgnoreCase);

            if (isBranchRefChanged || normalizedPath.EndsWith("/packed-refs", StringComparison.OrdinalIgnoreCase))
            {
                // FSW 디스패치 스레드를 막지 않도록 지연 처리를 스레드풀로 넘긴다.
                // (콜백 스레드에서 Thread.Sleep을 하면 후속 이벤트 처리가 막혀 버퍼 오버플로/이벤트 유실 위험)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(200); // 파일 쓰기 완료 대기 (짧은 지연)
                        if (_isRunning) CheckProject(project);
                    }
                    catch { /* 감시 중지 등으로 인한 예외 무시 */ }
                });
            }
        }

        /// <summary>FileSystemWatcher 오류 처리 (내부 버퍼 오버플로 등)</summary>
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // 버퍼 오버플로로 이벤트가 유실돼도 백업 폴링이 보완하므로 로그만 남긴다.
            LogMessage?.Invoke($"FileSystemWatcher 오류(폴링으로 보완): {e.GetException().Message}");
        }

        /// <summary>모든 프로젝트 폴링 확인</summary>
        private void PollAll(object? state)
        {
            if (!_isRunning) return;

            // 재진입 방지: 이전 폴링이 아직 끝나지 않았으면 이번 주기는 건너뛴다.
            // (Timer는 콜백 완료를 기다리지 않으므로 가드가 없으면 _projects 동시 순회/수정으로
            //  처리되지 않는 예외가 스레드풀 스레드에서 발생해 프로세스가 강제 종료될 수 있다.)
            if (Interlocked.CompareExchange(ref _pollRunning, 1, 0) != 0) return;

            try
            {
                // 순회 중 ScanForNewProjects가 _projects를 수정해도 안전하도록 스냅샷 사용
                var snapshot = _projects.ToArray();
                foreach (var project in snapshot)
                {
                    if (!project.HasDeployBat) continue;
                    if (project.Status == ProjectStatus.Deploying) continue;

                    CheckProject(project);
                }

                // 새 프로젝트 스캔
                ScanForNewProjects();
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"폴링 오류: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _pollRunning, 0);
            }
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

                    // deploy.bat 없는 repo는 같은 커밋에서 git ls-tree 재실행을 생략한다.
                    // (커밋 해시는 파일 읽기라 저렴하고, 해시가 바뀌면 캐시가 무효화되어 재확인된다)
                    var currentHash = RepoScanner.ReadCommitHash(dir, _defaultBranch);
                    if (_noDeployBatHashes.TryGetValue(dir, out var checkedHash) && checkedHash == currentHash)
                        continue;

                    // deploy.bat 존재 확인 (여기서만 git ls-tree 프로세스 실행)
                    if (!RepoScanner.HasDeployBatInRepo(dir, _defaultBranch, projectName, out _))
                    {
                        _noDeployBatHashes[dir] = currentHash; // 이 커밋엔 deploy.bat 없음으로 기록
                        continue;
                    }

                    _noDeployBatHashes.TryRemove(dir, out _); // 발견됐으니 캐시 해제

                    // 새 프로젝트 발견
                    var deployPath = Path.Combine(_deployFolder, projectName);
                    var commitHash = currentHash;
                    var (containerPrefix, deployTriggers, exitedOkContainers) =
                        RepoScanner.ReadDeployMetadata(dir, _defaultBranch, projectName);

                    var newProject = new ProjectInfo
                    {
                        Name = projectName,
                        BareRepoPath = dir,
                        DeployPath = deployPath,
                        HasDeployBat = true,
                        Branch = _defaultBranch,
                        LastCommitHash = commitHash,
                        ContainerPrefix = containerPrefix,
                        DeployTriggers = deployTriggers,
                        ExitedOkContainers = exitedOkContainers,
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
                    project.PreviousCommitHash = knownHash; // 선택적 배포 판단용
                    _knownHashes[project.Name] = currentHash;
                    project.LastCommitDetectedTime = DateTime.Now.ToString("HH:mm:ss");

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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeployMonitor.Models;

namespace DeployMonitor.Services
{
    /// <summary>
    /// deploy.bat 실행 및 출력 캡처
    /// 동시 배포 방지를 위한 큐 사용
    /// </summary>
    public class DeployRunner : IDisposable
    {
        private readonly ConcurrentQueue<ProjectInfo> _queue = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        /// <summary>로그 출력 이벤트</summary>
        public event Action<string>? LogMessage;

        /// <summary>배포 완료 이벤트 (프로젝트명, 성공여부)</summary>
        public event Action<string, bool>? DeployCompleted;

        /// <summary>배포 이력 기록 이벤트 (프로젝트명, 성공여부, 커밋해시, 시작시각, 로그요약, 트리거타입)</summary>
        public event Action<string, bool, string?, DateTime, string?, string>? DeployHistoryEvent;

        private readonly ConcurrentDictionary<string, string> _triggerTypes = new();

        /// <summary>
        /// Exited(0) 허용 컨테이너 전역 화이트리스트(공백/쉼표/세미콜론 구분).
        /// </summary>
        public string GlobalExitedOkContainers { get; set; } = "";

        /// <summary>배포 큐에 추가</summary>
        public void Enqueue(ProjectInfo project, string triggerType = "auto")
        {
            // 이미 배포 중이면 무시
            if (project.Status == ProjectStatus.Deploying) return;

            _triggerTypes[project.Name] = triggerType;
            _queue.Enqueue(project);
            _ = ProcessQueueAsync();
        }

        /// <summary>큐 처리 (한 번에 하나씩)</summary>
        private async Task ProcessQueueAsync()
        {
            if (!await _semaphore.WaitAsync(0)) return;

            try
            {
                while (_queue.TryDequeue(out var project))
                {
                    await RunDeployAsync(project);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>UI 프로퍼티를 Dispatcher에서 안전하게 변경</summary>
        private static void UpdateUI(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action);
        }

        /// <summary>단일 프로젝트 배포 실행</summary>
        private async Task RunDeployAsync(ProjectInfo project)
        {
            var startedAt = DateTime.Now;
            var triggerType = _triggerTypes.TryRemove(project.Name, out var tt) ? tt : "auto";

            // 배포별 로그 수집용
            var deployLog = new System.Text.StringBuilder();
            void Log(string msg)
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                deployLog.AppendLine(line);
                LogMessage?.Invoke($"[{project.Name}] {msg}");
            }

            if (string.IsNullOrEmpty(project.DeployPath))
            {
                Log("deploy 경로 없음");
                UpdateUI(() =>
                {
                    project.Status = ProjectStatus.Error;
                    project.LastMessage = "deploy 경로 없음";
                    project.LastDeploymentLog = deployLog.ToString();
                });
                FireHistoryEvent(project, false, startedAt, deployLog, triggerType);
                return;
            }

            UpdateUI(() =>
            {
                project.Status = ProjectStatus.Deploying;
                project.LastMessage = "소스 동기화 중...";
            });

            Log("소스 동기화 시작");

            // 1. 소스코드 동기화 (clone 또는 pull)
            var syncResult = await SyncSourceCodeAsync(project, deployLog);
            if (!syncResult)
            {
                UpdateUI(() => project.LastDeploymentLog = deployLog.ToString());
                FireHistoryEvent(project, false, startedAt, deployLog, triggerType);
                DeployCompleted?.Invoke(project.Name, false);
                return;
            }

            // 선택적 배포 트리거 확인
            if (!string.IsNullOrEmpty(project.DeployTriggers) && !string.IsNullOrEmpty(project.PreviousCommitHash))
            {
                UpdateUI(() => project.LastMessage = "변경 파일 확인 중...");
                Log($"배포 트리거 확인: {project.DeployTriggers}");

                var shouldDeploy = await CheckDeployTriggersAsync(project, deployLog);
                if (!shouldDeploy)
                {
                    Log("[SKIP] 배포 대상 변경 없음 - 빌드 생략");
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Idle;
                        project.LastMessage = "변경 없음 - 빌드 생략";
                        project.LastDeploymentLog = deployLog.ToString();
                    });
                    // Skip은 이력에 기록하지 않음
                    DeployCompleted?.Invoke(project.Name, true);
                    return;
                }
            }

            UpdateUI(() => project.LastMessage = "deploy.bat 실행중...");
            Log("deploy.bat auto 실행");

            try
            {
                var batPath = Path.Combine(project.DeployPath, "deploy.bat");
                var psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    Arguments = "auto",
                    WorkingDirectory = project.DeployPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        deployLog.AppendLine(e.Data);
                        LogMessage?.Invoke($"[{project.Name}] {e.Data}");
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        deployLog.AppendLine($"[ERR] {e.Data}");
                        LogMessage?.Invoke($"[{project.Name}] [ERR] {e.Data}");
                    }
                };

                process.Start();
                process.StandardInput.Close(); // EOF → unblock pause/choice commands
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 최대 30분 대기 (대용량 빌드 + cache miss 대응)
                var completed = await Task.Run(() => process.WaitForExit(1_800_000));

                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    deployLog.AppendLine("[TIMEOUT] 배포 타임아웃 (30분 초과)");
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Error;
                        project.LastMessage = "타임아웃 (30분 초과)";
                        project.LastDeploymentLog = deployLog.ToString();
                    });
                    LogMessage?.Invoke($"[{project.Name}] 배포 타임아웃");
                    FireHistoryEvent(project, false, startedAt, deployLog, triggerType);
                    DeployCompleted?.Invoke(project.Name, false);
                    return;
                }

                var exitCode = process.ExitCode;
                var now = DateTime.Now.ToString("HH:mm:ss");

                if (exitCode == 0)
                {
                    // Docker 컨테이너 상태 확인
                    var dockerStatus = await CheckDockerContainersAsync(project);

                    if (dockerStatus.AllRunning)
                    {
                        deployLog.AppendLine($"[SUCCESS] {dockerStatus.Summary}");
                        UpdateUI(() =>
                        {
                            project.Status = ProjectStatus.Success;
                            project.LastDeployTime = $"{now} 배포완료";
                            project.LastMessage = dockerStatus.Summary;
                            project.LastDeploymentLog = deployLog.ToString();
                        });
                        LogMessage?.Invoke($"[{project.Name}] 배포 완료 - {dockerStatus.Summary}");
                        FireHistoryEvent(project, true, startedAt, deployLog, triggerType);
                        DeployCompleted?.Invoke(project.Name, true);
                    }
                    else
                    {
                        deployLog.AppendLine($"[ERROR] 컨테이너 오류 - {dockerStatus.Summary}");
                        UpdateUI(() =>
                        {
                            project.Status = ProjectStatus.Error;
                            project.LastDeployTime = $"{now} 컨테이너 오류";
                            project.LastMessage = dockerStatus.Summary;
                            project.LastDeploymentLog = deployLog.ToString();
                        });
                        LogMessage?.Invoke($"[{project.Name}] 컨테이너 오류 - {dockerStatus.Summary}");

                        // 실패한 컨테이너 로그 조회
                        if (!string.IsNullOrEmpty(dockerStatus.FailedContainer))
                        {
                            await ShowContainerLogsAsync(project.Name, dockerStatus.FailedContainer);
                        }

                        FireHistoryEvent(project, false, startedAt, deployLog, triggerType);
                        DeployCompleted?.Invoke(project.Name, false);
                    }
                }
                else
                {
                    deployLog.AppendLine($"[ERROR] 배포 실패 (exit code {exitCode})");
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Error;
                        project.LastDeployTime = $"{now} 오류";
                        project.LastMessage = $"배포 실패 (exit code {exitCode})";
                        project.LastDeploymentLog = deployLog.ToString();
                    });
                    LogMessage?.Invoke($"[{project.Name}] 배포 실패 (exit code {exitCode})");
                    FireHistoryEvent(project, false, startedAt, deployLog, triggerType);
                    DeployCompleted?.Invoke(project.Name, false);
                }
            }
            catch (Exception ex)
            {
                deployLog.AppendLine($"[EXCEPTION] {ex.Message}");
                UpdateUI(() =>
                {
                    project.Status = ProjectStatus.Error;
                    project.LastMessage = $"실행 오류: {ex.Message}";
                    project.LastDeploymentLog = deployLog.ToString();
                });
                LogMessage?.Invoke($"[{project.Name}] 실행 오류: {ex.Message}");
                FireHistoryEvent(project, false, startedAt, deployLog, triggerType);
                DeployCompleted?.Invoke(project.Name, false);
            }
        }

        /// <summary>변경 파일이 배포 트리거 경로에 해당하는지 확인</summary>
        private async Task<bool> CheckDeployTriggersAsync(ProjectInfo project, System.Text.StringBuilder deployLog)
        {
            try
            {
                var triggers = project.DeployTriggers.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (triggers.Length == 0) return true;

                // bare repo에서 이전 커밋과 현재 커밋 사이의 변경 파일 조회
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"--git-dir \"{project.BareRepoPath}\" diff --name-only {project.PreviousCommitHash} {project.LastCommitHash}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    deployLog.AppendLine("[WARN] git diff 실패 - 배포 진행");
                    return true;
                }

                var changedFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var matched = false;

                foreach (var file in changedFiles)
                {
                    var trimmed = file.Trim();
                    foreach (var trigger in triggers)
                    {
                        if (trimmed.StartsWith(trigger, StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Equals(trigger, StringComparison.OrdinalIgnoreCase))
                        {
                            deployLog.AppendLine($"[MATCH] {trimmed} (trigger: {trigger})");
                            LogMessage?.Invoke($"[{project.Name}] [MATCH] {trimmed}");
                            matched = true;
                            break;
                        }
                    }
                }

                if (!matched)
                {
                    deployLog.AppendLine($"변경 파일 {changedFiles.Length}개 중 트리거 매칭 없음");
                    deployLog.AppendLine($"트리거: {project.DeployTriggers}");
                    foreach (var file in changedFiles)
                        deployLog.AppendLine($"  - {file.Trim()}");
                }

                return matched;
            }
            catch (Exception ex)
            {
                deployLog.AppendLine($"[WARN] 트리거 확인 실패: {ex.Message} - 배포 진행");
                return true; // 오류 시 안전하게 배포 진행
            }
        }

        /// <summary>소스코드 동기화 (clone 또는 pull)</summary>
        private async Task<bool> SyncSourceCodeAsync(ProjectInfo project, System.Text.StringBuilder? deployLog = null)
        {
            var deployPath = project.DeployPath!; // 호출 전에 null 체크됨
            var gitDir = Path.Combine(deployPath, ".git");
            var isGitRepo = Directory.Exists(gitDir);

            if (isGitRepo)
            {
                // 이미 clone된 상태 → git pull
                deployLog?.AppendLine("git pull 실행");
                LogMessage?.Invoke($"[{project.Name}] git pull 실행");
                return await RunGitCommandAsync(project, "pull", deployPath, deployLog);
            }
            else
            {
                // 최초 → git clone
                deployLog?.AppendLine("git clone 실행 (최초)");
                LogMessage?.Invoke($"[{project.Name}] git clone 실행 (최초)");

                // 배포 폴더의 부모 디렉토리에서 clone 실행
                var parentDir = Path.GetDirectoryName(deployPath) ?? deployPath;
                var folderName = Path.GetFileName(deployPath);

                // 기존 폴더가 있으면 삭제 (deploy.bat만 있던 폴더)
                if (Directory.Exists(deployPath))
                {
                    try
                    {
                        Directory.Delete(deployPath, true);
                    }
                    catch (Exception ex)
                    {
                        deployLog?.AppendLine($"[ERROR] 기존 폴더 삭제 실패: {ex.Message}");
                        LogMessage?.Invoke($"[{project.Name}] 기존 폴더 삭제 실패: {ex.Message}");
                        UpdateUI(() =>
                        {
                            project.Status = ProjectStatus.Error;
                            project.LastMessage = "폴더 삭제 실패";
                        });
                        return false;
                    }
                }

                // git clone (bare repo에서 clone, 브랜치 지정)
                var cloneArgs = $"clone --branch {project.Branch} \"{project.BareRepoPath}\" \"{folderName}\"";
                return await RunGitCommandAsync(project, cloneArgs, parentDir, deployLog);
            }
        }

        /// <summary>git 명령 실행</summary>
        private async Task<bool> RunGitCommandAsync(ProjectInfo project, string args, string workingDir, System.Text.StringBuilder? deployLog = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        deployLog?.AppendLine(e.Data);
                        LogMessage?.Invoke($"[{project.Name}] {e.Data}");
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        deployLog?.AppendLine(e.Data);
                        LogMessage?.Invoke($"[{project.Name}] {e.Data}");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var completed = await Task.Run(() => process.WaitForExit(120_000)); // 2분 타임아웃

                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    deployLog?.AppendLine("[TIMEOUT] git 명령 타임아웃");
                    LogMessage?.Invoke($"[{project.Name}] git 명령 타임아웃");
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Error;
                        project.LastMessage = "git 명령 타임아웃";
                    });
                    return false;
                }

                if (process.ExitCode != 0)
                {
                    deployLog?.AppendLine($"[ERROR] git 명령 실패 (exit code {process.ExitCode})");
                    LogMessage?.Invoke($"[{project.Name}] git 명령 실패 (exit code {process.ExitCode})");
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Error;
                        project.LastMessage = $"git 실패 (exit {process.ExitCode})";
                    });
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[{project.Name}] git 실행 오류: {ex.Message}");
                UpdateUI(() =>
                {
                    project.Status = ProjectStatus.Error;
                    project.LastMessage = $"git 오류: {ex.Message}";
                });
                return false;
            }
        }

        /// <summary>
        /// 배포 없이 현재 Docker 상태를 기준으로 프로젝트 상태를 동기화한다.
        /// </summary>
        public async Task RefreshProjectStatusFromDockerAsync(ProjectInfo project)
        {
            if (project.Status == ProjectStatus.Deploying)
                return;

            UpdateUI(() =>
            {
                project.Status = ProjectStatus.Checking;
                project.LastMessage = "Docker 상태 확인 중...";
            });

            var dockerStatus = await CheckDockerContainersAsync(project, noMatchIsSuccess: false, emitLog: false);

            UpdateUI(() =>
            {
                if (dockerStatus.HasError)
                {
                    project.Status = ProjectStatus.Error;
                    project.LastMessage = dockerStatus.Summary;
                    return;
                }

                if (!dockerStatus.HasMatchedContainers)
                {
                    project.Status = ProjectStatus.Idle;
                    project.LastMessage = "컨테이너 없음";
                    return;
                }

                if (dockerStatus.AllRunning)
                {
                    project.Status = ProjectStatus.Success;
                    project.LastMessage = dockerStatus.Summary;
                }
                else
                {
                    project.Status = ProjectStatus.Error;
                    project.LastMessage = dockerStatus.Summary;
                }
            });
        }

        /// <summary>Docker 컨테이너 상태 확인</summary>
        private async Task<DockerStatusResult> CheckDockerContainersAsync(
            ProjectInfo project,
            bool noMatchIsSuccess = true,
            bool emitLog = true)
        {
            var result = new DockerStatusResult();
            void Log(string message)
            {
                if (emitLog)
                    LogMessage?.Invoke($"[{project.Name}] {message}");
            }

            try
            {
                // Docker 전체 컨테이너 조회
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps -a --format \"{{.Names}}|{{.Status}}|{{.State}}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    var err = string.IsNullOrWhiteSpace(error)
                        ? $"exit code {process.ExitCode}"
                        : error.Trim();
                    result.HasError = true;
                    result.AllRunning = false;
                    result.Summary = $"Docker 확인 실패: {err}";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    result.HasMatchedContainers = false;
                    result.AllRunning = noMatchIsSuccess;
                    result.Summary = noMatchIsSuccess ? "배포 완료" : "컨테이너 없음";
                    return result;
                }

                var allLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // 여러 접두사 후보로 컨테이너 매칭 시도
                var prefixes = BuildPrefixCandidates(project);
                string[] lines = Array.Empty<string>();

                foreach (var prefix in prefixes)
                {
                    if (string.IsNullOrEmpty(prefix)) continue;

                    lines = allLines.Where(line =>
                        line.Split('|')[0].Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

                    if (lines.Length > 0)
                    {
                        Log($"컨테이너 매칭: prefix=\"{prefix}\", {lines.Length}개");
                        break;
                    }
                }

                if (lines.Length == 0)
                {
                    Log($"컨테이너 매칭 없음 (시도: {string.Join(", ", prefixes)})");
                    result.HasMatchedContainers = false;
                    result.AllRunning = noMatchIsSuccess;
                    result.Summary = noMatchIsSuccess ? "배포 완료" : "컨테이너 없음";
                    return result;
                }

                result.HasMatchedContainers = true;
                var running = 0;
                var total = 0;
                var toleratedStopped = 0;
                var exitedAllowedKeywords = ParseExitedAllowedKeywords(
                    $"{GlobalExitedOkContainers} {project.ExitedOkContainers}");

                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 3)
                    {
                        total++;
                        var name = parts[0].Trim();
                        var status = parts[1].Trim();
                        var state = parts[2].Trim().ToLowerInvariant();

                        var whitelisted = IsWhitelistedContainer(name, exitedAllowedKeywords);

                        // 화이트리스트 매칭: 키워드에 해당하면 상태와 무관하게 허용
                        if (whitelisted && state != "running")
                        {
                            toleratedStopped++;
                            Log($"{name}: {state} - 화이트리스트 허용");
                        }
                        else if (state == "running")
                        {
                            running++;
                            // unhealthy는 경고로만 표시, running이면 정상으로 간주
                            if (status.Contains("unhealthy", StringComparison.OrdinalIgnoreCase))
                                Log($"{name}: running (unhealthy 경고)");
                            else
                                Log($"{name}: running");
                        }
                        else if (IsExpectedStoppedContainer(name, status, state, exitedAllowedKeywords))
                        {
                            toleratedStopped++;
                            Log($"{name}: exited(0) - 정상 종료로 허용");
                        }
                        else
                        {
                            result.FailedContainer ??= name;
                            Log($"{name}: {state} - 오류");
                        }
                    }
                }

                if (total == 0)
                {
                    result.HasMatchedContainers = false;
                    result.AllRunning = noMatchIsSuccess;
                    result.Summary = noMatchIsSuccess ? "배포 완료" : "컨테이너 없음";
                    return result;
                }

                result.AllRunning =
                    (running + toleratedStopped == total &&
                     total > 0 &&
                     running > 0 &&
                     string.IsNullOrEmpty(result.FailedContainer));

                result.Summary = toleratedStopped > 0
                    ? $"컨테이너 {running}/{total} 실행중 ({toleratedStopped}개 허용)"
                    : $"컨테이너 {running}/{total} 실행중";

                if (!result.AllRunning && !string.IsNullOrEmpty(result.FailedContainer))
                {
                    result.Summary += $" ({result.FailedContainer} 오류)";
                }
            }
            catch (Exception ex)
            {
                result.HasError = true;
                result.Summary = $"Docker 확인 실패: {ex.Message}";
                Log($"Docker 상태 확인 오류: {ex.Message}");
            }

            return result;
        }

        /// <summary>화이트리스트 키워드에 매칭되는 컨테이너인지 판별 (상태 무관)</summary>
        private static bool IsWhitelistedContainer(string containerName, IReadOnlyCollection<string> allowedKeywords)
        {
            if (allowedKeywords.Count == 0)
                return false;
            var name = containerName.ToLowerInvariant();
            return allowedKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 종료(Exited 0) 상태가 정상일 수 있는 스케줄성 컨테이너인지 판별
        /// </summary>
        private static bool IsExpectedStoppedContainer(
            string containerName,
            string status,
            string state,
            IReadOnlyCollection<string> allowedKeywords)
        {
            if (!string.Equals(state, "exited", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!status.Contains("Exited (0)", StringComparison.OrdinalIgnoreCase))
                return false;

            // 기본 동작: EXITED_OK_CONTAINERS가 없으면 Exited(0) 전체를 정상 종료로 허용.
            // 프로젝트별로 엄격하게 제한하고 싶으면 EXITED_OK_CONTAINERS를 설정한다.
            if (allowedKeywords.Count == 0)
                return true;

            var name = containerName.ToLowerInvariant();
            return allowedKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> ParseExitedAllowedKeywords(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            return raw
                .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>컨테이너 접두사 후보 목록 생성 (우선순위 순)</summary>
        private List<string> BuildPrefixCandidates(ProjectInfo project)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. ContainerPrefix (deploy.bat의 PROJECT_NAME)
            var cp = project.ContainerPrefix;
            if (!string.IsNullOrEmpty(cp) && seen.Add(cp))
                candidates.Add(cp);

            // 2. project.Name (bare repo 폴더명)
            if (!string.IsNullOrEmpty(project.Name) && seen.Add(project.Name))
                candidates.Add(project.Name);

            // 3. 배포 디렉토리명 (docker-compose 기본 프로젝트명)
            if (!string.IsNullOrEmpty(project.DeployPath))
            {
                var dirName = Path.GetFileName(project.DeployPath);
                if (!string.IsNullOrEmpty(dirName) && seen.Add(dirName))
                    candidates.Add(dirName);
            }

            // 4. docker-compose.yml의 name: 필드
            if (!string.IsNullOrEmpty(project.DeployPath))
            {
                var composeName = ReadComposeProjectName(project.DeployPath);
                if (!string.IsNullOrEmpty(composeName) && seen.Add(composeName))
                    candidates.Add(composeName);
            }

            return candidates;
        }

        /// <summary>docker-compose 파일에서 프로젝트명 읽기</summary>
        private static string? ReadComposeProjectName(string deployPath)
        {
            var composeFiles = new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" };
            foreach (var file in composeFiles)
            {
                var path = Path.Combine(deployPath, file);
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (var line in File.ReadLines(path))
                    {
                        // 루트 레벨 name: 필드 (들여쓰기 없음)
                        if (line.StartsWith("name:"))
                        {
                            var value = line[5..].Trim().Trim('"', '\'');
                            if (!string.IsNullOrEmpty(value))
                                return value;
                        }
                    }
                }
                catch { /* compose 파일 읽기 실패 무시 */ }
            }
            return null;
        }

        /// <summary>실패한 컨테이너 로그 조회</summary>
        private async Task ShowContainerLogsAsync(string projectName, string containerName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"logs --tail 20 {containerName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                LogMessage?.Invoke($"[{projectName}] === {containerName} 로그 (최근 20줄) ===");

                var logs = string.IsNullOrWhiteSpace(error) ? output : error;
                foreach (var line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(10))
                {
                    LogMessage?.Invoke($"[{projectName}]   {line}");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[{projectName}] 로그 조회 실패: {ex.Message}");
            }
        }

        /// <summary>Docker 상태 확인 결과</summary>
        private class DockerStatusResult
        {
            public bool AllRunning { get; set; }
            public bool HasMatchedContainers { get; set; }
            public bool HasError { get; set; }
            public string Summary { get; set; } = "";
            public string? FailedContainer { get; set; }
        }

        /// <summary>배포 이력 이벤트 발행</summary>
        private void FireHistoryEvent(ProjectInfo project, bool success,
            DateTime startedAt, System.Text.StringBuilder deployLog, string triggerType)
        {
            try
            {
                var logSummary = deployLog.ToString();
                if (logSummary.Length > 2000)
                    logSummary = "...\n" + logSummary[^2000..];
                DeployHistoryEvent?.Invoke(project.Name, success, project.LastCommitHash,
                    startedAt, logSummary, triggerType);
            }
            catch { }
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }
}

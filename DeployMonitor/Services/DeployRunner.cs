using System;
using System.Collections.Concurrent;
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

        /// <summary>배포 큐에 추가</summary>
        public void Enqueue(ProjectInfo project)
        {
            // 이미 배포 중이면 무시
            if (project.Status == ProjectStatus.Deploying) return;

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
                DeployCompleted?.Invoke(project.Name, false);
                return;
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
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 최대 10분 대기
                var completed = await Task.Run(() => process.WaitForExit(600_000));

                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    deployLog.AppendLine("[TIMEOUT] 배포 타임아웃 (10분 초과)");
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Error;
                        project.LastMessage = "타임아웃 (10분 초과)";
                        project.LastDeploymentLog = deployLog.ToString();
                    });
                    LogMessage?.Invoke($"[{project.Name}] 배포 타임아웃");
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
                DeployCompleted?.Invoke(project.Name, false);
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

        /// <summary>Docker 컨테이너 상태 확인</summary>
        private async Task<DockerStatusResult> CheckDockerContainersAsync(ProjectInfo project)
        {
            var result = new DockerStatusResult();

            try
            {
                // 프로젝트명으로 시작하는 컨테이너 조회
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"ps -a --filter \"name={project.Name}-\" --format \"{{{{.Names}}}}|{{{{.Status}}}}|{{{{.State}}}}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output))
                {
                    result.Summary = "컨테이너 없음";
                    return result;
                }

                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var running = 0;
                var total = 0;

                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 3)
                    {
                        total++;
                        var name = parts[0].Trim();
                        var status = parts[1].Trim();
                        var state = parts[2].Trim().ToLower();

                        if (state == "running")
                        {
                            running++;
                            // health 상태 확인
                            if (status.Contains("unhealthy"))
                            {
                                result.FailedContainer = name;
                                LogMessage?.Invoke($"[{project.Name}] {name}: unhealthy");
                            }
                            else
                            {
                                LogMessage?.Invoke($"[{project.Name}] {name}: running");
                            }
                        }
                        else
                        {
                            result.FailedContainer ??= name;
                            LogMessage?.Invoke($"[{project.Name}] {name}: {state}");
                        }
                    }
                }

                result.AllRunning = (running == total && total > 0 && string.IsNullOrEmpty(result.FailedContainer));
                result.Summary = $"컨테이너 {running}/{total} 실행중";

                if (!result.AllRunning && !string.IsNullOrEmpty(result.FailedContainer))
                {
                    result.Summary += $" ({result.FailedContainer} 오류)";
                }
            }
            catch (Exception ex)
            {
                result.Summary = $"Docker 확인 실패: {ex.Message}";
                LogMessage?.Invoke($"[{project.Name}] Docker 상태 확인 오류: {ex.Message}");
            }

            return result;
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
            public string Summary { get; set; } = "";
            public string? FailedContainer { get; set; }
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }
}

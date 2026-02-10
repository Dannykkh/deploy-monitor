using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
            if (string.IsNullOrEmpty(project.DeployPath))
            {
                UpdateUI(() =>
                {
                    project.Status = ProjectStatus.Error;
                    project.LastMessage = "deploy 경로 없음";
                });
                return;
            }

            UpdateUI(() =>
            {
                project.Status = ProjectStatus.Deploying;
                project.LastMessage = "deploy.bat 실행중...";
            });
            LogMessage?.Invoke($"[{project.Name}] deploy.bat auto 실행");

            try
            {
                var batPath = System.IO.Path.Combine(project.DeployPath, "deploy.bat");
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
                        LogMessage?.Invoke($"[{project.Name}] {e.Data}");
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogMessage?.Invoke($"[{project.Name}] [ERR] {e.Data}");
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 최대 10분 대기
                var completed = await Task.Run(() => process.WaitForExit(600_000));

                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Error;
                        project.LastMessage = "타임아웃 (10분 초과)";
                    });
                    LogMessage?.Invoke($"[{project.Name}] 배포 타임아웃");
                    DeployCompleted?.Invoke(project.Name, false);
                    return;
                }

                var exitCode = process.ExitCode;
                var now = DateTime.Now.ToString("HH:mm:ss");

                if (exitCode == 0)
                {
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Success;
                        project.LastDeployTime = $"{now} 배포완료";
                        project.LastMessage = $"배포 성공 (exit code 0)";
                    });
                    LogMessage?.Invoke($"[{project.Name}] 배포 완료 (exit code 0)");
                    DeployCompleted?.Invoke(project.Name, true);
                }
                else
                {
                    UpdateUI(() =>
                    {
                        project.Status = ProjectStatus.Error;
                        project.LastDeployTime = $"{now} 오류";
                        project.LastMessage = $"배포 실패 (exit code {exitCode})";
                    });
                    LogMessage?.Invoke($"[{project.Name}] 배포 실패 (exit code {exitCode})");
                    DeployCompleted?.Invoke(project.Name, false);
                }
            }
            catch (Exception ex)
            {
                UpdateUI(() =>
                {
                    project.Status = ProjectStatus.Error;
                    project.LastMessage = $"실행 오류: {ex.Message}";
                });
                LogMessage?.Invoke($"[{project.Name}] 실행 오류: {ex.Message}");
                DeployCompleted?.Invoke(project.Name, false);
            }
        }

        public void Dispose()
        {
            _semaphore.Dispose();
        }
    }
}

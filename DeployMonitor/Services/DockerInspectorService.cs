using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeployMonitor.Models;

namespace DeployMonitor.Services
{
    /// <summary>
    /// Docker 컨테이너 상태/로그 조회 유틸리티.
    /// </summary>
    public static class DockerInspectorService
    {
        public sealed class ContainerInfo
        {
            public string Name { get; set; } = "";
            public string Status { get; set; } = "";
            public string State { get; set; } = "";
            public string Level { get; set; } = "idle"; // running | expected-stopped | error | idle
        }

        public sealed class ProjectContainersResult
        {
            public string ProjectName { get; set; } = "";
            public string MatchedPrefix { get; set; } = "";
            public List<string> TriedPrefixes { get; set; } = new();
            public List<ContainerInfo> Containers { get; set; } = new();
            public int RunningCount { get; set; }
            public int ExpectedStoppedCount { get; set; }
            public int ErrorCount { get; set; }
            public bool Success { get; set; }
            public string Message { get; set; } = "";
        }

        public static async Task<ProjectContainersResult> GetProjectContainersAsync(ProjectInfo project, string globalExitedOkContainers = "")
        {
            var result = new ProjectContainersResult { ProjectName = project.Name };
            var output = await RunDockerAsync("ps -a --format \"{{.Names}}|{{.Status}}|{{.State}}\"");

            if (!output.Success)
            {
                result.Success = false;
                result.Message = $"Docker 조회 실패: {output.Error}";
                return result;
            }

            var lines = output.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToArray();

            if (lines.Length == 0)
            {
                result.Success = true;
                result.Message = "컨테이너 없음";
                return result;
            }

            var prefixes = BuildPrefixCandidates(project);
            result.TriedPrefixes = prefixes;

            string[] matched = Array.Empty<string>();
            foreach (var prefix in prefixes)
            {
                if (string.IsNullOrWhiteSpace(prefix))
                    continue;

                matched = lines.Where(line =>
                    line.Split('|')[0].Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

                if (matched.Length > 0)
                {
                    result.MatchedPrefix = prefix;
                    break;
                }
            }

            if (matched.Length == 0)
            {
                result.Success = true;
                result.Message = "프로젝트와 매칭된 컨테이너 없음";
                return result;
            }

            var allowedKeywords = ParseExitedAllowedKeywords($"{globalExitedOkContainers} {project.ExitedOkContainers}");
            foreach (var line in matched)
            {
                var parts = line.Split('|');
                if (parts.Length < 3) continue;

                var name = parts[0].Trim();
                var status = parts[1].Trim();
                var state = parts[2].Trim().ToLowerInvariant();
                var info = new ContainerInfo
                {
                    Name = name,
                    Status = status,
                    State = state
                };

                // 화이트리스트 매칭: 키워드에 해당하면 상태와 무관하게 허용
                var isWhitelisted = IsWhitelistedContainer(name, allowedKeywords);

                if (isWhitelisted && state != "running")
                {
                    info.Level = "expected-stopped";
                }
                else if (state == "running")
                {
                    // running이면 healthcheck 결과와 무관하게 정상 (unhealthy는 경고로만)
                    info.Level = "running";
                }
                else if (IsExpectedStoppedContainer(name, status, state, allowedKeywords))
                {
                    info.Level = "expected-stopped";
                }
                else
                {
                    info.Level = "error";
                }

                if (info.Level == "running") result.RunningCount++;
                else if (info.Level == "expected-stopped") result.ExpectedStoppedCount++;
                else if (info.Level == "error") result.ErrorCount++;

                result.Containers.Add(info);
            }

            result.Success = result.ErrorCount == 0 && result.Containers.Count > 0;
            result.Message = $"총 {result.Containers.Count}개 (running {result.RunningCount}, 허용 {result.ExpectedStoppedCount}, 오류 {result.ErrorCount})";
            return result;
        }

        public static async Task<(bool success, string logs, string error)> GetContainerLogsAsync(string containerName, int tail)
        {
            if (!IsValidContainerName(containerName))
                return (false, "", "유효하지 않은 컨테이너 이름입니다.");

            if (tail < 1) tail = 1;
            if (tail > 500) tail = 500;

            var res = await RunDockerAsync($"logs --tail {tail} {containerName}");
            if (!res.Success)
                return (false, "", res.Error);

            return (true, res.Output, "");
        }

        /// <summary>
        /// 정지된 컨테이너를 삭제합니다. 실행 중인 컨테이너는 거부합니다.
        /// </summary>
        public static async Task<(bool success, string error)> RemoveContainerAsync(string containerName)
        {
            if (!IsValidContainerName(containerName))
                return (false, "유효하지 않은 컨테이너 이름입니다.");

            // running 상태 확인
            var inspect = await RunDockerAsync($"inspect --format \"{{{{.State.Running}}}}\" {containerName}");
            if (!inspect.Success)
                return (false, $"컨테이너 조회 실패: {inspect.Error}");

            if (inspect.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return (false, "실행 중인 컨테이너는 삭제할 수 없습니다. 먼저 중지해주세요.");

            var rm = await RunDockerAsync($"rm {containerName}");
            if (!rm.Success)
                return (false, $"컨테이너 삭제 실패: {rm.Error}");

            return (true, "");
        }

        private static bool IsValidContainerName(string containerName)
        {
            return Regex.IsMatch(containerName ?? "", @"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$");
        }

        private static async Task<(bool Success, string Output, string Error)> RunDockerAsync(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var output = await outTask;
                var error = await errTask;
                if (process.ExitCode != 0)
                {
                    var msg = string.IsNullOrWhiteSpace(error)
                        ? $"exit code {process.ExitCode}"
                        : error.Trim();
                    return (false, "", msg);
                }

                var combined = output ?? "";
                if (!string.IsNullOrWhiteSpace(error))
                {
                    if (!string.IsNullOrWhiteSpace(combined))
                        combined += Environment.NewLine;
                    combined += error;
                }
                return (true, combined, "");
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
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

        private static bool IsWhitelistedContainer(string containerName, IReadOnlyCollection<string> allowedKeywords)
        {
            if (allowedKeywords.Count == 0)
                return false;
            var name = containerName.ToLowerInvariant();
            return allowedKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

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

            if (allowedKeywords.Count == 0)
                return true;

            return allowedKeywords.Any(k => containerName.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> BuildPrefixCandidates(ProjectInfo project)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var cp = project.ContainerPrefix;
            if (!string.IsNullOrEmpty(cp) && seen.Add(cp))
                candidates.Add(cp);

            if (!string.IsNullOrEmpty(project.Name) && seen.Add(project.Name))
                candidates.Add(project.Name);

            if (!string.IsNullOrEmpty(project.DeployPath))
            {
                var dirName = Path.GetFileName(project.DeployPath);
                if (!string.IsNullOrEmpty(dirName) && seen.Add(dirName))
                    candidates.Add(dirName);
            }

            if (!string.IsNullOrEmpty(project.DeployPath))
            {
                var composeName = ReadComposeProjectName(project.DeployPath);
                if (!string.IsNullOrEmpty(composeName) && seen.Add(composeName))
                    candidates.Add(composeName);
            }

            return candidates;
        }

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
                        if (line.StartsWith("name:"))
                        {
                            var value = line[5..].Trim().Trim('"', '\'');
                            if (!string.IsNullOrEmpty(value))
                                return value;
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DeployMonitor.Models;

namespace DeployMonitor.Services
{
    /// <summary>
    /// bare repo 폴더를 스캔하여 프로젝트 목록 생성
    /// </summary>
    public class RepoScanner
    {
        /// <summary>디버그 로그 이벤트</summary>
        public event Action<string>? DebugLog;

        /// <summary>
        /// 저장소 폴더에서 프로젝트 목록을 스캔한다.
        /// </summary>
        public List<ProjectInfo> Scan(string repoFolder, string deployFolder, string defaultBranch)
        {
            var projects = new List<ProjectInfo>();

            if (!Directory.Exists(repoFolder))
            {
                DebugLog?.Invoke($"[DEBUG] 저장소 폴더 없음: {repoFolder}");
                return projects;
            }

            var dirs = Directory.GetDirectories(repoFolder);
            DebugLog?.Invoke($"[DEBUG] 저장소 폴더에서 {dirs.Length}개 하위 폴더 발견");

            foreach (var dir in dirs)
            {
                var dirName = Path.GetFileName(dir);

                // bare repo 확인: HEAD 파일이 있어야 함
                var headPath = Path.Combine(dir, "HEAD");
                if (!File.Exists(headPath))
                {
                    DebugLog?.Invoke($"[DEBUG] [{dirName}] HEAD 파일 없음 - 스킵");
                    continue;
                }

                // 프로젝트명: .git 확장자 제거
                var projectName = dirName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? dirName[..^4]
                    : dirName;

                DebugLog?.Invoke($"[DEBUG] [{projectName}] bare repo 확인됨");

                // deploy 폴더 매칭
                var deployPath = Path.Combine(deployFolder, projectName);

                // 저장소에 deploy.bat가 있는지 확인만 (복사는 DeployRunner에서 clone/pull로 처리)
                var hasDeployBat = HasDeployBatInRepo(dir, defaultBranch, projectName, out var foundPath, DebugLog);

                if (!hasDeployBat)
                {
                    DebugLog?.Invoke($"[DEBUG] [{projectName}] deploy.bat 없음 - 스킵");
                    continue;
                }

                DebugLog?.Invoke($"[DEBUG] [{projectName}] deploy.bat 발견: {foundPath}");

                // deploy.bat에서 Docker 컨테이너 접두사 추출
                var containerPrefix = "";
                var deployTriggers = "";
                if (TryReadDeployBatFromRepo(dir, defaultBranch, projectName, out var batContent, out _, DebugLog))
                {
                    containerPrefix = ExtractProjectName(batContent);
                    if (!string.IsNullOrEmpty(containerPrefix))
                        DebugLog?.Invoke($"[DEBUG] [{projectName}] PROJECT_NAME={containerPrefix}");

                    deployTriggers = ExtractDeployTriggers(batContent);
                    if (!string.IsNullOrEmpty(deployTriggers))
                        DebugLog?.Invoke($"[DEBUG] [{projectName}] DEPLOY_TRIGGERS={deployTriggers}");
                }

                // 현재 커밋 해시 읽기
                var commitHash = ReadCommitHash(dir, defaultBranch);

                projects.Add(new ProjectInfo
                {
                    Name = projectName,
                    BareRepoPath = dir,
                    DeployPath = deployPath,
                    HasDeployBat = hasDeployBat,
                    Branch = defaultBranch,
                    LastCommitHash = commitHash,
                    ContainerPrefix = containerPrefix,
                    DeployTriggers = deployTriggers,
                    Status = ProjectStatus.Idle,
                    LastMessage = ""
                });
            }

            return projects;
        }

        /// <summary>
        /// bare repo에서 브랜치의 커밋 해시를 읽는다.
        /// refs/heads/{branch} 파일 → packed-refs 순서로 확인.
        /// </summary>
        public static string ReadCommitHash(string bareRepoPath, string branch)
        {
            // 1. refs/heads/{branch} 파일 확인
            var refPath = Path.Combine(bareRepoPath, "refs", "heads", branch);
            if (File.Exists(refPath))
            {
                try
                {
                    return File.ReadAllText(refPath).Trim();
                }
                catch
                {
                    // 파일 읽기 실패 시 packed-refs 확인
                }
            }

            // 2. packed-refs에서 검색 (git gc 후 refs가 pack될 수 있음)
            var packedRefsPath = Path.Combine(bareRepoPath, "packed-refs");
            if (File.Exists(packedRefsPath))
            {
                try
                {
                    var target = $"refs/heads/{branch}";
                    foreach (var line in File.ReadLines(packedRefsPath))
                    {
                        if (line.StartsWith('#')) continue;
                        // 형식: {hash} {ref}
                        var parts = line.Split(' ', 2);
                        if (parts.Length == 2 && parts[1].Trim() == target)
                            return parts[0].Trim();
                    }
                }
                catch
                {
                    // packed-refs 읽기 실패
                }
            }

            return "";
        }

        /// <summary>
        /// bare repo에 deploy.bat이 있는지 확인만 한다 (복사 안 함).
        /// </summary>
        public static bool HasDeployBatInRepo(
            string bareRepoPath,
            string branch,
            string projectName,
            out string foundPath,
            Action<string>? debugLog = null)
        {
            return TryFindDeployBatPath(bareRepoPath, branch, projectName, out foundPath, debugLog);
        }

        private static bool TryReadDeployBatFromRepo(
            string bareRepoPath,
            string branch,
            string projectName,
            out string content,
            out string sourcePath,
            Action<string>? debugLog = null)
        {
            content = "";
            sourcePath = "";
            try
            {
                if (!TryFindDeployBatPath(bareRepoPath, branch, projectName, out var repoPath, debugLog))
                    return false;

                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"--git-dir \"{bareRepoPath}\" show {branch}:\"{repoPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                {
                    debugLog?.Invoke($"[DEBUG] git show 실패: {error}");
                    return false;
                }

                // BOM 제거 + CRLF 정규화
                if (output.Length > 0 && output[0] == '\uFEFF')
                    output = output[1..];
                output = output.Replace("\r\n", "\n").Replace("\n", "\r\n");

                content = output;
                sourcePath = repoPath;
                return true;
            }
            catch (Exception ex)
            {
                debugLog?.Invoke($"[DEBUG] TryReadDeployBat 예외: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// deploy.bat 내용에서 DEPLOY_TRIGGERS 값을 추출한다.
        /// 패턴: set "DEPLOY_TRIGGERS=path1 path2" 또는 set DEPLOY_TRIGGERS=path1 path2
        /// </summary>
        private static string ExtractDeployTriggers(string batContent)
        {
            var match = Regex.Match(batContent, @"set\s+""?DEPLOY_TRIGGERS=([^""\r\n]+)""?", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        /// <summary>
        /// deploy.bat 내용에서 PROJECT_NAME 값을 추출한다.
        /// 패턴: set "PROJECT_NAME=xxx" 또는 set PROJECT_NAME=xxx
        /// </summary>
        private static string ExtractProjectName(string batContent)
        {
            var match = Regex.Match(batContent, @"set\s+""?PROJECT_NAME=([^""\r\n]+)""?", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private static bool TryFindDeployBatPath(
            string bareRepoPath,
            string branch,
            string projectName,
            out string repoPath,
            Action<string>? debugLog = null)
        {
            repoPath = "";
            try
            {
                var args = $"--git-dir \"{bareRepoPath}\" ls-tree -r --name-only {branch}";
                debugLog?.Invoke($"[DEBUG] 실행: git {args}");

                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                {
                    debugLog?.Invoke($"[DEBUG] git ls-tree 실패 (exit {process.ExitCode}): {error}");
                    return false;
                }

                var fileCount = 0;
                var matches = new List<string>();
                using (var reader = new StringReader(output))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        fileCount++;
                        if (line.EndsWith("deploy.bat", StringComparison.OrdinalIgnoreCase))
                            matches.Add(line.Trim());
                    }
                }

                debugLog?.Invoke($"[DEBUG] ls-tree 결과: 총 {fileCount}개 파일, deploy.bat {matches.Count}개 매칭");

                if (matches.Count == 0)
                    return false;

                // 루트 deploy.bat 우선
                foreach (var m in matches)
                {
                    if (string.Equals(m, "deploy.bat", StringComparison.OrdinalIgnoreCase))
                    {
                        repoPath = m;
                        return true;
                    }
                }

                // 그 외 첫 번째
                repoPath = matches[0];
                return true;
            }
            catch (Exception ex)
            {
                debugLog?.Invoke($"[DEBUG] TryFindDeployBatPath 예외: {ex.Message}");
                return false;
            }
        }
    }
}

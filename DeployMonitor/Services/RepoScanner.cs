using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using DeployMonitor.Models;

namespace DeployMonitor.Services
{
    /// <summary>
    /// bare repo 폴더를 스캔하여 프로젝트 목록 생성
    /// </summary>
    public class RepoScanner
    {
        /// <summary>
        /// 저장소 폴더에서 프로젝트 목록을 스캔한다.
        /// </summary>
        public List<ProjectInfo> Scan(string repoFolder, string deployFolder, string defaultBranch)
        {
            var projects = new List<ProjectInfo>();

            if (!Directory.Exists(repoFolder))
                return projects;

            // depth 1에서 *.git 디렉토리 검색
            foreach (var dir in Directory.GetDirectories(repoFolder))
            {
                var dirName = Path.GetFileName(dir);

                // bare repo 확인: HEAD 파일이 있어야 함
                if (!File.Exists(Path.Combine(dir, "HEAD")))
                    continue;

                // 프로젝트명: .git 확장자 제거
                var projectName = dirName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? dirName[..^4]
                    : dirName;

                // deploy 폴더 매칭
                var deployPath = Path.Combine(deployFolder, projectName);

                // 저장소에 deploy.bat가 있으면 배포 폴더에 복사 (없으면 제외)
                var hasDeployBat = SyncDeployBatFromRepo(dir, deployPath, defaultBranch, projectName, out _, out _);

                // 현재 커밋 해시 읽기
                var commitHash = ReadCommitHash(dir, defaultBranch);

                if (hasDeployBat) // deploy.bat 파일이 있는 경우에만 추가
                {
                    projects.Add(new ProjectInfo
                    {
                        Name = projectName,
                        BareRepoPath = dir,
                        DeployPath = deployPath,
                        HasDeployBat = hasDeployBat,
                        Branch = defaultBranch,
                        LastCommitHash = commitHash,
                        Status = ProjectStatus.Idle, // deploy.bat이 있으면 항상 Idle
                        LastMessage = "" // deploy.bat이 있으면 메시지 없음
                    });
                }
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
        /// bare repo의 deploy.bat을 읽어 배포 폴더에 동기화한다.
        /// </summary>
        public static bool SyncDeployBatFromRepo(
            string bareRepoPath,
            string deployPath,
            string branch,
            string projectName,
            out bool updated,
            out string sourcePath)
        {
            updated = false;
            sourcePath = "";

            if (!TryReadDeployBatFromRepo(bareRepoPath, branch, projectName, out var content, out sourcePath))
                return false;

            try
            {
                Directory.CreateDirectory(deployPath);
                var dest = Path.Combine(deployPath, "deploy.bat");

                if (File.Exists(dest))
                {
                    var existing = File.ReadAllText(dest);
                    if (existing == content)
                        return true;
                }

                File.WriteAllText(dest, content, new UTF8Encoding(false));
                updated = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadDeployBatFromRepo(
            string bareRepoPath,
            string branch,
            string projectName,
            out string content,
            out string sourcePath)
        {
            content = "";
            sourcePath = "";
            try
            {
                if (!TryFindDeployBatPath(bareRepoPath, branch, projectName, out var repoPath))
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
                process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                    return false;

                content = output;
                sourcePath = repoPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindDeployBatPath(
            string bareRepoPath,
            string branch,
            string projectName,
            out string repoPath)
        {
            repoPath = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"--git-dir \"{bareRepoPath}\" ls-tree -r --name-only {branch}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                    return false;

                var matches = new List<string>();
                using (var reader = new StringReader(output))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.EndsWith("deploy.bat", StringComparison.OrdinalIgnoreCase))
                            matches.Add(line.Trim());
                    }
                }

                if (matches.Count == 0)
                    return false;

                // 프로젝트명/deploy.bat 우선
                var preferred = $"{projectName}/deploy.bat";
                foreach (var m in matches)
                {
                    if (string.Equals(m.Replace('\\', '/'), preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        repoPath = m;
                        return true;
                    }
                }

                repoPath = matches[0];
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

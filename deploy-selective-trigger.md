# deploy.bat 선택적 배포 트리거 개선

## 왜 바꿔야 하는가

현재 DeployMonitor는 bare repo에서 커밋 해시 변경을 감지하면 **무조건 git pull + deploy.bat 실행**을 수행한다.
하나의 저장소에 서버/클라이언트가 공존하는 경우, 배포 대상이 아닌 코드만 변경되어도 **불필요한 빌드 + 다운타임**이 발생한다.

---

## 해결 방법

각 프로젝트의 `deploy.bat`에 `DEPLOY_TRIGGERS` 설정 변수를 선언한다.
DeployMonitor가 git pull 후 변경된 파일이 트리거 경로에 해당하는지 확인하고, 해당 없으면 배포를 스킵한다.

---

## 1단계: deploy.bat에 설정 변수 추가

```bat
REM === Deploy Trigger Config ===
REM 이 경로에 변경이 있을 때만 배포 진행 (공백으로 구분)
REM 미설정 시 모든 변경에 대해 배포 진행 (기존 동작 유지)
set "DEPLOY_TRIGGERS=<서버소스경로>/ <Docker설정경로>/ deploy.bat"
```

프로젝트 구조에 맞게 경로를 지정한다. 디렉토리는 `/`로 끝내고, 개별 파일은 파일명을 그대로 쓴다.

```
예) 모노레포 (서버 + 클라이언트)
    set "DEPLOY_TRIGGERS=backend/ docker-images/ deploy.bat"

예) 서버만 있는 저장소 (설정 불필요)
    DEPLOY_TRIGGERS 미설정 → 모든 변경에 대해 배포 (기존 동작)
```

---

## 2단계: DeployMonitor C# 수정

### 2-1. ProjectInfo.cs - 속성 추가

```csharp
/// <summary>배포 트리거 경로 목록 (deploy.bat의 DEPLOY_TRIGGERS)</summary>
public List<string> DeployTriggers { get; set; } = new();
```

### 2-2. RepoScanner.cs - DEPLOY_TRIGGERS 추출

`ExtractProjectName()`처럼 deploy.bat 내용에서 DEPLOY_TRIGGERS를 파싱한다.

```csharp
/// <summary>deploy.bat에서 DEPLOY_TRIGGERS 값을 추출</summary>
private static List<string> ExtractDeployTriggers(string batContent)
{
    var match = Regex.Match(batContent,
        @"set\s+""?DEPLOY_TRIGGERS=([^""\r\n]+)""?",
        RegexOptions.IgnoreCase);

    if (!match.Success) return new List<string>(); // 미설정 = 빈 리스트

    return match.Groups[1].Value
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .ToList();
}
```

`Scan()` 메서드의 프로젝트 생성 부분에 추가:

```csharp
var deployTriggers = ExtractDeployTriggers(batContent);

projects.Add(new ProjectInfo
{
    // ... 기존 필드 ...
    DeployTriggers = deployTriggers,
});
```

### 2-3. DeployRunner.cs - 변경 파일 필터링

`RunDeployAsync()`에서 git pull 성공 후, deploy.bat 실행 전에 체크 로직을 추가한다.

```csharp
// git pull 성공 후 ---

// DEPLOY_TRIGGERS가 설정된 경우에만 필터링 (미설정 = 무조건 배포)
if (project.DeployTriggers.Count > 0)
{
    var changedFiles = await GetChangedFilesAsync(project);

    var needDeploy = changedFiles.Any(file =>
        project.DeployTriggers.Any(trigger =>
            file.StartsWith(trigger, StringComparison.OrdinalIgnoreCase)));

    if (!needDeploy)
    {
        Log("배포 대상 변경 없음 - 스킵");
        UpdateUI(() =>
        {
            project.Status = ProjectStatus.Idle;
            project.LastMessage = "변경 감지됨 (배포 대상 아님)";
        });
        return; // deploy.bat 실행하지 않음
    }
}

// --- deploy.bat auto 실행 ---
```

변경 파일 목록을 가져오는 헬퍼:

```csharp
/// <summary>직전 pull로 변경된 파일 목록 조회</summary>
private async Task<List<string>> GetChangedFilesAsync(ProjectInfo project)
{
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = "diff --name-only HEAD@{1} HEAD",
        WorkingDirectory = project.DeployPath,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = new Process { StartInfo = psi };
    process.Start();
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();

    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                 .Select(f => f.Trim())
                 .ToList();
}
```

---

## 동작 흐름 (수정 후)

```
CommitWatcher: 커밋 해시 변경 감지
    ↓
DeployRunner: git pull 실행
    ↓
DEPLOY_TRIGGERS 설정됨?
    ├─ NO → deploy.bat 실행 (기존 동작 유지)
    └─ YES → 변경 파일이 트리거 경로에 해당?
              ├─ YES → deploy.bat 실행
              └─ NO → 스킵 (로그만 남김)
```

## 참고사항

- `DEPLOY_TRIGGERS`가 미설정(빈 문자열)이면 **기존 동작 그대로** 유지 (하위 호환).
- `HEAD@{1}`은 git pull 직전 커밋을 가리킨다. pull로 가져온 변경분만 비교.
- 여러 커밋이 한번에 pull되어도 `HEAD@{1}..HEAD` 전체 diff를 보므로 누락 없음.

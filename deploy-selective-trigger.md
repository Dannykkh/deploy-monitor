# deploy.bat 선택적 배포 트리거

## 왜 필요한가

DeployMonitor는 bare repo에서 커밋 해시 변경을 감지하면 **무조건 git pull + deploy.bat 실행**을 수행한다.
하나의 저장소에 서버/클라이언트가 공존하는 경우, 배포 대상이 아닌 코드(프론트엔드, 문서 등)만 변경되어도 **불필요한 Docker 빌드 + 다운타임**이 발생한다.

---

## 사용법

각 프로젝트의 `deploy.bat` 상단에 `DEPLOY_TRIGGERS` 변수를 추가한다.

```bat
REM === Deploy Trigger Config ===
REM 이 경로에 변경이 있을 때만 배포 진행 (공백으로 구분)
set "DEPLOY_TRIGGERS=backend-spring/ docker-images/ deploy.bat"
```

- 디렉토리는 `/`로 끝낸다 (예: `backend-spring/`)
- 개별 파일은 파일명 그대로 쓴다 (예: `deploy.bat`)
- **미설정 시 기존 동작 유지** — 모든 변경에 대해 배포

### 프로젝트별 예시

| 프로젝트 구조 | DEPLOY_TRIGGERS |
|---------------|----------------|
| 모노레포 (서버 + 앱) | `backend-spring/ docker-images/ deploy.bat` |
| 모노레포 (서버 + 웹) | `backend/ docker/ deploy.bat` |
| 서버만 있는 저장소 | 미설정 (모든 변경에 배포) |

---

## 구현 (완료)

### 수정 파일

| 파일 | 변경 내용 |
|------|-----------|
| `ProjectInfo.cs` | `DeployTriggers` (string), `PreviousCommitHash` (string) 프로퍼티 추가 |
| `RepoScanner.cs` | `ExtractDeployTriggers()` — deploy.bat에서 DEPLOY_TRIGGERS 값 파싱 |
| `CommitWatcher.cs` | 새 커밋 감지 시 `PreviousCommitHash`에 이전 해시 보존 |
| `DeployRunner.cs` | `CheckDeployTriggersAsync()` — 변경 파일 vs 트리거 경로 비교, 불일치 시 배포 생략 |

### 핵심 로직

**CommitWatcher.CheckProject()** — 이전 해시 보존:

```csharp
if (currentHash != knownHash)
{
    project.PreviousCommitHash = knownHash; // 이전 해시 저장
    _knownHashes[project.Name] = currentHash;
    CommitDetected?.Invoke(project, currentHash);
}
```

**DeployRunner.RunDeployAsync()** — git pull 후, deploy.bat 실행 전 체크:

```csharp
if (!string.IsNullOrEmpty(project.DeployTriggers) && !string.IsNullOrEmpty(project.PreviousCommitHash))
{
    var shouldDeploy = await CheckDeployTriggersAsync(project, deployLog);
    if (!shouldDeploy)
    {
        // deploy.bat 실행하지 않음
        project.Status = ProjectStatus.Idle;
        project.LastMessage = "변경 없음 - 빌드 생략";
        return;
    }
}
```

**DeployRunner.CheckDeployTriggersAsync()** — bare repo에서 변경 파일 비교:

```csharp
// bare repo에서 이전 커밋 ~ 현재 커밋 사이 변경 파일 조회
git --git-dir "{bareRepoPath}" diff --name-only {PreviousCommitHash} {LastCommitHash}

// 변경 파일이 트리거 경로로 시작하면 매칭
trimmed.StartsWith(trigger, StringComparison.OrdinalIgnoreCase)
```

`HEAD@{1}` 대신 `PreviousCommitHash`를 사용하여 reflog에 의존하지 않고 정확한 범위를 비교한다.

---

## 동작 흐름

```
CommitWatcher: 커밋 해시 변경 감지 (PreviousCommitHash 보존)
    ↓
DeployRunner: git pull (소스 동기화)
    ↓
DEPLOY_TRIGGERS 설정됨?
    ├─ NO → deploy.bat 실행 (기존 동작)
    └─ YES → bare repo에서 git diff로 변경 파일 조회
              ↓
              변경 파일이 트리거 경로에 해당?
              ├─ YES → deploy.bat 실행
              └─ NO → [SKIP] 빌드 생략 (로그 남김)
```

### 로그 출력 예시

**배포 대상 변경 있음:**
```
[MelatoninApp] 새 커밋 감지 (abc1234)
[MelatoninApp] 배포 트리거 확인: backend-spring/ docker-images/ deploy.bat
[MelatoninApp] [MATCH] backend-spring/src/main/java/App.java
[MelatoninApp] deploy.bat auto 실행
```

**배포 대상 변경 없음:**
```
[MelatoninApp] 새 커밋 감지 (def5678)
[MelatoninApp] 배포 트리거 확인: backend-spring/ docker-images/ deploy.bat
[MelatoninApp] [SKIP] 배포 대상 변경 없음 - 빌드 생략
```

---

## 참고사항

- `DEPLOY_TRIGGERS` 미설정 = 기존 동작 유지 (하위 호환)
- 트리거 확인 중 git diff 실패 시 안전하게 배포 진행 (false positive 방지)
- 여러 커밋이 한번에 push되어도 `PreviousCommitHash..LastCommitHash` 전체 diff를 보므로 누락 없음
- 수동 배포 버튼은 트리거 체크 없이 항상 실행됨

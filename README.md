# Deploy Monitor

Bonobo Git Server의 bare repository를 감시하여 새 커밋이 push되면 자동으로 배포하는 WPF 애플리케이션입니다.

## 목적

- Git push 이벤트를 감지하여 자동 배포 수행
- 여러 프로젝트를 한 화면에서 모니터링
- deploy.bat 기반의 유연한 배포 스크립트 지원

## 전체 흐름

```
┌─────────────────────────────────────────────────────────────────────────┐
│  개발자 PC                                                               │
│  git push ─────────────────────┐                                        │
└────────────────────────────────┼────────────────────────────────────────┘
                                 ▼
┌─────────────────────────────────────────────────────────────────────────┐
│  서버                                                                    │
│                                                                          │
│  ┌──────────────────────┐      ┌──────────────────────┐                 │
│  │ Bonobo Git Server    │      │ Deploy Monitor       │                 │
│  │ (bare repo 폴더)     │ ───▶ │ (이 프로그램)        │                 │
│  │                      │ 감시 │                      │                 │
│  │ C:\Bonobo.Git.Server │      │ 1. 커밋 변경 감지    │                 │
│  │ \App_Data\Repositories      │ 2. git clone/pull    │                 │
│  │   ├── project-a.git  │      │ 3. deploy.bat 실행   │                 │
│  │   ├── project-b.git  │      │                      │                 │
│  │   └── project-c.git  │      └──────────┬───────────┘                 │
│  └──────────────────────┘                 │                              │
│                                           ▼                              │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │ Deploy 폴더 (D:\deploy) - git working copy                       │   │
│  │   ├── project-a\        (자동 clone/pull)                        │   │
│  │   │     ├── .git\                                                 │   │
│  │   │     ├── src\                                                  │   │
│  │   │     └── deploy.bat  ──▶ 실행 (auto 인자)                     │   │
│  │   ├── project-b\                                                  │   │
│  │   └── project-c\                                                  │   │
│  └──────────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────┘
```

## 상세 동작

### 1. 프로젝트 스캔 (RepoScanner)

```
bare repo 폴더 스캔
       │
       ▼
각 *.git 폴더에서 HEAD 파일 확인 (bare repo 검증)
       │
       ▼
git ls-tree로 deploy.bat 존재 여부 확인
       │
       ├── deploy.bat 있음 → 프로젝트 리스트에 등록
       │
       └── deploy.bat 없음 → 리스트에서 제외
```

### 2. 커밋 감시 (CommitWatcher)

```
감시 시작
    │
    ├── FileSystemWatcher: refs/heads/{branch} 파일 변경 감지
    │
    └── 폴링 타이머: N초마다 커밋 해시 확인 (백업)
           │
           ▼
    커밋 해시 변경 감지
           │
           ▼
    CommitDetected 이벤트 발생
```

### 3. 배포 실행 (DeployRunner)

```
CommitDetected 이벤트 수신
       │
       ▼
배포 폴더에 .git 존재 확인
       │
       ├── 없음 (최초) → git clone
       │
       └── 있음 → git pull
       │
       ▼
deploy.bat auto 실행
       │
       ▼
Docker 컨테이너 상태 확인 (docker ps)
       │
       ├── 모든 컨테이너 running → 성공
       │
       └── 실패한 컨테이너 있음 → 오류 + 로그 출력
```

## 프로젝트 구조

```
DeployMonitor/
├── Models/
│   ├── AppSettings.cs      # 설정 (settings.json)
│   └── ProjectInfo.cs      # 프로젝트 정보 모델
├── Services/
│   ├── RepoScanner.cs      # bare repo 스캔 (deploy.bat 존재 확인)
│   ├── CommitWatcher.cs    # 커밋 변경 감시 (FSW + 폴링)
│   └── DeployRunner.cs     # git clone/pull + deploy.bat 실행
├── ViewModels/
│   ├── MainViewModel.cs    # 메인 뷰모델
│   └── RelayCommand.cs     # ICommand 구현
├── MainWindow.xaml         # 메인 UI
└── App.xaml                # 앱 엔트리포인트
```

## 설정

`settings.json` 파일로 관리됩니다:

| 항목 | 설명 | 기본값 |
|------|------|--------|
| RepositoryFolder | Bonobo bare repo 폴더 | `C:\Bonobo.Git.Server\App_Data\Repositories` |
| DeployFolder | 배포 작업 폴더 (working copy) | `D:\deploy` |
| IntervalSeconds | 폴링 주기 (초) | 30 |
| DefaultBranch | 감시할 브랜치 | master |

> **참고:** 프로그램은 실행 시 자동으로 감시를 시작합니다.

## deploy.bat 작성 규칙

프로젝트 저장소에 `deploy.bat` 파일을 커밋하면 자동으로 감지됩니다.

**중요:** git clone/pull은 Deploy Monitor가 자동으로 수행하므로, deploy.bat에서는 빌드/배포 로직만 작성하세요.

```batch
@echo off
REM deploy.bat - 자동 배포 스크립트
REM 첫 번째 인자가 "auto"이면 Deploy Monitor에서 호출된 것

if "%1"=="auto" (
    echo [AUTO] 자동 배포 시작
)

REM git pull은 Deploy Monitor가 자동으로 수행하므로 불필요

REM 빌드
dotnet publish -c Release -o publish

REM 서비스 재시작 등
net stop MyService
xcopy /E /Y publish\* C:\Services\MyService\
net start MyService

exit /b 0
```

**위치:** 저장소 루트에 `deploy.bat` 파일 배치

## 주요 기능

### 시스템 트레이

- 창 닫기 버튼 → 트레이로 최소화
- 트레이 더블클릭 → 창 열기
- 우클릭 메뉴: 열기 / 감시 시작·중지 / 종료
- 배포 완료 시 풍선 알림

### 자동 시작

- 프로그램 실행 시 자동으로 프로젝트 스캔 및 감시 시작
- 별도의 "감시 시작" 버튼 클릭 불필요

### 로그 분리

- **감시 로그**: 프로젝트 스캔, 커밋 감지 메시지
- **배포 로그**: git clone/pull, deploy.bat 출력, Docker 상태
- 각 로그는 500줄 제한 (초과 시 최근 400줄만 유지)
- 탭 전환 시 자동으로 맨 아래로 스크롤

### Docker 연동

배포 완료 후 Docker 컨테이너 상태를 자동 확인합니다:

- `docker ps --filter "name={프로젝트명}-"` 로 컨테이너 조회
- 모든 컨테이너가 running 상태면 성공
- unhealthy 또는 stopped 컨테이너 발견 시 오류 표시
- 실패한 컨테이너의 최근 로그 20줄 출력

## 프로젝트 상태

| 상태 | 표시 | 설명 |
|------|------|------|
| Idle | ● 대기 | 감시 대기 중 |
| Checking | ⟳ 확인중 | 커밋 확인 중 |
| Deploying | ⟳ 배포중 | clone/pull + deploy.bat 실행 중 |
| Success | ✓ 정상 | 배포 성공 |
| Error | ✗ 오류 | 배포 실패 |
| NotConfigured | — 미설정 | deploy.bat 없음 |

## 요구사항

- .NET 8.0 Windows Desktop Runtime
- **Git for Windows** (PATH에 등록 필수)
- Windows OS
- Docker Desktop (선택, 컨테이너 상태 확인용)

## 설치

1. [Git for Windows](https://git-scm.com/download/win) 설치
   - 설치 시 "Add to PATH" 옵션 체크
2. [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 설치
3. DeployMonitor 실행 파일 배포

## 빌드

```bash
cd DeployMonitor
dotnet build -c Release
```

## 라이선스

MIT License

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeployMonitor.Models
{
    /// <summary>
    /// 프로젝트 상태
    /// </summary>
    public enum ProjectStatus
    {
        Idle,           // 대기
        Checking,       // 확인 중
        Deploying,      // 배포 중
        Success,        // 배포 성공
        Error,          // 배포 오류
        NotConfigured   // 미설정 (deploy.bat 없음)
    }

    /// <summary>
    /// 개별 프로젝트 정보 모델
    /// </summary>
    public class ProjectInfo : INotifyPropertyChanged
    {
        private string _name = "";
        private string _bareRepoPath = "";
        private string? _deployPath;
        private bool _hasDeployBat;
        private string _branch = "master";
        private string _lastCommitHash = "";
        private ProjectStatus _status = ProjectStatus.Idle;
        private string _lastDeployTime = "";
        private string _lastMessage = "";
        private string _lastCommitDetectedTime = "";
        private string _lastDeploymentLog = "";
        private string _containerPrefix = "";
        private string _deployTriggers = "";
        private string _previousCommitHash = "";

        /// <summary>프로젝트명 (폴더명에서 .git 제거)</summary>
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>bare repo 경로</summary>
        public string BareRepoPath
        {
            get => _bareRepoPath;
            set => SetField(ref _bareRepoPath, value);
        }

        /// <summary>작업 복사본(deploy) 경로</summary>
        public string? DeployPath
        {
            get => _deployPath;
            set => SetField(ref _deployPath, value);
        }

        /// <summary>deploy.bat 존재 여부</summary>
        public bool HasDeployBat
        {
            get => _hasDeployBat;
            set => SetField(ref _hasDeployBat, value);
        }

        /// <summary>감시 브랜치</summary>
        public string Branch
        {
            get => _branch;
            set => SetField(ref _branch, value);
        }

        /// <summary>마지막 확인된 커밋 해시</summary>
        public string LastCommitHash
        {
            get => _lastCommitHash;
            set => SetField(ref _lastCommitHash, value);
        }

        /// <summary>현재 상태</summary>
        public ProjectStatus Status
        {
            get => _status;
            set
            {
                if (SetField(ref _status, value))
                    OnPropertyChanged(nameof(StatusDisplay));
            }
        }

        /// <summary>마지막 배포 시각 (표시용)</summary>
        public string LastDeployTime
        {
            get => _lastDeployTime;
            set => SetField(ref _lastDeployTime, value);
        }

        /// <summary>마지막 상태 메시지</summary>
        public string LastMessage
        {
            get => _lastMessage;
            set => SetField(ref _lastMessage, value);
        }

        /// <summary>마지막 커밋 감지 시간</summary>
        public string LastCommitDetectedTime
        {
            get => _lastCommitDetectedTime;
            set => SetField(ref _lastCommitDetectedTime, value);
        }

        /// <summary>Docker 컨테이너 이름 접두사 (deploy.bat의 PROJECT_NAME). 미설정 시 Name 사용</summary>
        public string ContainerPrefix
        {
            get => string.IsNullOrEmpty(_containerPrefix) ? _name : _containerPrefix;
            set => SetField(ref _containerPrefix, value);
        }

        /// <summary>배포 트리거 경로 (공백 구분, deploy.bat의 DEPLOY_TRIGGERS). 비어있으면 모든 변경에 배포</summary>
        public string DeployTriggers
        {
            get => _deployTriggers;
            set => SetField(ref _deployTriggers, value);
        }

        /// <summary>이전 커밋 해시 (선택적 배포 판단용, CommitWatcher에서 설정)</summary>
        public string PreviousCommitHash
        {
            get => _previousCommitHash;
            set => SetField(ref _previousCommitHash, value);
        }

        /// <summary>마지막 배포 로그 (상세)</summary>
        public string LastDeploymentLog
        {
            get => _lastDeploymentLog;
            set => SetField(ref _lastDeploymentLog, value);
        }

        /// <summary>UI 표시용 상태 텍스트</summary>
        public string StatusDisplay => Status switch
        {
            ProjectStatus.Idle => "● 대기",
            ProjectStatus.Checking => "⟳ 확인중",
            ProjectStatus.Deploying => "⟳ 배포중",
            ProjectStatus.Success => "✓ 정상",
            ProjectStatus.Error => "✗ 오류",
            ProjectStatus.NotConfigured => "— 미설정",
            _ => "?"
        };

        // --- INotifyPropertyChanged ---

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}

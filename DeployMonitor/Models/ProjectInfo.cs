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

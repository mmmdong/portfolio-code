using GameLogic.Define;
using GameLogic.Network;

namespace GameLogic.TrophyChallenge
{
    // UI 가 직접 보는 뷰모델. UI 는 본 모델만 알면 되고, 패킷·테이블 원본은 알 필요가 없다(SRP/LSP).
    // 매니저(TrophyChallengeManager.BuildViewModels) 가 생성하고, 사용 측은 readonly 로 소비한다.
    public class TrophyChallengeViewModel
    {
        public long TrophyId;
        public int ChallengeId;
        public int MissionIndex;
        public TrophyChallengeState State;
        public int Progress;
        public int RequiredCount;
        public bool IsRewardClaimed;

        // 원본 참조 — UI 텍스트/아이콘 등 부가 정보가 필요할 때만 사용
        public TrophyMissionGroupPacketData MissionGroupRow;
        public TrophyChallengeMissionTableData MissionTableRow;
        public TrophyChallengePacketData UserChallenge;

        // 시즌 만료 후 _Clear 결과 그리드는 State 가 모두 Expired 로 평가된다(StateEvaluator 가 만료를
        // 클리어보다 먼저 판정). 따라서 결과 그리드의 클리어 트로피 여부는 만료와 무관한 '실제 완료 데이터'
        // (보상 수령 완료 isComplete / 진행도 도달)로 판정한다.
        public bool IsChallengeCleared
            => State == TrophyChallengeState.Completed
               || State == TrophyChallengeState.Rewarded
               || (UserChallenge != null
                   && (UserChallenge.isComplete || (RequiredCount > 0 && UserChallenge.progress >= RequiredCount)));
    }

    // 챌린지 리스트 행 단위 뷰모델 — 한 행에 챌린지 3개(슬롯) 가 들어가는 그리드 구조.
    // 마지막 행은 챌린지가 3개 미만일 수 있어 누락 칸은 null.
    public class TrophyChallengeRowViewModel
    {
        public const int CELLS_PER_ROW = 3;
        public TrophyChallengeViewModel[] Cells = new TrophyChallengeViewModel[CELLS_PER_ROW];
    }
}

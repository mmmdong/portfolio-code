using GameLogic.Define;
using GameLogic.Network;

namespace GameLogic.TrophyChallenge
{
    // 한 챌린지의 현재 상태를 결정하는 책임 (단일 책임).
    // 정렬 정책 / UI / 레드닷은 평가 결과(TrophyChallengeState)에만 의존한다.
    public interface ITrophyChallengeStateEvaluator
    {
        TrophyChallengeState Evaluate(
            TrophyMasterPacketData master,
            TrophyInfoPacketData info,
            TrophyChallengePacketData challenge,
            TrophyMissionGroupPacketData groupRow,
            int requiredCount,
            int currentLevel,
            long currentEpochSeconds);
    }
}

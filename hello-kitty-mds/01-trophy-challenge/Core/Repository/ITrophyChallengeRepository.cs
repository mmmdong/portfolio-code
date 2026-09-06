using System.Collections.Generic;

using GameLogic.Network;

namespace GameLogic.TrophyChallenge
{
    // 데이터 접근의 추상 — 매니저/뷰모델은 UserData 구조를 직접 모르고 본 인터페이스로만 접근한다(DIP).
    public interface ITrophyChallengeRepository
    {
        IReadOnlyDictionary<long, TrophyMasterPacketData> GetMasters();
        TrophyMasterPacketData GetMaster(long trophyId);

        IReadOnlyCollection<long> GetUserTrophyIds();
        TrophyInfoPacketData GetUserInfo(long trophyId);
        TrophyChallengePacketData GetChallenge(long trophyId, int challengeId);
        IReadOnlyDictionary<int, TrophyChallengePacketData> GetChallenges(long trophyId);

        // 진행도는 RQTrophyChallengeUpdate 응답의 누적값(amount)으로 동기화한다(명세서 §3.3).
        // 완료 / 최종 수령은 응답 페이로드가 없어(명세서 §3.4~3.5) 클라 캐시 보정 책임을 Repository 가 갖는다.
        void SetProgress(long trophyId, int challengeId, int amount);
        void MarkCompleted(long trophyId, int challengeId);
        void MarkFinalRewardClaimed(long trophyId);
    }
}

using System.Collections.Generic;

using GameLogic.Network;

namespace GameLogic.TrophyChallenge
{
    // DataManager (UserData.TrophyChallengeContentsData) 어댑터.
    // Repository 가 DataManager 만 의존하므로 도메인 클래스 입장에서는 DataManager 를 직접 알 필요가 없다.
    public class TrophyChallengeRepository : ITrophyChallengeRepository
    {
        private static readonly Dictionary<int, TrophyChallengePacketData> EMPTY_CHALLENGES = new();

        public IReadOnlyDictionary<long, TrophyMasterPacketData> GetMasters()
        {
            return DataManager.Instance.TrophyChallenge.MasterCache;
        }

        public TrophyMasterPacketData GetMaster(long trophyId)
        {
            var masters = DataManager.Instance.TrophyChallenge.MasterCache;
            return masters.TryGetValue(trophyId, out var master) ? master : null;
        }

        public IReadOnlyCollection<long> GetUserTrophyIds()
        {
            return DataManager.Instance.TrophyChallenge.UserEntries.Keys;
        }

        public TrophyInfoPacketData GetUserInfo(long trophyId)
        {
            var entries = DataManager.Instance.TrophyChallenge.UserEntries;
            return entries.TryGetValue(trophyId, out var entry) ? entry.Info : null;
        }

        public TrophyChallengePacketData GetChallenge(long trophyId, int challengeId)
        {
            var challenges = GetChallenges(trophyId);
            return challenges.TryGetValue(challengeId, out var challenge) ? challenge : null;
        }

        public IReadOnlyDictionary<int, TrophyChallengePacketData> GetChallenges(long trophyId)
        {
            var entries = DataManager.Instance.TrophyChallenge.UserEntries;
            if (!entries.TryGetValue(trophyId, out var entry) || entry.Challenges == null)
                return EMPTY_CHALLENGES;

            return entry.Challenges;
        }

        public void SetProgress(long trophyId, int challengeId, int amount)
        {
            DataManager.Instance.SetTrophyChallengeProgress(trophyId, challengeId, amount);
        }

        public void MarkCompleted(long trophyId, int challengeId)
        {
            DataManager.Instance.SetTrophyChallengeCompleted(trophyId, challengeId);
        }

        public void MarkFinalRewardClaimed(long trophyId)
        {
            DataManager.Instance.SetTrophyChallengeFinalRewardClaimed(trophyId);
        }
    }
}

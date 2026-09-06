using GameCore.Utils;
using GameLogic.Extension;
using GameLogic.Network;

namespace GameLogic
{
    // 트로피 챌린지 캐시 동기화 — 명세서.md §3 응답 처리부의 데이터 저장 책임.
    // 비즈니스 규칙(정렬/상태 분기/레드닷)은 GameLogic.TrophyChallenge 도메인 클래스가 담당하므로
    // 본 partial 은 "패킷 → UserData 캐시" 변환에만 집중한다(SRP).
    public partial class DataManager
    {
        public TrophyChallengeContentsData TrophyChallenge => UserData.TrophyChallengeContentsData;

        public void ResetTrophyChallengeCaching()
        {
            TrophyChallenge?.Clear();
        }

        public void SetTrophyChallengeMaster(RSTrophyChallengeMaster response)
        {
            if (null == response)
            {
                DLogger.Error($"{nameof(RSTrophyChallengeMaster)} : 트로피 챌린지 마스터 패킷 수신 실패");
                return;
            }

            var cache = TrophyChallenge;
            cache.MasterCache.Clear();

            var list = response.trophyMasterList;
            if (list.IsNullOrEmpty()) return;

            for (var i = 0; i < list.Length; ++i)
            {
                var data = list[i];
                if (null == data) continue;

                cache.MasterCache[data.trophyId] = data;
            }
        }

        public void SetTrophyChallengeInfo(RSTrophyChallengeInfo response)
        {
            if (null == response)
            {
                DLogger.Error($"{nameof(RSTrophyChallengeInfo)} : 트로피 챌린지 유저 정보 패킷 수신 실패");
                return;
            }

            var cache = TrophyChallenge;
            cache.UserEntries.Clear();

            var infoList = response.trophyInfoList;
            if (!infoList.IsNullOrEmpty())
            {
                for (var i = 0; i < infoList.Length; ++i)
                {
                    var info = infoList[i];
                    if (null == info) continue;

                    var entry = GetOrCreateEntry(info.trophyId);
                    entry.Info = info;
                }
            }

            var challengeList = response.trophyChallengeList;
            if (!challengeList.IsNullOrEmpty())
            {
                for (var i = 0; i < challengeList.Length; ++i)
                {
                    var challenge = challengeList[i];
                    if (null == challenge) continue;

                    var entry = GetOrCreateEntry(challenge.trophyId);
                    entry.Challenges[challenge.challengeId] = challenge;
                }
            }
        }

        // RQTrophyChallengeUpdate 응답이 갱신 후 누적값(amount)을 내려주므로(명세서 §3.3),
        // 클라 캐시는 로컬 누적이 아닌 서버 권위값으로 동기화한다.
        public void SetTrophyChallengeProgress(long trophyId, int challengeId, int amount)
        {
            var entry = GetOrCreateEntry(trophyId);
            if (!entry.Challenges.TryGetValue(challengeId, out var challenge))
            {
                challenge = new TrophyChallengePacketData
                {
                    trophyId = trophyId,
                    challengeId = challengeId,
                };
                entry.Challenges[challengeId] = challenge;
            }

            challenge.progress = amount;
        }

        public void SetTrophyChallengeCompleted(long trophyId, int challengeId)
        {
            var entry = GetOrCreateEntry(trophyId);
            if (!entry.Challenges.TryGetValue(challengeId, out var challenge))
            {
                challenge = new TrophyChallengePacketData
                {
                    trophyId = trophyId,
                    challengeId = challengeId,
                };
                entry.Challenges[challengeId] = challenge;
            }

            challenge.isComplete = true;
        }

        public void SetTrophyChallengeFinalRewardClaimed(long trophyId)
        {
            if (!TrophyChallenge.UserEntries.TryGetValue(trophyId, out var entry)) return;
            if (null == entry.Info) return;

            entry.Info.finalRewardClaim = true;
        }

        private TrophyChallengeUserEntry GetOrCreateEntry(long trophyId)
        {
            var cache = TrophyChallenge;
            if (!cache.UserEntries.TryGetValue(trophyId, out var entry))
            {
                entry = new TrophyChallengeUserEntry();
                cache.UserEntries[trophyId] = entry;
            }
            return entry;
        }
    }
}

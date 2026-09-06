using System.Collections.Generic;

using GameLogic.Network;

namespace GameLogic
{
    // 명세서.md §4 — 유저 진행 상태(TrophyInfo) 와 개별 챌린지 진행도(TrophyChallenge) 를 트로피ID 기준으로 묶어 보관한다.
    // 명세서.md §10 (재접속/점검 처리) — 서버 응답 기준으로 통째로 덮어쓰는 단방향 동기화를 전제로 한다.
    public class TrophyChallengeUserEntry
    {
        public TrophyInfoPacketData Info;
        public Dictionary<int, TrophyChallengePacketData> Challenges = new();
    }

    public class TrophyChallengeContentsData
    {
        // 운영툴이 내려주는 시즌 마스터 정보 (수신 즉시 통째로 덮어쓴다)
        public Dictionary<long, TrophyMasterPacketData> MasterCache = new();

        // 유저 진행 정보 (RQTrophyChallengeInfo / Update / Complete / FinalRewardClaim 응답으로 갱신)
        public Dictionary<long, TrophyChallengeUserEntry> UserEntries = new();

        public void Clear()
        {
            MasterCache.Clear();
            UserEntries.Clear();
        }
    }
}

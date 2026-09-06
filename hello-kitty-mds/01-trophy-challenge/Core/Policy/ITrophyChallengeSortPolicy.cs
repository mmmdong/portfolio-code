using System.Collections.Generic;

namespace GameLogic.TrophyChallenge
{
    // 챌린지 리스트 정렬 전략 — 명세서.md §7.2 정렬 규칙.
    // 1순위: 보상 미획득(완료&미수령) → 2순위: 미완료(진행/잠금 모두 포함)
    //  → 3순위: 완료&수령 → 4순위: 종료. 동일 상태 내에서는 missionRow.index 오름차순.
    public interface ITrophyChallengeSortPolicy
    {
        void Sort(List<TrophyChallengeViewModel> items);
    }
}

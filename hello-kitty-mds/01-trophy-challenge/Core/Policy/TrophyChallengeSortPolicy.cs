using System.Collections.Generic;

namespace GameLogic.TrophyChallenge
{
    public class TrophyChallengeSortPolicy : ITrophyChallengeSortPolicy
    {
        // 명세서.md §7.2 — 보상 미획득(완료&미수령) → 미완료(진행/잠금) → 완료&수령 → 종료 순.
        // 미완료 우선순위에 "잠금" 도 포함됨에 유의 (시즌 진입 후에는 잠금/진행이 동일 우선).
        private static int ToOrder(TrophyChallengeState state)
        {
            switch (state)
            {
                case TrophyChallengeState.Completed:    return 0;
                case TrophyChallengeState.InProgress:   return 1;
                case TrophyChallengeState.Locked:       return 1;
                case TrophyChallengeState.Rewarded:     return 2;
                case TrophyChallengeState.Expired:      return 3;
                default:                                return 9;
            }
        }

        public void Sort(List<TrophyChallengeViewModel> items)
        {
            if (items == null) return;

            items.Sort((a, b) =>
            {
                var orderA = ToOrder(a.State);
                var orderB = ToOrder(b.State);
                if (orderA != orderB) return orderA - orderB;

                // 동일 상태 — TrophyChallenge_Mission.index 오름차순
                return a.MissionIndex - b.MissionIndex;
            });
        }
    }
}

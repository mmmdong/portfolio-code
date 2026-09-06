using GameLogic.Define;
using GameLogic.Network;

namespace GameLogic.TrophyChallenge
{
    // 명세서.md §7.3 챌린지 상태별 GUI 의 ②~⑥ 매핑.
    // 시즌 미참여(잠금) / 진행 / 완료&미수령 / 완료&수령 / 종료의 5단계로 분기한다.
    public class TrophyChallengeStateEvaluator : ITrophyChallengeStateEvaluator
    {
        public TrophyChallengeState Evaluate(
            TrophyMasterPacketData master,
            TrophyInfoPacketData info,
            TrophyChallengePacketData challenge,
            TrophyMissionGroupPacketData groupRow,
            int requiredCount,
            int currentLevel,
            long currentEpochSeconds)
        {
            if (null == master)
                return TrophyChallengeState.Locked;

            // 종료 — 마스터 운영 기간 또는 유저 기준 기간이 지났으면 종료(명세서 §7.3 ⑥).
            if (IsExpired(master, info, currentEpochSeconds))
                return TrophyChallengeState.Expired;

            // 잠금 — 오픈 레벨 미충족 또는 시즌 시작 전.
            if (currentLevel < master.openLevel)
                return TrophyChallengeState.Locked;

            // 시작 전 — 마스터 운영 시작(begin) 또는 유저 기준 시작(startAt) 중 하나라도 미도래면 잠금.
            // IsExpired 가 master.end / info.endAt 를 함께 보는 것과 대칭(명세서 §4.4 유저 기준 기간 일원화).
            if (IsBeforeStart(master, info, currentEpochSeconds))
                return TrophyChallengeState.Locked;

            // 명세서.md §7.3 ③④ — Info RS 의 challenge.isCompleted 는 "보상 수령까지 완료" 를 의미한다.
            //   isCompleted == true  → 보상 수령 완료 → Rewarded(④)
            //   isCompleted == false → 보상 미수령. 로컬 캐싱 진행도(progress = RQTrophyChallengeUpdate 응답 amount
            //                          동기화값)가 conditionCount(requiredCount) 에 도달했으면 Completed(③, 완료&미수령).
            if (null != challenge)
            {
                if (challenge.isComplete)
                    return TrophyChallengeState.Rewarded;

                if (requiredCount > 0 && challenge.progress >= requiredCount)
                    return TrophyChallengeState.Completed;
            }

            // 미션(챌린지) 단위 기간 — 연계 이벤트가 시즌 중간에 열리거나(startDate) 시즌보다 먼저
            // 끝나는(endDate) 케이스를 표현(명세서 §7.3 ⑤⑥). 데이터 소스는 패킷 `TrophyMissionGroupPacketData`
            // — 운영툴에서 내려준 마스터 응답이 시즌 단위 권위값이므로, CSV 테이블 대신 패킷 기준으로 판정.
            // 이미 완료/수령한 챌린지는 위에서 상태가 확정되므로, 본 판정은 미완료 챌린지에만 적용된다.
            if (IsMissionExpired(groupRow, currentEpochSeconds))
                return TrophyChallengeState.Expired;

            if (IsMissionBeforeStart(groupRow, currentEpochSeconds))
                return TrophyChallengeState.Locked;

            return TrophyChallengeState.InProgress;
        }

        public static bool IsExpired(TrophyMasterPacketData master, TrophyInfoPacketData info, long currentEpochSeconds)
        {
            if (null != master && master.end > 0 && master.end <= currentEpochSeconds)
                return true;

            if (null != info && info.endAt > 0 && info.endAt <= currentEpochSeconds)
                return true;

            return false;
        }

        // 시즌 시작 전 판정 — 마스터 운영 기간 또는 유저 기준 기간(startAt) 중 하나라도 미도래면 true.
        public static bool IsBeforeStart(TrophyMasterPacketData master, TrophyInfoPacketData info, long currentEpochSeconds)
        {
            if (null != master && master.begin > 0 && master.begin > currentEpochSeconds)
                return true;

            if (null != info && info.startAt > 0 && info.startAt > currentEpochSeconds)
                return true;

            return false;
        }

        // 미션(챌린지) 단위 종료 — TrophyMissionGroupPacketData.endDate 경과 시 true (명세서 §7.3 ⑥).
        public static bool IsMissionExpired(TrophyMissionGroupPacketData groupRow, long currentEpochSeconds)
        {
            return null != groupRow && groupRow.endDate > 0 && groupRow.endDate <= currentEpochSeconds;
        }

        // 미션(챌린지) 단위 시작 전 — TrophyMissionGroupPacketData.startDate 미도래 시 true (명세서 §7.3 ⑤).
        public static bool IsMissionBeforeStart(TrophyMissionGroupPacketData groupRow, long currentEpochSeconds)
        {
            return null != groupRow && groupRow.startDate > 0 && groupRow.startDate > currentEpochSeconds;
        }
    }
}

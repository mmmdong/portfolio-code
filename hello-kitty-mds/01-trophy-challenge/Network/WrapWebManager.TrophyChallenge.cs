using System.Text;

using GameCore.Pattern;
using GameCore.Utils;
using GameLogic.Define;
using UnityEngine.Events;

namespace GameLogic.Network
{
    // 트로피 챌린지 패킷 래퍼 — 명세서.md §3 (RQTrophyChallengeMaster / Info / Update / Complete / FinalRewardClaim)
    // 응답 본문은 공통 헤더(code) 외 추가 페이로드가 없는 패킷도 있으므로,
    // 데이터 보존이 필요한 응답(Master / Info)만 DataManager 캐시로 흘려 보낸다.
    public partial class WrapWebManager : MonoSingleton<WrapWebManager>
    {
        #region Request

        public void RequestTrophyChallengeMaster(UnityAction<RSCallBackData> responseCallback)
        {
            try
            {
                var packet = new RQTrophyChallengeMaster();
                SendPacket(null, packet, responseCallback);
            }
            catch (System.Exception e)
            {
                RequestException(e.Message);
            }
        }

        public void RequestTrophyChallengeInfo(UnityAction<RSCallBackData> responseCallback)
        {
            try
            {
                var packet = new RQTrophyChallengeInfo();
                SendPacket(null, packet, responseCallback);
            }
            catch (System.Exception e)
            {
                RequestException(e.Message);
            }
        }

        public void RequestTrophyChallengeUpdate(long trophyId, int challengeId, int progressCount, UnityAction<RSCallBackData> responseCallback)
        {
            try
            {
                var packet = new RQTrophyChallengeUpdate
                {
                    trophyId = trophyId,
                    challengeId = challengeId,
                    progressCount = progressCount,
                };
                SendPacket(null, packet, responseCallback);
            }
            catch (System.Exception e)
            {
                RequestException(e.Message);
            }
        }

        public void RequestTrophyChallengeComplete(long trophyId, int challengeId, UnityAction<RSCallBackData> responseCallback)
        {
            try
            {
                var packet = new RQTrophyChallengeComplete
                {
                    trophyId = trophyId,
                    challengeId = challengeId,
                };
                SendPacket(null, packet, responseCallback);
            }
            catch (System.Exception e)
            {
                RequestException(e.Message);
            }
        }

        public void RequestTrophyChallengeFinalRewardClaim(long trophyId, UnityAction<RSCallBackData> responseCallback)
        {
            try
            {
                var packet = new RQTrophyChallengeFinalRewardClaim
                {
                    trophyId = trophyId,
                };
                SendPacket(null, packet, responseCallback);
            }
            catch (System.Exception e)
            {
                RequestException(e.Message);
            }
        }

        #endregion

        #region Response

        private void OnResponseTrophyChallengeMaster(RSCallBackData callbackData)
        {
            if (callbackData.code != 0)
            {
                DLogger.Error($"OnResponseTrophyChallengeMaster: 실패 [{(ResponseError)callbackData.code}]");
                return;
            }

            var responseObject = callbackData.GetBody<RSTrophyChallengeMaster>();
            if (responseObject == null)
            {
                DLogger.Error("OnResponseTrophyChallengeMaster response packet is null!");
                return;
            }

            // 공통 패킷 로그(WebManager.OnResponsePacket) 가 직렬화 결과를 한 줄로 출력하므로,
            // 트로피 마스터/미션 그룹은 항목별 줄바꿈으로 보기 쉽게 재포맷한 진단 로그를 별도 출력.
            LogTrophyChallengeMasterDiagnostics(responseObject);

            DataManager.Instance.SetTrophyChallengeMaster(responseObject);
        }

        private void OnResponseTrophyChallengeInfo(RSCallBackData callbackData)
        {
            if (callbackData.code != 0)
            {
                DLogger.Error($"OnResponseTrophyChallengeInfo: 실패 [{(ResponseError)callbackData.code}]");
                return;
            }

            var responseObject = callbackData.GetBody<RSTrophyChallengeInfo>();
            if (responseObject == null)
            {
                DLogger.Error("OnResponseTrophyChallengeInfo response packet is null!");
                return;
            }

            // TODO[TrophyChallenge-Diag]: 보상 수령 후 재접속 시 미수령 UI 노출 증상 추적용 임시 로그.
            LogTrophyChallengeInfoDiagnostics(responseObject);

            DataManager.Instance.SetTrophyChallengeInfo(responseObject);
        }

        // 진단 로그 — 트로피 마스터/미션 그룹 구성을 줄바꿈 포맷으로 출력.
        // 공통 패킷 로그(WebManager.OnResponsePacket: `RSPacket<T> : {SerializeObject()}`) 가 한 줄
        // 직렬화 결과를 찍어 가독성이 떨어지므로, 마스터별/미션 그룹별 라인을 분리해 다시 찍는다.
        private static void LogTrophyChallengeMasterDiagnostics(RSTrophyChallengeMaster response)
        {
            var masterList = response.trophyMasterList;
            var masterCount = masterList != null ? masterList.Length : 0;

            var sb = new StringBuilder();
            sb.Append($"[TrophyChallenge][Diag] RSTrophyChallengeMaster 수신 — trophyMasterList={masterCount}");

            for (var i = 0; i < masterCount; ++i)
            {
                var master = masterList[i];
                if (master == null) continue;

                sb.Append($"\n  Master[{i}] trophyId={master.trophyId}, challengeGroupIdx={master.challengeGroupIdx}")
                  .Append($", missionGroupIdx={master.missionGroupIdx}, openLevel={master.openLevel}")
                  .Append($", begin={master.begin}, end={master.end}");

                if (master.group != null)
                {
                    var group = master.group;
                    var completeRewards = group.completeReward != null ? string.Join(",", group.completeReward) : "";
                    sb.Append($"\n    Group: resourceIdx={group.resourceIdx}, minVersion={group.minVersion}, completeReward=[{completeRewards}]");
                }

                var missionGroup = master.missionGroup;
                if (missionGroup == null) continue;
                var missionCount = missionGroup.Length;
                for (var j = 0; j < missionCount; ++j)
                {
                    var mg = missionGroup[j];
                    if (mg == null) continue;
                    var rewards = mg.rewards != null ? string.Join(",", mg.rewards) : "";
                    sb.Append($"\n    MissionGroup[{j}] groupIdx={mg.groupIdx}, groupSeq={mg.groupSeq}")
                      .Append($", conditionIdx={mg.conditionIdx}, conditionValue={mg.conditionValue}, conditionCount={mg.conditionCount}")
                      .Append($", startDate={mg.startDate}, endDate={mg.endDate}, rewards=[{rewards}]");
                }
            }

            DLogger.Log(sb.ToString());
        }

        // TODO[TrophyChallenge-Diag]: 임시 진단 로그 — "보상받기 완료 후 재접속 시 미수령 UI 노출" 증상 추적용.
        // Info RS 의 challenge.isCompleted("보상 수령 완료") 가 서버에서 실제로 내려오는지 확인한다.
        // SetTrophyChallengeInfo 는 packet 객체를 변형 없이 캐시에 그대로 대입하므로,
        // 여기서 찍힌 isCompleted 값이 곧 캐시/상태판정(TrophyChallengeStateEvaluator)에 쓰이는 값이다.
        //   isCompleted == true  → Rewarded(④ 수령 완료)
        //   isCompleted == false → progress 가 조건 충족이면 Completed(③ 완료 & 미수령) UI 노출
        // 원인 확정 후 본 메서드와 위 호출부를 함께 제거할 것.
        private static void LogTrophyChallengeInfoDiagnostics(RSTrophyChallengeInfo response)
        {
            var infoList = response.trophyInfoList;
            var challengeList = response.trophyChallengeList;
            var infoCount = infoList != null ? infoList.Length : 0;
            var challengeCount = challengeList != null ? challengeList.Length : 0;

            var sb = new StringBuilder();
            sb.Append($"[TrophyChallenge][Diag] RSTrophyChallengeInfo 수신 — trophyInfoList={infoCount}, trophyChallengeList={challengeCount}");

            for (var i = 0; i < infoCount; ++i)
            {
                var info = infoList[i];
                if (info == null) continue;
                sb.Append($"\n  Info[{i}] trophyId={info.trophyId}, clearCount={info.clearCount}, finalRewardClaim={info.finalRewardClaim}");
            }

            for (var i = 0; i < challengeCount; ++i)
            {
                var challenge = challengeList[i];
                if (challenge == null) continue;
                sb.Append($"\n  Challenge[{i}] trophyId={challenge.trophyId}, challengeId={challenge.challengeId}")
                  .Append($", progress={challenge.progress}, isCompleted(보상수령완료)={challenge.isComplete}");
            }

            DLogger.Log(sb.ToString());
        }

        // Update/Complete/FinalRewardClaim 은 공통 OnResponse* 핸들러를 두지 않고 nullable 로 등록한다.
        // 후속 처리(Update 응답 amount 동기화 포함)는 호출 측 callback 으로 트리거한다.

        #endregion
    }
}

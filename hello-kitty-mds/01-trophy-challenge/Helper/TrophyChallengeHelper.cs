using ACTGames.Table;
using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Management;
using GameLogic.Network;
using GameLogic.TrophyChallenge;
using UnityEngine;

namespace ACTGames.Content.Helper
{
    // 명세서 §7.8 / Confluence 880017421 §2-6 (2026-05-22) — 트로피 챌린지 최초 튜토리얼 헬퍼.
    //   기획서 §6-2 4단계 시퀀스(LIdx 31417 → 31418 → 31419 → 31420):
    //     ① 챌린지 아이콘 포커싱  (actionCondition2 = 28, endCondition = 54)
    //     ② 챌린지 상세 정보 영역 (actionCondition2 = 29)
    //     ③ 최종 보상 영역        (actionCondition2 = 30)
    //     ④ 인포 버튼             (actionCondition2 = 31)
    //
    // CommonTutorialManager.GetActionUI(actionCondition2) / GetButton(endCondition) 에서 본 헬퍼의
    // 정적 메서드들을 통해 RectTransform 을 얻어 마스크/포커싱 대상으로 사용한다.
    // 다른 컨텐츠 튜토리얼 헬퍼(MergeEventHelper / LuckyMatchingHelper / MergeEventBossRaidHelper)
    // 와 동일 패턴.
    public static class TrophyChallengeHelper
    {
        // 명세서 §6.2 (1) — 트로피 챌린지 팝업이 노출됐을 때 (트리거 타입 8 = OpenPopup, 트리거 컨디션 28).
        // _Main 패널 진입 시점에서 PanelTrophyChallenge_Main.Start() 가 호출한다.
        public static void OnStartTutorial()
        {
            DLogger.Log("[TrophyChallenge-Tutorial] OnStartTutorial — 트리거 호출(TutorialTrigger.OpenPopup, TrophyChallenge=28)");
            CommonTutorialManager
                .Instance.OnTutorialTrigger(
                    new TutorialTriggerData(
                        TutorialTrigger.OpenPopup,
                        TutorTrigger_OpenPopup.TrophyChallenge, 0));
        }

        // 첫번째 챌린지 아이콘 포커싱 (① / endCondition 54 종료 터치 대상).
        public static RectTransform GetFirstChallengeSlot()
        {
            var popup = GetPopup();
            if (popup == null || popup.MainPanel == null) return null;
            var rect = popup.MainPanel.GetFirstChallengeSlotRect();
            LogActivatedRect("FirstChallenge(28/54)", rect);
            return rect;
        }

        // 챌린지 상세 정보 영역 (②).
        public static RectTransform GetMissionDetailArea()
        {
            var popup = GetPopup();
            if (popup == null || popup.MainPanel == null) return null;
            var rect = popup.MainPanel.GetMissionDetailRect();
            LogActivatedRect("MissionDetail(29)", rect);
            return rect;
        }

        // 최종 보상 정보 영역 (③).
        public static RectTransform GetFinalRewardArea()
        {
            var popup = GetPopup();
            if (popup == null || popup.MainPanel == null) return null;
            var rect = popup.MainPanel.GetFinalRewardRect();
            LogActivatedRect("FinalReward(30)", rect);
            return rect;
        }

        // 인포 버튼 (④).
        public static RectTransform GetInfoButton()
        {
            var popup = GetPopup();
            if (popup == null || popup.MainPanel == null) return null;
            var rect = popup.MainPanel.GetInfoButtonRect();
            LogActivatedRect("InfoButton(31)", rect);
            return rect;
        }

        // 마스크가 활성화될 RectTransform 의 GameObject name + 크기/위치 진단 로그.
        //   본 헬퍼의 GetXxx() 가 CommonTutorialManager 에 RectTransform 을 반환하는 시점 = 마스크가
        //   그 RectTransform 으로 그려지기 직전. 어떤 GameObject 가 포커싱되는지 추적 가능.
        private static void LogActivatedRect(string stage, RectTransform rect)
        {
            if (rect == null)
            {
                DLogger.Error($"[TrophyChallenge-Tutorial] {stage} — RectTransform = null (가이드/폴백 모두 미바인딩)");
                return;
            }
            DLogger.Log($"[TrophyChallenge-Tutorial] {stage} — GameObject='{rect.gameObject.name}' " +
                        $"sizeDelta={rect.sizeDelta} anchoredPos={rect.anchoredPosition} worldPos={rect.position}");
        }

        // 현재 활성화된 트로피 챌린지 메인 팝업 인스턴스. 미오픈이면 null.
        private static UIPopupTrophyChallenge GetPopup()
        {
            var popup = UIManager.Instance.FindUIWindow<UIPopupTrophyChallenge>();
            if (popup == null)
            {
                DLogger.Error("TrophyChallengeHelper::Need Open UIPopupTrophyChallenge");
            }
            return popup;
        }

        public static void OnShowCompleteToast(long trophyId, int challengeId, int count)
        {
            // ISSUE-04 — challengeId(=groupSeq) 를 미션 테이블 index 로 직접 조회하면 다른 행이 잡혀
            // (예: 머지 도감 챌린지인데 "뱃지 N개 수집" 문구가 노출됨), 팝업과 동일한 VM 매핑으로 해소한다.
            var viewModel = TrophyChallengeManager.Instance.GetViewModel(trophyId, challengeId);
            if (viewModel == null) return;

            var groupRow = viewModel.MissionGroupRow;
            var targetMissionTable = viewModel.MissionTableRow;
            if (groupRow == null || targetMissionTable == null) return;

            CommonScrollToastItemInfo info = new()
            {
                iconPath = targetMissionTable.missionIcon,
                conditionCount = groupRow.conditionCount,
                descText = targetMissionTable.subLIdx.L(groupRow.conditionCount, groupRow.conditionValue),
                count = count,
            };
            EffectHelper.StartAni_CommonMissionTost(info);
        }

        public static bool IsCompleteChallenge(long trophyId, int missionIdx, int count)
        {
            //var targetMissionTable = TableHelper.GetTable<TrophyChallengeMissionTableData>(missionIdx);
            //var conditionTable = TableHelper.GetTable<MissionConditionTableData>(targetMissionTable.conditionIdx);
            var packetData = TrophyChallengeManager.Instance.GetMisssionGroupPacket(trophyId, missionIdx);
            if(null == packetData) return false;

            return packetData.conditionCount <= count;
        }
    }
}

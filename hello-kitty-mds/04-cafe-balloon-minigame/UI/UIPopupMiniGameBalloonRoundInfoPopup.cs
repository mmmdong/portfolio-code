using System;
using System.Collections.Generic;

using GameCore.Utility;
using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;
using StatefulUI.Runtime.Core;
using StatefulUISupport.Scripts.Components;

using UnityEngine;

// 캐릭터 카페 2차 — 우사하나 풍선 게임 "다음 라운드 팝업"(key-modal, 상세 892698729 §3-4)
// 열쇠 발견 시 노출. Reward1~4 = 아직 수령하지 못한(남은) 보상 표시.
// 버튼: "바로 다음 라운드"(Btn_Next)/"바로 완료"(Btn_Finish) = 진행, "보상 찾고 가기"(Btn_Confirm) = 현재 라운드 유지(닫기).
public class UIPopupMiniGameBalloonRoundInfoPopup : UIBasePopup
{
    // [로컬라이징 LIdx] 기획 892698729 §3-2 4)(다음 라운드 팝업) / 5)(게임 클리어 팝업)
    //   풍선게임 결과 창은 본 팝업으로 일원화 — 마지막 라운드+모든 보물 수집(isClear) 시 §3-2 5) 클리어 UI로 전환한다(Clear 프리팹 미사용).
    private const int LIDX_TITLE = 43168;        // §3-2 4) 제목 "열쇠를 찾았어요!"
    private const int LIDX_TITLE_CLEAR = 43173;  // §3-2 5) 제목 "보물 찾기 성공!"
    private const int LIDX_DESC = 43169;         // §3-2 4) 내용(가변) "아직 숨겨진 보물이 {0}개 남았어요.\n마저 찾아볼까요?"
    private const int LIDX_DESC_CLEAR = 43174;   // §3-2 5) 내용 "축하해요!\n숨겨진 보물을 찾았어요!"
    private const int LIDX_BTN_NEXT = 43171;     // §3-2 4) "바로 다음 라운드" (다음 라운드 있을 때)
    private const int LIDX_BTN_FINISH = 43172;   // §3-2 4) "바로 완료" (다음 라운드 없을 때)
    private const int LIDX_BTN_LIKE = 40513;     // §3-2 5) "좋아요!"

    public class Info : IUIInfoData
    {
        public int remainingHidden;                 // 남은 히든 보상 수 ({0})
        public bool isLastRound;                    // 마지막 라운드 여부 (다음 라운드 vs 완료)
        public bool isClear;                        // [§3-2 5] 마지막 라운드 + 모든 보물 수집 → 게임 클리어 모드("보물 찾기 성공!")
        public Action onProceed;                    // "바로 다음 라운드/완료/좋아요" 선택 시 호출
        public List<RewardInfo> uncollectedRewards; // §3-2 4) 아직 수령하지 못한(남은) 보상 — Reward1~4 표시
        public List<RewardInfo> clearRewards;       // §3-2 5) 클리어 보상(마지막 라운드 보상) — isClear 시 표시
        public Action onClosed;                     // 팝업 닫힘(보상 찾고 가기/진행/닫기 등 모든 경로) 시 호출
    }

    [SerializeField] private UITextEx titleText;              // 제목 — §3-2 4) 43168 / §3-2 5) 43173 코드 전환
    [SerializeField] private StatefulComponent[] rewardSlots; // 남은/클리어 보상 슬롯(Reward1~4)
    [SerializeField] private UIButtonEx btnNext;    // "바로 다음 라운드" — 다음 라운드가 있을 때
    [SerializeField] private UIButtonEx btnFinish;  // "바로 완료" — 마지막 라운드
    [SerializeField] private UIButtonEx btnConfirm; // "보상 찾고 가기" — 현재 라운드 유지(닫기)
    [SerializeField] private StatefulComponent rewardsContainer; // 남은 보상 컨테이너 — 개수별 크기 상태(Reward1~4, RectTransformScale) 보유(연출문서 p33)

    private Info curInfo;
    private CommonRewardItem[] rewardSlotItems; // rewardSlots 와 평행한 보상 아이콘 캐시(첫 사용 시)

    protected override void OnEnable()
    {
        base.OnEnable();
        if (null != btnNext)
            btnNext.onClick.AddListener(OnClickProceed);
        if (null != btnFinish)
            btnFinish.onClick.AddListener(OnClickProceed);
        if (null != btnConfirm)
            btnConfirm.onClick.AddListener(OnClickStay);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.AddButtonListener(ButtonRole.Close, OnClickStay);
    }

    protected override void OnDisable()
    {
        if (null != btnNext)
            btnNext.onClick.RemoveListener(OnClickProceed);
        if (null != btnFinish)
            btnFinish.onClick.RemoveListener(OnClickProceed);
        if (null != btnConfirm)
            btnConfirm.onClick.RemoveListener(OnClickStay);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.RemoveButtonAllListener(ButtonRole.Close);
        curInfo?.onClosed?.Invoke(); // 닫힘 통지(모든 경로) — 메인 팝업이 풍선 조작 차단 해제 등에 사용
        base.OnDisable();
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);
        curInfo = data as Info;
        if (null == curInfo)
        {
            DLogger.Error($"[{GetType().Name}] invalid info");
            return;
        }
        RefreshTexts();
        RefreshButtons();
        RefreshRewards();
    }

    private void RefreshTexts()
    {
        bool clear = curInfo.isClear;

        // 제목 — §3-2 4) "열쇠를 찾았어요!"(43168) / §3-2 5) "보물 찾기 성공!"(43173). 제목 UITextEx 는 StringKey 타입이라 SetText(int) 로 전환.
        if (null != titleText)
            titleText.SetText(clear ? LIDX_TITLE_CLEAR : LIDX_TITLE);

        // 내용 — §3-2 5) "축하해요!…"(43174, 고정) / §3-2 4) "아직 숨겨진 보물이 {0}개…"(43169, 남은 수 가변).
        if (Stateful.HasText(TextRole.Text))
        {
            string desc = clear
                ? TableManager.GetText(LIDX_DESC_CLEAR)
                : string.Format(TableManager.GetText(LIDX_DESC), curInfo.remainingHidden);
            Stateful.SetText(TextRole.Text, desc);
        }
    }

    // 진행 버튼 (§3-2 4·5):
    //  · 비-마지막: "보상 찾고 가기"(btnConfirm, 43170) + "바로 다음 라운드"(btnNext, 43171) — 2버튼
    //  · 마지막+미수집(§3-2 4): "보상 찾고 가기"(btnConfirm, 43170) + "바로 완료"(btnNext, 43172) — 2버튼
    //  · 마지막+모두수집(§3-2 5, isClear): "좋아요!"(btnFinish, 40513) — 단일
    //  진행 버튼은 btnNext 로 일원화하고 라벨만 전환한다(btnConfirm 과 페어 정렬 유지). btnFinish 는 클리어 단일 버튼 전용.
    private void RefreshButtons()
    {
        bool last = curInfo.isLastRound;
        bool clear = curInfo.isClear;

        if (null != btnConfirm)
            btnConfirm.gameObject.SetActive(!clear);             // "보상 찾고 가기"(43170) — 클리어(모두수집)일 때만 숨김
        if (null != btnNext)
        {
            btnNext.gameObject.SetActive(!clear);                // 진행 버튼 — 비-마지막/마지막-미수집 모두 사용
            if (!clear)
                SetButtonLabel(btnNext, last ? LIDX_BTN_FINISH : LIDX_BTN_NEXT); // 마지막-미수집 "바로 완료"(43172) / 비-마지막 "바로 다음 라운드"(43171)
        }
        if (null != btnFinish)
        {
            btnFinish.gameObject.SetActive(clear);               // 클리어(모두수집) 단일 버튼
            if (clear)
                SetButtonLabel(btnFinish, LIDX_BTN_LIKE);        // "좋아요!"(40513)
        }
    }

    // 버튼 라벨(중첩 Btn 프리팹의 UITextEx)을 모드에 맞춰 전환. StringKey 타입만 반영(SetText(int) graceful).
    private void SetButtonLabel(UIButtonEx button, int lidx)
    {
        foreach (UITextEx label in button.GetComponentsInChildren<UITextEx>(true))
            label.SetText(lidx);
    }

    // Reward1~4 — 아직 수령하지 못한(남은) 보상 표시(RewardBefore 상태). 남은 보상 수보다 많은 슬롯은 비활성.
    private void RefreshRewards()
    {
        if (rewardSlots.IsNullOrEmpty())
            return;

        // §3-2 5) 클리어 모드는 클리어 보상(획득=RewardGet), §3-2 4)는 남은(미획득=RewardBefore) 보상.
        bool clear = curInfo.isClear;
        List<RewardInfo> rewards = clear ? curInfo.clearRewards : curInfo.uncollectedRewards;
        StateRole slotState = clear ? StateRole.RewardGet : StateRole.RewardBefore;
        int rewardCount = (rewards != null) ? rewards.Count : 0;
        int slotCount = rewardSlots.Length;

        // [ISSUE-67] 개수별 상태를 슬롯 루프보다 "먼저" 적용한다.
        //  컨테이너의 Reward1~4 상태는 스케일뿐 아니라 슬롯 4개의 활성(GameObject)까지 제어하므로,
        //  루프 뒤에 적용하면 데이터상 꺼야 할 슬롯이 되살아나 SetInfo 안 된 기본 아이콘이 노출된다(0건일 때 Clamp→Reward1).
        //  팝업이 파괴되지 않고 재사용되는 점(mCloseDestory=0)에서도, 마지막 판단을 데이터가 쥐어야 직전 노출 상태가 남지 않는다.
        RefreshRewardCountScale(rewardCount);

        for (int i = 0; i < slotCount; i++)
        {
            StatefulComponent slot = rewardSlots[i];
            if (null == slot)
                continue;

            if (i >= rewardCount || null == rewards[i])
            {
                slot.gameObject.SetActive(false);
                continue;
            }
            slot.gameObject.SetActive(true);

            CommonRewardItem rewardItem = GetSlotRewardItem(i);
            if (null != rewardItem)
                rewardItem.SetInfo(rewards[i]);

            // 클리어=획득(RewardGet) / 일반=미획득(RewardBefore). 상태 미정의 시 RewardBefore 폴백(graceful).
            if (slot.HasState((int)slotState))
                slot.SetState((int)slotState);
            else if (slot.HasState((int)StateRole.RewardBefore))
                slot.SetState((int)StateRole.RewardBefore);
        }

        // [ISSUE-67] 개수별 상태 적용은 루프 앞으로 이동(위 주석 참조). 원본 호출 위치 보존:
        //  RefreshRewardCountScale(rewardCount);
    }

    // 남은 보상 개수별 크기 상태(연출문서 p33 / §4-13) — RewardsContainer Stateful 의 Reward1~4 가
    //  슬롯의 RectTransformScale(1·2개=1.2 / 3개=1.1 / 4개=1.0)을 보유하므로, 개수에 맞는 상태만 선택한다(별도 사이즈 코드 불요).
    //  ⚠️[ISSUE-67] 프리팹의 Reward1~4 상태는 스케일 외에 슬롯 4개의 활성(GameObject)도 함께 제어한다.
    //   따라서 호출 순서가 중요 — 반드시 슬롯별 SetActive/SetInfo 루프보다 먼저 호출할 것(RefreshRewards 참조).
    //  rewardsContainer 미바인딩/상태 미정의 시 graceful no-op.
    private void RefreshRewardCountScale(int rewardCount)
    {
        if (null == rewardsContainer)
            return;

        StateRole state = Mathf.Clamp(rewardCount, 1, 4) switch
        {
            1 => StateRole.Reward1,
            2 => StateRole.Reward2,
            3 => StateRole.Reward3,
            _ => StateRole.Reward4,
        };
        if (rewardsContainer.HasState((int)state))
            rewardsContainer.SetState((int)state);
    }

    // 슬롯의 보상 아이콘(CommonRewardItem) — 중첩 프리팹이라 첫 사용 시 자식에서 캐싱(메인 팝업 GetSlotRewardItem 과 동일 패턴)
    private CommonRewardItem GetSlotRewardItem(int index)
    {
        if (rewardSlots.IsNullOrEmpty() || index < 0 || index >= rewardSlots.Length)
            return null;

        rewardSlotItems ??= new CommonRewardItem[rewardSlots.Length];
        if (null == rewardSlotItems[index] && null != rewardSlots[index])
            rewardSlotItems[index] = rewardSlots[index].GetComponentInChildren<CommonRewardItem>(true);
        return rewardSlotItems[index];
    }

    // 바로 다음 라운드 / 바로 완료 — 팝업 닫고 진행 콜백 실행(진행/클리어 분기는 onProceed=메인 팝업 ProceedToNextRoundOrClear 가 처리)
    private void OnClickProceed()
    {
        Action proceed = curInfo?.onProceed;
        Close();
        proceed?.Invoke();
    }

    // 보상 찾고 가기 / 닫기 — 현재 라운드 유지(남은 풍선 계속 터뜨리기)
    private void OnClickStay()
    {
        Close();
    }
}

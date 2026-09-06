using System;

using ACTGames.Content.Helper;

using GameLogic.Extension;

using UnityEngine;

// 드림 벌룬 페스티벌 — 난이도 카드 버튼 (구현 명세서 §3-3 / ShoppingRoad UIShoppingRoadDifficultyButton 참고)
// 카드별 최종보상 미리보기(rewardItems)를 해당 난이도로 채우고, 선택 연출(Animator default/on/off)을 재생한다.
// 클릭 시 난이도(1쉬움/0보통/2어려움 — 패킷·테이블 공통 인코딩)를 팝업에 통지한다.
public class UIDreamBalloonDifficultyButton : MonoBehaviour
{
    private const string ANIM_DEFAULT = "default";
    private const string ANIM_ON = "on";
    private const string ANIM_OFF = "off";

    [SerializeField] private UIButtonEx button;
    [SerializeField] private Animator animator;
    [SerializeField] private CommonRewardItem[] rewardItems;   // 카드 최종보상 미리보기 슬롯(§3-3)
    [SerializeField] private int difficulty;                   // 1: 쉬움 / 0: 보통 / 2: 어려움 (테이블·패킷 공통)

    private Action<int> onSelectDifficulty;

    protected void Start()
    {
        if (null != button)
        {
            button.onClick.RemoveListener(OnClick);
            button.onClick.AddListener(OnClick);
        }
    }

    protected void OnDestroy()
    {
        if (null != button)
        {
            button.onClick.RemoveListener(OnClick);
        }
    }

    // 카드 최종보상 미리보기 채우기 + 기본(미선택) 상태로 초기화. (팝업 SetInfo 시 호출)
    public void SetInfo()
    {
        EventDreamBalloonHelper.BindFinalRewards(rewardItems, difficulty);
        SetDefault();
    }

    public void SetButtonAction(Action<int> callback)
    {
        onSelectDifficulty = callback;
    }

    public bool IsSameDifficulty(int target)
    {
        return difficulty == target;
    }

    // 선택 연출 — 미선택(default)/선택(on)/해제(off). Animator 미바인딩·상태 미존재 시 no-op.
    private void SetDefault()
    {
        if (null != animator)
        {
            animator.ResetTrigger(ANIM_ON);
            animator.ResetTrigger(ANIM_OFF);
            animator.Play(ANIM_DEFAULT);
        }
    }

    // 기본 선택 상태로 즉시 세팅(오픈 시 default 난이도용 §4-1, ShoppingRoad SetOn 대응).
    // 사용자 클릭 전이 연출(ChangeOn) 과 달리 트리거가 아니라 on 상태를 바로 재생한다.
    public void SetOn()
    {
        if (null != animator)
        {
            animator.ResetTrigger(ANIM_ON);
            animator.ResetTrigger(ANIM_OFF);
            animator.Play(ANIM_ON);
        }
    }

    public void ChangeOn()
    {
        if (null != animator)
        {
            animator.ResetTrigger(ANIM_OFF);   // 대기 중인 off 제거 → on 직후 되뒤집힘 방지
            animator.SetTrigger(ANIM_ON);
        }
    }

    public void ChangeOff()
    {
        if (null != animator)
        {
            animator.ResetTrigger(ANIM_ON);
            animator.SetTrigger(ANIM_OFF);
        }
    }

    private void OnClick()
    {
        onSelectDifficulty?.Invoke(difficulty);
    }
}

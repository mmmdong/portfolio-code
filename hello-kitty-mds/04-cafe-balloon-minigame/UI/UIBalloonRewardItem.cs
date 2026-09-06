using GameLogic.Define;

using StatefulUI.Runtime.Core;

using UnityEngine;

// 풍선 결과 비행 아이템(§4-15) — 풀링되는 독립 결과 연출 인스턴스.
//  결과별 상태(StatefulComponent)를 SetState 로 전환해 결과 1개만 표시한다. 각 인스턴스가 결과 1개만 들고 있어 공유 노드 오염이 없다.
//  ※ '아이템 발견' 상태는 StateRole.TreasureReward 가 enum 에 없어, 프리팹과 동일한 StateRole.TreasureHiddenReward 를 사용한다.
public class UIBalloonRewardItem : MonoBehaviour
{
    [SerializeField] private StatefulComponent stateful;

    [SerializeField] private CommonRewardItem hiddenReward;
    [SerializeField] private CommonRewardItem specialReward;
    [SerializeField] private Animator animator; // 결과 연출 애니메이터 — 비행 시간을 현재 재생 클립의 잔여 시간으로 산출(GetCurrentAnimationRemainingTime)

    private bool isReady = false;
    public bool IsReady => isReady;

    // 결과별 상태 1개만 표시 + 보상 아이콘 교체.
    //  content == -1 열쇠 발견(TreasureKey) / content > 0 아이템 발견(TreasureHiddenReward, important=황금 퍼즐은 SpecialReward) / 그 외 미표시.
    public void ShowResult(int content, bool important, RewardInfo rewardInfo)
    {
        if (null == stateful)
            return;

        SetStateSafe(StateRole.Init); // 초기화(모든 결과 노드 off) 후 이번 결과만 활성화
        isReady = !important;

        if (content == -1)
        {
            SetStateSafe(StateRole.TreasureKey);
        }
        else if (content > 0)
        {
            SetStateSafe(important ? StateRole.SpecialReward : StateRole.TreasureHiddenReward);

            // 활성 결과 노드의 보상 아이콘을 실제 보상으로 교체(§4-15 "Icon 이미지 교체") — 중요=specialReward / 일반=hiddenReward.
            CommonRewardItem icon = important ? specialReward : hiddenReward;

            if (null != rewardInfo && null != icon)
                icon.SetInfo(rewardInfo);
        }
    }

    // 초기화 — 모든 결과 노드 off(풀 반환 전).
    public void Hide()
    {
        SetStateSafe(StateRole.Init);
    }

    private void SetStateSafe(StateRole role)
    {
        if (null != stateful && stateful.HasState((int)role))
            stateful.SetState((int)role);
    }

    public void OnStartFly()
    {
        isReady = true;
    }

    // 애니메이터에서 현재 재생 중인 클립의 현재 프레임 → 마지막 프레임까지 남은 시간(초). 전환 중이면 전환 대상 상태 기준.
    //  미바인딩이거나 모션 없는 상태(기본 상태 등)면 0 → 호출부에서 종류별 기본 비행 시간으로 폴백한다.
    public float GetCurrentAnimationRemainingTime()
    {
        if (null == animator)
            return 0f;

        AnimatorStateInfo state = animator.IsInTransition(0)
            ? animator.GetNextAnimatorStateInfo(0)
            : animator.GetCurrentAnimatorStateInfo(0);

        float length = state.length;
        if (length <= 0f)
            return 0f;

        float progress = Mathf.Repeat(state.normalizedTime, 1f); // 현재 재생 위치(0~1)
        return length * (1f - progress);
    }
}

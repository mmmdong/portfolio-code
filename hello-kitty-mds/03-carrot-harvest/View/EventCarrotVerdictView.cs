using System;

using Cysharp.Threading.Tasks;     // UniTask

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 슈퍼 레어 당근의 머리 위 박스/HP 바 연출(§7-2). 당근 슬롯 자식 `EventCarrotVerdictBox` 에 부착한다.
///
/// 흐름: 슈퍼 레어 등장 시 <see cref="Show"/>(박스+HP 100%) → 매 탭 <see cref="SetHp"/>(비율 감소) →
///       처치 시 <see cref="PlayKill"/>(박스/HP 제거 + 폭발 이펙트) → 복귀/정리 시 <see cref="Hide"/>.
/// 비슈퍼 당근이 같은 슬롯에 등장하면 <see cref="Hide"/> 로 숨긴다.
/// </summary>
[DisallowMultipleComponent]
public sealed class EventCarrotVerdictView : MonoBehaviour
{
    // Carrot_Reward_Box 애니메이터 — 상태 idle/hit/fadeout, 트리거 Idle/Hit/FadeOut. fadeout(사라짐)은 FadeOut 트리거로만 진입.
    private static readonly int IDLE_STATE = Animator.StringToHash("idle");
    private static readonly int HIT_TRIGGER = Animator.StringToHash("Hit");
    private static readonly int FADEOUT_TRIGGER = Animator.StringToHash("FadeOut");

    [SerializeField] private Slider hpSlider;       // CarrotHPSlider
    [SerializeField] private GameObject rewardBox;  // EventCarrot_Reward_Box
    [SerializeField] private GameObject boomFx;     // Fx_Carrot_Box_Boom
    [SerializeField] private Animator boxAnimator;  // Carrot_Reward_Box 애니메이터(EventCarrot.prefab 에서 verdict box 루트에 추가) — Awake GetComponent 폴백
    [SerializeField] private float boomFxDuration = 1f; // 폭발 FX 자동 숨김까지 시간(초) — boom 파티클 길이에 맞춰 조정

    private void Awake()
    {
        if (hpSlider == null)
            hpSlider = GetComponentInChildren<Slider>(true);

        // 애니메이터는 EventCarrot.prefab 에서 이 GameObject(verdict box 루트)에 추가되므로 verdict 프리팹 단독으로는 바인딩 불가 → 런타임 GetComponent 로 해석.
        if (boxAnimator == null)
            boxAnimator = GetComponent<Animator>();
    }

    /// <summary>슈퍼 레어 등장 — 박스/HP 바 표시(가득).</summary>
    public void Show()
    {
        gameObject.SetActive(true);

        if (boomFx != null)
            boomFx.SetActive(false);

        if (rewardBox != null)
            rewardBox.SetActive(true);

        if (hpSlider != null)
        {
            hpSlider.gameObject.SetActive(true);
            hpSlider.normalizedValue = 1f;
        }

        // 직전 처치(fadeout)에서 같은 슬롯이 재사용될 수 있으므로 idle 로 강제 리셋(fadeout 상태에는 빠져나가는 전이가 없음).
        if (boxAnimator != null)
            boxAnimator.Play(IDLE_STATE, 0, 0f);
    }

    /// <summary>HP 비율(0~1) 반영 — 매 탭 감소(피격 연출 포함).</summary>
    public void SetHp(float ratio)
    {
        if (hpSlider != null)
            hpSlider.normalizedValue = Mathf.Clamp01(ratio);

        // 매 탭 피격 연출(Carrot_Reward_Box_Hit). 등장(Show)은 SetHp 를 거치지 않으므로 실제 피격에서만 발동.
        if (boxAnimator != null)
            boxAnimator.SetTrigger(HIT_TRIGGER);
    }

    /// <summary>처치(체력 소진) — 박스 사라짐(FadeOut) 연출 + 박스 폭발 이펙트(§7-2 처치/드롭).</summary>
    public void PlayKill()
    {
        if (hpSlider != null)
            hpSlider.gameObject.SetActive(false);

        // 체력이 다 닳았을 때만 박스 사라짐(FadeOut) 트리거. 애니메이터 미존재(verdict 단독 프리팹) 시 즉시 비활성 폴백.
        if (boxAnimator != null)
            boxAnimator.SetTrigger(FADEOUT_TRIGGER);
        else if (rewardBox != null)
            rewardBox.SetActive(false);

        if (boomFx != null)
        {
            boomFx.SetActive(true);
            HideBoomFxAfterAsync().Forget();    // 파티클 길이 후 자동 숨김
        }
    }

    // 폭발 FX 를 boomFxDuration 후 자동으로 끈다(슬롯 파괴 시 토큰으로 취소).
    private async UniTaskVoid HideBoomFxAfterAsync()
    {
        await UniTask.Delay(TimeSpan.FromSeconds(boomFxDuration), cancellationToken: gameObject.GetCancellationTokenOnDestroy());

        if (boomFx != null)
            boomFx.SetActive(false);
    }

    /// <summary>즉시 숨김 — 미클릭 복귀 / 게임 시작 정리 / 비슈퍼 등장 시.</summary>
    public void Hide()
    {
        if (boomFx != null)
            boomFx.SetActive(false);

        gameObject.SetActive(false);
    }
}

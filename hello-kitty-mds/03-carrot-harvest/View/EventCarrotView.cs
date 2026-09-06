using System;

using GameLogic.Management.UISupport;   // SpineAnimationController

using Spine;   // TrackEntry

using UnityEngine;  // GameObject(흙/점수 FX 토글)

/// <summary>
/// 당근 1개의 SkeletonGraphic 표현 — 구멍에 배치되는 당근 프리팹에 부착한다.
/// 공용 <see cref="SpineAnimationController"/> 를 상속해 당근 전용 진입점(스킨/상태 전이)만 덧붙인다.
///
/// 스킬레톤 데이터: Spine_UI_Event_Carrot_Harves_SkeletonData
///   (Assets/Resources_moved/UI/Spine/Spine_UI_Event_Carrot_Harves/)
///   - 스킨   : Normal / Rare / SuperRare  (= EventCarrotType 이름)
///   - 애니   : 0_None / 1_Appear / 2_Idle / 3_Return / 4_Exit / 5_Hit  (= EventCarrotAnimation)
///
/// 역할 분리: 등장 "종류/위치" 추첨은 <see cref="EventCarrotSpawnSelector"/>(순수 로직),
///            화면 표현은 본 클래스가 담당한다. 보드 매니저가 추첨 결과(EventCarrotSpawnInfo)를
///            받아 이 뷰에 SetCarrotType → PlayAppear 순으로 구동한다.
/// </summary>
public sealed class EventCarrotView : SpineAnimationController
{
    // 프리팹의 SkeletonGraphic 에 지정할 SkeletonDataAsset 이름(인스펙터 바인딩 기준).
    public const string SKELETON_DATA_ASSET_NAME = "Spine_UI_Event_Carrot_Harves_SkeletonData";

    [SerializeField] private GameObject dirtFx;     // 흙 이펙트 Fx_Carrot_Touch — 복귀/뽑힘/피격 시(§6-4, PDF p.10·11·13)
    [SerializeField] private GameObject scoreFx;    // 점수 이펙트 Fx_Carrot_Score — 뽑힘(Exit) 시(§6-4, PDF p.11)

    // 현재 None(빈 구멍) 상태인지 — 경직 판정 기준(§6-2). 뷰의 애니 전이(PlayAppear/PlayNone 등)와 함께 갱신한다.
    // ※ 라이브 트랙(SkeletonGraphic.AnimationState.GetCurrent) 직접 읽기는 SkeletonGraphic 의 startingAnimation(=0_None)
    //    자동 리셋 때문에 보이는 당근도 None 으로 잘못 읽혀 경직 오판정이 발생한다. 우리가 명령한 상태를 신뢰원으로 둔다.
    private bool isNone = true;  /// <summary>뷰가 None(당근 없음) 상태인지. true 일 때만 클릭 시 경직 대상이며,
    /// Appear/Idle/Return/Exit/Hit 재생 중(false)에는 수확 판정으로 처리한다.
    /// None 전환은 애니 완료 콜백(PlayReturn/PlayExit 의 onComplete→PlayNone)으로 일어나므로 '애니 완료' 기준이다(테이블 시간 아님).</summary>
    public bool IsNone => isNone;

    /// <summary>당근 종류 지정 — 스킨(Normal/Rare/SuperRare) 적용 후 None 상태로 둔다.</summary>
    public void SetCarrotType(EventCarrotType type)
    {
        ApplySkin($"{type}");                   // 스킨명 = enum 이름과 1:1
        SetAnimation(AnimNameOf(EventCarrotAnimation.None), isLoop: true);
        isNone = true;
    }

    /// <summary>등장 → 등장 애니(1회) 후 대기(Idle) 루프로 자동 연결.</summary>
    public void PlayAppear()
    {
        SetAnimation(AnimNameOf(EventCarrotAnimation.Appear), isLoop: false);
        AddAnimation(AnimNameOf(EventCarrotAnimation.Idle), isLoop: true);
        isNone = false;
    }

    /// <summary>클릭되지 않아 다시 땅으로 복귀(1회). 복귀 애니 동안에도 수확 가능하며, 완료 콜백(보통 PlayNone)에서 None 으로 전환된다.</summary>
    public void PlayReturn(Action onComplete = null)
    {
        isNone = false;     // 복귀 애니 재생 중 — 아직 화면에 존재(경직 대상 아님)
        PlayOnce(EventCarrotAnimation.Return, onComplete);
        PlayFx(dirtFx);     // 흙 이펙트(Fx_Carrot_Touch)
    }

    /// <summary>클릭되어 뽑힘(1회). 뽑힘 애니 동안에는 경직 대상이 아니며, 완료 콜백(보통 PlayNone)에서 None 으로 전환된다.</summary>
    public void PlayExit(Action onComplete = null)
    {
        isNone = false;     // 뽑힘 애니 재생 중 — 아직 화면에 존재(경직 대상 아님)
        PlayOnce(EventCarrotAnimation.Exit, onComplete);
        PlayFx(dirtFx);     // 흙 이펙트(Fx_Carrot_Touch)
        PlayFx(scoreFx);    // 점수 이펙트(Fx_Carrot_Score)
    }

    /// <summary>빈 구멍(None) 상태로 — 복귀/뽑힘 완료 후 또는 게임 시작 시 호출(고정 슬롯은 영구 존재). 이때부터 클릭 시 경직.</summary>
    public void PlayNone()
    {
        SetAnimation(AnimNameOf(EventCarrotAnimation.None), isLoop: true);
        isNone = true;
    }

    /// <summary>탭 피격 반응(1회) 후 다시 대기(Idle) — 슈퍼 레어가 처치되지 않은 매 탭에 사용(§7-2).</summary>
    public void PlayHit()
    {
        SetAnimation(AnimNameOf(EventCarrotAnimation.Hit), isLoop: false);
        AddAnimation(AnimNameOf(EventCarrotAnimation.Idle), isLoop: true);
        isNone = false;
        PlayFx(dirtFx);     // 흙 이펙트(Fx_Carrot_Touch)
    }

    // 흙/점수 파티클 FX 재생 — 비활성→활성 재토글로 (playOnAwake) 파티클을 매번 처음부터 재생(§6-4).
    private void PlayFx(GameObject fx)
    {
        if (fx == null)
            return;

        fx.SetActive(false);
        fx.SetActive(true);
    }

    private void PlayOnce(EventCarrotAnimation anim, Action onComplete)
    {
        var entry = SetAnimation(AnimNameOf(anim), isLoop: false);
        if (entry == null)
        {
            onComplete?.Invoke();
            return;
        }

        if (onComplete != null)
        {
            // TrackEntry 는 매 재생마다 새로 생성되므로 핸들러가 누적되지 않는다(해제 불필요).
            entry.Complete += _ => onComplete();
        }
    }

    // 트랙명 규칙: "{(int)anim}_{anim}" → "0_None","1_Appear","2_Idle","3_Return","4_Exit","5_Hit".
    private string AnimNameOf(EventCarrotAnimation anim)
    {
        return $"{(int)anim}_{anim}";
    }
}

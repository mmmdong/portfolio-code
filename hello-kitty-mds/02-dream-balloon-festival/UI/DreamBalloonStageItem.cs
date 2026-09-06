using System;
using System.Collections.Generic;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using StatefulUI.Runtime.Core;
using StatefulUISupport.Scripts.Components;
using UnityEngine;

// 드림 벌룬 — 스테이지(구름) 셀 뷰 (구현 명세서 §7-2·§3-4).
// LoopScroll ProvideData → SendMessage("SetInfo", DreamBalloonStageData) 로 주입.
// 구름 위: 단계번호(별), 게이지({curCoin}/{goalCoin}), 성공 시 별 녹색+깃발(state==2), 최종 구름(isFinal), 단계 보상 이미지(§3-4 ⑨).
// 상태/텍스트/슬라이더는 StatefulUI Role 로 바인딩(미정의 role 은 HasXXX 가드로 graceful no-op).
public class DreamBalloonStageItem : UIStatefulBase<DreamBalloonStageItem>
{
    private const int MAX_STAGE_REWARD = 2; // 구름 위 단계 보상 최대 2개(§3-4 ⑨)

    [SerializeField] private GameObject rewardRoot; // StageCloudRweard — 단계 보상 컨테이너(보상 있을 때만 노출)
    [SerializeField] private Transform rewardContent; // StageCloudRweard/Content — 보상 아이템 배치(중앙정렬 레이아웃)
    [SerializeField] private GameObject rewardItemPrefab; // 보상 아이템 프리팹(CommonRewardItem) — rewardItems 미바인딩 시 런타임 생성 폴백
    [SerializeField] private CommonRewardItem[] rewardItems; // Content 아래 authored 보상 슬롯(최대 2개) — 바인딩돼 있으면 그대로 사용
    [SerializeField] private UnityEngine.UI.Image[] rewardCheckIcons; // 슬롯별 check 아이콘 — 라운드 클리어 시에만 표시(RefreshRewardChecks)
    [SerializeField] private Transform airBalloonTrans; // 구름 위 열기구 위치(좌) — 열기구 오버레이가 이 위치를 따라간다
    [SerializeField] private Transform partnerTrans; // 구름 위 파트너 위치(우) — 파트너를 이 아래에 SetParent
    [SerializeField] private UITextEx gaugeText;   // 구름 게이지 위 코인 진행 텍스트(AllClearText) — {획득}/{목표}
    [SerializeField] private RectTransform portraitPanel;   // 경쟁 친구 포트레이트 컨테이너(PortraitPanel) — 기본 비활성, 모집 연출 때만 노출
    [SerializeField] private List<Vector2> portraitVecList = new();   // 각 포트레이트의 착지 목표(PortraitPanel 로컬) — 자식 순서와 1:1

    // 경쟁자 모집 연출(§4-3) — 포트레이트가 열기구에서 착지 위치로 뛰어내리는 DOJump 파라미터(내 캐릭터 하차 DisembarkAsync 참조).
    private const float PORTRAIT_JUMP_POWER = 90f;      // 착지 점프 포물선 높이(패널 로컬 px)
    private const float PORTRAIT_JUMP_SEC = 0.45f;      // 점프 1회 시간
    private const float PORTRAIT_SPAWN_GAP_SEC = 0.1f;  // 친구 간 등장 간격(우르르)
    private const float PORTRAIT_LAND_HOLD_SEC = 0.4f;  // 마지막 포트레이트 착지 후 정착 홀드(연출 마무리 = START 로 이어짐)

    // 별 상태 전환 이펙트(§4-4) — 파티클(Fx_Star_Change_*)이 looping·playOnAwake 라 **코드가 반드시 꺼야** 한다.
    private const float FX_STAR_CHANGE_SEC = 1f;

    // 최종 구름(EventDreamBalloon_StageFinal) 전용 — 도착 연출(회전목마·관람차 회전 + 무지개, §4-4 최종).
    // 아트가 Animator(DreamBalloonFestival_Last_Stage)로 authoring 했다: Idle_Before →[Trigger_In]→ In →(exit)→ Idle_After.
    // 코드는 도착 시점에 트리거만 쏘고, 재생 시간은 실제 In 클립 길이를 조회해 쓴다(하드코딩 금지).
    private const string FINAL_IN_TRIGGER_NAME = "Trigger_In";
    private const string FINAL_IN_CLIP_NAME = "DreamBalloonFestival_Last_Stage_In";
    private const string FINAL_IDLE_AFTER_STATE_NAME = "Idle_After";   // In 종료 후 상태 — 축제장이 계속 돌아간다(스킵 스냅 대상)

    private static readonly int FINAL_IN_TRIGGER = Animator.StringToHash(FINAL_IN_TRIGGER_NAME);
    private static readonly int FINAL_IDLE_AFTER_STATE = Animator.StringToHash(FINAL_IDLE_AFTER_STATE_NAME);

    // 경쟁자 포트레이트 하차(§4-4 순서2 "프로필이 구름 밑으로 떨어짐") — 아트가 각 Portrait 자식에 Animator
    // (DreamBalloonFestival_PortraitPanel: Appear → Idle → [FallDown 트리거] → 낙하)를 authoring 했다.
    // 라운드 클리어 순간 이 트리거를 쏘면 포트레이트가 구름 밑으로 떨어진다(코드 DOJump 등장 + 아트 트리거 낙하의 조합).
    private static readonly int PORTRAIT_FALLDOWN_TRIGGER = Animator.StringToHash("FallDown");

    // [ISSUE-34 후속] 구름 재화 게이지 노출의 단일 소유자 = 구름 Animator.
    // 아트가 상태별 클립으로 게이지 표시를 전담하도록 authoring 했다(전 상태 Write Defaults ON):
    //   In         — BoneCloudSlider.m_IsActive = 0 (등장 중 게이지 없음)
    //   Idle       — CloudSlider.m_LocalScale = 0   (예정 구름: 게이지 숨김)
    //   Gauge_Idle — BoneCloudSlider 활성 + 스케일 복원 (진행 중: 게이지 노출)
    //   Clear_Idle — CloudSlider.m_LocalScale = 0   (성공 구름: 게이지 숨김)
    // ⚠️ 숨김이 m_IsActive 가 아니라 **LocalScale 0** 으로 구현돼 있어, 코드가 슬라이더 GO 를 SetActive(true) 해도
    //    Animator 가 매 갱신 스케일을 0 으로 되써서 보이지 않는다 → 노출 판단을 Animator 에 일임한다.
    // ⚠️ 컨트롤러의 Gauge/Clear/Idle 트리거는 전이 그래프가 미완성이다(Clear 전이는 대상 미지정, Idle 파라미터는 미사용,
    //    Clear_In 클립은 어느 상태에도 미할당) → 트리거 대신 Play(상태) 로 직접 구동한다.
    // 게이지 등장 연출(Gauge_In)은 **라운드 완료 후 다음 라운드로 진행했을 때만** 재생한다(PlayGaugeInAsync ← 모집 연출).
    // 진행중·라운드 클리어 상태로 메인 팝업에 들어오거나 재화가 갱신될 때는 등장 없이 정착 상태(Gauge_Idle)로 바로 들어간다.
    // ※ Gauge_In 은 나가는 전이가 없어(Gauge_Idle 은 Play 로만 도달) 등장 후 마지막 프레임에 멈춘다 → 재생 뒤 코드가 Idle 로 넘긴다.
    // 구름 등장 클립(기본 진입 상태 In) — 이벤트 최초 시작 1회만 재생(ISSUE-13).
    // 등장 중에는 게이지 상태 Play 를 미뤄 등장을 덮지 않는다(data.cloudIntro).
    private const string CLOUD_CLIP_IN_NAME = "Ani_Clip_EventBalloonCloud_In";

    private const string CLOUD_STATE_IDLE_NAME = "Idle";
    private const string CLOUD_STATE_GAUGE_NAME = "Gauge_Idle";
    private const string CLOUD_STATE_GAUGE_IN_NAME = "Gauge_In";
    private const string CLOUD_STATE_CLEAR_NAME = "Clear_Idle";

    // ⚠️ 프리팹 StageGauge State Description 에 `AnimParamSetTrigger: Gauge` 가 들어 있어, RefreshView 의
    //    SetState(StageGauge) 마다 이 트리거가 선다. Gauge 는 **AnyState → Gauge_In(HasExitTime 0)** 이라
    //    Play(Gauge_Idle) 직후에도 다음 Animator 갱신에서 등장 애니로 덮인다(진행중 상태로 팝업에 들어가도 Gauge_In 재생).
    //    → 상태를 Play 로 확정하기 전에 반드시 소비되지 않은 트리거를 지운다(SnapFinalStageEnd 와 동일 계약).
    private const string CLOUD_GAUGE_TRIGGER_NAME = "Gauge";
    private static readonly int CLOUD_GAUGE_TRIGGER = Animator.StringToHash(CLOUD_GAUGE_TRIGGER_NAME);

    private static readonly int CLOUD_STATE_IDLE = Animator.StringToHash(CLOUD_STATE_IDLE_NAME);
    private static readonly int CLOUD_STATE_GAUGE = Animator.StringToHash(CLOUD_STATE_GAUGE_NAME);
    private static readonly int CLOUD_STATE_GAUGE_IN = Animator.StringToHash(CLOUD_STATE_GAUGE_IN_NAME);
    private static readonly int CLOUD_STATE_CLEAR = Animator.StringToHash(CLOUD_STATE_CLEAR_NAME);

    private Animator cloudAnimator;
    private Animator[] portraitAnimators;   // 각 포트레이트 슬롯(Portrait_NN) 안쪽 Portrait 의 Animator — 1회 캐싱
    private DreamBalloonStageData data;
    private bool portraitsPlaying;   // 모집 연출(등장) 재생 중 — 이 구간엔 RefreshView 의 상태 기준 배치를 적용하지 않는다(DOJump 를 덮어쓰지 않도록)
    private bool portraitsExiting;   // 퇴장 연출(성공=FallDown 낙하 / 실패=열기구 탑승) 재생 중 — 상태 기준 숨김/재배치를 적용하지 않는다
    private bool gaugeStateApplied;   // 게이지 노출 상태(Gauge_Idle)를 코드가 강제 적용했는지 — 해제 시에만 되돌린다(ApplyCloudGaugeState)

    // 최종 구름 도착 연출(In 클립)의 **하차 시점 큐** — 아트가 클립에 심어 둔 Animation Event 가 발신한다(§4-4 최종 ③).
    // Animation Event 는 Animator 와 **같은 GameObject 의 컴포넌트 메서드만** 호출할 수 있는데, 이 스크립트가 최종 구름
    // 프리팹(EventDreamBalloon_StageFinal) 루트에 Animator 와 함께 붙어 있어 커튼(DreamBalloonCurtainEventRelay)과 달리
    // 별도 릴레이가 필요 없다. 하차 연출은 구독자(DreamBalloonRoadController)가 담당한다.
    public event Action FinalStageDisembarkCue;

    public int Round => data?.round ?? 0;
    public Transform AirBalloonTrans => airBalloonTrans; // 구름 위 열기구 기준 위치(§3-2 좌)
    public Transform PartnerTrans => partnerTrans; // 구름 위 파트너 기준 위치(§3-2 우)

    // 구름 애니(Ani_Contoller_EventBalloonCloud)는 기본 상태가 등장(In)이라, LoopScroll 풀링으로
    // 셀이 재활성(SetActive off→on)될 때마다 In 이 다시 재생돼 스와이프 시 구름이 깜빡인다.
    // keepAnimatorStateOnDisable = true 로 재활성 시 상태를 유지(첫 등장 후 Idle 유지)해 깜빡임을 제거한다.
    // (구름 애니는 코드로 구동하지 않는 장식이라 상태 유지가 안전하다.)
    private void Awake()
    {
        cloudAnimator = GetComponent<Animator>();
        if (null != cloudAnimator)
        {
            cloudAnimator.keepAnimatorStateOnDisable = true;
        }
    }

    /// <summary>
    /// 최종 구름 도착 연출 재생(§4-4 최종) — 회전목마·관람차가 돌고 무지개가 뜬다.
    /// 연출 자체는 아트 Animator 가 전담하므로 트리거만 쏜다. 반환값 = In 클립 길이(초).
    /// 일반 구름(해당 트리거 미보유)에서는 no-op → 0 반환.
    /// </summary>
    public float PlayFinalStageIn()
    {
        if (null == cloudAnimator || !HasFinalInTrigger())
        {
            return 0f;
        }

        cloudAnimator.SetTrigger(FINAL_IN_TRIGGER);

        // 별사탕 이펙트(Fx_StarCandy) — "도착애니 출력할 때 같이 켜주세요"(아트 952860690 §7 최종스테이지).
        // 원샷 파티클(비루프)이라 fire & forget — 끄는 처리 불필요. State 미발행 시 no-op.
        StatefulComponent stateful = Stateful;
        if (null != stateful && stateful.HasState((int)StateRole.FxLastStageIn))
        {
            stateful.SetState((int)StateRole.FxLastStageIn);
        }

        return ResolveFinalInClipLength();
    }

    /// <summary>
    /// 최종 구름 착지 이펙트(Fx_Landing) 점등 — 파트너가 열기구에서 **내리는 순간**에 호출한다(§4-4 최종 순서4).
    /// State 미발행(일반 구름 등)이면 no-op.
    /// </summary>
    /// <remarks>
    /// ⚠️ Fx_Landing 은 <c>looping = 1</c> 파티클이라 켜두면 계속 재생된다. 현재 프리팹에 Off 짝 State
    /// (FxLandingOff 류)가 없어 코드로 끌 수단이 없다 — 최종 연출 직후 <c>_Final</c> 팝업이 메인 팝업을 닫아
    /// (ISSUE-31) 오브젝트가 사라지는 흐름에 기대고 있다. 팝업 캐싱 등으로 살아남는 경로가 생기면 Off State 가 필요하다.
    /// (대비: FxLastStageIn 은 원샷이라 fire &amp; forget 이 안전하다.)
    /// </remarks>
    public void PlayFinalStageLandingFx()
    {
        StatefulComponent stateful = Stateful;
        if (null == stateful || !stateful.HasState((int)StateRole.FxLanding))
        {
            return;
        }

        stateful.SetState((int)StateRole.FxLanding);
    }

    // Animation Event 진입점 — DreamBalloonFestival_Last_Stage_In 클립에 아트가 심어 둔 이벤트의 Function 명과
    // **반드시 일치**해야 한다(불일치 시 조용히 호출되지 않는다). 수신 사실만 방송하고 후속 연출은 구독자가 담당한다.
    public void OnFinalStageDisembarkCue()
    {
        FinalStageDisembarkCue?.Invoke();
    }

    /// <summary>
    /// 최종 구름 도착 연출을 끝 상태로 즉시 스냅(스킵) — 축제장이 이미 돌아가고 있는 Idle_After 로 점프한다.
    /// 아직 트리거를 쏘지 않았어도(연출 초반 스킵) 동일한 끝 상태가 되도록 대기 중인 트리거를 지운다. 멱등.
    /// </summary>
    public void SnapFinalStageEnd()
    {
        if (null == cloudAnimator || !HasFinalInTrigger())
        {
            return;
        }

        cloudAnimator.ResetTrigger(FINAL_IN_TRIGGER);   // 소비되지 않은 트리거가 남아 Idle_After 직후 In 이 다시 도는 것을 막는다
        cloudAnimator.Play(FINAL_IDLE_AFTER_STATE, 0, 0f);
        cloudAnimator.Update(0f);
    }

    /// <summary>
    /// 구름 등장(In) 애니를 건너뛰고 곧바로 정착(끝 프레임) 상태로 스냅한다(ISSUE-13).
    /// 라운드 변경/재진입 시 셀 재생성(BuildCells)으로 새 cloudAnimator 가 기본 상태(등장 In)를 재생하는데,
    /// 이벤트 최초 시작이 아니면(트랙 컨트롤러가 판정) 이 등장을 화면에 보이지 않게 한다.
    /// 상태명/레이어에 의존하지 않도록 **모든 레이어의 현재(기본=등장) 상태를 마지막 프레임으로** 밀어 정착시킨다. 멱등.
    /// </summary>
    public void SnapCloudToIdle()
    {
        if (null == cloudAnimator)
        {
            return;
        }

        cloudAnimator.Update(0f);   // 재생성 직후 기본 상태 초기화 보장
        int layerCount = cloudAnimator.layerCount;
        for (int layer = 0; layer < layerCount; layer++)
        {
            AnimatorStateInfo state = cloudAnimator.GetCurrentAnimatorStateInfo(layer);
            cloudAnimator.Play(state.fullPathHash, layer, 1f);   // 등장 상태를 끝(정착) 프레임으로 — 올라오는 연출 미노출
        }

        cloudAnimator.Update(0f);   // 스냅 즉시 적용
    }

    // 트리거 파라미터 보유 여부 — 미보유 애니메이터에 SetTrigger 를 쏘면 경고가 남는다(일반 구름 방어).
    private bool HasFinalInTrigger()
    {
        AnimatorControllerParameter[] parameters = cloudAnimator.parameters;
        int count = parameters.Length;
        for (int i = 0; i < count; i++)
        {
            if (parameters[i].type == AnimatorControllerParameterType.Trigger && parameters[i].nameHash == FINAL_IN_TRIGGER)
            {
                return true;
            }
        }

        return false;
    }

    // In 상태에 물린 클립의 실제 길이(열기구 Up 클립 조회와 동일 패턴). 미조회 시 0.
    private float ResolveFinalInClipLength()
    {
        return ResolveClipLength(FINAL_IN_CLIP_NAME);
    }

    // 클립 길이 조회(하드코딩 금지) — 아트가 클립을 조정해도 코드 수정이 필요 없도록 실제 길이를 읽는다. 미조회 시 0.
    private float ResolveClipLength(string clipName)
    {
        if (null == cloudAnimator)
        {
            return 0f;
        }

        RuntimeAnimatorController controller = cloudAnimator.runtimeAnimatorController;
        if (null == controller)
        {
            return 0f;
        }

        AnimationClip[] clips = controller.animationClips;
        int count = clips.Length;
        for (int i = 0; i < count; i++)
        {
            if (clips[i].name == clipName)
            {
                return clips[i].length;
            }
        }

        return 0f;
    }

    /// <summary>
    /// 단계 성공(§4-4) — 별이 진행중(노랑) → 클리어(파랑) 로 바뀌는 순간 이펙트. 별·깃발 상태도 즉시 반영한다
    /// (트랙 데이터 갱신을 기다리면 연출이 끝난 뒤에야 별이 바뀐다).
    /// </summary>
    public async UniTask PlayStarChangeClearAsync(CancellationToken ct)
    {
        StatefulComponent stateful = Stateful;
        if (null != stateful && stateful.HasState((int)StateRole.StageClear))
        {
            stateful.SetState((int)StateRole.StageClear);   // 별 파랑 + 깃발
        }

        await PlayStarFxAsync(StateRole.FxStarChangeClearOn, StateRole.FxStarChangeClearOff, ct);
    }

    /// <summary>라운드 시작(§4-3) — 별이 잠금(회색) → 진행중(노랑) 으로 풀리는 순간 이펙트.</summary>
    public async UniTask PlayStarChangeUnlockAsync(CancellationToken ct)
    {
        await PlayStarFxAsync(StateRole.FxStarChangeLockOn, StateRole.FxStarChangeLockOff, ct);
    }

    // 프리팹 State 로 파티클 GO 를 켜고 끈다. 파티클이 looping 이라 On 만 하면 영구히 남는다.
    // ⚠️ RefreshView 에서 켜면 팝업 재진입 시 이미 클리어된 구름 전부에서 이펙트가 동시에 터진다 → **연출 시점에만** 호출할 것.
    // 스킵/파괴로 취소돼도 finally 에서 반드시 끈다.
    private async UniTask PlayStarFxAsync(StateRole onState, StateRole offState, CancellationToken ct)
    {
        StatefulComponent stateful = Stateful;
        if (null == stateful || !stateful.HasState((int)onState))
        {
            return;
        }

        stateful.SetState((int)onState);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(FX_STAR_CHANGE_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }
        finally
        {
            if (null != this && null != Stateful && Stateful.HasState((int)offState))
            {
                Stateful.SetState((int)offState);
            }
        }
    }

    /// <summary>단계 성공 별 전환(§4-4)의 끝 상태 — 별은 클리어(파랑+깃발), 전환 이펙트는 꺼진 모습. 스킵 스냅용. 멱등.</summary>
    public void SnapStarChangeClearEnd()
    {
        StatefulComponent stateful = Stateful;
        if (null != stateful && stateful.HasState((int)StateRole.StageClear))
        {
            stateful.SetState((int)StateRole.StageClear);
        }

        SnapStarFxOff(StateRole.FxStarChangeClearOff);
    }

    /// <summary>라운드 시작 별 잠금해제(§4-3)의 끝 상태 — 전환 이펙트가 꺼진 모습(별 상태는 트랙 데이터가 반영). 스킵 스냅용. 멱등.</summary>
    public void SnapStarChangeUnlockEnd()
    {
        SnapStarFxOff(StateRole.FxStarChangeLockOff);
    }

    // 별 전환 파티클 끄기 — looping 파티클이라 켜둔 채 스킵하면 화면에 영구히 남는다.
    private void SnapStarFxOff(StateRole offState)
    {
        StatefulComponent stateful = Stateful;
        if (null != stateful && stateful.HasState((int)offState))
        {
            stateful.SetState((int)offState);
        }
    }

    /// <summary>
    /// 경쟁자 모집 연출(§4-3) — PortraitPanel 의 포트레이트가 열기구(AirBalloonTrans)에서 각자 착지 위치(portraitVecList)로
    /// DOJump 하며 우르르 등장한다. 내 캐릭터가 열기구에서 내리는 로직(DreamBalloonPartnerController.DisembarkAsync 의
    /// JumpBetweenAsync)을 포트레이트용으로 옮긴 것 — 시작점을 열기구로 잡고 자식 순서대로 vecList 로 뛰어내린다.
    ///
    /// **기획 계산식(count = GetRecruitFriendCount) 만큼** 앞에서부터 노출하며, 자식 순서대로 앞쪽 자리(portraitVecList[i])로 DOJump 한다.
    /// (랜덤 선정 없음 — 앞에서 count 개. 단계가 오를수록 감소, 기획서 920223827. portraitVecList 는 자식 순서와 1:1.)
    ///
    /// ⚠️ **착지한 포트레이트는 라운드 도전 중 내내 구름 위에 남는다**(기획 §4-3 "구름 위에 서 있음" → §4-4 성공 연출 순서2
    ///    에서야 "구름 밑으로 떨어짐"). 구 구현은 착지 직후 `finally` 에서 패널을 꺼버려 **열기구에서 나오자마자 사라졌다.**
    ///    이제 연출은 **배치만** 하고, 노출 여부는 <see cref="ApplyPortraitVisibility"/> 가 **상태로 판정**한다
    ///    (현재 라운드 구름 + 진행중일 때만 노출) → 팝업 재진입·게이지 갱신에도 그대로 유지된다.
    ///
    /// 스킵/파괴 안전(ct·SetLink) — 취소돼도 `finally` 가 착지 상태로 스냅한다.
    /// </summary>
    /// <summary>
    /// 이 라운드에 노출할 경쟁자 포트레이트 수 — 자리(portraitVecList)·PortraitPanel 자식 수·기획값(GetRecruitFriendCount) 중 최소.
    /// 기획값의 권위는 <c>Event_DreamBalloon_AiRound.profileCount</c>(§4-3, 라운드별 10·10·9·9·8·8·7·7·6·5).
    /// 상한 9(<c>EventDreamBalloonHelper.MAX_RECRUIT_PROFILE_COUNT</c>)는 이 프리팹의 자리 수와 같아야 한다 —
    /// 자리보다 큰 값이 들어오면 여기서 조용히 잘려 테이블 의도와 화면이 어긋난다.
    /// </summary>
    private int ShownPortraitCount()
    {
        if (null == portraitPanel)
        {
            return 0;
        }

        int slots = Mathf.Min(portraitPanel.childCount, portraitVecList.Count);
        int friendCount = null != data ? EventDreamBalloonHelper.GetRecruitFriendCount(data.round, EventDreamBalloonHelper.GetTotalRound()) : 0;
        return Mathf.Min(slots, friendCount);
    }

    public async UniTask PlayRecruitPortraitsAsync(int count, CancellationToken ct)
    {
        if (null == portraitPanel || null == airBalloonTrans)
        {
            return;
        }

        int childCount = portraitPanel.childCount;
        int show = Mathf.Min(childCount, portraitVecList.Count, count);   // 기획 계산식 결과(count)만큼만 앞에서 노출(920223827)

        portraitsPlaying = true;
        portraitsExiting = false;   // 새 라운드 모집 시작 — 이전 라운드 퇴장 상태를 지운다
        portraitPanel.gameObject.SetActive(true);

        // 시작점 = 열기구 위치를 패널 로컬로 환산(모두 열기구에서 내린다). 노출분만 시작점에 세팅, 나머지는 숨김.
        Vector3 startLocal = portraitPanel.InverseTransformPoint(airBalloonTrans.position);
        for (int i = 0; i < childCount; i++)
        {
            RectTransform portrait = portraitPanel.GetChild(i) as RectTransform;
            if (null == portrait)
            {
                continue;
            }

            bool active = i < show;
            portrait.gameObject.SetActive(active);
            if (active)
            {
                portrait.DOKill();   // 이전 라운드 DOJump 잔상 제거 → Init 을 확실히 열기구 위치로
                portrait.localScale = Vector3.one;
                portrait.localPosition = startLocal;   // Init = 열기구 위치(사용자 요구)
            }
        }

        try
        {
            // 자식 순서대로 우르르 — 열기구 → 앞쪽 자리(portraitVecList[i]) 로 DOJump.
            for (int i = 0; i < show; i++)
            {
                RectTransform portrait = portraitPanel.GetChild(i) as RectTransform;
                Vector3 target = portraitVecList[i];
                portrait.DOLocalJump(target, PORTRAIT_JUMP_POWER, 1, PORTRAIT_JUMP_SEC).SetEase(Ease.Linear).SetLink(portrait.gameObject);
                await UniTask.Delay(TimeSpan.FromSeconds(PORTRAIT_SPAWN_GAP_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
            }

            await UniTask.Delay(TimeSpan.FromSeconds(PORTRAIT_LAND_HOLD_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }
        finally
        {
            // 정상 종료·스킵·파괴 공통 — **끄지 않는다.** 착지한 상태로 확정만 하고 라운드 도전 중 내내 남긴다(위 ⚠️).
            portraitsPlaying = false;
            SnapRecruitPortraitsEnd();
        }
    }

    /// <summary>
    /// 모집 연출의 끝 상태 — 자리 있는 포트레이트를 **착지 위치(portraitVecList)에 배치한 채 노출**한다. 멱등.
    /// 스킵/취소로 DOJump 가 중간에 끊겨도, 재진입(<see cref="DreamBalloonRoadController.RestorePortraitsAtTarget"/>)에도 이 함수가 최종 위치를 확정한다.
    ///
    /// **경쟁자 수 게이트**: 이 라운드의 경쟁자 수가 0이면(최종 라운드 등, 모집 연출도 포트레이트를 안 띄운다) 노출하지 않고 숨긴다
    /// — 그래야 재진입·스킵 스냅에서 있지도 않은 포트레이트가 살아나지 않는다.
    /// </summary>
    public void SnapRecruitPortraitsEnd()
    {
        if (null == portraitPanel)
        {
            return;
        }

        int show = ShownPortraitCount();
        if (show <= 0)
        {
            portraitPanel.gameObject.SetActive(false);   // 경쟁자 없음(단계 1개뿐 등) — 내 파트너만 남는다
            return;
        }

        int childCount = portraitPanel.childCount;

        portraitPanel.gameObject.SetActive(true);
        for (int i = 0; i < childCount; i++)
        {
            RectTransform portrait = portraitPanel.GetChild(i) as RectTransform;
            if (null == portrait)
            {
                continue;
            }

            bool active = i < show;
            portrait.gameObject.SetActive(active);
            if (active)
            {
                portrait.DOKill();
                portrait.localPosition = portraitVecList[i];
            }
        }
    }

    // §4-4 순서2 — 열기구에 타는 프로필 수 = 표시 프로필의 절반(소수점 올림). 나머지 절반은 구름에 남는다.
    private int PortraitBoardCount()
    {
        int show = ShownPortraitCount();
        return show <= 0 ? 0 : Mathf.CeilToInt(show * 0.5f);
    }

    /// <summary>
    /// 경쟁자 포트레이트 낙하(§4-4 순서4) — **라운드 성공 후 클리어 구름에 깃발이 꽂히면** 남은 프로필이 구름 밑으로 떨어진다.
    /// 낙하 애니는 아트 Animator(DreamBalloonFestival_PortraitPanel)가 전담하고, 코드는 트리거만 쏜다.
    /// `fromIndex` 이상만 발화한다 — 성공 시 앞쪽 절반은 열기구에 탑승(<see cref="BoardHalfIntoBalloonAsync"/>)해 이미 숨겨졌으므로 나머지만 낙하한다.
    ///
    /// ⚠️ **Disable/Enable 을 하지 않는다.** 남은 포트레이트는 이미 활성 상태이므로 바로 트리거만 발화한다(규칙 1).
    /// </summary>
    public void PlayPortraitFallDown(int fromIndex = 0)
    {
        if (null == portraitPanel)
        {
            return;
        }

        int show = ShownPortraitCount();
        if (show <= 0)
        {
            return;   // 표시 중인 경쟁자가 없으면 낙하도 없다
        }

        EnsurePortraitAnimators();
        portraitsExiting = true;
        for (int i = Mathf.Max(0, fromIndex); i < show; i++)
        {
            Animator anim = i < portraitAnimators.Length ? portraitAnimators[i] : null;
            if (null != anim && anim.isActiveAndEnabled)
            {
                anim.SetTrigger(PORTRAIT_FALLDOWN_TRIGGER);
            }
        }
    }

    /// <summary>§4-4 순서4 — 성공 후 열기구에 타지 못하고 남은(뒤쪽) 프로필만 구름 밑으로 낙하시킨다. 탑승한 앞쪽 절반은 이미 숨겨져 있다.</summary>
    public void PlayRemainingPortraitFallDown()
    {
        PlayPortraitFallDown(PortraitBoardCount());
    }

    /// <summary>
    /// 경쟁자 포트레이트 탑승(§4-5 순서1 · 규칙 3) — **라운드 실패 시** 빈 열기구가 다음 구름으로 날아가기 **전에**
    /// 포트레이트가 열기구(AirBalloonTrans)로 뛰어 들어간다(내 파트너는 태우지 않는다). 성공 시 파트너 탑승 점프와 동일한 DOJump.
    /// 착지 위치(vecList)에서 열기구 위치로 뛴 뒤 숨긴다(열기구 안으로 들어감). 스킵/파괴 안전(ct·SetLink).
    /// </summary>
    public async UniTask BoardPortraitsIntoBalloonAsync(CancellationToken ct)
    {
        if (null == portraitPanel || null == airBalloonTrans)
        {
            return;
        }

        int show = ShownPortraitCount();
        if (show <= 0)
        {
            return;
        }

        portraitsExiting = true;
        Vector3 balloonLocal = portraitPanel.InverseTransformPoint(airBalloonTrans.position);   // 열기구 위치를 패널 로컬로

        try
        {
            for (int i = 0; i < show; i++)
            {
                RectTransform portrait = portraitPanel.GetChild(i) as RectTransform;
                if (null == portrait || !portrait.gameObject.activeSelf)
                {
                    continue;
                }

                portrait.DOKill();
                portrait.DOLocalJump(balloonLocal, PORTRAIT_JUMP_POWER, 1, PORTRAIT_JUMP_SEC).SetEase(Ease.Linear).SetLink(portrait.gameObject);
                await UniTask.Delay(TimeSpan.FromSeconds(PORTRAIT_SPAWN_GAP_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
            }

            await UniTask.Delay(TimeSpan.FromSeconds(PORTRAIT_JUMP_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }
        finally
        {
            HidePortraits();   // 열기구에 다 탔다 = 사라짐(정상 종료·스킵·파괴 공통)
        }
    }

    /// <summary>
    /// 경쟁자 포트레이트 절반 탑승(§4-4 순서2) — **라운드 성공 시** 파트너를 뒤따라 구름 위 프로필의 **절반(올림)** 이
    /// 열기구(AirBalloonTrans)로 뛰어 들어간다. 탑승한 앞쪽 절반만 숨기고, 나머지 절반은 구름에 남긴다(순서4에서 낙하).
    /// 실패(<see cref="BoardPortraitsIntoBalloonAsync"/>)는 전원 탑승·패널 전체 숨김이라 별도다. 스킵/파괴 안전(ct·SetLink).
    /// </summary>
    public async UniTask BoardHalfIntoBalloonAsync(CancellationToken ct)
    {
        if (null == portraitPanel || null == airBalloonTrans)
        {
            return;
        }

        int board = PortraitBoardCount();
        if (board <= 0)
        {
            return;
        }

        portraitsExiting = true;
        Vector3 balloonLocal = portraitPanel.InverseTransformPoint(airBalloonTrans.position);   // 열기구 위치를 패널 로컬로

        try
        {
            for (int i = 0; i < board; i++)
            {
                RectTransform portrait = portraitPanel.GetChild(i) as RectTransform;
                if (null == portrait || !portrait.gameObject.activeSelf)
                {
                    continue;
                }

                portrait.DOKill();
                portrait.DOLocalJump(balloonLocal, PORTRAIT_JUMP_POWER, 1, PORTRAIT_JUMP_SEC).SetEase(Ease.Linear).SetLink(portrait.gameObject);
                await UniTask.Delay(TimeSpan.FromSeconds(PORTRAIT_SPAWN_GAP_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
            }

            await UniTask.Delay(TimeSpan.FromSeconds(PORTRAIT_JUMP_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }
        finally
        {
            HideBoardedPortraits(board);   // 탑승한 앞쪽 절반만 숨김(나머지는 구름에 남아 순서4에서 낙하) — 정상 종료·스킵·파괴 공통
        }
    }

    // 앞쪽 count 개(열기구 탑승분)만 즉시 숨긴다. 나머지는 그대로 둔다. 멱등.
    private void HideBoardedPortraits(int count)
    {
        if (null == portraitPanel)
        {
            return;
        }

        int limit = Mathf.Min(count, portraitPanel.childCount);
        for (int i = 0; i < limit; i++)
        {
            RectTransform portrait = portraitPanel.GetChild(i) as RectTransform;
            if (null != portrait)
            {
                portrait.DOKill();
                portrait.gameObject.SetActive(false);
            }
        }
    }

    // 포트레이트를 즉시 숨긴다(스킵 스냅 — 퇴장 애니가 끊겨도 최종 상태 = 사라짐). 멱등.
    public void HidePortraits()
    {
        portraitsExiting = false;
        if (null != portraitPanel)
        {
            portraitPanel.gameObject.SetActive(false);
        }
    }

    // 각 포트레이트 슬롯(Portrait_NN) 안쪽 Portrait 의 Animator 를 1회 캐싱한다(런타임 GetComponent 반복 지양).
    private void EnsurePortraitAnimators()
    {
        if (null != portraitAnimators)
        {
            return;
        }

        int childCount = portraitPanel.childCount;
        portraitAnimators = new Animator[childCount];
        for (int i = 0; i < childCount; i++)
        {
            portraitAnimators[i] = portraitPanel.GetChild(i).GetComponentInChildren<Animator>(true);
        }
    }

    /// <summary>
    /// 포트레이트 정리 규칙 — **여기서는 노출하지 않는다.** 노출은 모집 연출(<see cref="PlayRecruitPortraitsAsync"/>)이
    /// **단독으로 소유**하며, Init = 열기구 위치 → DOJump 로 시작한다(스킵 시퀀스 안). 여기서 미리 노출하면
    /// 열기구 등장(In) 애니 동안 **목표 위치에서 먼저 떠 버려**(Init=열기구 위반) 스킵의 영향도 받지 못한다.
    ///
    /// 역할은 **정리(숨김)** 뿐이다 — 현재 도전 중(state 1 · 진행중)이 아닌 셀의 남은 포트레이트만 끈다.
    /// 진행중 구름은 모집이 노출한 포트레이트를 유지하고(게이지 갱신에도 안 끔), 클리어(2)는 퇴장 연출
    /// (<see cref="PlayPortraitFallDown"/> / <see cref="BoardPortraitsIntoBalloonAsync"/>)이 소유하므로 건드리지 않는다.
    /// 모집·퇴장 연출 재생 중에는 적용하지 않는다(진행 중 애니를 덮어쓰지 않도록).
    /// </summary>
    private void ApplyPortraitVisibility()
    {
        if (null == portraitPanel || portraitsPlaying || portraitsExiting)
        {
            return;
        }

        bool currentProgressing = null != data && 1 == data.state && EventDreamBalloonHelper.IsRoundProgressing();
        bool cleared = null != data && 2 == data.state;
        if (!currentProgressing && !cleared)
        {
            portraitPanel.gameObject.SetActive(false);   // 예정(0) 등 — 남은 포트레이트 정리
        }
    }

    // LoopScroll 이 SendMessage 로 호출(반환값 없음).
    public void SetInfo(DreamBalloonStageData info)
    {
        // 이 셀이 다른 라운드로 재사용되면 낙하 상태를 초기화한다(같은 라운드 갱신 — 코인 게이지 등 — 중에는 낙하 애니를 끊지 않도록 유지).
        bool roundChanged = null == data || null == info || data.round != info.round;
        data = info;
        if (null == data)
        {
            return;
        }

        if (roundChanged)
        {
            // 새 라운드 셀(재활용 포함) — 이전 라운드의 남은 포트레이트를 즉시 끈다. 노출은 모집 연출이
            // 열기구 위치에서 다시 시작한다(Init=열기구). 이 숨김이 없으면 재활용 셀의 목표 위치 포트레이트가
            // 열기구 등장 동안 잔상으로 보인다.
            portraitsExiting = false;
            if (null != portraitPanel)
            {
                portraitPanel.gameObject.SetActive(false);
            }
        }

        RefreshView();
    }

    private void RefreshView()
    {
        var stateful = Stateful;
        if (null == stateful || null == data)
        {
            return;
        }

        // 단계번호(별 위 숫자) — EventBalloon_Stage
        if (stateful.HasText(TextRole.RoundText))
        {
            stateful.GetText(TextRole.RoundText).TMP.text = $"{data.round}";
        }

        // 진행 상태(§3-4 ⑩): 예정(잠김) / 현재 진행 / 성공(별 녹색+깃발)
        // 프리팹 authored role 정합: 성공=StageClear / 현재 진행=StageGauge(게이지 노출) / 예정=StageLock.
        var progressState = data.state == 2 ? StateRole.StageClear
            : data.state == 1 ? StateRole.StageGauge
            : StateRole.StageLock;
        if (stateful.HasState((int)progressState))
        {
            stateful.SetState((int)progressState);
        }

        // 재화 게이지 {curCoin}/{goalCoin} — **값만** 반영한다.
        // 노출 여부는 구름 Animator 가 단일 소유(ApplyCloudGaugeState) — 여기서 GO 를 켜고 끄면 클립의
        // LocalScale/m_IsActive 커브와 충돌해 매 갱신 되쓰기 싸움이 난다(ISSUE-34 후속).
        if (stateful.HasSlider(SliderRole.EventPointSlider))
        {
            UnityEngine.UI.Slider slider = stateful.GetSlider(SliderRole.EventPointSlider).Slider;
            slider.value = data.goalCoin > 0 ? Mathf.Clamp01((float)data.curCoin / data.goalCoin) : 0f;
        }

        ApplyCloudGaugeState();   // 게이지 노출 = 구름 Animator 상태로 전환(단일 소유자)

        // 게이지 위 코인 진행 텍스트(AllClearText) — 게이지는 진행중(state 1)에만 노출되므로 값만 채운다(미바인딩 시 no-op).
        if (null != gaugeText)
        {
            gaugeText.SetText($"{data.curCoin}/{data.goalCoin}");
        }

        RefreshStageReward();
        ApplyPortraitVisibility();   // 경쟁자 포트레이트 — 도전 중인 구름에서만 노출·유지(§4-3)
    }

    /// <summary>
    /// 재화 게이지 노출을 구름 Animator 상태로 전환한다(노출 판단의 단일 소유자).
    /// 노출 = 현재 라운드 구름(<c>data.state == 1</c>) **그리고 이벤트가 라운드 진행 중**일 때만.
    /// 현재 라운드 셀은 진행중/쉬는중 모두 <c>data.state == 1</c> 이라 이벤트 상태로 한 번 더 가른다(쉬는중엔 숨김).
    /// </summary>
    /// <remarks>
    /// 게이지가 필요할 때만 <c>Gauge_Idle</c> 을 강제 재생하고, 그 외에는 Animator 기본 흐름(In → Idle)을 건드리지 않는다.
    /// 무조건 Play 하면 구름 등장 연출(ISSUE-13)을 덮어쓰기 때문이다. 강제 적용했던 셀이 진행중을 벗어날 때만
    /// (셀 재활용으로 진행중 → 성공 전환) 되돌린다.
    /// RefreshView 는 재화 갱신마다 호출되므로 같은 상태에서는 재생하지 않는다 — 매번 Play 하면 클립이 처음부터 다시 돈다.
    /// </remarks>
    private void ApplyCloudGaugeState()
    {
        if (null == cloudAnimator || null == data)
        {
            return;
        }

        // 노출 = 현재 라운드 구름(data.state == 1)이면서 **진행중**이거나, 라운드 클리어(state 3)에서는
        //   **성공 연출 중인 바로 그 구름**(게이지가 목표까지 채워진 셀, displayRoundGoalFilled)일 때만.
        //   ⚠️ IsRoundCleared() 는 '이벤트 완주'가 아니라 매 라운드 통과 시 내려오는 state 3 이다(서버 스펙).
        //   ⚠️ ISSUE-29 — 클리어 연출 도중 강제 종료 후 재접속하면 라운드가 이미 이동해(displayRound = 다음 구름) 그 셀의
        //      코인이 0 이라 채워지지 않는다 → 게이지가 숨겨진다(대기/휴식과 동일). 연출 중인 셀만 curCoin>=goalCoin 으로 남는다.
        bool showGauge = data.state == 1
            && (EventDreamBalloonHelper.IsRoundProgressing()
                || (EventDreamBalloonHelper.IsRoundCleared() && data.goalCoin > 0 && data.curCoin >= data.goalCoin));
        // SetState(StageGauge) 가 방금 세워 둔 Gauge 트리거를 지운다 — 남겨 두면 AnyState 전이로 Gauge_In 이 재생된다.
        cloudAnimator.ResetTrigger(CLOUD_GAUGE_TRIGGER);

        if (showGauge)
        {
            if (gaugeStateApplied)
            {
                return;
            }

            gaugeStateApplied = true;

            // 구름 등장(In) 재생 차례면 등장을 먼저 보여 주고, 클립이 끝난 뒤 게이지를 얹는다.
            // 여기서 바로 Play 하면 기본 진입 상태(In)를 덮어써 등장 애니가 아예 보이지 않는다.
            if (data.cloudIntro)
            {
                ApplyGaugeAfterCloudIntroAsync(gameObject.GetCancellationTokenOnDestroy()).Forget();
                return;
            }

            cloudAnimator.Play(CLOUD_STATE_GAUGE, 0, 0f);
            return;
        }

        if (!gaugeStateApplied)
        {
            return;   // 강제한 적이 없으면 Animator 기본 흐름 유지(등장 연출 보존)
        }

        gaugeStateApplied = false;
        cloudAnimator.Play(data.state == 2 ? CLOUD_STATE_CLEAR : CLOUD_STATE_IDLE, 0, 0f);
    }

    // 구름 등장(In) 클립이 끝난 뒤 게이지를 정착 상태로 얹는다 — 등장 애니(이벤트 최초 시작 1회)를 덮지 않기 위한 지연 적용.
    // 클립 길이 미조회(0) 면 대기 없이 즉시 적용한다.
    private async UniTaskVoid ApplyGaugeAfterCloudIntroAsync(CancellationToken ct)
    {
        float introSec = ResolveClipLength(CLOUD_CLIP_IN_NAME);
        if (introSec > 0f)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(introSec), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }

        SnapGaugeIdle();
    }

    /// <summary>
    /// 게이지 등장 연출(Gauge_In) — **라운드를 완료하고 다음 라운드로 진행했을 때만** 재생한다(모집 연출 §4-3과 동시 진행).
    /// 실패 재시작·쉬어가기 후 시작·최초 진입, 그리고 메인 팝업 진입·재화 갱신에서는 호출하지 않는다
    /// (<see cref="ApplyCloudGaugeState"/> 가 등장 없이 정착 상태로 바로 들어간다). 호출 게이트는 RunRecruitAsync 의 playIn.
    /// </summary>
    /// <remarks>
    /// Gauge_In 은 나가는 전이가 없어 등장 후 마지막 프레임에 멈추므로, 클립 길이만큼 기다린 뒤 Gauge_Idle 로 넘긴다.
    /// <c>gaugeStateApplied</c> 를 선점해 두어 재생 중 RefreshView 가 Gauge_Idle 로 덮어써 등장 연출을 끊지 않게 한다.
    /// 스킵/취소로 중단되면 <see cref="SnapGaugeIdle"/> 이 끝 상태를 보장한다.
    /// </remarks>
    public async UniTask PlayGaugeInAsync(CancellationToken ct)
    {
        if (null == cloudAnimator)
        {
            return;
        }

        gaugeStateApplied = true;   // 등장 연출 재생 중 RefreshView 의 Gauge_Idle 재생 선점(끊김 방지)
        cloudAnimator.ResetTrigger(CLOUD_GAUGE_TRIGGER);   // 대기 중인 트리거가 재생 도중 AnyState 로 다시 진입하는 것 방지
        cloudAnimator.Play(CLOUD_STATE_GAUGE_IN, 0, 0f);

        float clipSec = ResolveClipLength(CLOUD_STATE_GAUGE_IN_NAME);
        if (clipSec > 0f)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(clipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }

        SnapGaugeIdle();
    }

    /// <summary>게이지를 정착 상태(Gauge_Idle)로 스냅 — 등장 연출의 끝 상태. 멱등.</summary>
    public void SnapGaugeIdle()
    {
        if (null == cloudAnimator)
        {
            return;
        }

        gaugeStateApplied = true;
        cloudAnimator.ResetTrigger(CLOUD_GAUGE_TRIGGER);   // 남은 트리거가 스냅 직후 Gauge_In 으로 되돌리는 것 방지

        // 스킵으로 이미 스냅된 뒤 PlayGaugeInAsync 의 대기가 뒤늦게 끝나 다시 호출될 수 있다(스킵은 취소가 아니라 정상 진행).
        // 같은 상태에서 Play 하면 루프가 처음부터 다시 도므로 건너뛴다.
        if (cloudAnimator.GetCurrentAnimatorStateInfo(0).shortNameHash == CLOUD_STATE_GAUGE)
        {
            return;
        }

        cloudAnimator.Play(CLOUD_STATE_GAUGE, 0, 0f);
    }

    // 단계 보상 이미지(§3-4 ⑨) — 보상이 있는 **예정(잠김) 구름에서만 미리보기** 노출(최대 2개).
    // 진행중 구름은 아트 설계상 숨김(StageGauge 상태가 컨테이너 OFF + Gauge 클립이 y를 -139 로 이동 — 게이지가 자리 차지),
    // 클리어 구름은 Clear 클립이 알파 0(보상은 이미 지급). 최종 라운드 보상은 상단 "최종 보상"(§3-4 ⑦) 별도 표시.
    private void RefreshStageReward()
    {
        int difficulty = EventDreamBalloonHelper.GetDifficulty();
        // 구름 K 는 **직전 라운드(K-1) 보상(goalValue1==K-1)** 을 미리보기한다 — 기획 "N 보상은 N+1 구름에 노출"(2026-07-16).
        // → 1번 구름은 보상 없음(row 0 없음), 최종 보상(goalValue1 마지막)은 트랙 셀이 아닌 상단 "최종 보상"(§3-4 ⑦)·최종 구름이 담당.
        //   (마지막 셀 K==totalRound 도 row(K-1) 을 노출하므로 isFinal 제외 가드는 두지 않는다. 최종 구름 finalCloud 는 SetInfo 미대상이라 이 경로를 안 탄다.)
        int rewardRound = data.round - 1;
        // 1번 구름은 직전 라운드가 없어 rewardRound 가 0 이 된다(위 주석의 "row 0 없음").
        //  AiRound 는 eventRound 1~10 만 발행되므로 0 조회는 테이블 계층에서 "not found" Error 로그를 남긴다
        //  — 정상 케이스가 에러로 찍혀 진짜 테이블 누락을 묻으므로 조회 전에 끊는다(hasReward 는 기존에도 항상 false).
        bool hasReward = rewardRound >= 1 && EventDreamBalloonHelper.HasStageReward(rewardRound, difficulty);
        bool showPreview = hasReward && 0 == data.state;   // 예정(잠김) 구름 전용 미리보기
        RefreshRewardChecks(hasReward && 0 != data.state); // check 아이콘 = 이 구름 도착(=직전 라운드 클리어)으로 보상 획득
        SetRewardContainerActive(showPreview);
        if (!showPreview)
        {
            return;
        }

        EnsureRewardItems();
        EventDreamBalloonHelper.BindRewards(rewardItems, EventDreamBalloonHelper.GetRewardRow(rewardRound, difficulty));
    }

    // 보상 노출 토글 = 컨테이너 루트가 아니라 자식 Content GO 를 켜고 끈다.
    //
    // ⚠️ 루트(StageCloudRweard)의 m_IsActive 는 구름 Animator 가 소유한다 — Clear_In 클립이 바인딩하고
    // 전 상태가 Write Defaults ON 이라, 현재 클립이 안 건드리는 동안에도 Animator 가 기본값(authored 값)을
    // 매 갱신 되쓴다. 그래서 루트는 authored 활성으로 두어 Animator 가 켠 상태를 유지하게 하고(클리어 알파0·
    // 진행중 y이동 등 아트 연출은 클립이 전담), 코드는 클립에 바인딩되지 않은 Content 로만 노출을 결정한다.
    // (같은 이유로 DefaultRweard/DefaultRweardOff 스위치 상태도 루트 대상이라 사용하지 않는다 — Animator 와 충돌)
    private void SetRewardContainerActive(bool active)
    {
        if (null == rewardContent)
        {
            return;
        }

        // StageGauge 상태 Description 이 루트를 꺼둔 직후의 프레임 창 대비 — Animator(Write Defaults)가 곧 되켜지만 즉시 보장(멱등).
        if (active && null != rewardRoot && !rewardRoot.activeSelf)
        {
            rewardRoot.SetActive(true);
        }

        rewardContent.gameObject.SetActive(active);
    }

    // 보상 슬롯 check 아이콘(§3-4 ⑨) — 해당 라운드 클리어(state 2) 여부로만 표시를 결정한다.
    // ⚠️ 슬롯1 check 의 GO 활성(m_IsActive)은 구름 Animator 클립(Idle/Clear_In/Clear_Idle)이 바인딩해 소유
    //    (Write Defaults ON) — GO 토글은 애니 갱신에 덮이므로, 클립이 바인딩하지 않는 Image.enabled 가 판정 기준.
    //    GO 토글은 클립 미바인딩 슬롯(2번)이 꺼진 채 남지 않도록 병행한다(소유 슬롯에선 애니가 덮어도 무해).
    private void RefreshRewardChecks(bool cleared)
    {
        int count = rewardCheckIcons.Length;
        for (int i = 0; i < count; i++)
        {
            UnityEngine.UI.Image icon = rewardCheckIcons[i];
            if (null == icon)
            {
                continue;
            }

            icon.enabled = cleared;
            icon.gameObject.SetActive(cleared);
        }
    }

    // 보상 슬롯 확보 — 프리팹 authored 슬롯(rewardItems 바인딩)이 있으면 그대로 사용하고,
    // 미바인딩 프리팹에서만 rewardItemPrefab 으로 런타임 생성(폴백). 그마저 없으면 빈 배열(BindRewards no-op).
    private void EnsureRewardItems()
    {
        if (null != rewardItems && 0 < rewardItems.Length)
        {
            return;
        }

        if (null == rewardContent || null == rewardItemPrefab)
        {
            rewardItems = new CommonRewardItem[0];
            return;
        }

        rewardItems = new CommonRewardItem[MAX_STAGE_REWARD];
        for (int i = 0; i < MAX_STAGE_REWARD; i++)
        {
            GameObject go = Instantiate(rewardItemPrefab, rewardContent);
            rewardItems[i] = go.GetComponent<CommonRewardItem>();
        }
    }
}

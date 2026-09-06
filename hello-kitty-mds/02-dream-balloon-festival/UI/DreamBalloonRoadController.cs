using System;
using System.Collections.Generic;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using GameCore.Utils;

using GameCore.Skip;

using GameLogic.Management;

using UnityEngine;

// 드림 벌룬 — 구름 단계 트랙 · 열기구 · 카메라 이동 컨트롤러 (구현 명세서 §7-2, 기획 §4-3·§4-4·§4-5).
// 열기구(HotAirBalloon)는 화면 고정 오버레이(Animator "Up" = 부유/상승 연출), 좌우 이동은 DOTween(코드).
// 스테이지 트랙(DreamBalloonStageLoopScroll)의 스크롤이 "카메라 이동"(구름 동시 하강)을 담당한다.
// 구름 좌우 배치는 배열 index 홀짝 기준(짝수 Left / 홀수 Right, 기획 "이벤트 단계 배치 예시") → 열기구도 같은 기준으로 좌우 정렬.
// 연출은 순수 view(데이터 조작 없음)·스킵/파괴 안전(§7-3).
//
// 스킵(기획 §4 "연출이 출력될 때 화면을 터치하면 연출 스킵 가능"): 표준 SkipableBase 를 상속한다.
// 네 연출(성공/실패/최종/모집)은 한 번에 하나만 재생되므로, 재생 시 본체(pendingSequence)와
// 그 연출의 최종 상태 스냅(pendingSnapToEnd)을 세팅하고 베이스의 PlayAsync 로 태운다.
// 스킵 = 비주얼만 끝으로 점프(OnSkipToEnd) 후 후속 진행(결과 팝업/머지판 이동)은 그대로 실행된다.
public class DreamBalloonRoadController : SkipableBase
{
    [SerializeField] private DreamBalloonStageLoopScroll stageScroll;
    [SerializeField] private Animator balloonAnimator;   // HotAirBalloon (트리거 "Up")
    [SerializeField] private RectTransform balloonRect;   // HotAirBalloon RectTransform (좌우 이동 = DOTween)
    [SerializeField] private float leftStageX = -250f;    // 짝수 index 구름 아래 열기구 X (좌측)
    [SerializeField] private float rightStageX = 250f;    // 홀수 index 구름 아래 열기구 X (우측)
    [SerializeField] private CanvasGroup startText;       // 경쟁자 모집(§4-3) START 문구 = StartText (미현지화 authored — LIdx 미지정)
    [SerializeField] private GameObject fxNextRound;      // 라운드 시작 이펙트 Fx_NextRound — START 문구와 동시 재생(아트 952860690 요청, §4-8 ③)
    [SerializeField] private GameObject fxArriveIn;       // 열기구 착지 이펙트(중첩 HotAirBalloon/Fx_Arrive_In) — 도착 시 점등(아트 "UP출력 후에 켜주세요"). 루프 파티클 포함이라 정리 시 반드시 소등
    [SerializeField] private DreamBalloonPartnerController partnerController;   // 파트너·경쟁 친구 캐릭터(§3-2·§4-3·§4-5)
    [SerializeField] private RectTransform bgPanel;   // 세로 롱 배경(§4-7 ⑦) — 라운드 상승에 맞춰 아래로 내려가 밤하늘이 드러난다
    [SerializeField] private RectTransform bgViewport;   // 배경이 보이는 화면 영역(= 해상도 높이). 미바인딩 시 루트 캔버스로 폴백
    [SerializeField] private DreamBalloonStageItem finalCloud;   // 최종 구름(EventBalloonCloudFinal) — 트랙 밖 고정 오브젝트. 최종 연출에서 팝업 중앙으로 내려온다(§4-4 최종)

    // [ISSUE-30] 최종 단계 줌인 — 최종 연출 끝(파트너 하차 직전)에 카메라가 축제장으로 들어간다.
    //   uGUI 라 카메라가 없어 **컨테이너 스케일**로 구현한다. 대상은 두 개면 충분하다:
    //     · worldRoot(= 팝업 `Middle`)  — 배경(BgPanel)·트랙(Scroll View) 을 담는다. pivot(0.5,0.5) 스트레치라 자기 pivot 기준 스케일이 곧 카메라 줌.
    //     · finalCloud                  — 축제장. **파트너·열기구도 이 아래로 들어오므로**(파트너는 하차 시 PartnerTrans 자식,
    //                                     열기구는 AttachBalloonToFinalCloud 로 AirBalloonTrans 자식) 함께 확대된다.
    //   타이틀·타이머·닫기(`Top`)와 하단 버튼(`Bottom`)은 스케일 대상이 아니라 원래 크기를 유지한다(기획 요청과 일치).
    //   ⚠️ 두 대상이 **같은 월드 점**을 기준으로 커져야 한 화면처럼 보인다 → 기준점 = `worldRoot.position`(RectTransform 의 pivot 월드 위치).
    [SerializeField] private RectTransform worldRoot;            // 줌 대상 1 — 배경·트랙 컨테이너(`Middle`). 미바인딩 시 배경·트랙은 줌에서 제외(축제장만 확대)
    [SerializeField] private float finalZoomScale = 1.6f;        // 줌 배율(아트 감수 대상 — 지라 첨부 박스 기준 추정치)
    [SerializeField] private float finalZoomSec = 0.5f;          // 줌 소요 시간
    // N-1 라운드부터 최종 구름이 내려올 때(AlignFinalCloud) N 라운드 구름과의 **시각 간격** 보정값(px).
    // 최종 구름은 축제장이라 일반 구름보다 훨씬 크고 스프라이트 여백도 달라, 계산상 한 칸이어도 화면에선 붙어 보인다.
    // 양수 = 최종 구름을 위로(간격 넓힘) / 0 = 보정 없음. 순수 시각 튜닝값이라 최종 조정은 인스펙터에서 한다.
    [SerializeField] private float finalCloudGapOffsetY = 100f;

    // 상승 애니 클립 — 열기구 이동(좌우)·트랙 스크롤이 이 클립 길이에 맞춰 재생된다(상승 연출과 이동이 어긋나지 않도록).
    private const string UP_CLIP_NAME = "Ani_Clip_UiPopUpEventHotAirBalloon_Up";
    private const string IN_CLIP_NAME = "Ani_Clip_UiPopUpEventHotAirBalloon_In";   // 최초 등장(In) 클립 — 라운드 1 모집 연출은 이 애니 종료 후 시작
    private const float FALLBACK_UP_SEC = 1f;   // 클립 조회 실패 시 폴백

    // 연출 시간(기획: §4-3 ~3초 / §4-4·§4-5 ~2초).
    private const float SETTLE_HOLD_SEC = 0.35f;   // 도착 구름 안착 후 결과 팝업 전 짧은 정착 홀드(§4-4)
    private const float RECRUIT_SEC = 3f;
    private const float FALLBACK_START_FLASH_SEC = 0.8f;   // StartText 에 아트 DOTweenAnimation 이 없을 때만 쓰는 폴백 길이
    private const float FINAL_CUE_FALLBACK_MARGIN_SEC = 1f;   // 최종 하차 큐(Animation Event) 유실 시 폴백 여유 — 클립 길이 + 이 값 후 진행

    // 연출 사운드(기획 §4 정본 매핑 · 2026-07-16 최신화). ⚠️ sound.csv 미발행(최대 1060) — 발행 전엔 미조회 에러 로그만 남고 무음.
    private const int SFX_BALLOON_BOARD = 1207;    // 열기구에 올라타는(§4-4 성공·최종 / §4-5) — 중국판: 1191은 트럭배송 경적이라 1207로 재배정
    private const int SFX_BALLOON_RISE = 1182;     // 열기구 올라가는(§4-4 순서2·최종 / §4-5)
    private const int SFX_BALLOON_DESCEND = 1183;  // 열기구 내리는 — 파트너·프로필 하차(§4-3 순서1 / §4-4 최종 / §4-7)
    private const int SFX_START_TEXT = 1184;       // START 문구 출력(§4-3 / §4-7)
    private const int SFX_FINAL_JOY = 1185;        // 최종 단계 하차 후 기뻐함(§4-4 최종)
    private const int SFX_FINAL_RAINBOW = 1189;    // 최종 단계 무지개(§4-4 최종)

    private static readonly int UP_TRIGGER = Animator.StringToHash("Up");
    private static readonly int IN_TRIGGER = Animator.StringToHash("In");
    private static readonly int IDLE_TRIGGER = Animator.StringToHash("Idle");

    private Tween balloonMoveTween;
    private Tween bgMoveTween;

    // [ISSUE-30] 최종 줌인 상태 — 원본 값은 줌 시작 전 1회만 캡처하고 ResetFinalZoom 이 되돌린다(캐시 재사용 대비).
    private Tween finalZoomWorldTween;
    private Tween finalZoomCloudScaleTween;
    private Tween finalZoomCloudMoveTween;
    private bool hasFinalZoomOrigin;
    private Vector3 zoomOriginWorldScale = Vector3.one;
    private Vector3 zoomOriginFinalScale = Vector3.one;
    private Vector2 zoomOriginFinalAnchoredPos;
    private Vector3 zoomOriginFinalWorldPos;
    private Tween finalCloudMoveTween;
    private float finalCloudHomeY;               // 최종 구름의 기본 Y(프리팹 authored, Awake 캐싱) — 최종 연출 외엔 항상 여기
    private float upClipSec = FALLBACK_UP_SEC;   // Up 클립 길이(Awake 캐싱)
    private float inClipSec;                      // In(등장) 클립 길이(Awake 캐싱) — RunRecruitAsync 가 라운드 시작 시 재생·대기. 없으면 0
    private float balloonHomeY;                  // 열기구의 화면 고정 Y(프리팹 authored, Awake 캐싱) — 연출로 옮겨져도 트랙 정렬 시 반드시 복귀
    private Transform balloonHomeParent;         // 열기구의 오버레이 부모(Awake 캐싱) — idle 엔 구름 앵커로 SetParent(스크롤 추종), 이동·연출 엔 이 부모로 복귀
    private float bgStartY;                      // 배경 1단계 기준 Y(프리팹 authored 값, Awake 캐싱)
    private RectTransform bgViewRect;            // 배경 노출 영역(= 화면) — 해석 성공 후에만 캐싱한다(BgViewRect 참조)
    private int stagedRound = -1;                // 마지막으로 트랙을 구성한 현재 라운드(값 갱신 vs 재생성 판정)
    private DOTweenAnimation[] startTextAnims;   // StartText 의 아트 authored 연출(등장 Scale / 퇴장 Scale·Fade) — Awake 캐싱
    private float startTextAnimSec;              // 그 연출의 전체 길이(delay + duration 최댓값, Awake 캐싱)

    private Func<CancellationToken, UniTask> pendingSequence;   // 재생 중인 연출 본체(SkipableBase.OnPlayAsync 가 실행)
    private Action pendingSnapToEnd;                            // 그 연출의 최종 상태 스냅(SkipableBase.OnSkipToEnd 가 실행)
    private bool sequencePlaying;                               // 연출 재생 중(스킵 스냅 포함) — 대기 화면의 상태 기준 숨김을 적용하지 않는다
    private bool keepScrollLocked;                              // 최종 연출 후 스크롤을 다시 켜지 않는다(OnCleanup) — 아래 이유 참조

    private CancellationToken DestroyToken => this.GetCancellationTokenOnDestroy();

    // ── SkipableBase 훅 ────────────────────────────────────────────────
    // 드림벌룬 연출은 전부 순수 view(서버 커밋 후 재생)라 전 구간 스킵을 허용한다(§4·§7-3).
    protected override UniTask OnPlayAsync(CancellationToken token)
    {
        sequencePlaying = true;   // 재생 구간 동안 상태 기준 숨김(ShowPartnerAtStage) 을 끈다
        stageScroll?.SetInteractable(false);   // 연출 재생 동안 유저 스크롤 잠금(§7-2) — OnCleanup 에서 복원
        SetSkippable(true);
        return null != pendingSequence ? pendingSequence(token) : UniTask.CompletedTask;
    }

    // 스킵 = 남은 단계까지 포함한 "연출의 끝 상태"로 즉시 스냅. 각 연출이 재생 시점에 자기 스냅을 등록한다.
    protected override void OnSkipToEnd()
    {
        pendingSnapToEnd?.Invoke();
    }

    // 완료·중단·예외 공통 — 진행 중이던 트윈을 남기지 않는다(스냅을 덮어쓰거나 다음 연출과 겹치는 것 방지).
    // OnSkipToEnd 는 이보다 먼저 돌므로(SkipableBase), 스냅 시점엔 아직 sequencePlaying = true 다.
    protected override void OnCleanup()
    {
        sequencePlaying = false;   // 이후부터 대기 화면 규칙(쉬는중 → 열기구 숨김)이 다시 적용된다

        // 연출 종료/중단/스킵 공통 — 유저 스크롤 재허용.
        // ⚠️ 단 최종 연출 뒤에는 다시 켜지 않는다. 최종 연출은 트랙을 Clamped 범위 **밖**으로 내리는데
        //    (DreamBalloonStageLoopScroll.MoveContentByAsync — 최종 라운드는 이미 상한이라 클램프하면 이동이 죽는다),
        //    ScrollRect 를 켜면 Clamped 가 다음 프레임에 content 를 범위로 되돌려 하강이 통째로 튕겨 올라간다.
        //    최종 연출 후엔 최종 보상 팝업 → 종료 팝업으로 흘러가고 메인 팝업은 _Final 이 닫으므로(ISSUE-31) 스크롤이 필요 없다.
        stageScroll?.SetInteractable(!keepScrollLocked);
        KillBalloonMove();
        KillBgMove();
        KillFinalCloudMove();
        partnerController?.KillJump();
        SetArriveFx(false);   // 착지 이펙트 소등 — 연출 종료/중단/스킵 공통(루프 파티클 잔존 방지)
    }

    // 열기구 착지 이펙트(아트 952860690 §7) — 도착 순간 점등. 미바인딩 시 no-op.
    private void SetArriveFx(bool active)
    {
        if (null != fxArriveIn)
        {
            fxArriveIn.SetActive(active);
        }
    }

    /// <summary>
    /// 연출 재생 공통 진입점 — 네 연출(성공/실패/최종/모집)이 모두 이 하나의 SkipableBase 를 공유하므로,
    /// 이미 재생 중인 연출이 있으면 **중단하고 새 연출로 교체**한다.
    ///
    /// ⚠️ <see cref="SkipableBase.PlayAsync"/> 는 재생 중 재진입을 **조용히 무시하고 즉시 반환**한다.
    ///    그대로 두면 새 연출이 통째로 사라지고 호출측 await 가 곧바로 풀려, 후속(결과 팝업)만 뜬다
    ///    = "화면을 클릭하지도 않았는데 연출이 스킵된" 것처럼 보인다.
    ///    (예: 모집 연출 재생 중 라운드 성공이 확정되면 성공 연출이 버려짐)
    ///
    /// pendingSequence/pendingSnapToEnd 는 **재생을 시작할 수 있을 때에만** 갈아끼운다.
    /// 먼저 덮어쓰면 아직 돌고 있는 이전 연출을 스킵할 때 엉뚱한 연출의 끝 상태로 스냅된다.
    /// </summary>
    private async UniTask PlaySequenceAsync(Func<CancellationToken, UniTask> sequence, Action snapToEnd, CancellationToken ct)
    {
        if (sequencePlaying)
        {
            Cancel();   // 중단(≠스킵) — 이전 연출의 후속은 실행되지 않는다(새 연출이 대체하므로 의도된 것)
            await UniTask.WaitUntil(() => !sequencePlaying, cancellationToken: DestroyToken);
        }

        keepScrollLocked = false;   // 기본은 연출 후 스크롤 복원. 최종 연출(RunFinalAsync)만 자기 시작 시점에 true 로 올린다.
        pendingSequence = sequence;
        pendingSnapToEnd = snapToEnd;
        await PlayAsync(ct);
    }

    private void Awake()
    {
        upClipSec = ResolveClipLength(UP_CLIP_NAME, FALLBACK_UP_SEC);
        inClipSec = ResolveClipLength(IN_CLIP_NAME, 0f);
        balloonHomeY = null != balloonRect ? balloonRect.anchoredPosition.y : 0f;
        balloonHomeParent = null != balloonRect ? balloonRect.parent : null;   // idle 구름 자식화 후 이동·연출 시 복귀할 오버레이 부모
        finalCloudHomeY = null != FinalCloudRect ? FinalCloudRect.anchoredPosition.y : 0f;
        bgStartY = null != bgPanel ? bgPanel.anchoredPosition.y : 0f;

        // START 문구 연출은 아트가 StartText 에 얹은 DOTweenAnimation(등장 Scale / 퇴장 Scale·Fade)이 전담한다.
        // 재생 시간도 하드코딩하지 않고 authored 값(delay + duration)의 최댓값에서 얻는다.
        startTextAnims = startText.GetComponents<DOTweenAnimation>();
        startTextAnimSec = 0f;
        int animCount = startTextAnims.Length;
        for (int i = 0; i < animCount; i++)
        {
            DOTweenAnimation anim = startTextAnims[i];
            startTextAnimSec = Mathf.Max(startTextAnimSec, anim.delay + anim.duration);
        }

        if (startTextAnimSec <= 0f)
        {
            startTextAnimSec = FALLBACK_START_FLASH_SEC;   // 아트 클립 미발행 시 폴백
        }
    }

    // 최종 구름(EventBalloonCloudFinal)의 RectTransform — 별도 바인딩을 두지 않고 finalCloud 에서 파생한다(어긋날 여지 제거).
    private RectTransform FinalCloudRect => null != finalCloud ? finalCloud.transform as RectTransform : null;

    /// <summary>
    /// 배경이 실제로 보이는 영역 = 화면(해상도).
    ///
    /// ⚠️ **Awake 에서 캐싱하면 안 된다.** 팝업은 Instantiate 직후 Awake 가 돌고 캔버스 부모는 그 뒤에 붙으므로
    /// (UIManager.SetUIParent), 그 시점의 `GetComponentInParent&lt;Canvas&gt;()` 는 null 이다 → 화면 영역이 영영 null 로 굳어
    /// 배경 이동량(BgStepHeight)이 0 이 되고, 최종 구름의 중앙 정렬 목표도 현재 위치와 같아져(이동량 0) 연출이 통째로 죽는다.
    /// → **사용 시점에 해석하고, 성공한 뒤에만 캐싱한다.**
    /// </summary>
    private RectTransform BgViewRect
    {
        get
        {
            if (null == bgViewRect)
            {
                bgViewRect = ResolveBgViewRect();
            }

            return bgViewRect;
        }
    }

    /// <summary>
    /// 인스펙터 바인딩(bgViewport) 우선, 없으면 루트 캔버스로 폴백.
    ///
    /// ⚠️ `bgPanel.parent`(Middle)를 쓰면 안 된다 — 화면보다 큰 패딩 컨테이너(stretch + sizeDelta.y)라
    /// 이동량 `h` 가 실제보다 작게 나와 최종 라운드에서도 배경 상단이 드러나지 않는다.
    /// </summary>
    private RectTransform ResolveBgViewRect()
    {
        if (null != bgViewport)
        {
            return bgViewport;
        }

        Canvas canvas = GetComponentInParent<Canvas>();
        Canvas rootCanvas = null != canvas ? canvas.rootCanvas : null;
        return null != rootCanvas ? rootCanvas.transform as RectTransform : null;
    }

    // 지정 이름 클립의 실제 길이. 애니메이터/클립 미구성 시 fallback.
    private float ResolveClipLength(string clipName, float fallback)
    {
        RuntimeAnimatorController controller = null != balloonAnimator ? balloonAnimator.runtimeAnimatorController : null;
        if (null == controller)
        {
            return fallback;
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

        return fallback;
    }

    // 트랙 구성 + 현재 단계 위치로 배치(§7-2).
    // 열기구를 먼저 좌우 정렬(고정 Y) → 앵커 전달 → 셀 생성/스크롤이 "현재 셀을 열기구에 정렬"하도록 한다.
    public void SetStages(List<DreamBalloonStageData> stages, int currentRound)
    {
        if (null == stageScroll)
        {
            return;
        }

        // [ISSUE-30] 최종 줌인 복원 — 팝업이 캐시 재사용되므로 트랙 재구축(=진입) 시점에 확대 상태를 되돌린다.
        //   연출 종료(OnCleanup)에서 되돌리면 줌인이 끝나자마자 원래 크기로 튀므로 여기서만 복원한다.
        ResetFinalZoom();

        // 순서 중요: 셀을 먼저 만들어야 GetStageAnchor(실제 노출된 구름)를 읽을 수 있다.
        // 열기구 X 는 그 앵커를 따라가므로 SetStages 이후에 정렬한다(이전에는 앵커 없는 상태로 홀짝 상수 폴백이 걸렸다).
        ResetBalloonHomeY();                         // ⚠️ ScrollToStage 정렬 기준은 열기구 홈 Y — 먼저 홈으로 복귀(직전 연출/구름 자식화 되돌림) 후 캐싱해야 트랙이 안 밀린다
        stageScroll.SetBalloonAnchor(balloonRect);   // 홈 위치를 정렬 기준으로 1회 캐싱(셀 생성 전에 필요)
        // 구름 등장(In) 재생 여부를 **셀 생성 전에** 확정해 데이터에 실어 준다.
        //   SetStages → BuildCells → SetInfo → RefreshView → ApplyCloudGaugeState 순서라, 여기서 미리 알려주지 않으면
        //   진행중 구름이 게이지 상태(Gauge_Idle)로 Play 되며 등장 애니를 덮어쓴다(등장 미재생).
        bool playCloudIntro = EventDreamBalloonHelper.GetContent()?.ConsumeCloudIntro() == true;
        if (null != stages)
        {
            int stageCount = stages.Count;
            for (int i = 0; i < stageCount; i++)
            {
                if (null != stages[i])
                {
                    stages[i].cloudIntro = playCloudIntro;
                }
            }
        }

        stageScroll.SetStages(stages, TrackAnchorRound(currentRound));   // 마지막 라운드는 스크롤하지 않는다(N-1 에서 멈춤)
        // 라운드 변경/재진입 시 셀 재생성으로 구름 등장(In)이 다시 재생되는 것 방지(ISSUE-13) — 이벤트 최초 시작에만 등장, 그 외엔 정착 스냅.
        if (!playCloudIntro)
        {
            stageScroll.SnapAllCloudsToIdle();
        }
        PositionBalloonAtRound(currentRound);                            // 스크롤 확정 후 배치 — 마지막 라운드는 구름 앵커 위
        AlignBgPanel(currentRound);
        AlignFinalCloud(currentRound);   // 스크롤·셀 확정 후 — 9라운드(N-1)부터 최종 구름을 최상단 라운드 구름 위 한 칸에 맞춰 하강(ISSUE-32)
        ShowPartnerAtStage(currentRound);   // 셀 생성 직후 = 앵커 확보 시점(§3-2). 열기구 노출/숨김도 여기서 상태별로 결정.
        // 열기구 등장(In)은 라운드 진행 연출(RunRecruitAsync)에서 매 라운드 시작 시 재생한다(쉬는중 숨김 → 진행 시작 재등장).
        stagedRound = currentRound;
    }

    /// <summary>
    /// 값만 갱신(재화 획득 등) — 셀을 재생성하지 않고 구름 게이지/상태만 반영한다(§3-4 ②).
    /// 라운드가 바뀌었거나 트랙 구조가 달라졌으면 전량 재생성(<see cref="SetStages"/>)으로 폴백한다.
    /// </summary>
    public void RefreshStages(List<DreamBalloonStageData> stages, int currentRound)
    {
        if (null == stageScroll)
        {
            return;
        }

        if (currentRound != stagedRound || !stageScroll.TryRefreshStages(stages))
        {
            SetStages(stages, currentRound);
        }
    }

    /// <summary>
    /// 내 파트너를 해당 단계 구름 위에 올린다(§3-2). 좌표를 잡지 않고 런타임 셀에 SetParent 하므로
    /// 구름이 스크롤로 움직여도 파트너가 따라 움직인다. 셀 미생성/미바인딩 시 no-op.
    ///
    /// 노출 상태(§7-1a): 진행중(2)·쉬는중(1) 만 표시하고, 미시작(0)·클리어(3)·종료(4) 는 숨긴다.
    /// 진행중이면 진행 단계, 쉬는중이면 진행 예정 단계 — 둘 다 currentRound 구름이라 앵커가 같다.
    /// </summary>
    public void ShowPartnerAtStage(int round)
    {
        if (null == partnerController || null == stageScroll)
        {
            return;
        }

        // ⚠️ 파트너·열기구의 "상태 기준 숨김"은 **대기 화면(idle) 규칙**이다. 연출 중에는 적용하지 않는다.
        //    결과는 연출보다 먼저 서버에 커밋되므로(성공 직후 state=쉬는중, 최종 성공 후 클리어),
        //    재생 중에 상태로 판단하면 연출이 시작되는 순간 열기구·파트너가 사라진다.
        //    연출 중 노출은 각 연출(탑승/하차/스냅)이 직접 관리한다.
        int state = EventDreamBalloonHelper.GetState();
        bool idle = !sequencePlaying;

        // 노출 상태: 진행중(2)·쉬는중(1)·**라운드 클리어(3)** 는 표시, 미시작(0)·종료(4)는 숨김.
        // ⚠️ state 3 을 숨김 대상에 두면 안 된다 — 3 은 '이벤트 완주'가 아니라 **매 라운드 통과 시** 내려오므로(서버 스펙 2026-07-19),
        //    라운드를 깰 때마다 파트너·열기구가 사라지고 이 메서드가 여기서 return 해 **대기 모션 판정 자체에 도달하지 못했다**
        //    (성공 후 재진입 시 Happy 미재생의 직접 원인 — 진단 로그로 확인).
        if (idle && state != EventDreamBalloonHelper.PROGRESS_STATE && state != EventDreamBalloonHelper.REST_STATE
            && state != EventDreamBalloonHelper.CLEAR_STATE)
        {
            partnerController.HideMyPartner();
            SetBalloonVisible(false);   // 미시작/종료 — 열기구 숨김
            return;
        }

        partnerController.ShowMyPartnerAt(stageScroll.GetPartnerAnchor(round));   // 구름 위 파트너 위치(우)
        if (idle)
        {
            partnerController.SetIdleMotion(ResolveIdleMotionGroup(state));
        }
        SetBalloonVisible(!idle || state == EventDreamBalloonHelper.PROGRESS_STATE);   // 대기 화면은 진행중만 노출 / 쉬는중 숨김
    }

    // ── 파트너 위치 안내 프로필(기획 §3-2) 지원 — 팝업이 stageScroll 을 직접 만지지 않도록 위임한다 ──────────

    /// <summary>스크롤 위치 변경 콜백 등록 — 파트너가 화면 밖으로 나갔는지 재판정하는 용도(§3-2). 미바인딩 시 no-op.</summary>
    public void SetStageScrollAction(Action action)
    {
        stageScroll?.SetScrollAction(action);
    }

    public void RemoveStageScrollAction()
    {
        stageScroll?.RemoveScrollAction();
    }

    /// <summary>해당 단계 구름(=파트너 위치)이 뷰포트 위쪽 밖인지. 미바인딩/셀 미생성 시 false.</summary>
    public bool IsStageAboveViewport(int round)
    {
        return true == stageScroll?.IsStageAboveViewport(round);
    }

    /// <summary>해당 단계 구름(=파트너 위치)이 뷰포트 아래쪽 밖인지. 미바인딩/셀 미생성 시 false.</summary>
    public bool IsStageBelowViewport(int round)
    {
        return true == stageScroll?.IsStageBelowViewport(round);
    }

    /// <summary>현재 단계로 스크롤해 파트너를 화면 안으로 되돌린다(안내 프로필 클릭, §3-2).</summary>
    public void ScrollToCurrentStage()
    {
        stageScroll?.ScrollToStage(EventDreamBalloonHelper.GetCurrentRound());
    }

    /// <summary>
    /// 대기 모션만 다시 평가한다 — 파트너 위치/부모(<see cref="ShowPartnerAtStage"/>)는 건드리지 않는다.
    /// 트랙 구조가 그대로인 상태 전이(결과 팝업에서 '잠시 쉬어가기' 선택 → 쉬는중 확정)에서 호출한다:
    /// 그 경로는 라운드가 바뀌지 않아 <c>RefreshStages</c> 가 <c>TryRefreshStages</c> 로 빠지므로
    /// <see cref="ShowPartnerAtStage"/> 가 호출되지 않고, 대기 모션이 낡은 채로 남는다(ISSUE-33 후속).
    /// 연출 재생 중에는 각 연출이 파트너 감정을 직접 구동하므로 no-op.
    /// </summary>
    public void RefreshPartnerIdleMotion()
    {
        if (null == partnerController || sequencePlaying)
        {
            return;
        }

        partnerController.SetIdleMotion(ResolveIdleMotionGroup(EventDreamBalloonHelper.GetState()));
    }

    /// <summary>
    /// 대기 화면(idle)에서 재생할 파트너 대기 모션 그룹(연출 중엔 각 연출이 감정을 직접 관리한다).
    /// 메인 팝업 활성 시 기준(기획 확정 2026-07-19):
    ///   라운드 성공 조건 달성 → 기뻐함(Motion_Happy_3) / 쉬는중 → 수면(§4-6 Emotion_Sleep) / 진행중·좌석소진 실패 → 표준 Idle.
    ///
    /// ⚠️ 성공 판정을 **쉬는중보다 먼저** 본다 — 서버가 라운드 성공 직후 곧바로 state=1(쉬는중)을 내려주기 때문에,
    ///   순서를 바꾸면 진급 대기(성공 연출 전)가 수면으로 새어 나간다.
    /// ⚠️ 쉬는중은 **성공 후/실패 후를 구분하지 않는다**(기획 확정) — 어떻게 들어왔든 휴식이면 수면이다.
    ///   재접속 복귀도 자연히 여기로 떨어진다(진급 대기가 없으므로).
    /// </summary>
    private int ResolveIdleMotionGroup(int state)
    {
        bool goalReached = EventDreamBalloonHelper.IsRoundGoalReached();
        bool clearPending = EventDreamBalloonHelper.IsRoundClearPending();

        int group;
        // 기뻐함(Motion_Happy_3)은 **진급 대기(성공 연출 직전)** 에만 — 목표 달성 직후(goalReached) 또는
        //  결과 확정 후 연출 대기(clearPending) 다. 라운드 클리어(state 3) 자체는 조건에 넣지 않는다.
        //  ⚠️ ISSUE-29 — 클리어 연출 도중 강제 종료 후 재접속하면 연출이 이미 끝났고(재진입이라 clearPending 소비 + 코인 소각으로
        //     goalReached/clearPending 모두 false), 대기 중인 연출도 없다. 이때는 '진급 대기'가 아니라 '다음 라운드 대기'
        //     (휴식과 동일)이므로 클리어(3)도 수면(REST)으로 정착시킨다 — 진급 대기가 살아 있을 때만 기뻐함이 유지된다.
        //     (이 메서드 주석의 "재접속 복귀도 자연히 여기로 떨어진다"는 원래 의도와 일치.)
        if (goalReached || clearPending)
        {
            group = EventDreamBalloonHelper.MOTION_GROUP_STAGE_SUCCESS;
        }
        else if (state == EventDreamBalloonHelper.REST_STATE || state == EventDreamBalloonHelper.CLEAR_STATE)
        {
            group = EventDreamBalloonHelper.MOTION_GROUP_REST;
        }
        else
        {
            group = 0;
        }

        return group;
    }

    // 열기구(HotAirBalloon) 노출 토글 — 쉬는중엔 숨기고(사용자 요구), 진행중·연출 시 보인다. 미바인딩 시 no-op.
    private void SetBalloonVisible(bool visible)
    {
        if (null != balloonRect)
        {
            balloonRect.gameObject.SetActive(visible);
        }
    }

    /// <summary>
    /// 트랙(ScrollView)이 따라 올라가는 상한 라운드 = <c>totalRound - 1</c>.
    ///
    /// **마지막 라운드(N)는 스크롤하지 않는다** — 트랙은 N-1 에 멈추고, 열기구가 화면에 이미 보이는 마지막 구름까지
    /// 직접 올라가 그 위에 머문다. 그래서 스크롤 기준 라운드는 한 칸 앞에서 클램프한다.
    /// (연출·즉시 정렬·재진입이 모두 이 규칙을 공유해야 팝업을 다시 열 때 트랙이 튀지 않는다.)
    /// </summary>
    private int TrackAnchorRound(int round)
    {
        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        return totalRound > 1 ? Mathf.Min(round, totalRound - 1) : round;
    }

    // 마지막 라운드에서는 열기구가 홈 Y 가 아니라 그 구름 위(앵커)에 떠 있다(위 규칙).
    private bool IsBalloonOnCloud(int round)
    {
        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        return totalRound > 1 && round >= totalRound;
    }

    // 카메라 이동(즉시) — 스크롤을 먼저 확정한 뒤 열기구를 배치한다.
    //   ⚠️ 순서 중요: 마지막 라운드의 열기구는 "구름 앵커" 기준이라, 스크롤로 구름 위치가 확정된 뒤에 읽어야 한다.
    //      또 스크롤 정렬 자체가 열기구 Y 를 기준점으로 쓰므로(TryGetTargetContentY), 먼저 홈 Y 로 되돌린다.
    // showPartner=false 는 하차 점프 연출처럼 파트너를 직접 배치할 때 사용(즉시 표시 억제).
    public void SnapToStage(int round, bool showPartner = true)
    {
        ResetBalloonHomeY();
        stageScroll?.SetBalloonAnchor(balloonRect);
        stageScroll?.ScrollToStage(TrackAnchorRound(round));
        AlignFinalCloud(round);   // ⚠️ 스크롤 확정 후 — 9라운드(N-1)부터 최상단 라운드 구름 기준으로 최종 구름 하강(ISSUE-32). 중앙 하강은 최종 연출(MoveFinalCloudToAsync)뿐.
        PositionBalloonAtRound(round);
        AlignBgPanel(round);
        if (showPartner)
        {
            ShowPartnerAtStage(round);   // 트랙 재배치 시 파트너도 해당 구름으로(§3-2)
        }
    }

    /// <summary>
    /// 라운드 1칸당 배경 이동량 — `h = (배경 높이 − 화면 높이) / 총 라운드`.
    /// 배경(§4-7 ⑦)은 화면보다 긴 세로 1장이라, 1단계에서 하단(하늘색) → 위로 올라가며 상단(밤하늘)이 드러난다.
    /// ⚠️ 분모가 `총 라운드`인 이유: 라운드 1~N 은 (N−1)칸만 이동하고, **마지막 1칸은 최종 보상 연출용으로 남긴다**
    ///    (최종 라운드 클리어 후 배경이 끝까지 내려가며 축제장/최종보상 영역을 드러냄, <see cref="BgFinalY"/>·<see cref="MoveBgToFinalAsync"/>).
    /// 이동 여력이 없거나(배경이 화면보다 작음) 단계가 1개뿐이면 0 → 이동 no-op.
    /// </summary>
    private float BgStepHeight()
    {
        RectTransform viewRect = BgViewRect;
        if (null == bgPanel || null == viewRect)
        {
            return 0f;
        }

        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        if (totalRound <= 1)
        {
            return 0f;
        }

        float travel = bgPanel.rect.height - viewRect.rect.height;
        return travel <= 0f ? 0f : travel / totalRound;
    }

    // 해당 라운드의 배경 Y — 라운드가 오를수록 배경이 아래로 내려간다(위쪽 밤하늘이 드러남). round 1-base.
    private float BgYAtRound(int round)
    {
        return bgStartY - BgStepHeight() * (Mathf.Max(1, round) - 1);
    }

    // 배경의 절대 끝 Y(상단 완전 노출) — 최종 보상 연출에서 도달한다. 라운드별 이동이 남겨둔 마지막 구간까지 모두 내려간 위치.
    private float BgFinalY()
    {
        RectTransform viewRect = BgViewRect;
        if (null == bgPanel || null == viewRect)
        {
            return bgStartY;
        }

        float travel = bgPanel.rect.height - viewRect.rect.height;
        return travel <= 0f ? bgStartY : bgStartY - travel;
    }

    // 배경 즉시 정렬(팝업 진입·트랙 재배치). 미바인딩 시 no-op.
    private void AlignBgPanel(int round)
    {
        if (null == bgPanel)
        {
            return;
        }

        KillBgMove();
        Vector2 pos = bgPanel.anchoredPosition;
        pos.y = BgYAtRound(round);
        bgPanel.anchoredPosition = pos;
    }

    // 배경 이동 연출(§4-4) — 트랙 스크롤과 동일 시간(upClipSec)으로 다음 라운드 위치까지 내려간다.
    // 스킵/파괴 시 킬 후 목표로 스냅(순수 view). 미바인딩·이동량 0 이면 no-op.
    private async UniTask MoveBgToRoundAsync(int round, float duration, CancellationToken token)
    {
        if (null == bgPanel || duration <= 0f || BgStepHeight() <= 0f)
        {
            return;
        }

        float targetY = BgYAtRound(round);

        KillBgMove();
        bgMoveTween = bgPanel.DOAnchorPosY(targetY, duration).SetEase(Ease.InOutSine).SetLink(bgPanel.gameObject);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(duration), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        catch (OperationCanceledException)
        {
            KillBgMove();
            Vector2 pos = bgPanel.anchoredPosition;
            pos.y = targetY;
            bgPanel.anchoredPosition = pos;
            throw;
        }
    }

    // 최종 보상 연출(§4-4 최종) — 배경을 절대 끝(BgFinalY)까지 내린다. 라운드별 이동이 남겨둔 마지막 구간을 모두 이동해
    //   축제장/최종보상 배경 상단을 드러낸다(사용자 요구). 열기구·Top 하강과 동일 시간(upClipSec)으로 동시 재생.
    /// <summary>
    /// 최종 상승(§4-4 최종)에서 배경이 도달할 Y — **트랙(카메라) 이동량에 비례**시킨다.
    /// </summary>
    /// <remarks>
    /// 라운드 이동은 한 칸당 <see cref="BgStepHeight"/> 만큼 움직이므로, 최종 상승도 "트랙이 몇 칸 내려왔는지"로 환산해
    /// 같은 비율을 적용한다. 고정으로 <see cref="BgFinalY"/> 까지만 가면, 라운드 1~N 이 travel 의 (N-1)/N 을 소모해
    /// 최종 상승에는 **잔여 1칸**만 남는다 — 트랙·구름은 여러 칸 내려오는데 배경만 한 칸 움직여 배경이 제자리처럼 보였다.
    /// 총 travel 을 넘지 않도록 <see cref="BgFinalY"/> 에서 클램프한다(그 이상 내리면 배경 아래가 비어 버린다).
    /// 환산 불가(스텝/셀 높이 미확보)면 기존 동작대로 끝까지 이동한다.
    /// </remarks>
    private float BgYAfterTrackDelta(float trackDeltaY)
    {
        if (null == bgPanel)
        {
            return bgStartY;
        }

        float step = BgStepHeight();
        float cellStep = null != stageScroll ? stageScroll.CellHeight : 0f;
        if (step <= 0f || cellStep <= 0f)
        {
            return BgFinalY();
        }

        float cells = Mathf.Abs(trackDeltaY) / cellStep;
        return Mathf.Max(BgFinalY(), bgPanel.anchoredPosition.y - step * cells);
    }

    // 최종 상승 배경 이동 — 트랙 이동량에 비례한 목표까지(BgYAfterTrackDelta). 시간·이징은 상승 연출과 공유(락스텝).
    private async UniTask MoveBgByTrackAsync(float trackDeltaY, float duration, CancellationToken token)
    {
        if (null == bgPanel || duration <= 0f)
        {
            return;
        }

        float targetY = BgYAfterTrackDelta(trackDeltaY);

        KillBgMove();
        bgMoveTween = bgPanel.DOAnchorPosY(targetY, duration).SetEase(Ease.InOutSine).SetLink(bgPanel.gameObject);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(duration), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        catch (OperationCanceledException)
        {
            KillBgMove();
            Vector2 pos = bgPanel.anchoredPosition;
            pos.y = targetY;
            bgPanel.anchoredPosition = pos;
            throw;
        }
    }

    private async UniTask MoveBgToFinalAsync(float duration, CancellationToken token)
    {
        if (null == bgPanel || duration <= 0f)
        {
            return;
        }

        float targetY = BgFinalY();

        KillBgMove();
        bgMoveTween = bgPanel.DOAnchorPosY(targetY, duration).SetEase(Ease.InOutSine).SetLink(bgPanel.gameObject);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(duration), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        catch (OperationCanceledException)
        {
            KillBgMove();
            Vector2 pos = bgPanel.anchoredPosition;
            pos.y = targetY;
            bgPanel.anchoredPosition = pos;
            throw;
        }
    }

    private void KillBgMove()
    {
        if (null == bgMoveTween)
        {
            return;
        }

        bgMoveTween.Kill();
        bgMoveTween = null;
    }

    /// <summary>
    /// 최종 구름(EventBalloonCloudFinal)이 팝업 중앙에 오도록 하는 그 오브젝트의 anchoredPosition.y
    /// (§4-4 최종 "최종 단계도 화면 중앙으로 내려옴").
    /// 현재 위치 기준 델타를 더하므로 멱등하다 — 이미 중앙이면 그대로 반환된다(스킵 스냅에서 재호출 안전).
    /// 미바인딩·화면 영역 미확보 시 홈 Y.
    /// </summary>
    private float FinalCloudCenteredY()
    {
        RectTransform cloudRect = FinalCloudRect;
        RectTransform viewRect = BgViewRect;
        if (null == cloudRect || null == viewRect || cloudRect.parent is not RectTransform parent)
        {
            return finalCloudHomeY;
        }

        Vector3 centerWorld = viewRect.TransformPoint(Vector3.zero);   // 화면 중앙(루트 캔버스 로컬 원점)
        float centerY = parent.InverseTransformPoint(centerWorld).y;
        float cloudY = parent.InverseTransformPoint(cloudRect.position).y;

        return cloudRect.anchoredPosition.y + (centerY - cloudY);
    }

    // 최종 구름 정렬(ISSUE-32) — **9라운드(= N-1) 진입 시부터** 최상단 라운드(N) 구름 위 "한 칸(셀 높이)"에 맞춰 내려온다(사용자 요청).
    // 그 전 라운드(1~N-2)는 홈(finalCloudHomeY) 고정 = 축제장이 화면 상단에 상시 노출(§3-2). 홈보다 위로는 올리지 않는다.
    // ⚠️ 스크롤 확정(ScrollToStage/SetStages) **이후**에 호출해야 round N 앵커가 새 위치를 반영한다. 최종 연출 하강은 별도(MoveFinalCloudToAsync).
    private void AlignFinalCloud(int currentRound)
    {
        RectTransform cloudRect = FinalCloudRect;
        if (null == cloudRect || cloudRect.parent is not RectTransform parent)
        {
            return;
        }

        KillFinalCloudMove();

        // 우선 홈으로 두고 로컬 Y 를 읽는다(앵커/피벗 무관 델타 계산 기준).
        Vector2 pos = cloudRect.anchoredPosition;
        pos.y = finalCloudHomeY;
        cloudRect.anchoredPosition = pos;

        // 9라운드(= 마지막 직전, N-1) 진입 시부터만 최종 구름이 내려온다(사용자 요청). 그 전 라운드는 홈 고정.
        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        if (totalRound <= 1 || currentRound < totalRound - 1)
        {
            return;
        }

        Transform topAnchor = null != stageScroll ? stageScroll.GetStageAnchor(totalRound) : null;
        if (null == topAnchor)
        {
            return;   // 셀 미생성/최상단 앵커 미확보 — 홈 고정
        }

        float homeLocalY = parent.InverseTransformPoint(cloudRect.position).y;
        float topRoundLocalY = parent.InverseTransformPoint(topAnchor.position).y;

        // 한 칸 간격 — 인접 구름의 **실제 앵커 간 거리**를 재서 쓴다. CellHeight 단독은 레이아웃 spacing/패딩 변경(ISSUE-34)에
        // 영향을 받을 수 있어, 트랙에 이미 배치된 두 구름에서 직접 재는 편이 항상 정확하다. 실측 실패 시에만 CellHeight 폴백.
        float step = stageScroll.CellHeight;
        Transform prevAnchor = stageScroll.GetStageAnchor(totalRound - 1);
        if (null != prevAnchor)
        {
            float measuredStep = Mathf.Abs(topRoundLocalY - parent.InverseTransformPoint(prevAnchor.position).y);
            if (measuredStep > 0f)
            {
                step = measuredStep;
            }
        }

        // 최상단 구름 위 한 칸 = 가상의 N+1 라운드 자리.
        // ⚠️ 최종 구름은 일반 구름과 크기·원점이 다른 별도 프리팹(EventDreamBalloon_StageFinal — 축제장이라 훨씬 크고
        //   스프라이트 여백도 다르다)이라, 계산상 한 칸이어도 화면에서 보이는 간격은 좁게 느껴진다.
        //   자체 열기구 앵커(AirBalloonTrans)에 맞추는 방식은 그 앵커가 축제장 **위쪽**에 있어 오히려 더 좁아진다(검증 완료).
        //   → 시각 간격은 규칙으로 유도하지 않고 인스펙터에서 조정한다(finalCloudGapOffsetY, 양수 = 위로 = 간격 넓힘).
        float gluedLocalY = topRoundLocalY + step + finalCloudGapOffsetY;

        // 홈보다 아래로 내려가야 할 때만 하강(홈보다 위로는 고정). anchoredPosition 델타 = 로컬 Y 델타(스케일 1).
        if (gluedLocalY < homeLocalY)
        {
            pos.y = finalCloudHomeY - (homeLocalY - gluedLocalY);
            cloudRect.anchoredPosition = pos;
        }
    }

    // 상위 라운드 전환 스크롤(성공 연출 순서2-b, 예: 8→9) 동안 최종 구름이 트랙(round N 구름)을 따라 **매 프레임 정렬**돼 함께 내려온다(ISSUE-32, 사용자 요청).
    // AlignFinalCloud 가 round N 앵커의 라이브 위치를 읽으므로, 스크롤로 round N 이 내려오면 최종 구름도 그만큼 부드럽게 하강한다(스냅 대신 스크롤과 동기).
    // 스킵/중단(token 취소) 시 즉시 종료 — 끝 상태(하강 위치)는 스킵 스냅 SnapStageSuccessEnd → SnapToStage → AlignFinalCloud 가 확정하므로 여기서 별도 스냅 불필요.
    private async UniTask FollowFinalCloudDuringScrollAsync(int round, float duration, CancellationToken token)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            AlignFinalCloud(round);
            await UniTask.Yield(PlayerLoopTiming.Update, token);
            elapsed += Time.deltaTime;
        }

        AlignFinalCloud(round);   // 스크롤 종료 프레임 위치 확정
    }

    // 최종 구름 하강 연출(§4-4 최종) — 열기구가 날아오는 동안 축제장이 팝업 중앙으로 내려온다.
    // 스킵/파괴 시 킬 후 목표로 스냅(순수 view).
    private async UniTask MoveFinalCloudToAsync(float targetY, float duration, CancellationToken token)
    {
        RectTransform cloudRect = FinalCloudRect;
        if (null == cloudRect || duration <= 0f)
        {
            return;
        }

        KillFinalCloudMove();
        finalCloudMoveTween = cloudRect.DOAnchorPosY(targetY, duration).SetEase(Ease.InOutSine).SetLink(cloudRect.gameObject);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(duration), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        catch (OperationCanceledException)
        {
            KillFinalCloudMove();
            Vector2 pos = cloudRect.anchoredPosition;
            pos.y = targetY;
            cloudRect.anchoredPosition = pos;
            throw;
        }
    }

    private void KillFinalCloudMove()
    {
        if (null == finalCloudMoveTween)
        {
            return;
        }

        finalCloudMoveTween.Kill();
        finalCloudMoveTween = null;
    }

    /// <summary>
    /// 최종 구름이 <paramref name="cloudY"/> 에 있을 때의 앵커 착지점(열기구 anchoredPosition)을 미리 구한다.
    ///
    /// 앵커(AirBalloonTrans/PartnerTrans)는 최종 구름의 자식이라 구름과 함께 움직인다. 열기구 이동과 구름 하강을
    /// 동시에 재생하려면 **도착 시점의** 앵커 위치가 필요하므로, 구름을 목표 위치에 잠깐 두고 읽은 뒤 원위치한다
    /// (같은 프레임 안에서 끝나 렌더에 영향이 없다). 미바인딩이면 현재 위치 그대로.
    /// </summary>
    private Vector2 ResolveAnchoredPosWithFinalCloudAt(float cloudY, Transform anchor)
    {
        RectTransform cloudRect = FinalCloudRect;
        if (null == cloudRect)
        {
            return AnchoredPosOf(anchor);
        }

        Vector2 saved = cloudRect.anchoredPosition;
        cloudRect.anchoredPosition = new Vector2(saved.x, cloudY);
        Vector2 result = AnchoredPosOf(anchor);
        cloudRect.anchoredPosition = saved;
        return result;
    }

    // 단계 성공 연출(§4-4): 파트너 탑승 → 열기구 상승("Up") + 다음 단계 구름으로 대각 이동(좌우=DOTween) + 트랙 스크롤(카메라 하강).
    //   남은 구름 있으면 상승+카메라 동시 / 없으면 상승만(기획 §4-4). 스킵 가능(SkipableBase).
    public UniTask PlayStageSuccessAsync(int round, int totalRound, CancellationToken ct = default)
    {
        return PlaySequenceAsync(
            token => RunStageSuccessAsync(round, totalRound, token),
            () => SnapStageSuccessEnd(round, totalRound),
            ct);
    }

    // 성공 연출 끝 상태 — 열기구·트랙·배경·파트너가 도착 구름(다음 단계, 없으면 현재 단계)에 정렬된 모습.
    private void SnapStageSuccessEnd(int round, int totalRound)
    {
        int endRound = round < totalRound ? round + 1 : round;

        partnerController?.KillJump();
        KillBalloonMove();
        SnapBalloonAnimToEnd();   // [ISSUE-35] 상승(Up) 클립도 끝 프레임으로 — 좌표만 맞추면 본이 뜬 채 남아 열기구가 구름 위로 어긋난다
        KillBgMove();

        DreamBalloonStageItem clearedStage = stageScroll?.GetStageItem(round);
        clearedStage?.SnapStarChangeClearEnd();   // 별 노랑→파랑 전환 이펙트도 끝 상태로
        clearedStage?.HidePortraits();             // 포트레이트 낙하 끝 상태 = 사라짐(스킵해도 남지 않는다, §4-4 순서2)

        // 열기구·스크롤·배경을 도착 구름으로 정렬. showPartner:false 로 두는 이유는 정상 재생 경로와 동일하다 —
        // 파트너는 하차(DisembarkAsync)로 도착 구름에 정착하며, ShowPartnerAtStage 는 열기구 노출을 state 기준으로
        // 재결정하기 때문에(성공 직후 서버 state = 쉬는중) 그대로 부르면 연출 끝에 열기구가 사라진다.
        SnapToStage(endRound, showPartner: false);

        partnerController?.ShowMyPartnerAt(stageScroll?.GetPartnerAnchor(endRound));   // 하차 완료 = 도착 구름 위
        SetBalloonVisible(true);   // 연출의 끝은 "열기구가 도착 구름에 있는" 상태 (쉬는중 숨김은 이후 상태 갱신이 처리)
    }

    private async UniTask RunStageSuccessAsync(int round, int totalRound, CancellationToken token)
    {
        // 연출은 현재 단계(round) → 다음 단계(round+1) 로 재생된다. 결과 저장(ResolveRoundResult→SyncFromServer)으로
        // 서버 라운드가 이미 다음으로 진행됐어도, 시작 위치를 round 로 강제 정렬해 열기구·파트너를 같은 구름에서 함께 상승시킨다(§4-4).
        SnapToStage(round);

        // 순서1: 파트너가 열기구에 탑승 — 파트너 위치(우)에서 열기구(좌)로 DOJump 점프 후 숨김(SetActive false).
        DreamBalloonStageItem clearedStage = stageScroll?.GetStageItem(round);
        if (null != partnerController)
        {
            // 파트너 스파인 로드 완료까지 대기 — 신선 진입(머지판 성공 후 팝업 오픈)에서 로드 레이스로 Happy 가 스킵되고
            // Idle 로 남는 것을 막는다(로드 완료 콜백 ApplyIdleMotion 뒤에 Happy 를 얹어 덮어쓴다).
            await partnerController.WaitUntilPartnerReadyAsync(token);

            // §4-4 순서1 — "기뻐하는 연출(Motion_Happy_3) 출력 후" 탑승. happy 클립이 끝난 뒤에 탑승 점프를 시작한다.
            float happySec = partnerController.PlayPartnerHappy();
            if (happySec > 0f)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(happySec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
            }

            SoundManager.Instance.PlaySound(SFX_BALLOON_BOARD);   // 캐릭터가 열기구에 올라탈 때(§4-4 순서1)
            await partnerController.BoardBalloonAsync(stageScroll?.GetStageAnchor(round), stageScroll?.GetPartnerAnchor(round), token);
        }

        // 순서2: 파트너를 뒤따라 구름 위 프로필의 절반(올림)이 열기구에 탑승한다(나머지 절반은 구름에 남아 순서4에서 낙하, §4-4).
        if (null != clearedStage)
        {
            SoundManager.Instance.PlaySound(SFX_BALLOON_BOARD);   // 프로필도 열기구에 올라탈 때(§4-4 순서2 · 1191)
            await clearedStage.BoardHalfIntoBalloonAsync(token);
        }

        TriggerBalloonUp();
        SoundManager.Instance.PlaySound(SFX_BALLOON_RISE);   // 열기구가 다음 구름으로 올라갈 때(§4-4 순서2)

        // 별 클리어 전환 + 깃발 + **포트레이트 낙하**(§4-4) — 통과한 구름(round)의 별이 노랑 → 파랑으로 바뀌고 깃발이 꽂히며,
        //   열기구에 타지 못하고 남은 프로필이 **그와 동시에** 구름 밑으로 떨어진다(사용자 확정 2026-07-20).
        //   구 구현은 낙하를 깃발 연출 종료 후(이동 완료 뒤)로 미뤘으나, 깃발과 한 박자로 보이도록 시작 시점을 맞춘다.
        //   상승 연출과도 **동시 진행**한다(여기서 await 하지 않는다 — 낙하는 반환값 없는 트리거라 대기 대상이 아니다).
        //   스킵/파괴 시 셀 쪽 finally 가 이펙트를 끄고, SnapStageSuccessEnd 가 끝 상태를 확정한다.
        if (null != clearedStage)
        {
            clearedStage.PlayStarChangeClearAsync(token).Forget();
            clearedStage.PlayRemainingPortraitFallDown();
        }

        bool hasNext = round < totalRound;
        if (hasNext)
        {
            int nextRound = round + 1;

            if (IsBalloonOnCloud(nextRound))
            {
                // 순서2-a: **마지막 라운드(N)로 올라가는 경우** — 트랙(ScrollView)은 움직이지 않는다.
                //   마지막 구름은 이미 화면에 보이므로, 카메라를 따라 올리는 대신 열기구가 그 구름까지 직접 올라간다(사용자 요구).
                //   따라서 X 만이 아니라 X·Y 로 이동한다(실패 연출과 동일 헬퍼). 배경은 라운드 기준으로 계속 이동한다.
                Vector2 finalCloudPos = AnchoredPosOf(stageScroll?.GetStageAnchor(nextRound));
                await UniTask.WhenAll(
                    UniTask.Delay(TimeSpan.FromSeconds(upClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token),
                    MoveBalloonToAsync(finalCloudPos, upClipSec, token),
                    MoveBgToRoundAsync(nextRound, upClipSec, token));
            }
            else
            {
                // 순서2-b: 다음 구름(round+1)의 실제 X 로 대각 상승. 열기구 이동 + 트랙 스크롤 + 배경 하강(파트너는 열기구 안=숨김).
                //   이동·스크롤·배경 이동 시간을 모두 Up 클립 길이에 맞춰 상승 애니가 끝나는 순간 도착하도록 한다.
                //
                // ⚠️ **X 만이 아니라 Y 도 홈으로 되돌린다**(1→2 에서 열기구가 구름에 파묻히던 버그).
                //   트랙 스크롤은 다음 구름을 **열기구 홈 Y**(SetBalloonAnchor 캐싱값)에 맞추므로, 이동이 끝나는 지점의
                //   열기구 Y 도 홈이어야 둘이 만난다. 그런데 이동 직전 TriggerBalloonUp 의 DetachBalloonToOverlay 는
                //   worldPositionStays:true 라 **직전 구름 앵커의 Y 를 그대로 물려받는다** — 라운드 1 은 트랙이 최하단
                //   클램프(ClampContentY)에 걸려 구름 1 이 홈 Y 보다 아래에 있으므로, 그 낮은 Y 가 이동 내내 유지돼
                //   열기구가 구름 아래로 파묻혔다(대기 중엔 구름 자식이라 어긋남이 보이지 않고, 연출 종료 시
                //   SnapToStage → ResetBalloonHomeY 가 홈으로 되돌려 "끝나면 제자리"로 보였다).
                //   라운드 2+ 는 클램프가 걸리지 않아 앵커 Y == 홈 Y 라 기존에도 정상이었고, 이 변경으로 동작이 바뀌지 않는다.
                float nextX = StageAnchoredX(nextRound);
                await UniTask.WhenAll(
                    UniTask.Delay(TimeSpan.FromSeconds(upClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token),
                    MoveBalloonToAsync(new Vector2(nextX, balloonHomeY), upClipSec, token),
                    MoveBgToRoundAsync(nextRound, upClipSec, token),
                    FollowFinalCloudDuringScrollAsync(nextRound, upClipSec, token),   // 트랙 스크롤에 맞춰 최종 구름도 함께 내려온다(ISSUE-32, 9라운드 진입 전환)
                    stageScroll != null
                        ? stageScroll.ScrollToStageAsync(nextRound, upClipSec, token)
                        : UniTask.CompletedTask);
            }

            // ※ 포트레이트 낙하(순서4)는 깃발과 함께 상승 시작 시점에 이미 트리거됐다(위 참조).

            // 순서3: 다음 구름(round+1)에 안착(파트너 즉시표시 억제) → 파트너가 열기구(좌)에서 파트너 위치(우)로 DOJump 하차.
            //   SnapToStage 가 위 두 경로의 끝 상태를 동일하게 확정한다(마지막 라운드면 스크롤 없이 열기구를 구름 위에 둔다).
            SnapToStage(round + 1, showPartner: false);
            SetArriveFx(true);   // 착지 이펙트 — 도착 구름 안착 순간(아트 "UP출력 후"). OnCleanup 이 소등
            if (null != partnerController)
            {
                await partnerController.DisembarkAsync(stageScroll?.GetStageAnchor(round + 1), stageScroll?.GetPartnerAnchor(round + 1), token);
            }
            await UniTask.Delay(TimeSpan.FromSeconds(SETTLE_HOLD_SEC), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        else
        {
            // 더 이상 올라갈 구름 없음 → 상승 연출만(기획 §4-4). 트랙 스크롤도, 배경 이동도 하지 않는다
            // (다음 구름이 없어 카메라가 따라 올라갈 대상 자체가 없다). 최종 단계는 PlayFinalAsync 로 별도 처리.
            await UniTask.Delay(TimeSpan.FromSeconds(upClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }

    }

    // 단계 실패 연출(§4-5): 파트너 미탑승 → 빈 열기구가 다음 단계로 상승(좌우=DOTween), 파트너는 구름에 남아 아쉬워함.
    //   재도전이므로 트랙 스크롤(카메라)은 하지 않는다(현재 단계 유지). 스킵 가능(SkipableBase).
    public UniTask PlayStageFailAsync(int round, CancellationToken ct = default)
    {
        return PlaySequenceAsync(
            token => RunStageFailAsync(round, token),
            () => SnapStageFailEnd(round),
            ct);
    }

    // 실패 연출 끝 상태 — 빈 열기구는 다음 구름 위에 가 있고, 트랙·파트너는 현재 단계 그대로(재도전) + 아쉬움 모션.
    //   성공과 달리 SnapToStage 를 쓰면 안 된다(열기구를 현재 단계로 되돌려버린다).
    private void SnapStageFailEnd(int round)
    {
        partnerController?.KillJump();
        KillBalloonMove();
        SnapBalloonAnimToEnd();   // [ISSUE-35] 상승(Up) 클립도 끝 프레임으로 — 좌표만 맞추면 본이 뜬 채 남는다

        stageScroll?.GetStageItem(round)?.HidePortraits();   // 포트레이트 탑승 끝 상태 = 사라짐(스킵해도 남지 않는다, §4-5 규칙 3)

        if (null != balloonRect)
        {
            // [ISSUE-36] 1~9 라운드는 빈 열기구가 다음 구름(round+1) 위에 도착한 상태 / 최종 라운드는 다음 트랙 구름이 없어
            //   최종 구름(EventBalloonCloudFinal)의 AirBalloonTrans 로 이동한 상태 — 스킵 끝상태도 재생 끝상태와 동일하게 맞춘다.
            Transform nextAnchor = stageScroll?.GetStageAnchor(round + 1);
            balloonRect.anchoredPosition = null != nextAnchor ? AnchoredPosOf(nextAnchor) : FinalFailBalloonEndPos();
        }

        partnerController?.PlayPartnerFail();
    }

    private async UniTask RunStageFailAsync(int round, CancellationToken token)
    {
        // 실패 연출은 파트너가 순서2(끝)에서야 슬픈 모션(Sullen)을 재생하므로, 시작 시 쉬는중 sleep 대기모션(§4-6)을 해제한다.
        //   결과 확정 직후 서버 state=쉬는중이라 ShowPartnerAtStage 가 sleep 을 걸어 둔 채 실패 연출이 시작될 수 있다(Emotion_Sleep 으로 시작되는 버그 방지).
        //   ⚠️ 성공/최종/모집 연출은 각자 파트너 모션(Happy/Joy/Surprise)을 즉시 재생하므로 이 해제가 불필요하며, OnPlayAsync 에서 전역 해제하면
        //      성공 후 대기 Happy·쉬는중 Sleep 까지 Idle 로 덮어써 버린다 → 실패 연출에서만 국소 해제한다.
        partnerController?.SetIdleMotion(0);

        // 실패 연출도 서버 진행과 무관하게 현재 단계(round)에서 시작 — 시작 위치를 강제 정렬(열기구=파트너 옆).
        SnapToStage(round);

        // 순서0(§4-5 · 규칙 3): 열기구가 날아가기 **전에** 경쟁자 포트레이트가 열기구에 탑승한다(내 파트너는 태우지 않는다).
        //   성공 시 파트너 탑승 점프와 동일한 DOJump — "프로필만 열기구에 탄 이후에 열기구가 다음 단계로 날라감"(기획 §4-5).
        DreamBalloonStageItem failedStage = stageScroll?.GetStageItem(round);
        if (null != failedStage)
        {
            SoundManager.Instance.PlaySound(SFX_BALLOON_BOARD);   // 프로필이 열기구에 올라탈 때(§4-5 순서1)
            await failedStage.BoardPortraitsIntoBalloonAsync(token);
        }

        // 순서1: 파트너를 태우지 않은 빈 열기구가 날아간다(§4-5).
        //   재도전이므로 트랙 스크롤(카메라)은 하지 않는다: 현재 단계를 그대로 유지한다.
        //
        // 파트너 스파인 로드 완료까지 대기 — 신선 진입(머지판 실패 후 팝업 오픈)에서 로드 레이스로 실패 모션이 스킵되고 Idle 로 남는 것 방지.
        //   ⚠️ **이동 시작 전에** 기다린다 — 모션을 이동과 동시에 재생하려면 이 시점에 이미 로드가 끝나 있어야 한다.
        if (null != partnerController)
        {
            await partnerController.WaitUntilPartnerReadyAsync(token);
        }

        TriggerBalloonUp();
        SoundManager.Instance.PlaySound(SFX_BALLOON_RISE);   // 빈 열기구가 다음 구름으로 올라갈 때(§4-5 순서2)

        // 파트너의 아쉬워하는 모션은 **열기구가 날아가기 시작하는 순간 함께** 재생한다(사용자 요구).
        //   구 구현은 이동이 끝난 뒤(도착 직후)에야 재생해, 열기구가 떠나는 동안 파트너가 무표정으로 서 있었다.
        //   `await` 하지 않는다 — 이동과 동시에 진행시키고, 남은 모션 시간만 아래에서 마저 기다린다.
        float failMotionSec = partnerController?.PlayPartnerFail() ?? 0f;
        float failMotionStartTime = Time.time;

        Transform nextAnchor = stageScroll?.GetStageAnchor(round + 1);
        if (null != nextAnchor)
        {
            // 다음 단계 구름(round+1)의 AirBalloonTrans 로 날아간다.
            //   X 만 이동하면 "오른쪽으로만 이동"해 구름에 도달하지 못한다 — 앵커의 X·Y 로 함께 이동한다(성공 연출과 동일 헬퍼).
            //   다음 구름이 화면에 있어 그 위로 날아간다.
            Vector2 nextBalloonPos = AnchoredPosOf(nextAnchor);
            await UniTask.WhenAll(
                UniTask.Delay(TimeSpan.FromSeconds(upClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token),
                MoveBalloonToAsync(nextBalloonPos, upClipSec, token));
        }
        else
        {
            // [ISSUE-36] 최종 라운드 실패 — 다음 트랙 구름(round+1)이 없다. Up 클립은 BoneHotAirBalloon 을 제자리로
            //   되돌리는 출렁임일 뿐 순수 상승이 없으므로, 이동 트윈 없이 대기만 하면 열기구가 날아가지 않는다(재발 원인).
            //   빈 열기구를 최종 구름(EventBalloonCloudFinal)의 AirBalloonTrans 로 올려보낸다.
            await UniTask.WhenAll(
                UniTask.Delay(TimeSpan.FromSeconds(upClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token),
                MoveBalloonToAsync(FinalFailBalloonEndPos(), upClipSec, token));
        }

        SetArriveFx(true);   // 착지 이펙트 — 빈 열기구가 다음 구름에 도착한 순간(아트 "UP출력 후"). OnCleanup 이 소등

        // 순서2: 위에서 이미 시작한 아쉬움 모션(Motion_Hurry_Up)의 **남은 시간만** 기다린 뒤 연출을 종료한다.
        //   PlayResultThenOpenAsync 가 이 연출을 await 하므로, 모션이 끝나야 실패 팝업이 출력된다(기획 §4-5 "연출 종료 후 실패 팝업 출력").
        //   이동(upClipSec)이 모션보다 길면 잔여가 0 이하 → 추가 지연 없이 즉시 종료한다.
        float failMotionRemainSec = failMotionSec - (Time.time - failMotionStartTime);
        if (failMotionRemainSec > 0f)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(failMotionRemainSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
    }

    /// <summary>
    /// 최종 단계 성공 연출(§4-4 최종): 파트너 탑승 → 열기구가 최종 구름(축제장)으로 상승·이동 → 파트너 하차.
    ///
    /// 최종 구름(<see cref="finalCloud"/> = EventBalloonCloudFinal)은 트랙 셀이 아니라 팝업에 놓인 고정 오브젝트라,
    /// 트랙 스크롤 대신 열기구를 그 구름의 앵커(AirBalloonTrans)로 직접 이동시킨다.
    /// 미바인딩 시 기존 동작(상승 연출만)으로 폴백한다. 스킵 가능(SkipableBase).
    /// </summary>
    public UniTask PlayFinalAsync(int round, CancellationToken ct = default)
    {
        return PlaySequenceAsync(
            token => RunFinalAsync(round, token),
            () => SnapFinalEnd(round),
            ct);
    }

    // 최종 연출 끝 상태 — 열기구는 최종 구름 위, 축제장(회전목마·관람차·무지개)은 이미 돌아가는 중,
    //   파트너는 최종 구름에 내려서 기뻐하는 모습. 최종 구름 미바인딩이면 상승만 하는 폴백이라 스냅할 것이 없다.
    private void SnapFinalEnd(int round)
    {
        partnerController?.KillJump();
        KillBalloonMove();
        SnapBalloonAnimToEnd();   // [ISSUE-35] 상승(Up) 클립도 끝 프레임으로. ⚠️ 아래 balloonAnchor 미확보 조기반환보다 위에 둔다(축제장 미바인딩 시에도 적용)
        KillFinalCloudMove();

        // [ISSUE-30] 스킵 끝 상태도 **줌인된 화면**이다(정상 재생과 동일). 열기구를 축제장에 붙인 뒤 끝 스케일로 점프한다.
        //   ⚠️ 순서 주의 — 아래에서 최종 구름 위치를 확정한 뒤 줌을 적용해야 기준점이 어긋나지 않는다(맨 끝에서 호출).

        // 이동 전 구간(별 전환·프로필)도 성공 연출과 동일하게 끝 상태로 — SnapStageSuccessEnd 와 같은 계약.
        DreamBalloonStageItem clearedStage = stageScroll?.GetStageItem(round);
        clearedStage?.SnapStarChangeClearEnd();   // 별 노랑→파랑 전환 이펙트 끝 상태
        clearedStage?.HidePortraits();            // 포트레이트 낙하 끝 상태 = 사라짐(스킵해도 남지 않는다)

        // 배경도 정상 재생과 **같은 기준**으로 스냅한다 — 남은 트랙 델타만큼 비례 환산(MoveBgByTrackAsync 와 동일 계약).
        //   배경·트랙·구름이 같은 시간·이징을 공유하므로 진행 비율이 같다 → 중간 스킵에서도 정확히 같은 끝 위치로 모이고,
        //   이미 끝까지 간 뒤 호출되면 델타 0 → 멱등하다. 최종 구름 미바인딩이면 기존대로 끝 위치(BgFinalY)로 스냅한다.
        KillBgMove();
        if (null != bgPanel)
        {
            RectTransform bgRefCloudRect = FinalCloudRect;
            Vector2 bgPos = bgPanel.anchoredPosition;
            bgPos.y = null != bgRefCloudRect
                ? BgYAfterTrackDelta(FinalCloudCenteredY() - bgRefCloudRect.anchoredPosition.y)
                : BgFinalY();
            bgPanel.anchoredPosition = bgPos;
        }

        Transform balloonAnchor = null != finalCloud ? finalCloud.AirBalloonTrans : null;
        if (null == balloonAnchor)
        {
            return;
        }

        // ⚠️ 최종 구름을 먼저 중앙으로 내린다. 앵커(AirBalloonTrans/PartnerTrans)가 그 구름의 자식이라,
        //    구름이 제자리에 있는 상태에서 열기구·파트너를 앵커에 맞추면 둘 다 엉뚱한 곳에 놓인다.
        RectTransform finalCloudRect = FinalCloudRect;
        if (null != finalCloudRect)
        {
            Vector2 pos = finalCloudRect.anchoredPosition;
            float centeredY = FinalCloudCenteredY();

            // 트랙도 최종 구름이 아직 못 내려온 거리만큼 함께 내려 끝 상태를 맞춘다(정상 재생과 동일한 락스텝).
            // **남은 델타**를 쓰므로 연출 중간에 스킵해도(둘이 같은 시간·이징이라 진행 비율이 같다) 정확히 끝 위치로 모이고,
            // 이미 끝까지 간 뒤 호출되면 델타 0 → no-op 이라 멱등하다.
            stageScroll?.MoveContentBy(centeredY - pos.y);

            pos.y = centeredY;
            finalCloudRect.anchoredPosition = pos;
        }

        if (null != balloonRect)
        {
            balloonRect.anchoredPosition = AnchoredPosOf(balloonAnchor);
        }

        finalCloud.SnapFinalStageEnd();   // 축제장 애니를 Idle_After(도착 완료) 로 점프

        partnerController?.ShowMyPartnerAt(finalCloud.PartnerTrans);   // 최종 구름에 하차한 상태로 정착
        partnerController?.PlayPartnerJoy();

        // [ISSUE-30] 줌인 끝 상태 — 최종 구름 위치·파트너 정착이 모두 확정된 **뒤**에 적용해야 기준점(worldRoot.position 대비 거리)이 맞다.
        AttachBalloonToFinalCloud();
        SnapFinalZoomEnd();
    }

    private async UniTask RunFinalAsync(int round, CancellationToken token)
    {
        // 연출 시작 위치를 마지막 구름으로 강제 정렬(서버 라운드가 이미 진행됐을 수 있다 — 성공 연출과 동일 규칙).
        SnapToStage(round);

        // 이 연출은 트랙을 Clamped 범위 밖으로 내린다 → 끝난 뒤 스크롤을 다시 켜면 안 된다(OnCleanup 참조).
        keepScrollLocked = true;

        Transform balloonAnchor = null != finalCloud ? finalCloud.AirBalloonTrans : null;
        Transform partnerAnchor = null != finalCloud ? finalCloud.PartnerTrans : null;

        // 이번에 클리어한 마지막 구름. **최종 구름으로 이동하기 전까지는 일반 성공 연출과 완전히 동일하게** 재생한다(사용자 확정).
        //   과거엔 이 셀을 조회조차 하지 않아 별 전환·깃발·프로필 처리가 통째로 빠져 있었다.
        DreamBalloonStageItem clearedStage = stageScroll?.GetStageItem(round);

        // 순서1: 파트너가 기뻐한 뒤(Motion_Happy_3) 열기구에 탑승 — RunStageSuccessAsync 순서1과 동일.
        if (null != partnerController)
        {
            // 파트너 스파인 로드 완료까지 대기 — 신선 진입(머지판 최종 성공 후 팝업 오픈)에서 로드 전 빈 스파인으로 탑승/기쁨 모션이 뜨는 것 방지.
            await partnerController.WaitUntilPartnerReadyAsync(token);

            float happySec = partnerController.PlayPartnerHappy();
            if (happySec > 0f)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(happySec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
            }

            SoundManager.Instance.PlaySound(SFX_BALLOON_BOARD);   // 파트너가 열기구에 올라탈 때(§4-4 최종 ①)
            await partnerController.BoardBalloonAsync(stageScroll?.GetStageAnchor(round), stageScroll?.GetPartnerAnchor(round), token);
        }

        // 순서1-b: 파트너를 뒤따라 구름 위 프로필의 절반(올림)이 탑승 — RunStageSuccessAsync 순서2와 동일.
        if (null != clearedStage)
        {
            SoundManager.Instance.PlaySound(SFX_BALLOON_BOARD);
            await clearedStage.BoardHalfIntoBalloonAsync(token);
        }

        TriggerBalloonUp();
        SoundManager.Instance.PlaySound(SFX_BALLOON_RISE);   // 열기구가 최종 구름으로 올라갈 때(§4-4 최종 ②)

        // 별 노랑 → 파랑 전환 + 깃발 + **포트레이트 낙하**(RunStageSuccessAsync 와 동일 시점 — 상승 시작과 동시).
        //   최종 라운드도 '라운드 클리어'이므로 일반 성공 연출과 같은 정책을 쓴다(두 경로가 갈라지면 최종만 구식으로 남는다).
        if (null != clearedStage)
        {
            clearedStage.PlayStarChangeClearAsync(token).Forget();
            clearedStage.PlayRemainingPortraitFallDown();
        }

        if (null == balloonAnchor)
        {
            // 최종 구름 미바인딩 → 상승 연출만(폴백).
            await UniTask.Delay(TimeSpan.FromSeconds(upClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
            return;
        }

        // 순서2: 열기구가 최종 구름으로 상승·이동 + **최종 구름(축제장)이 팝업 중앙으로 내려온다**(§4-4 최종
        //   "열기구가 최종 단계로 내려오며 최종 단계도 화면 중앙으로 내려옴"). 트랙 스크롤은 없다(다음 구름 없음).
        //
        //   ⚠️ AirBalloonTrans 는 최종 구름의 자식이라 구름이 내려오면 **함께 내려온다**. 그래서 착지점을 지금 좌표로 잡으면
        //      열기구가 구름을 놓친다 → 구름을 목표 위치에 잠깐 두고 앵커를 읽어 **도착 시점의 착지점**을 미리 구한다(같은 프레임, 화면 영향 없음).
        float cloudTargetY = FinalCloudCenteredY();
        Vector2 balloonTarget = ResolveAnchoredPosWithFinalCloudAt(cloudTargetY, balloonAnchor);

        // 최종 구름이 내려오는 **그 거리만큼 트랙(아래 단계 구름들)도 함께** 내려온다(사용자 요청).
        //   델타·시간·이징을 공유하므로 최종 구름과 아래 구름들의 간격이 유지된 채 화면 전체가 하강한다(락스텝).
        //   ⚠️ 트랙은 최종 라운드에서 이미 Clamped 상한이라 ScrollToStageAsync 로는 못 내린다 → 클램프 미적용 전용 API 를 쓴다.
        //   열기구 착지점(balloonTarget)은 최종 구름의 자식 앵커 기준이라 트랙 이동과 무관하다(영향 없음).
        RectTransform finalCloudRect = FinalCloudRect;
        float trackDeltaY = null != finalCloudRect ? cloudTargetY - finalCloudRect.anchoredPosition.y : 0f;

        await UniTask.WhenAll(
            UniTask.Delay(TimeSpan.FromSeconds(upClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token),
            MoveBalloonToAsync(balloonTarget, upClipSec, token),
            MoveFinalCloudToAsync(cloudTargetY, upClipSec, token),
            MoveBgByTrackAsync(trackDeltaY, upClipSec, token),   // 배경도 트랙 이동량에 비례해 내려간다(고정 1칸이면 배경만 제자리처럼 보인다)
            null != stageScroll
                ? stageScroll.MoveContentByAsync(trackDeltaY, upClipSec, token)
                : UniTask.CompletedTask);

        // ※ 깃발·포트레이트 낙하는 상승 시작 시점에 이미 트리거됐다(위 참조) — RunStageSuccessAsync 와 동일.

        SetArriveFx(true);   // 착지 이펙트 — 열기구가 최종 구름에 도착한 순간(아트 "UP출력 후"). OnCleanup 이 소등

        // 순서3: 도착 후 축제장이 살아난다 — 회전목마·관람차가 돌고 무지개가 뜬다(§4-4 최종).
        //   연출은 아트 Animator(DreamBalloonFestival_Last_Stage: Idle_Before →[Trigger_In]→ In →(exit)→ Idle_After)가 전담한다.
        //
        // ⚠️ **하차는 In 클립 종료를 기다리지 않는다**(사용자 확정 B안). 기획 §4-4 최종은 ③하차·기뻐함 → ④무지개 순서지만,
        //    무지개는 회전목마·관람차와 **한 클립에 묶여** 있어 무지개만 뒤로 뺄 수 없다. 대신 아트가 In 클립에 심어 둔
        //    Animation Event(OnFinalStageDisembarkCue)가 알려주는 시점에 하차해 **무지개가 뜨는 동안 파트너가 내려 기뻐하도록** 겹친다.
        //    → 하차 타이밍 조정은 아트가 클립 이벤트 위치만 옮기면 된다(코드 무수정).
        //    단 **연출 자체는 클립이 끝날 때까지 이어진다** — 최종 보상 팝업이 클립을 자르지 않도록 끝에서 잔여 시간을 기다린다(아래).
        SoundManager.Instance.PlaySound(SFX_FINAL_RAINBOW);   // 축제장이 살아나며 무지개가 뜰 때(§4-4 최종)
        float festivalStartTime = Time.time;
        float festivalSec = finalCloud.PlayFinalStageIn();
        await WaitFinalDisembarkCueAsync(festivalSec, token);

        // [ISSUE-30] 순서3-b: 하차 직전 **카메라 줌인**(기획 요청 — 첨부 박스 영역까지).
        //   ⚠️ 하차 점프와 **동시에 하지 않는다** — DisembarkAsync → JumpBetweenAsync 가 **월드 좌표 DOJump** 라,
        //      진행 중에 부모(축제장)가 스케일되면 목표 월드 위치가 계속 움직여 궤적이 흔들린다. 줌을 먼저 끝내고 하차한다.
        //   열기구를 축제장 앵커에 붙여(AttachBalloonToFinalCloud) 축제장·파트너·열기구가 한 덩어리로 확대되게 한다.
        AttachBalloonToFinalCloud();
        await PlayFinalZoomAsync(token);

        // 순서4: 파트너가 최종 구름 위로 하차 → 방방 뛰며 기뻐하고 연출 종료(§4-4 최종).
        if (null != partnerController)
        {
            SoundManager.Instance.PlaySound(SFX_BALLOON_DESCEND);   // 파트너가 열기구에서 내릴 때(§4-4 최종)
            finalCloud?.PlayFinalStageLandingFx();   // 착지 이펙트(Fx_Landing) — 하차 시작과 동시에 점등
            await partnerController.DisembarkAsync(balloonAnchor, partnerAnchor, token);
            SoundManager.Instance.PlaySound(SFX_FINAL_JOY);         // 파트너가 하차 후 기뻐할 때(§4-4 최종)
            partnerController.PlayPartnerJoy();
        }

        // 최종 보상 팝업(_Final)은 이 메서드가 끝나는 **즉시** 열린다(UIPopupEventDreamBalloon.PlayResultThenOpenAsync → OpenFinalPopup).
        // 하차 큐가 In 클립 중간(아트 지정 시점)에 오므로 하차·기쁨까지 마쳐도 클립이 아직 재생 중일 수 있고, 그대로 반환하면
        // **팝업이 축제장 연출을 덮어 잘라 버린다** → 클립 잔여 시간만큼 붙잡았다가 반환한다(사용자 요구).
        // 이미 클립이 끝난 뒤라면 잔여가 0 이하 → 즉시 반환(추가 지연 없음). 스킵 경로는 SnapFinalEnd 가 Idle_After 로 점프시켜 무관.
        float festivalRemainSec = festivalSec - (Time.time - festivalStartTime);
        if (festivalRemainSec > 0f)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(festivalRemainSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }

        // TODO[binding]: 최종 구름 앵커(AirBalloonTrans/PartnerTrans) 위치는 일반 구름 규칙을 복사한 임시값 — 아트 확인 후 조정.
    }

    // 최종 구름 도착 연출의 **하차 시점** 대기 — 아트가 In 클립에 심어 둔 Animation Event(OnFinalStageDisembarkCue) 수신 시 완료.
    // 폴백 2중(커튼 전환 WaitCurtainFinishAsync 과 동일 계약): 최종 구름 미바인딩 → 즉시 진행 /
    // 이벤트 유실(아트 클립 재발행으로 Function 명이 지워진 경우 등) → 클립 길이 + 여유 후 진행해 연출이 멈추지 않게 한다.
    private async UniTask WaitFinalDisembarkCueAsync(float festivalSec, CancellationToken token)
    {
        if (null == finalCloud)
        {
            return;
        }

        var cue = new UniTaskCompletionSource();
        Action onCue = () => cue.TrySetResult();

        finalCloud.FinalStageDisembarkCue += onCue;
        try
        {
            await UniTask.WhenAny(
                cue.Task.AttachExternalCancellation(token),
                UniTask.Delay(TimeSpan.FromSeconds(festivalSec + FINAL_CUE_FALLBACK_MARGIN_SEC),
                    DelayType.DeltaTime, PlayerLoopTiming.Update, token));
        }
        finally
        {
            finalCloud.FinalStageDisembarkCue -= onCue;
        }
    }

    /// <summary>
    /// 경쟁자 모집(게임 시작) 연출(§4-3): 열기구에서 파트너 친구들이 우르르 하차 → 내 파트너만 남고 → **START + Fx_NextRound**.
    /// 하차 인원 = ⌈4×(총단계−현재단계)/(총단계−1)⌉ (내 파트너는 항상 출력).
    ///
    /// **스킵 경계**: 모집 구간(등장·하차)만 스킵 대상이고, 마지막 비트인 **START 문구 + `Fx_NextRound` 는 스킵되지 않는다.**
    /// 아트 요청(952860690 「스테이지 시작시 StartText(DOTween) FxNextRound 같이 재생시켜주세요」)의 "스테이지 시작" 신호이자
    /// 라운드가 실제로 시작됐음을 알리는 유일한 피드백이라, 스킵으로 사라지면 시작 시점이 화면에서 증발한다.
    /// → <see cref="FlashStartAsync"/> 를 **스킵 가능 구간(<see cref="PlaySequenceAsync"/>) 밖**에 두어,
    ///   스킵하면 모집 구간만 끝 상태로 점프한 뒤 START 는 그대로 재생된다.
    ///
    /// 단, 다른 연출(라운드 결과)이 이 연출을 **중단(Cancel)** 한 경우엔 <see cref="PlaySequenceAsync"/> 가
    /// <see cref="OperationCanceledException"/> 을 던져 START 도 재생되지 않는다 — 라운드 시작이 결과 연출로 대체된 것이므로 의도된 동작이다.
    /// </summary>
    public async UniTask PlayRecruitAsync(int curRound, int totalRound, CancellationToken ct = default)
    {
        await PlaySequenceAsync(
            token => RunRecruitAsync(curRound, totalRound, token),
            () => SnapRecruitEnd(curRound),
            ct);

        // 스킵 불가 구간 — 파괴/팝업 닫힘에는 반응해야 하므로 호출측 토큰과 파괴 토큰을 함께 건다.
        using CancellationTokenSource startFlashCts = CancellationTokenSource.CreateLinkedTokenSource(ct, DestroyToken);
        await FlashStartAsync(startFlashCts.Token);
    }

    /// <summary>
    /// 재진입 복원(사용자 요구) — 메인 팝업을 껐다 켰을 때 진행중 라운드의 경쟁자 포트레이트를 **목표 위치에 정적 복원**한다.
    /// 모집 연출(열기구 Init → DOJump)은 라운드당 1회뿐이라, 재진입 시엔 애니 없이 착지 상태로 되돌린다.
    /// 경쟁자 수 게이트는 <see cref="DreamBalloonStageItem.SnapRecruitPortraitsEnd"/> 가 처리(최종 라운드 등 0명이면 미노출).
    /// </summary>
    public void RestorePortraitsAtTarget(int round)
    {
        stageScroll?.GetStageItem(round)?.SnapRecruitPortraitsEnd();
    }

    // 모집 구간(등장·하차) 끝 상태 — 친구는 모두 사라지고 내 파트너만 시작 구름 위에 남는다.
    //   스킵 시 여기로 점프한 직후 PlayRecruitAsync 가 START 를 재생한다(START 는 스킵 대상이 아니다).
    //   HideStartFlash 는 그 재생 직전의 초기화(멱등) — 이전 재생의 잔상이 남아 있지 않도록 확정한다.
    private void SnapRecruitEnd(int curRound)
    {
        partnerController?.KillJump();

        DreamBalloonStageItem stageItem = stageScroll?.GetStageItem(curRound);
        stageItem?.SnapStarChangeUnlockEnd();
        stageItem?.SnapGaugeIdle();   // 게이지 등장 연출의 끝 상태 = 정착(Gauge_Idle)
        stageItem?.SnapRecruitPortraitsEnd();   // 경쟁자 포트레이트는 착지 상태로 남는다(§4-3) — 스킵해도 사라지지 않는다
        HideStartFlash();
        ShowPartnerAtStage(curRound);   // 파트너를 시작 구름 위로

        // 라운드 시작 연출의 끝 = "열기구가 현재 라운드 구름에 등장해 있는" 상태 — 정상 재생(RunRecruitAsync)이 보장하는 것과 동일하게 맞춘다.
        SetBalloonVisible(true);
        SnapBalloonAnimToEnd();   // [ISSUE-35] 등장(In) 애니도 끝 프레임으로 — 좌표만 맞추면 열기구가 구름 밑에 그려진다
        PositionBalloonAtRound(curRound);
    }

    private async UniTask RunRecruitAsync(int curRound, int totalRound, CancellationToken token)
    {
        // §4-7: 쉬어가기(RequestRest)를 거쳐 재시작한 경우, 파트너가 놀라는 연출(Motion_Surprise_Happy)을 먼저 얹는다(1회 소비).
        //   RefreshTrack 이 이미 파트너를 시작 구름에 배치했으므로 감정만 재생한다. 그 외 라운드 시작(성공 후 바로 시작 등)은 미재생.
        if (EventDreamBalloonHelper.GetContent()?.ConsumeRoundStartFromRest() == true)
        {
            partnerController?.PlayPartnerSurprise();
        }

        // 라운드 진행 시작 — 열기구 애니 결정(사용자 요구):
        //   · 실패 재시작 / 메인 팝업 「다시하기」·「도전하기」(쉬는 후) / 최초 → **In(등장)** 트리거로 나타낸다.
        //   · 성공 후 바로 「다음 라운드 진행하기」 → 열기구가 이미 도착 구름에 있으므로 **Idle 유지**(등장 생략).
        bool playIn = EventDreamBalloonHelper.GetContent()?.ConsumeRoundStartPlayIn() ?? true;
        SetBalloonVisible(true);
        PositionBalloonAtRound(curRound);
        if (null != balloonAnimator)
        {
            if (playIn)
            {
                DetachBalloonToOverlay();   // In(등장) 애니는 오버레이 부모 기준(현행 연출) — 재생 중엔 구름 자식 아님
                balloonAnimator.SetTrigger(IN_TRIGGER);
                if (inClipSec > 0f)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(inClipSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
                }
                AttachBalloonToCloud(curRound);   // In 완료 → 구름 앵커에 안착(스크롤 추종)
            }
            else
            {
                balloonAnimator.SetTrigger(IDLE_TRIGGER);   // 성공 후 다음 라운드 — 등장 없이 Idle 유지
                AttachBalloonToCloud(curRound);
            }
        }

        // 파트너 컨트롤러가 없을 때만 쓰는 대기 시간 — 전체 모집 시간에서 뒤에 이어지는 START 구간을 뺀 만큼(§4-3 ~3초).
        float remainSec = Mathf.Max(0f, RECRUIT_SEC - startTextAnimSec);
        Transform stageAnchor = null != stageScroll ? stageScroll.GetPartnerAnchor(curRound) : null;   // 내 파트너는 파트너 위치(우) 기준
        DreamBalloonStageItem stageItem = null != stageScroll ? stageScroll.GetStageItem(curRound) : null;   // 친구 = 현재 구름 포트레이트

        // 별 잠금 해제 이펙트(§4-3) — 시작하는 구름의 별이 회색 → 노랑으로 풀리는 순간. 모집 연출과 동시 진행(await 하지 않음).
        stageItem?.PlayStarChangeUnlockAsync(token).Forget();

        // 재화 게이지 등장 연출 — **라운드를 완료하고 다음 라운드로 진행했을 때만** 재생한다(사용자 확정).
        //   위 열기구 분기의 playIn 이 그대로 반대 신호다: playIn == false ⇔ 성공 후 바로 다음 라운드(연속 진행).
        //   실패 재시작 / 쉬어가기 후 시작 / 최초 진입은 등장 없이 정착 상태로 노출된다
        //   (RefreshTrack → ApplyCloudGaugeState 가 Gauge_Idle 을 이미 적용).
        // 별 연출과 마찬가지로 모집 연출과 동시 진행(await 하지 않음).
        if (!playIn)
        {
            stageItem?.PlayGaugeInAsync(token).Forget();
        }

        if (null != partnerController && null != stageAnchor)
        {
            SoundManager.Instance.PlaySound(SFX_BALLOON_DESCEND);   // 열기구에서 파트너·프로필이 내릴 때(§4-3 순서1 / §4-7)
            await partnerController.PlayRecruitPartnersAsync(curRound, totalRound, stageAnchor, stageItem, token);
        }
        else
        {
            await UniTask.Delay(TimeSpan.FromSeconds(remainSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }

        // ⚠️ START 문구·Fx_NextRound 는 여기서 재생하지 않는다 — 스킵되면 안 되는 구간이라
        //    스킵 가능 시퀀스 밖(PlayRecruitAsync 말미)에서 재생한다. 자세한 이유는 PlayRecruitAsync 주석 참조.
    }

    /// <summary>
    /// 라운드 시작 신호 — **StartText 와 Fx_NextRound 를 같은 프레임에 함께 켜서** 재생한다
    /// (§4-3 순서7 · §4-8 ③ · 아트 952860690 「스테이지 시작시 StartText(DOTween) FxNextRound 같이 재생시켜주세요」).
    /// 스킵 대상이 아니다 — 호출 지점이 스킵 가능 구간 밖이다(<see cref="PlayRecruitAsync"/> 참조).
    ///
    /// ⚠️ **문구 연출은 코드가 만들지 않는다.** StartText 에는 아트가 `DOTweenAnimation` 을 얹어 두었고
    ///    (등장 Scale `isFrom` → 유지 → 퇴장 Scale·Fade), 이 컴포넌트들이 연출의 유일한 소스다.
    ///    구 구현은 코드가 `DOScale`/`DOFade` 로 직접 트윈을 돌린 뒤 <c>DOKill()</c> 로 정리했는데,
    ///    그 Kill 이 **아트 트윈까지 파괴**했다. `DOTweenAnimation` 은 트윈을 `Start()` 에서 **단 한 번** 만들므로
    ///    한 번 죽으면 다시 생성되지 않는다 → **라운드 1 이후 START 연출이 영영 재생되지 않았다.**
    ///    이제 코드는 **켜고 · 되감고 · 재시작하고 · 끄기만** 한다(Kill 금지).
    /// </summary>
    private async UniTask FlashStartAsync(CancellationToken token)
    {
        GameObject textObject = startText.gameObject;

        fxNextRound.SetActive(true);
        textObject.SetActive(true);
        SoundManager.Instance.PlaySound(SFX_START_TEXT);   // START 가 출력될 때(§4-3 순서5)
        RestartStartTextAnims();

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(startTextAnimSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        finally
        {
            HideStartFlash();   // 파괴/팝업 닫힘으로 취소돼도 문구·이펙트가 화면에 남지 않도록 정리
        }
    }

    /// <summary>
    /// 아트 authored 연출을 처음부터 재생한다. **최초 활성화(라운드 1) 프레임에는 아직 트윈이 없다** —
    /// `DOTweenAnimation` 이 자기 `Start()` 에서 만들고 `autoPlay` 로 스스로 재생하므로 그대로 두면 된다.
    /// 2회차부터는 완료된 채 남아 있는(`autoKill: 0`) 트윈을 되감아 재시작한다.
    /// </summary>
    private void RestartStartTextAnims()
    {
        int animCount = startTextAnims.Length;
        for (int i = 0; i < animCount; i++)
        {
            DOTweenAnimation anim = startTextAnims[i];
            if (null != anim.tween)
            {
                anim.DORestart();
            }
        }
    }

    /// <summary>
    /// START 문구·이펙트를 감춘다(연출 종료·취소·스냅 공통). 멱등.
    ///
    /// ⚠️ **아트 트윈을 `DOKill()` 하지 않는다** — `DOTweenAnimation` 은 트윈을 `Start()` 에서 한 번만 만들므로
    ///    죽이면 다음 라운드에 재생할 트윈이 사라진다(<see cref="FlashStartAsync"/> 주석). 대신 **되감아(DORewind)**
    ///    시작 상태(스케일·알파)로 되돌려 두고 GO 만 끈다 → 다음 라운드에 `DORestart` 로 그대로 다시 쓴다.
    /// </summary>
    private void HideStartFlash()
    {
        if (null != startText)
        {
            int animCount = startTextAnims.Length;
            for (int i = 0; i < animCount; i++)
            {
                DOTweenAnimation anim = startTextAnims[i];
                if (null != anim.tween)
                {
                    anim.DORewind();
                }
            }

            startText.gameObject.SetActive(false);
        }

        if (null != fxNextRound)
        {
            fxNextRound.SetActive(false);
        }
    }

    /// <summary>
    /// [ISSUE-35] 열기구 애니를 **끝 프레임**으로 밀어 정착시킨다. 네 스냅 핸들러가 공통으로 호출한다.
    /// </summary>
    /// <remarks>
    /// 등장(In)·상승(Up) 클립은 열기구 RectTransform 이 아니라 **내부 본(BoneHotAirBalloon)의 AnchoredPosition.y**
    /// 를 움직인다 — In 은 -1121 → 71(길이 2.37초), Up 은 → 437 → 47 → 71(길이 3.53초). 그래서 스킵으로 클립이
    /// 중간에 멈추면 좌표(<see cref="PositionBalloonAtRound"/>)는 구름에 맞춰져도 **그려지는 열기구만 어긋난 채 남는다**
    /// (In=구름 밑 / Up=구름 위). 애니는 스킵으로 멈추지 않으므로 클립이 끝나면 자연 복구되지만,
    /// 그때까지 최대 2~3.5초간 어긋난 화면이 그대로 노출된다.
    ///
    /// 상태명이 아니라 **현재 상태를 마지막 프레임으로** 미는 이유: 이 컨트롤러의 DefaultState 가 모션 없는
    /// `New State` 이고(그 외 Idle/Up/In), 어느 클립을 타다 멈췄는지에 따라 목표 상태가 달라지기 때문이다.
    /// 현재 상태를 그대로 끝으로 밀면 In·Up 어느 쪽이든 정착 프레임(본 y=71)으로 수렴한다
    /// (<c>DreamBalloonStageItem.SnapCloudToIdle</c> 과 동일 계약).
    /// In·Up 모두 HasExitTime 이라(각 0.929 / 0.894) 끝으로 민 직후 다음 갱신에 Idle 로 자연 전이된다.
    /// </remarks>
    private void SnapBalloonAnimToEnd()
    {
        if (null == balloonAnimator)
        {
            return;
        }

        balloonAnimator.ResetTrigger(IN_TRIGGER);   // 아직 소비되지 않은 등장 트리거가 남아 다시 도는 것 방지
        balloonAnimator.Update(0f);                 // 트리거 직후 스킵 등 상태 전이 미반영 구간 보정

        int layerCount = balloonAnimator.layerCount;
        for (int layer = 0; layer < layerCount; layer++)
        {
            AnimatorStateInfo state = balloonAnimator.GetCurrentAnimatorStateInfo(layer);
            balloonAnimator.Play(state.fullPathHash, layer, 1f);
        }

        balloonAnimator.Update(0f);   // 스냅 즉시 적용
    }

    /// <summary>
    /// 열기구를 해당 단계(round) 구름의 X 위치 + 화면 고정 Y(홈)로 즉시 정렬. round 1-base.
    ///
    /// ⚠️ Y 도 반드시 되돌린다. 실패 연출(<see cref="MoveBalloonToAsync"/>)은 열기구를 다음 구름 앵커로 **X·Y 모두** 옮기는데,
    ///    트랙 스크롤(<c>ScrollToStage</c>)이 "구름을 열기구의 현재 Y 에 맞추는" 방식이라 열기구 Y 가 한 칸 올라간 채 남으면
    ///    다음 정렬에서 트랙이 한 칸 밀려 올라간다(실패·재시작을 반복할수록 누적).
    /// </summary>
    private void PositionBalloonAtRound(int round)
    {
        if (null == balloonRect)
        {
            return;
        }

        KillBalloonMove();
        DetachBalloonToOverlay();   // 오버레이 좌표로 위치 계산 — 구름 자식 상태면 먼저 복귀

        // 마지막 라운드는 트랙이 따라 올라가지 않으므로(TrackAnchorRound), 열기구가 홈 Y 가 아니라 그 구름 위에 떠 있다.
        if (IsBalloonOnCloud(round))
        {
            balloonRect.anchoredPosition = AnchoredPosOf(stageScroll?.GetStageAnchor(round));
        }
        else
        {
            balloonRect.anchoredPosition = new Vector2(StageAnchoredX(round), balloonHomeY);
        }

        AttachBalloonToCloud(round);   // idle 안착 = 구름 앵커(AirBalloonTrans)의 자식으로(스크롤 추종, §7-2)
    }

    // 열기구를 현재 구름 앵커(AirBalloonTrans = GetStageAnchor)의 자식으로 붙여 스크롤을 따라가게 한다(idle §7-2, 파트너 AttachTo 대응).
    // 앵커 미확보(셀 미생성 등) 시 no-op — 오버레이에 남는다.
    private void AttachBalloonToCloud(int round)
    {
        if (null == balloonRect)
        {
            return;
        }

        Transform anchor = stageScroll?.GetStageAnchor(round);
        if (null == anchor)
        {
            return;
        }

        balloonRect.SetParent(anchor, false);
        balloonRect.anchoredPosition = Vector2.zero;   // AirBalloonTrans 위치에 정확히 안착
    }

    // [ISSUE-30] 열기구를 **최종 구름의 열기구 앵커** 자식으로 붙인다 — 줌인 시 축제장과 함께 확대되도록.
    //   트랙 구름용 AttachBalloonToCloud 와 같은 계약(SetParent + anchoredPosition 0)이며, 앵커 출처만 finalCloud 다.
    private void AttachBalloonToFinalCloud()
    {
        if (null == balloonRect || null == finalCloud)
        {
            return;
        }

        Transform anchor = finalCloud.AirBalloonTrans;
        if (null == anchor)
        {
            return;
        }

        balloonRect.SetParent(anchor, false);
        balloonRect.anchoredPosition = Vector2.zero;
    }

    /// <summary>
    /// [ISSUE-30] 최종 단계 줌인 — <see cref="worldRoot"/>(배경·트랙)와 <see cref="finalCloud"/>(축제장·파트너·열기구)를
    /// **같은 월드 점 기준**으로 함께 확대한다. 기준점은 `worldRoot.position`(pivot 월드 위치)이며,
    /// worldRoot 는 자기 pivot 기준으로 커지므로 스케일만 주면 되고, 부모가 다른 finalCloud 는 위치까지 보정한다.
    ///
    /// 원래 값(스케일·위치)은 <see cref="ResetFinalZoom"/> 이 되돌린다 — 팝업이 캐시 재사용되므로 복원이 없으면 재진입 시 줌인 상태로 열린다.
    /// </summary>
    private async UniTask PlayFinalZoomAsync(CancellationToken token)
    {
        RectTransform finalRect = FinalCloudRect;
        if (finalZoomScale <= 1f || (null == worldRoot && null == finalRect))
        {
            return;
        }

        CaptureFinalZoomOrigin();

        Vector3 centerWorld = null != worldRoot ? worldRoot.position : (null != finalRect ? finalRect.position : Vector3.zero);
        float k = finalZoomScale;

        KillFinalZoom();
        if (null != worldRoot)
        {
            finalZoomWorldTween = worldRoot.DOScale(zoomOriginWorldScale * k, finalZoomSec).SetEase(Ease.InOutSine).SetLink(worldRoot.gameObject);
        }

        if (null != finalRect)
        {
            // 축제장은 부모(Top)가 달라 pivot 기준 스케일만으로는 기준점이 어긋난다 → 기준점 대비 거리도 k 배로 민다.
            Vector3 targetWorldPos = centerWorld + (zoomOriginFinalWorldPos - centerWorld) * k;
            finalZoomCloudScaleTween = finalRect.DOScale(zoomOriginFinalScale * k, finalZoomSec).SetEase(Ease.InOutSine).SetLink(finalRect.gameObject);
            finalZoomCloudMoveTween = finalRect.DOMove(targetWorldPos, finalZoomSec).SetEase(Ease.InOutSine).SetLink(finalRect.gameObject);
        }

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(finalZoomSec), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        catch (OperationCanceledException)
        {
            SnapFinalZoomEnd();
            throw;
        }
    }

    // 줌 끝 상태로 즉시 점프(스킵). 멱등 — 이미 끝 상태면 같은 값을 다시 쓴다.
    private void SnapFinalZoomEnd()
    {
        RectTransform finalRect = FinalCloudRect;
        if (finalZoomScale <= 1f || (null == worldRoot && null == finalRect))
        {
            return;
        }

        CaptureFinalZoomOrigin();
        KillFinalZoom();

        Vector3 centerWorld = null != worldRoot ? worldRoot.position : (null != finalRect ? finalRect.position : Vector3.zero);
        float k = finalZoomScale;

        if (null != worldRoot)
        {
            worldRoot.localScale = zoomOriginWorldScale * k;
        }

        if (null != finalRect)
        {
            finalRect.localScale = zoomOriginFinalScale * k;
            finalRect.position = centerWorld + (zoomOriginFinalWorldPos - centerWorld) * k;
        }
    }

    /// <summary>
    /// 줌을 원래 상태로 되돌린다. **팝업 캐시 재사용 대비 필수** — 복원하지 않으면 재진입 시 확대된 채로 열린다.
    /// ⚠️ 연출 종료(OnCleanup)에서는 부르지 않는다 — 최종 연출은 줌인 상태로 끝나고 그 위로 최종 보상 팝업(_Final)이 열린다.
    ///    되돌리는 시점은 **다음 진입(트랙 재구축 SetStages)** 과 최종 연출 시작 지점이다.
    /// </summary>
    private void ResetFinalZoom()
    {
        KillFinalZoom();

        if (!hasFinalZoomOrigin)
        {
            return;
        }

        if (null != worldRoot)
        {
            worldRoot.localScale = zoomOriginWorldScale;
        }

        RectTransform finalRect = FinalCloudRect;
        if (null != finalRect)
        {
            finalRect.localScale = zoomOriginFinalScale;
            finalRect.anchoredPosition = zoomOriginFinalAnchoredPos;
        }

        hasFinalZoomOrigin = false;
    }

    // 줌 시작 전 원본 값 1회 캡처(재진입·스킵에서 중복 캡처 방지 — 확대된 값을 원본으로 덮어쓰면 복원이 깨진다).
    private void CaptureFinalZoomOrigin()
    {
        if (hasFinalZoomOrigin)
        {
            return;
        }

        RectTransform finalRect = FinalCloudRect;
        zoomOriginWorldScale = null != worldRoot ? worldRoot.localScale : Vector3.one;
        zoomOriginFinalScale = null != finalRect ? finalRect.localScale : Vector3.one;
        zoomOriginFinalAnchoredPos = null != finalRect ? finalRect.anchoredPosition : Vector2.zero;
        zoomOriginFinalWorldPos = null != finalRect ? finalRect.position : Vector3.zero;
        hasFinalZoomOrigin = true;
    }

    private void KillFinalZoom()
    {
        finalZoomWorldTween?.Kill();
        finalZoomWorldTween = null;
        finalZoomCloudScaleTween?.Kill();
        finalZoomCloudScaleTween = null;
        finalZoomCloudMoveTween?.Kill();
        finalZoomCloudMoveTween = null;
    }

    // 열기구를 원래 오버레이 부모로 되돌린다(이동·연출용). worldPositionStays=true 로 시각적 점프 없이 복귀.
    private void DetachBalloonToOverlay()
    {
        if (null == balloonRect || null == balloonHomeParent || balloonRect.parent == balloonHomeParent)
        {
            return;
        }

        balloonRect.SetParent(balloonHomeParent, true);
    }

    // 열기구 Y 만 홈으로 복귀(X 는 셀 앵커가 필요해 셀 생성 뒤에 맞춘다). 트랙 스크롤 정렬 전에 호출한다.
    private void ResetBalloonHomeY()
    {
        if (null == balloonRect)
        {
            return;
        }

        KillBalloonMove();
        DetachBalloonToOverlay();   // 홈 Y 는 오버레이 부모 좌표 기준 — 구름 자식 상태면 먼저 복귀
        Vector2 pos = balloonRect.anchoredPosition;
        pos.y = balloonHomeY;
        balloonRect.anchoredPosition = pos;
    }

    /// <summary>
    /// 해당 단계(round) 구름의 실제 X 를 열기구 부모 좌표계의 anchoredPosition.x 로 환산한다.
    ///
    /// ⚠️ 상수(leftStageX/rightStageX)로 좌우를 추정하지 않는다 — 셀 프리팹의 `Balloon_Left`/`Balloon_Right`는
    /// **이름과 실제 X 가 반대**(Left=+250 / Right=-250)여서 홀짝 규칙으로 계산하면 열기구가 반대편에 놓인다.
    /// 트랙이 실제로 활성화한 구름(<see cref="DreamBalloonStageLoopScroll.GetStageAnchor"/>)의 위치를 그대로 따른다.
    /// 셀 미생성 등으로 앵커가 없으면 홀짝 상수 폴백.
    /// </summary>
    private float StageAnchoredX(int round)
    {
        // 미시작(state 0)이면 currentRound == 0 으로 내려온다 → 1단계 기준으로 보정(트랙도 idx 0 으로 클램프).
        int safeRound = Mathf.Max(1, round);

        Transform anchor = null != stageScroll ? stageScroll.GetStageAnchor(safeRound) : null;
        if (null == anchor || null == balloonRect || balloonRect.parent is not RectTransform parent)
        {
            return SideX(safeRound - 1);
        }

        // 월드 X → 열기구 부모 로컬 X. anchoredPosition 과 localPosition 의 차(앵커 오프셋)를 보정해 앵커 설정과 무관하게 동작한다.
        float localX = parent.InverseTransformPoint(anchor.position).x;
        return balloonRect.anchoredPosition.x + (localX - balloonRect.localPosition.x);
    }

    // 폴백 전용 — 배열 index 홀짝 → 열기구 X. (셀 앵커를 못 얻은 경우에만 사용)
    private float SideX(int index)
    {
        return (index & 1) == 0 ? leftStageX : rightStageX;
    }

    /// <summary>
    /// 앵커(월드 위치) → 열기구 부모 좌표계의 anchoredPosition(X·Y 모두). <see cref="StageAnchoredX"/> 의 2축 버전으로,
    /// 트랙 밖 고정 오브젝트(최종 구름)로 이동할 때 사용한다. 앵커/열기구 미확보 시 현재 위치를 유지한다.
    /// </summary>
    private Vector2 AnchoredPosOf(Transform anchor)
    {
        if (null == balloonRect)
        {
            return Vector2.zero;
        }

        Vector2 current = balloonRect.anchoredPosition;
        if (null == anchor || balloonRect.parent is not RectTransform parent)
        {
            return current;
        }

        // anchoredPosition 과 localPosition 의 차(앵커 오프셋)를 보정해 앵커 설정과 무관하게 동작한다.
        Vector3 local = parent.InverseTransformPoint(anchor.position);
        Vector2 offset = current - (Vector2)balloonRect.localPosition;
        return new Vector2(local.x, local.y) + offset;
    }

    // [ISSUE-36] 최종 라운드 실패 시 빈 열기구가 이동할 위치 — 최종 구름(EventBalloonCloudFinal)의 AirBalloonTrans.
    //   다음 트랙 구름(round+1)이 없어, 화면 밖으로 내보내는 대신 축제장(최종 구름)의 열기구 앵커로 올려보낸다.
    //   Up 클립은 BoneHotAirBalloon 을 제자리로 되돌리는 출렁임일 뿐 순수 상승이 없어, 이동 트윈이 없으면 열기구가 날아가지 않는다.
    //   앵커 미확보 시 현재 위치 유지(AnchoredPosOf 규약) → 이동 no-op.
    private Vector2 FinalFailBalloonEndPos()
    {
        return AnchoredPosOf(null != finalCloud ? finalCloud.AirBalloonTrans : null);
    }

    /// <summary>
    /// 열기구 이동(X·Y) — DOTween. 스킵/파괴 시 트윈만 킬한다(순수 view).
    ///
    /// ⚠️ [ISSUE-35] **여기서 좌표를 쓰면 안 된다.** <see cref="SkipableBase.Skip"/> 는 `OnSkipToEnd()` → `skipCts.Cancel()` 순서라,
    ///    이 catch 는 스냅 핸들러(SnapStageSuccessEnd/SnapFinalEnd)가 <see cref="AttachBalloonToCloud"/>·
    ///    <see cref="AttachBalloonToFinalCloud"/> 로 **열기구를 구름 앵커 자식으로 재부모화한 뒤에** 실행된다.
    ///    그 시점에 오버레이 기준 좌표인 target 을 쓰면 구름 앵커 로컬로 재해석돼 열기구가 구름 밑으로 튀었다.
    ///    끝 상태(위치·부모)는 네 스냅 핸들러가 단독으로 확정한다 — 이동 헬퍼는 관여하지 않는다(트랙 스크롤과 동일 계약).
    /// </summary>
    private async UniTask MoveBalloonToAsync(Vector2 target, float duration, CancellationToken token)
    {
        if (null == balloonRect || duration <= 0f)
        {
            return;
        }

        DetachBalloonToOverlay();   // 이동은 오버레이 좌표(현행 연출) — 구름 자식이면 복귀
        KillBalloonMove();
        balloonMoveTween = balloonRect.DOAnchorPos(target, duration).SetEase(Ease.InOutSine).SetLink(balloonRect.gameObject);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(duration), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        catch (OperationCanceledException)
        {
            KillBalloonMove();   // 좌표 스냅 금지(위 주석 ISSUE-35) — 끝 상태는 스냅 핸들러가 확정한다
            throw;
        }
    }

    // 열기구 X 이동(좌우) — DOTween. 스킵/파괴 시 트윈만 킬한다(좌표 스냅 금지 — MoveBalloonToAsync 주석 ISSUE-35 와 동일 계약).
    private async UniTask MoveBalloonXAsync(float targetX, float duration, CancellationToken token)
    {
        if (null == balloonRect || duration <= 0f)
        {
            return;
        }

        DetachBalloonToOverlay();   // 이동은 오버레이 좌표(현행 연출) — 구름 자식이면 복귀
        KillBalloonMove();
        balloonMoveTween = balloonRect.DOAnchorPosX(targetX, duration).SetEase(Ease.InOutSine).SetLink(balloonRect.gameObject);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(duration), DelayType.DeltaTime, PlayerLoopTiming.Update, token);
        }
        catch (OperationCanceledException)
        {
            KillBalloonMove();
            throw;
        }
    }

    private void KillBalloonMove()
    {
        if (null == balloonMoveTween)
        {
            return;
        }

        balloonMoveTween.Kill();
        balloonMoveTween = null;
    }

    private void TriggerBalloonUp()
    {
        DetachBalloonToOverlay();   // Up 애니는 오버레이 부모 기준(현행 연출) — 구름 자식이면 복귀
        if (null == balloonAnimator)
        {
            return;
        }

        balloonAnimator.SetTrigger(UP_TRIGGER);
    }

}

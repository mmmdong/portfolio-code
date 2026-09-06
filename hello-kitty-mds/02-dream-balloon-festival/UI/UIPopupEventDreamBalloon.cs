using System;
using System.Collections.Generic;

using ACTGames.Content.Helper;

using GameCore.Utils;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using GameCore.Skip;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;
using GameLogic.Management.UISupport.UIEvent.UIEventSupport;

using StatefulUISupport.Scripts.Components;

using UnityEngine;

// 드림 벌룬 페스티벌 — 진행(main) 팝업 (구현 명세서 §3-4·§5-1, 프리팹 UIPopupEventDreamBalloon)
// 진입 컨테이너. 성공/실패/최종 연출을 이 팝업 위에서 재생 후 결과 팝업(_Success/_Fail/_Final)을 오버레이한다(§5-1).
// 좌석 시뮬(SeatChangedMsg)·판정(StageResultMsg) 구독으로 게이지/순위 갱신 및 연출→결과 팝업 오픈을 트리거한다.
public class UIPopupEventDreamBalloon : UIBasePopup
{
    public const string PREFAB_PATH = "UIPopupEventDreamBalloon";

    [SerializeField] private DreamBalloonRoadController roadController;   // 구름 트랙·열기구·카메라 연출(§7-2)
    [SerializeField] private CommonRewardItem[] finalRewardItems;    // 최종 보상 미리보기(§3-4 ⑦, 최대 3개) — 프리팹 슬롯 타입(base CommonRewardItem)에 정렬
    [SerializeField] private UILiveEventTimer eventTimer;   // 이벤트 종료까지 남은 시간(§3-4 ④)
    [SerializeField] private UIEventSettingIcons difficultyIcons;   // 현재 선택 난이도 뱃지(§3-3 · EventSettingIconPreset) — 선택 난이도(GetTableDifficulty)로 구동
    [SerializeField] private UITextEx remainSeatText;   // 남은 자리 값 표시(§3-2 이벤트 진행 ③, LIdx 43526) — Seat/Text_3
    [SerializeField] private GameObject seatRoot;       // 「남은 자리」 영역(Bottom/Seat) — 진행중에만 노출(§4-7 ③ 하단 상태 스왑)

    // 진행도 값 {현재}/{최종} (§3-4 ⑤) — Top/CurrentProgress/CurrentText_1.
    // 라벨 "현재 진행도"(LIdx 43525)는 형제 UITextEx 가 StringKey 로 자동 현지화하므로 코드 대상이 아니다.
    // 값 텍스트는 루트 StatefulComponent 가 아니라 Top 의 StatefulComponent 에 등록돼 있어 Stateful 로는 잡히지 않는다
    // → 이 팝업의 다른 텍스트(remainSeatText)와 동일하게 프리팹 직접 바인딩한다.
    [SerializeField] private UITextEx progressText;

    // 쉬어가기 화면(§3-2 이벤트 쉬어가기 ②)의 시작 버튼 — StatefulComponent 에 Button role 이 등록돼 있지 않아
    // 결과 팝업(_Success/_Fail)과 동일하게 프리팹 버튼을 직접 바인딩한다.
    [SerializeField] private UIButtonEx btnNextStage;   // 다음 단계 시작하기 — Btn_NextStage (성공 후)
    [SerializeField] private UIButtonEx btnRestart;     // 다시 시작하기 — Btn_Restart (실패 후)

    // 연출 스킵 터치 영역(기획 §4 "연출이 출력될 때 화면을 터치하면 연출 스킵 가능") — 전체 화면 투명 버튼.
    // 연출 중에만 켜지므로 "연출 중 조작 불가"(§4 4-3·4-4·4-5) 입력 차단 역할도 겸한다.
    [SerializeField] private UIButtonEx btnSkipTouch;

    // 인포(설명) 페이지 — **메인 팝업에 중첩**(InfoRoot 아래), `?` 버튼으로 오픈(§4-7 ③). Carrot·시작 팝업과 동일한 표준:
    // 별도 UIBasePopup 오픈이 아니라 임베드된 UIEventInfoPage 를 직접 구동한다(SetActive → InitializePage → OnSequenceOpenPopup).
    // (구 구현은 OpenUIMsgWitNameAsync 로 _Info 를 표준 팝업으로 열었으나, _Info 프리팹에 UIBase 파생 컴포넌트가 없어
    //  OpenUIAsync 가 즉시 실패(파괴)해 아무것도 안 열렸다 → 임베드 구동으로 교체.)
    [SerializeField] private UIEventInfoPage infoPage;

    // 파트너 위치 안내 프로필(기획 §3-2 "화면이 스크롤이 되어 파트너가 화면 밖으로 사라질 경우 파트너의 위치 안내 표시" —
    // "쇼핑 로드에서 스크롤 되었을 때 프로필이 출력되는 형태와 동일"). 프리팹 오브젝트·컴포넌트 구성도 쇼핑 로드와 같다
    // (UIProfileBox + UIButtonEx + Animator, 자식 Root/BG/IconCharacter/ProfileBasicsOutline).
    //   위로 벗어나면 상단(_Up), 아래로 벗어나면 하단(_Down)에 유저 프로필을 노출하고, 누르면 현재 단계로 스크롤한다.
    [SerializeField] private GameObject topProfileObject;      // UIEventProfileBox_Up — 파트너가 화면 위쪽 밖일 때
    [SerializeField] private GameObject bottomProfileObject;   // UIEventProfileBox_Down — 파트너가 화면 아래쪽 밖일 때
    // Awake 캐싱 — 런타임 GetComponent 회피.
    // ⚠️ 쇼핑 로드는 UIProfileBox 를 **아래쪽 박스만** 캐싱해 SetProfileImage 를 부르지만(위쪽 박스는 이미지 미로드),
    //    여기서는 두 박스 모두 로드한다 — 위로 벗어났을 때도 프로필이 보여야 하기 때문(§3-2 "유저가 설정한 프로필을 활용하여 노출").
    private UIProfileBox topProfileBox;
    private UIProfileBox bottomProfileBox;
    private UIButtonEx topProfileButton;
    private UIButtonEx bottomProfileButton;
    private bool shownTopProfile;            // 직전 노출 상태 — 스크롤 콜백이 매 프레임 올 수 있어 변화 시에만 SetActive
    private bool shownBottomProfile;

    private const int LIDX_BTN_NEXT_STAGE = 43528;   // "다음 단계 시작하기"
    private const int LIDX_BTN_RESTART = 43529;      // "다시 시작하기"
    private const float SEAT_COUNT_SEC = 0.35f;      // 남은 자리 숫자 카운트다운 트윈 시간(§4-3 좌석 카운팅)

    private UITextEx btnNextStageLabel;   // 버튼 라벨(자식 UITextEx, 1회 캐싱)
    private UITextEx btnRestartLabel;
    private bool startRequesting;         // 연출 중 중복 클릭 방지
    private bool startAllowed;            // 라운드 진행 버튼 노출 허용 — 기본 false, 결과 팝업의 "쉬어가기" 선택으로만 켜진다(§5-1)
    private bool resultPresenting;        // 결과 연출 재생 ~ 결과 팝업 선택 전 구간 — 머지판 이동 버튼을 숨긴다(§4 연출 중 조작 불가)
    private bool recruitPlaying;          // 경쟁자 모집 연출(§4-3) 재생 중 — RoundStartedMsg 중복 방송 방어
    private Tween seatTween;              // 남은 자리 카운트다운
    private int shownRemainSeat = -1;     // 현재 화면에 표시 중인 잔여 좌석(트윈 시작점) — 미표시 상태는 -1

    public class Info : IUIInfoData
    {
        public ContentEventDreamBalloon.DreamBalloonModel model;
    }

    protected override void Awake()
    {
        base.Awake();

        // 파트너 위치 안내 프로필 박스 — 컴포넌트 캐싱만(구독은 OnEnable).
        if (null != topProfileObject)
        {
            topProfileBox = topProfileObject.GetComponent<UIProfileBox>();
            topProfileButton = topProfileObject.GetComponent<UIButtonEx>();
        }

        if (null != bottomProfileObject)
        {
            bottomProfileBox = bottomProfileObject.GetComponent<UIProfileBox>();
            bottomProfileButton = bottomProfileObject.GetComponent<UIButtonEx>();
        }
    }

    protected override void OnEnable()
    {
        base.OnEnable();

        if (Stateful.HasButton(ButtonRole.Info))
            Stateful.AddButtonListener(ButtonRole.Info, OnClickInfo);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.AddButtonListener(ButtonRole.Close, Close);

        EnsureInfoPage();
        if (null != infoPage)
        {
            infoPage.gameObject.SetActive(false);   // 기본 숨김 — `?` 버튼으로만 노출
            infoPage.OnPopupClosed -= OnInfoPageClosed;
            infoPage.OnPopupClosed += OnInfoPageClosed;
        }

        // 쉬는중 화면(§3-5)의 시작 버튼: 성공 후 "다음 단계 시작하기"(BtnNextStage) / 실패 후 "다시 시작하기"(BtnRestart)
        if (Stateful.HasButton(ButtonRole.BtnNextStage))
            Stateful.AddButtonListener(ButtonRole.BtnNextStage, OnClickStartNext);
        if (Stateful.HasButton(ButtonRole.BtnRestart))
            Stateful.AddButtonListener(ButtonRole.BtnRestart, OnClickStartNext);

        // 프리팹 직접 바인딩(role 미등록 대비) — 위 role 경로와 중복 등록되지 않도록 HasButton 이 false 일 때만.
        if (null != btnNextStage && !Stateful.HasButton(ButtonRole.BtnNextStage))
            btnNextStage.onClick.AddListener(OnClickStartNext);
        if (null != btnRestart && !Stateful.HasButton(ButtonRole.BtnRestart))
            btnRestart.onClick.AddListener(OnClickStartNext);

        if (null != btnSkipTouch)
            btnSkipTouch.onClick.AddListener(OnClickSkip);

        // 파트너 위치 안내 프로필 — 누르면 현재 단계로 스크롤(쇼핑 로드 OnClickProfile 과 동일 계약).
        if (null != topProfileButton)
            topProfileButton.onClick.AddListener(OnClickProfile);
        if (null != bottomProfileButton)
            bottomProfileButton.onClick.AddListener(OnClickProfile);

        roadController?.SetStageScrollAction(RefreshProfileGuide);

        Message.AddListener<SeatChangedMsg>(OnSeatChanged);
        Message.AddListener<StageResultMsg>(OnStageResult);
        Message.AddListener<StageResultDecisionMsg>(OnStageResultDecision);
        Message.AddListener<RoundStartedMsg>(OnRoundStarted);
        Message.AddListener<OnRefreshEventCurrencyMsg>(OnRefreshCurrency);
        Message.AddListener<OnRefreshEventIconMsg>(OnRefreshEventIcon);
    }

    protected override void OnDisable()
    {
        Message.RemoveListener<SeatChangedMsg>(OnSeatChanged);
        Message.RemoveListener<StageResultMsg>(OnStageResult);
        Message.RemoveListener<StageResultDecisionMsg>(OnStageResultDecision);
        Message.RemoveListener<RoundStartedMsg>(OnRoundStarted);
        Message.RemoveListener<OnRefreshEventCurrencyMsg>(OnRefreshCurrency);
        Message.RemoveListener<OnRefreshEventIconMsg>(OnRefreshEventIcon);

        if (null != infoPage)
            infoPage.OnPopupClosed -= OnInfoPageClosed;

        if (null != topProfileButton)
            topProfileButton.onClick.RemoveListener(OnClickProfile);
        if (null != bottomProfileButton)
            bottomProfileButton.onClick.RemoveListener(OnClickProfile);

        roadController?.RemoveStageScrollAction();
        shownTopProfile = false;      // 재진입 시 RefreshProfileGuide 가 다시 판정한다
        shownBottomProfile = false;
        SetProfileActive(topProfileObject, false);
        SetProfileActive(bottomProfileObject, false);

        KillSeatTween();
        shownRemainSeat = -1;   // 재진입 시 첫 표시는 트윈 없이 즉시 반영
        startAllowed = false;   // 기본 비활성 — 재진입 시 SetInfo 가 다시 판단한다
        resultPresenting = false;
        recruitPlaying = false;

        if (Stateful.HasButton(ButtonRole.Info))
            Stateful.RemoveButtonListener(ButtonRole.Info, OnClickInfo);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.RemoveButtonListener(ButtonRole.Close, Close);

        if (Stateful.HasButton(ButtonRole.BtnNextStage))
            Stateful.RemoveButtonListener(ButtonRole.BtnNextStage, OnClickStartNext);
        if (Stateful.HasButton(ButtonRole.BtnRestart))
            Stateful.RemoveButtonListener(ButtonRole.BtnRestart, OnClickStartNext);

        if (null != btnNextStage && !Stateful.HasButton(ButtonRole.BtnNextStage))
            btnNextStage.onClick.RemoveListener(OnClickStartNext);
        if (null != btnRestart && !Stateful.HasButton(ButtonRole.BtnRestart))
            btnRestart.onClick.RemoveListener(OnClickStartNext);

        if (null != btnSkipTouch)
            btnSkipTouch.onClick.RemoveListener(OnClickSkip);

        SetSkipTouchActive(false);

        base.OnDisable();
    }

    /// <summary>
    /// 이벤트 진입 상태 해제 — 빙고·인형뽑기·해머 등 다른 라이브이벤트 팝업과 동일한 표준 처리다.
    ///
    /// ⚠️ **[ISSUE-16] 이 호출이 빠져 있었다.** 드림벌룬은 코드 전체에 `CloseEvent` 호출부가 없어
    ///    <c>ContentLiveEventBase.isOnEvent</c> 가 이벤트 버튼을 한 번 누른 뒤 **세션 내내 true 로 고정**됐다.
    ///    그러면 <see cref="ContentEventDreamBalloon"/>.ResolveRoundResult 의 `!isOnEvent` 게이트가 죽은 조건이 되어,
    ///    **머지판에서 목표를 채우는 즉시 라운드 결과가 전송되고 보상까지 지급**된다
    ///    (= "클리어 조건 달성하자마자 보상 지급" 증상. 팝업 진입 시점 커밋이라는 설계가 통째로 무력화된 상태였다).
    ///
    /// 결과 팝업(_Success/_Fail/_Final)은 이 팝업을 닫지 않고 **위에 오버레이로** 열리므로 연출 도중 여기로 오지 않는다.
    /// 즉 이 시점은 "유저가 이벤트 화면을 실제로 떠난 때"(닫기 버튼 · <see cref="MoveToMergeBoard"/>)가 맞다.
    /// </summary>
    public override void Close()
    {
        EventCommonHelper.CloseEvent(LiveEventType.DREAMBALLOON);
        base.Close();
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);

        // 라운드 진행 버튼은 기본 비활성이다(§5-1). 재생할 결과 연출이 예약돼 있으면 잠근 채 진입하고,
        // 결정은 결과 팝업(_Success/_Fail)이 내린다. 연출이 없는 진입(쉬는중 재진입)만 곧바로 허용한다.
        //
        // ⚠️ 예약된 결과는 **트랙을 짓기 전에** 확인해야 한다 — 트랙의 기준 라운드가 달라진다(아래 RefreshTrack 참고).
        StageResultMsg entryPending = EventDreamBalloonHelper.GetContent()?.PeekPendingResultPresentation();
        startAllowed = null == entryPending;
        resultPresenting = !startAllowed;

        EventDreamBalloonHelper.BindEventTimer(eventTimer);   // 이벤트 종료까지 남은 시간(§3-4 ④)
        RefreshProgress();
        // 진입 시 남은 자리 초기 표시(§3-2 이벤트 진행 ③) — 시뮬 구동 중이면 실시간 잔여, 미구동이면 테이블 정원.
        RefreshSeat(EventDreamBalloonHelper.GetRemainSeat(), EventDreamBalloonHelper.GetSlotMax(), animate: false);
        RefreshTrack(entryPending);   // 구름 단계 트랙 데이터 공급(§7-2) — 결과 연출 예약 시 '결과 반영 전' 라운드 기준
        RefreshRestView();   // 진행중/쉬는중 하단 버튼 전환(§3-4 ⑪ ↔ §3-5 ②)
        RefreshPartnerPlacement();   // 파트너 배치 — 위 RefreshTrack(SetStages)도 배치하므로 멱등 보강이다

        // 파트너 위치 안내 프로필(§3-2) — 파트너 캐릭터 이미지 로드 + 초기 노출 판정(트랙 생성 후라 셀 앵커가 유효하다).
        LoadPartnerProfileImage(topProfileBox);
        LoadPartnerProfileImage(bottomProfileBox);
        RefreshProfileGuide();
        EventDreamBalloonHelper.BindFinalRewards(finalRewardItems, EventDreamBalloonHelper.GetDifficulty());   // 최종 보상(§3-4 ⑦)
        // 현재 선택된 난이도 뱃지(§3-3) — 활성 Event_Setting 행이 아닌 **선택 난이도**(GetTableDifficulty)로 EventSettingIconPreset 을 구동(미바인딩 시 no-op).
        difficultyIcons?.SetEventType(LiveEventType.DREAMBALLOON, EventDreamBalloonHelper.GetTableDifficulty());

        // 진입 self-check(§5-1·§7-3): 재접속/상주 중 이미 확정된 직전 라운드 결과면 컨텐츠가 성공/실패를
        // 판정(시뮬 기준)하고 StageResultMsg 로 연출·결과 팝업이 이어진다(OnStageResult). 시뮬 미구동 시 no-op.
        EventDreamBalloonHelper.GetContent()?.EvaluateRoundOnOpen();

        // 팝업 미개방 중 확정된 결과(치트 즉시 처리·재접속 등으로 StageResultMsg 유실)가 있으면 진입 시 재생(§5-1 self-check).
        // 진입 시점에 없던 결과를 위 self-check 가 방금 확정했을 수 있으므로 다시 읽는다.
        // (그 경로는 확정 전에 트랙이 지어졌으므로 이미 '결과 반영 전' 라운드 기준이다 — 재구성 불필요.)
        ContentEventDreamBalloon content = EventDreamBalloonHelper.GetContent();
        StageResultMsg pending = content?.PeekPendingResultPresentation();
        if (null != pending)
        {
            // 결과 연출(§4-4 성공 / §4-5 실패)은 **구름 위 경쟁자 초상화를 열기구에 태우는 것으로 시작**한다.
            //  머지판(화면 밖)에서 결과가 확정된 뒤 팝업을 여는 경로는 라운드 시작(모집) 연출을 거치지 않아
            //  초상화가 아직 생성돼 있지 않다 → 연출을 걸기 **전에** 결과 라운드의 초상화를 착지 상태로 복원한다.
            //  (메인 팝업 안에서 진행할 때는 직전 모집 연출이 이미 만들어 둬서 이 누락이 드러나지 않는다.)
            roadController?.RestorePortraitsAtTarget(pending.round);

            content.ClearPendingResultPresentation();
            PlayResultThenOpenAsync(pending).Forget();
        }

        // 라운드 1(난이도 선택 직후) — 팝업이 방금 열려 RoundStartedMsg 를 놓쳤으므로 진입 시 모집 연출을 재생한다(§4-3).
        bool recruited = TryPlayRoundStartRecruit();

        // 재진입(모집 연출 없음 + 재생할 결과 없음 + 진행중) — 경쟁자 포트레이트를 **목표 위치에 정적 복원**한다(사용자 요구).
        //   라운드 시작(recruited)은 열기구 Init → DOJump 로 노출하므로 여기서 복원하지 않는다(중복·튐 방지).
        //   friendCount 게이트는 SnapRecruitPortraitsEnd 내부에서 처리(최종 라운드 등 경쟁자 0명이면 미노출).
        if (!recruited && null == pending && EventDreamBalloonHelper.IsRoundProgressing())
        {
            roadController?.RestorePortraitsAtTarget(EventDreamBalloonHelper.GetCurrentRound());
        }

        // TODO[binding]: 최종 목표 단계 이미지(§3-4 ⑥) 배선(§7-2).
    }

    // 좌석 감소(클라 시뮬) → 재화 게이지 + 남은 자리 실시간 갱신(§7-3·§3-2 이벤트 진행 ③).
    private void OnSeatChanged(SeatChangedMsg msg)
    {
        RefreshSeat(msg.remainSeat, msg.slotMax, animate: true);
    }

    // 남은 자리 표시(§3-2 이벤트 진행 ③, LIdx 43526). {잔여}/{정원} — 프리팹 authored "800/900" 포맷.
    // 좌석 감소(SeatChangedMsg)는 1석씩 들어오므로 숫자 카운트다운 트윈으로 잇고, 진입 초기화는 즉시 반영한다.
    private void RefreshSeat(int remainSeat, int slotMax, bool animate)
    {
        if (null == remainSeatText)
        {
            return;
        }

        KillSeatTween();

        if (!animate || shownRemainSeat < 0 || shownRemainSeat == remainSeat)
        {
            SetSeatText(remainSeat, slotMax);
            return;
        }

        seatTween = DOVirtual.Int(shownRemainSeat, remainSeat, SEAT_COUNT_SEC, value => SetSeatText(value, slotMax))
            .SetEase(Ease.OutCubic)
            .SetLink(gameObject);
    }

    private void SetSeatText(int remainSeat, int slotMax)
    {
        shownRemainSeat = remainSeat;
        remainSeatText.SetText($"{remainSeat}/{slotMax}");
    }

    private void KillSeatTween()
    {
        if (null == seatTween)
        {
            return;
        }

        seatTween.Kill();
        seatTween = null;
    }

    // 재화 변동(itemIdx 224) → 현재 구름 게이지 실시간 갱신(§7-1 3·§3-4 ②). 게이지는 팝업이 아니라 진행중 구름 위에 있다.
    private void OnRefreshCurrency(OnRefreshEventCurrencyMsg msg)
    {
        if (msg.eventType != LiveEventType.DREAMBALLOON)
        {
            return;
        }

        // ⚠️ **결과 확정 직후(제시 대기·재생 중)에는 트랙을 갱신하지 않는다.** 결과 확정 시 SyncFromServer 가 이 콜백을
        //    발신하는데, 여기서 트랙을 갱신하면 **퇴장 연출(성공=FallDown / 실패=탑승)이 발화되기 전에 경쟁자 포트레이트가
        //    지워진다**(성공=재빌드+스크롤로 셀 재활용 / 실패=IsRoundProgressing false 로 숨김). 트랙의 라운드 전이는
        //    성공/실패 연출(RunStageSuccess/FailAsync)이 직접 수행하므로, 이 구간의 갱신은 불필요하고 유해하다.
        bool presentingResult = resultPresenting || null != EventDreamBalloonHelper.GetContent()?.PeekPendingResultPresentation();
        if (!presentingResult)
        {
            RefreshStageValues();
        }

        RefreshRestView();   // 쉬어가기/재시작 등 상태 변화 시 하단 버튼 전환
        RefreshPartnerPlacement();
    }

    // 진행 상태 전이(쉬는중 확정·라운드 시작 등) → 파트너 **대기 모션만** 재평가한다(ISSUE-33 후속).
    //  결과 팝업에서 '잠시 쉬어가기'를 고르면 라운드는 그대로라 RefreshStages 가 TryRefreshStages 로 빠지고,
    //  ShowPartnerAtStage 가 호출되지 않아 수면(§4-6)으로 전환되지 않는다(팝업을 다시 열어야 반영되던 문제).
    //  이 메시지는 콘텐츠가 쉬어가기/라운드 시작을 **서버에 확정한 뒤** 발신하므로(SendIconRefresh),
    //  이 시점의 state·쉬어가기 선택 플래그는 이미 최신이다. 연출 중이면 컨트롤러 쪽에서 no-op.
    private void OnRefreshEventIcon(OnRefreshEventIconMsg msg)
    {
        if (null == msg || msg.eventType != LiveEventType.DREAMBALLOON)
        {
            return;
        }

        // ⚠️ 결과 연출이 **예약됐거나 재생 중**이면 대기 모션을 건드리지 않는다 — 그 구간의 파트너 감정
        //   (성공 Happy / 실패 Sullen)은 연출이 직접 구동하며, 서버 state 는 이미 쉬는중(1)이라
        //   여기서 재평가하면 연출 감정을 수면(§4-6)으로 덮어쓴다. 결과 확정(SyncFromServer)·보상 지급도
        //   이 메시지를 발신하므로 그 사이 어느 틱에든 끼어들 수 있다.
        //   (연출 재생 중은 컨트롤러 쪽에서도 막지만, 확정~재생 시작 사이의 빈틈은 여기서만 막을 수 있다.)
        if (resultPresenting || null != EventDreamBalloonHelper.GetContent()?.PeekPendingResultPresentation())
        {
            return;
        }

        roadController?.RefreshPartnerIdleMotion();
    }

    // 클라 판정 결과 → 성공/실패 연출 재생(트랙 컨트롤러) 후 결과 팝업 오픈(§5-1·§7-2).
    private void OnStageResult(StageResultMsg msg)
    {
        EventDreamBalloonHelper.GetContent()?.ClearPendingResultPresentation();   // 라이브 수신 = 제시 완료 → 재개방 시 중복 재생 방지
        PlayResultThenOpenAsync(msg).Forget();
    }

    /// <summary>
    /// 결과 팝업(_Success/_Fail)의 **선택 결과**로 하단 시작 버튼 노출을 결정한다(§5-1).
    /// 쉬어가기 → 노출 / 라운드 진행·다시 시작 → 계속 비활성(곧 진행중으로 전이하며 머지판으로 이동).
    /// </summary>
    private void OnStageResultDecision(StageResultDecisionMsg msg)
    {
        startAllowed = msg.rest;
        resultPresenting = false;
        RefreshRestView();
        RefreshPartnerPlacement();   // 결정 완료 → 연출이 놓았던 파트너 소유권을 대기 화면 규칙으로 되돌린다
    }

    // 순수 view 연출(§7-3)을 await 한 뒤 결과 팝업 오버레이. 연출 없으면 즉시 오픈.
    private async UniTaskVoid PlayResultThenOpenAsync(StageResultMsg msg)
    {
        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        // totalRound > 0 을 명시 — AiRound 테이블 미로드로 0 이면 round(1) >= 0 이 참이 되어 1라운드 성공이 최종으로 새는 것을 막는다(claim 3).
        bool isFinal = msg.success && totalRound > 0 && msg.round >= totalRound;

        // 서버는 RoundResult 응답에서 곧바로 state=1(쉬는중)을 내려주지만, 진행/쉬어가기 결정은 결과 팝업의 몫이다.
        // 결정 전까지는 시작 버튼을 켜지 않고, 머지판 이동도 막는다(연출 중 조작 불가).
        startAllowed = false;
        resultPresenting = true;
        RefreshRestView();
        // ⚠️ 여기서는 RefreshPartnerPlacement 를 부르지 않는다 — 지금부터 파트너의 배치·감정은 아래 연출이 소유한다(ISSUE-33).

        // 최종 보상만 여기서 수령한다(기획 확정 2026-07-19): 마지막 라운드 클리어 → 메인 팝업 이동 →
        //   RQEventBalloonRewardClaim → RS 수신 후 지급 → 최종 연출 → _Final → End. RS 대기가 필요해 경로가 다르다.
        //
        // ⚠️ [ISSUE-16] **단계 보상(1~9)은 더 이상 여기서 지급하지 않는다.** 지급·수령 기록은
        //    ContentEventDreamBalloon.ResolveRoundResult 의 **RoundResult 성공 콜백**이 담당한다.
        //    지급을 이 시점(pending 소비)에 묶어 두면, 그 전에 pending 이 폐기될 경우
        //    서버는 클리어를 기록했는데 아이템만 안 나가는 보상 미지급이 발생한다(상세는 그쪽 주석).
        //    표시(공용 획득 팝업)는 종전대로 아래 OpenRoundRewardThenSuccess 가 담당한다.
        ContentEventDreamBalloon claimContent = EventDreamBalloonHelper.GetContent();
        if (isFinal && null != claimContent)
        {
            await claimContent.ClaimFinalRewardAsync();
        }

        if (null != roadController)
        {
            // 연출 구간에만 스킵 터치 영역을 연다 — 터치 시 SkipController 가 최상단 연출(RoadController)을 끝으로 점프시킨다.
            // 스킵은 순수 view 만 건너뛰므로 아래 결과 팝업 오픈은 그대로 실행된다(§7-3·Skip 사용설명서 §4).
            SetSkipTouchActive(true);
            try
            {
                if (isFinal)
                    await roadController.PlayFinalAsync(msg.round);
                else if (msg.success)
                    await roadController.PlayStageSuccessAsync(msg.round, totalRound);
                else
                    await roadController.PlayStageFailAsync(msg.round);
            }
            catch (OperationCanceledException)
            {
                // 중단(≠스킵) — 다른 연출로 교체되거나 팝업이 닫힌 경우. 결과 팝업은 열지 않되,
                // 잠금 플래그는 반드시 되돌린다(그대로 두면 머지판 이동 버튼이 계속 잠긴다).
                resultPresenting = false;
                RefreshRestView();
                RefreshPartnerPlacement();
                throw;
            }
            finally
            {
                SetSkipTouchActive(false);
            }
        }

        if (isFinal)
            EventDreamBalloonHelper.OpenFinalPopup();
        else if (msg.success)
            EventDreamBalloonHelper.OpenRoundRewardThenSuccess(msg);   // N+1 보상 공용 획득 팝업(있으면) → 닫힘 후 성공 팝업(2026-07-16)
        else
            EventDreamBalloonHelper.OpenFailPopup(msg);
    }

    // 화면 터치 → 표준 스킵 입력(SkipController 가 최상단 스킵 가능 연출만 처리, 1프레임 디바운스 내장).
    private void OnClickSkip()
    {
        SkipController.Instance.OnSkipInput();
    }

    // 연출 구간에만 전체화면 터치 영역을 연다(스킵 입력 + 연출 중 조작 차단 겸용).
    private void SetSkipTouchActive(bool active)
    {
        if (null == btnSkipTouch)
        {
            return;
        }

        btnSkipTouch.gameObject.SetActive(active);
    }

    /// <summary>
    /// 머지판으로 이동 — 프로젝트 표준 바로가기 경로(트로피 챌린지 `MoveToShortCut`의 `InGame` 분기와 동일).
    ///
    /// ⚠️ 팝업을 반드시 먼저 닫는다. 팝업이 남아 있으면 `UIBasePopup.ShouldLockInput`(기본 true) 때문에
    /// `ControllerUserInputBlock.IsInputLockedByPopup()` 이 머지 보드 입력을 통째로 막는다(ISSUE-08).
    /// </summary>
    private void MoveToMergeBoard()
    {
        Close();
        Message.Send(new OnChangeLobbyUITypeMsg(LobbyUIType.Merge));
    }

    // 쉬는중 화면 "다음 단계 시작하기"/"다시 시작하기"(§3-2 이벤트 쉬어가기 ②) →
    //   서버에 라운드 시작 요청 → 확정 후 게임 시작 연출(§4-3 경쟁자 모집) 재생 → 머지판으로 이동(바로가기).
    private void OnClickStartNext()
    {
        if (startRequesting)
        {
            return;   // 연출 중 중복 클릭 방지
        }

        startRequesting = true;
        SetObjectActive(btnNextStage, false);
        SetObjectActive(btnRestart, false);

        // 연출은 서버 확정(SyncFromServer) 후 RoundStartedMsg 수신 시 재생한다 — 순수 view 이므로 데이터 위험은 없다(§7-3).
        // 실패 시에는 라운드가 시작되지 않았으므로 연출 없이 버튼·입력 잠금만 되돌린다.
        EventDreamBalloonHelper.GetContent()?.RequestNextRoundStart(onFailed: OnStartNextFailed);
    }

    // 라운드 시작 확정(§4-3) — 메인·성공 팝업·실패 팝업 어느 시작 버튼을 눌렀든 이 메시지로 연출을 재생한다.
    // (결과 팝업은 콜백을 넘기지 않아 연출이 스킵되던 문제 → 서버 확정 시점 단일 방송으로 통일)
    private void OnRoundStarted(RoundStartedMsg msg)
    {
        TryPlayRoundStartRecruit();
    }

    // 라운드 시작 모집 연출 트리거 — 라이브 수신(OnRoundStarted, 팝업 개방 = 라운드 2+) / 진입 self-check(SetInfo, 라운드 1) 공용.
    // pending 을 소비한 한쪽만 재생한다(ConsumeRoundStartRecruit 1회성). 재생 중이면 방어.
    // 반환값: 모집 연출을 실제로 시작했으면 true(SetInfo 가 재진입 복원 여부 판단에 사용).
    private bool TryPlayRoundStartRecruit()
    {
        if (recruitPlaying)
        {
            return true;   // 이미 재생 중 — 재진입 복원 불필요
        }

        if (EventDreamBalloonHelper.GetContent()?.ConsumeRoundStartRecruit() != true)
        {
            return false;   // 재생할 라운드 시작 없음(= 재진입일 수 있음)
        }

        recruitPlaying = true;
        PlayRecruitAsync().Forget();
        return true;
    }

    // 라운드 시작 요청 실패 — 쉬는중 화면을 유지한 채 버튼을 복구한다(§5-2 서버 확정 전 진행 금지).
    private void OnStartNextFailed()
    {
        startRequesting = false;
        RefreshRestView();
        RefreshPartnerPlacement();
    }

    // 게임 시작 연출(§4-3) 재생. 연출 중 조작 불가(버튼 비활성). 파괴/스킵 안전.
    // 연출이 끝나도 머지판으로 이동하지 않고 진행중 화면을 유지한다(하단 = 「남은 자리」, §4-7 ③).
    private async UniTaskVoid PlayRecruitAsync()
    {
        // ⚠️ 재생 플래그 해제는 반드시 finally 에서 한다. 이 연출은 도중에 **중단**될 수 있다 —
        //    재생 중 라운드 결과가 확정되면 결과 연출이 RoadController 에서 이 연출을 Cancel 하고 자리를 가져간다.
        //    그때 await 가 OperationCanceledException 을 던지므로, 해제를 await 뒤에 두면 recruitPlaying 이 true 로 박제되고
        //    다음 라운드 시작 연출이 TryPlayRoundStartRecruit 의 가드에 막혀 영영 재생되지 않는다.
        ContentEventDreamBalloon content = EventDreamBalloonHelper.GetContent();

        try
        {
            if (null != roadController)
            {
                int curRound = EventDreamBalloonHelper.GetCurrentRound();
                int totalRound = EventDreamBalloonHelper.GetTotalRound();

                RefreshTrack();   // 새 라운드 기준으로 트랙·파트너 재배치 후 연출 시작

                // 연출이 끝나야(START) 라운드가 실제로 시작된다 — 재생 중에는 라운드 결과를 확정하지 않는다(§4-3).
                // 재접속 직후처럼 좌석이 이미 소진돼 재생 도중 좌석이 0이 되는 경우, 시작 연출이 끊기고 실패 연출로 튀는 것을 막는다.
                content?.SetRoundStartPresenting(true);

                SetSkipTouchActive(true);   // 연출 구간 = 스킵 터치 가능 + 그 외 조작 차단(§4)
                await roadController.PlayRecruitAsync(curRound, totalRound, gameObject.GetCancellationTokenOnDestroy());
            }
        }
        finally
        {
            SetSkipTouchActive(false);
            startRequesting = false;
            recruitPlaying = false;
            RefreshRestView();   // 진행중 전환 반영 — 시작 버튼 숨김 + 좌석 노출
            RefreshPartnerPlacement();

            // 보류해 둔 라운드 결과가 있으면 여기서 확정된다 → 곧바로 결과 연출(성공/실패)로 이어진다.
            // 플래그를 먼저 정리한 뒤 호출해야 결과 경로가 일관된 상태에서 시작한다.
            content?.SetRoundStartPresenting(false);
        }
    }

    // 중첩 인포 페이지(InfoRoot 아래 임베드) 참조 확보 — SerializeField 미바인딩 시 자식에서 1회 탐색해 캐싱한다.
    // (중첩 프리팹 인스턴스 참조는 인스펙터 배선이 유실되기 쉬워 폴백을 둔다. 이 팝업에 UIEventInfoPage 는 임베드 1개뿐이다.)
    private void EnsureInfoPage()
    {
        if (null == infoPage)
        {
            infoPage = GetComponentInChildren<UIEventInfoPage>(includeInactive: true);
        }
    }

    // `?` 버튼 — 중첩 인포 페이지를 연다(Carrot·시작 팝업 표준: 임베드 UIEventInfoPage 직접 구동).
    // 별도 팝업 오픈이 아니라 SetActive → InitializePage → OnSequenceOpenPopup 순으로 구동한다. 미바인딩 시 no-op.
    private void OnClickInfo()
    {
        EnsureInfoPage();
        if (null == infoPage)
        {
            return;
        }

        // 인포(가이드) 페이지를 메인 팝업의 다른 레이어(Bottom/Top/StartText 등) 위로 올린다(ISSUE-38).
        // InfoRoot 가 프리팹상 뒤쪽이 아닌 형제라 활성만 하면 이후 형제 레이어에 가려진다(Unity UI = 뒤 형제가 위) → 최상위 형제로.
        BringInfoToFront();

        infoPage.gameObject.SetActive(true);
        infoPage.InitializePage(0);
        infoPage.OnSequenceOpenPopup();
    }

    // 인포 페이지가 속한 팝업 루트 직속 조상(InfoRoot)을 최상위 형제로 올려 다른 레이어 위에 그린다(ISSUE-38).
    private void BringInfoToFront()
    {
        Transform node = infoPage.transform;
        Transform parent = node.parent;
        while (null != parent && parent != transform)
        {
            node = parent;
            parent = node.parent;
        }

        node.SetAsLastSibling();
    }

    // 인포 페이지 내부 닫기(좋아요/닫기) → 페이지만 숨긴다(메인 팝업은 유지).
    private void OnInfoPageClosed()
    {
        if (null != infoPage)
        {
            infoPage.gameObject.SetActive(false);
        }
    }

    // 진행중/쉬는중에 따라 하단 영역 전환(§4-7 ③) — **하단 버튼·좌석 표시 전용**이다.
    //  진행중  : 「남은 자리」 영역(seatRoot) 노출 — 머지판 복귀는 닫기 버튼이 담당한다(기획에 별도 이동 버튼 없음).
    //  쉬는중  : 성공 후 "다음 단계 시작하기"(BtnNextStage) / 실패 후 "다시 시작하기"(BtnRestart) 노출
    //  파트너 배치는 <see cref="RefreshPartnerPlacement"/> 가 소유한다 — 필요한 호출부에서 명시적으로 함께 부른다.
    private void RefreshRestView()
    {
        bool lastFailed = EventDreamBalloonHelper.GetLastRoundResult() == 2;   // 2: 실패
        // 라운드 대기 = 쉬어가기(1) **또는 라운드 클리어(3)**. 서버는 라운드 성공마다 3 을 내려주므로(스펙 2026-07-19)
        //  1 만 보면 성공 후 재진입에서 시작 버튼이 안 떠 진행이 막힌다.
        bool isWaitingNextRound = EventDreamBalloonHelper.IsResting() || EventDreamBalloonHelper.IsRoundCleared();

        // 라운드 진행 버튼은 **기본 비활성**이며, 결과 팝업의 선택(StageResultDecisionMsg.rest)으로만 켜진다(§5-1).
        // 결과 연출이 없는 진입(대기 상태 재진입)은 SetInfo 가 startAllowed 를 세워 준다.
        bool showStart = isWaitingNextRound && startAllowed;

        // 하단 영역 상태 스왑(§4-7 ③): 진행중 → 「남은 자리」 + 좌석 카운터 / 대기중 → 시작 버튼(좌석 미표시).
        // 결과 연출 중에는 서버 state 가 이미 대기(1/3)라 좌석이 자연히 숨겨진다(멈춘 숫자 노출 방지).
        if (null != seatRoot)
        {
            seatRoot.SetActive(!isWaitingNextRound);
        }

        SetButtonActive(ButtonRole.BtnNextStage, showStart && !lastFailed);
        SetButtonActive(ButtonRole.BtnRestart, showStart && lastFailed);

        // 쉬는중일 때만 시작 버튼 노출(§3-2 이벤트 쉬어가기 ②) — 성공 후 "다음 단계 시작하기" / 실패 후 "다시 시작하기".
        // StatefulComponent Buttons 미등록이라 프리팹 버튼 GO 를 직접 토글한다.
        //
        // 현재 메인 프리팹에는 시작 버튼 GO 가 `Btn_Start` **하나뿐**이다(btnRestart 미바인딩). 이 경우 한 버튼이 두 문구를
        // 겸하도록 라벨만 교체한다 — 그렇지 않으면 실패 후 쉬는중에 어떤 버튼도 뜨지 않아 재도전이 막힌다.
        // 프리팹이 두 버튼을 갖도록 바뀌면(btnRestart 바인딩) 아래 분기가 자동으로 기존 동작으로 돌아간다.
        if (null == btnRestart)
        {
            SetObjectActive(btnNextStage, showStart);
            SetButtonLabel(btnNextStage, ref btnNextStageLabel, lastFailed ? LIDX_BTN_RESTART : LIDX_BTN_NEXT_STAGE);
        }
        else
        {
            SetObjectActive(btnNextStage, showStart && !lastFailed);
            SetObjectActive(btnRestart, showStart && lastFailed);

            SetButtonLabel(btnNextStage, ref btnNextStageLabel, LIDX_BTN_NEXT_STAGE);
            SetButtonLabel(btnRestart, ref btnRestartLabel, LIDX_BTN_RESTART);
        }

    }

    /// <summary>
    /// 파트너 위치 안내 프로필 갱신(기획 §3-2) — 파트너(현재 단계 구름)가 화면 밖이면 그 방향 가장자리에 유저 프로필을 띄운다.
    /// 스크롤 콜백(<c>DreamBalloonStageLoopScroll.SetScrollAction</c>)으로 드래그·코드 스크롤 양쪽에서 호출되므로
    /// **매 프레임 들어올 수 있다** → 상태가 바뀔 때만 SetActive 한다.
    ///
    /// 연출 중에는 카메라가 파트너를 화면에 잡아 주므로(§3-2 "연출 시 캐릭터가 중앙") 안내가 필요 없고,
    /// 결과 연출 구간에는 트랙이 라운드 전이 중이라 판정 기준도 흔들린다 → 그 구간은 통째로 숨긴다.
    /// </summary>
    private void RefreshProfileGuide()
    {
        int round = EventDreamBalloonHelper.GetCurrentRound();
        bool guideAllowed = !resultPresenting && !recruitPlaying && round > 0;

        bool showTop = guideAllowed && true == roadController?.IsStageAboveViewport(round);
        bool showBottom = guideAllowed && true == roadController?.IsStageBelowViewport(round);

        if (shownTopProfile != showTop)
        {
            shownTopProfile = showTop;
            SetProfileActive(topProfileObject, showTop);
        }

        if (shownBottomProfile != showBottom)
        {
            shownBottomProfile = showBottom;
            SetProfileActive(bottomProfileObject, showBottom);
        }
    }

    // 안내 프로필 클릭 → 현재 단계로 스크롤해 파트너를 화면 안으로 되돌린다(쇼핑 로드 OnClickProfile 과 동일 계약).
    private void OnClickProfile()
    {
        roadController?.ScrollToCurrentStage();
        RefreshProfileGuide();
    }

    /// <summary>
    /// 안내 프로필에 **내 파트너 캐릭터**를 표시한다.
    /// 인물 아이콘은 전부 프로필 체계(이미지 + 프레임)로 통일하므로, 파트너 캐릭터에 대응하는
    /// 프로필 이미지를 찾아 기본 프레임과 함께 그린다. 대응 프로필이 없으면 기본 프로필로 폴백한다.
    /// </summary>
    private void LoadPartnerProfileImage(UIProfileBox profileBox)
    {
        if (null == profileBox)
        {
            return;
        }

        profileBox.SetProfileImageByCharacter(EventDreamBalloonHelper.GetPartnerId());
    }

    private void SetProfileActive(GameObject profileObject, bool active)
    {
        if (null != profileObject)
        {
            profileObject.SetActive(active);
        }
    }

    /// <summary>
    /// 마이드림파트너 배치·대기 모션 갱신(§3-2) — 진행중이면 진행 단계 구름 위(이벤트 진행 ①),
    /// 쉬는중이면 진행 예정 단계 구름 위(쉬어가기 ①). 둘 다 currentRound 구름이라 앵커가 동일하다.
    /// 위치는 트랙이 만든 구름 셀에 SetParent 로 붙는다(좌표 지정 없음). 파트너는 표시 시점 실시간 조회(§7-5).
    ///
    /// ⚠️ 결과 연출이 예약·재생 중이면 **아무것도 하지 않는다** — 그 구간의 배치·감정은 연출이 직접 소유한다
    ///   (RunStageSuccessAsync 의 SnapToStage → PlayPartnerHappy 등). 지키지 않으면 두 가지가 겹쳐 깨진다(ISSUE-33):
    ///   ① 연출 **시작 직전**(PlayResultThenOpenAsync 도입부)에는 아직 RoadController.sequencePlaying 이 서지 않아
    ///      ShowPartnerAtStage 가 '대기 화면'으로 오판한다. 서버 state 는 결과 직후 이미 쉬는중(1)이고 클리어 대기도
    ///      소비된 뒤라 → 성공 연출 직전에 수면(§4-6)이 얹힌다.
    ///   ② GetCurrentRound() 는 성공 시 이미 **다음 라운드**라, 연출 시작 전에 파트너가 엉뚱한 구름으로 옮겨 붙는다.
    ///
    /// ⚠️ 원래 <see cref="RefreshRestView"/> 말미에 묻혀 있었다(이름과 달리 파트너까지 소유). 호출부가 의도를
    ///   드러내도록 분리했으니, 하단 버튼만 갱신하면 되는 곳에서 이 메서드를 같이 부르지 말 것.
    /// </summary>
    private void RefreshPartnerPlacement()
    {
        if (resultPresenting)
        {
            return;
        }

        roadController?.ShowPartnerAtStage(EventDreamBalloonHelper.GetCurrentRound());
    }

    // 프리팹 버튼 GO 토글(미바인딩 시 no-op).
    private void SetObjectActive(UIButtonEx button, bool active)
    {
        if (null != button)
        {
            button.gameObject.SetActive(active);
        }
    }

    // 버튼(UIButtonEx)은 텍스트 API가 없어 자식 UITextEx 를 1회 캐싱해 스트링 테이블 문구를 주입한다(_Fail 동일 패턴).
    private void SetButtonLabel(UIButtonEx button, ref UITextEx cache, int lidx)
    {
        if (null == button)
        {
            return;
        }

        if (null == cache)
        {
            cache = button.GetComponentInChildren<UITextEx>(true);
        }

        if (null != cache)
        {
            // 공용 버튼 텍스트는 StringKey 타입 → SetText(int) 로 키 교체. (String 타입이면 int 오버로드는 무시되고 string 이 적용됨)
            // ⚠️ string 오버로드만 부르면 UITextEx(StringKey) 에서 즉시 return 되어 라벨이 바뀌지 않는다(_Success/_Fail 동일 패턴).
            cache.SetText(lidx);
            cache.SetText(TableManager.GetText(lidx));
        }
    }

    // 역할 버튼의 노출 토글(미태깅 시 no-op). StatefulUI 상태 미구성이라 버튼 GameObject 를 직접 토글한다.
    private void SetButtonActive(ButtonRole role, bool active)
    {
        if (!Stateful.HasButton(role))
        {
            return;
        }

        var button = Stateful.GetButton(role).Button;
        if (null != button)
        {
            button.gameObject.SetActive(active);
        }
    }

    // 진행도 {현재}/{최종} (§3-4 ⑤) = GetCurrentRound / GetTotalRound.
    // 미시작(currentRound 0)에도 1단계부터 표시되도록 하한 1 로 보정한다(트랙·열기구 정렬과 동일 규칙).
    private void RefreshProgress()
    {
        if (null == progressText)
        {
            return;
        }

        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        int currentRound = Mathf.Clamp(EventDreamBalloonHelper.GetCurrentRound(), 1, Mathf.Max(1, totalRound));
        progressText.SetText($"{currentRound}/{totalRound}");
    }

    // 구름 단계 트랙 데이터 공급(§7-2). 테이블/캐싱 정보로 구성해 트랙 컨트롤러에 전달하고 현재 단계로 배치한다.
    private void RefreshTrack()
    {
        RefreshTrack(null);
    }

    /// <summary>
    /// 구름 단계 트랙 구성(§7-2). <paramref name="pending"/> 은 진입 시 재생 대기 중인 결과 연출.
    ///
    /// ⚠️ 재생할 결과가 있으면 트랙을 **결과 반영 전 라운드**(<c>pending.round</c>)로 지어야 한다.
    ///    성공이 머지판(화면 밖)에서 확정되면 결과 저장(ResolveRoundResult→SyncFromServer) 시점에 서버
    ///    <c>currentRound</c> 가 이미 다음 라운드(N+1)로 올라간다. 그대로 지으면 통과한 구름(N)이 **처음부터
    ///    클리어 상태**(별 파랑·깃발 꽂힘·경쟁자 초상화 없음)로 그려져, 뒤이어 재생되는 성공 연출(§4-4:
    ///    초상화 탑승 → 별 전환 → 깃발)이 보여줄 것이 남지 않는다.
    ///    메인 팝업 안에서 진행할 때(F8·라이브 StageResultMsg)는 트랙이 결과 확정 **전에** 지어지므로
    ///    이 문제가 드러나지 않는다 — 머지판 경유 진입만 깨졌던 이유다.
    /// </summary>
    private void RefreshTrack(StageResultMsg pending)
    {
        if (null == roadController)
        {
            return;
        }

        // [ISSUE-28] 하한 1 클램프 — 미로드 창(GetCurrentRound()=0)에서도 셀 상태(BuildStages)와 스크롤 타깃(SetStages 2번째 인자)이
        //  함께 1라운드 기준으로 정합되게 한다(둘이 어긋나면 게이지는 1라운드인데 스크롤만 0으로 튄다). pending.round(≥1)엔 무영향.
        int displayRound = Mathf.Max(1, pending?.round ?? EventDreamBalloonHelper.GetCurrentRound());
        // 성공 대기 중이면 그 구름의 게이지는 이미 목표를 채운 상태로 보여야 한다(코인은 ResetRoundCoin 으로 0 이다).
        bool displayRoundGoalFilled = true == pending?.success;

        roadController.SetStages(BuildStages(displayRound, displayRoundGoalFilled), displayRound);
    }

    // 구름 게이지 값만 갱신(§3-4 ②) — 재화 획득처럼 트랙 구조가 그대로인 변동에 사용한다.
    // 셀을 재생성하지 않으므로 구름 등장 애니가 다시 재생되지 않고, 구름에 붙어 있는 파트너도 파괴되지 않는다.
    private void RefreshStageValues()
    {
        int currentRound = Mathf.Max(1, EventDreamBalloonHelper.GetCurrentRound());   // [ISSUE-28] 하한 1 클램프(위 RefreshTrack 동일 취지 · 셀/스크롤 정합)
        roadController?.RefreshStages(BuildStages(currentRound, false), currentRound);
    }

    // 구름 단계(1~totalRound) 데이터 구성(§2-2·§7-2). 상태: 통과 라운드=2 / 현재=1 / 예정=0.
    //  displayRound          — '현재'로 그릴 구름. 통상 서버 currentRound 지만, 결과 연출 대기 중에는 그 이전 라운드다(RefreshTrack 참고).
    //  displayRoundGoalFilled — displayRound 구름의 게이지를 목표치로 채워 그릴지(성공 연출 대기 — 코인은 이미 소각됐다).
    // [ISSUE-28] displayRound 하한 1 로 보정(호출부 RefreshTrack/RefreshStageValues 에서 이미 클램프됨) — RefreshProgress 의
    //  "트랙·열기구 정렬과 동일 규칙". 미시작(0)·미로드 창(seq 전환 직후 서버 info 미도착 → GetCurrentRound()=0)에서도 현재
    //  구름(state==1)이 존재하도록 하한을 둔다. 상한은 두지 않는다 — 완주 대기(displayRound=totalRound+1)면 전 셀이 클리어
    //  (state 2)로 그려져야 하므로 totalRound 로 조이면 최종 구름이 '현재'로 오표시된다.
    private List<DreamBalloonStageData> BuildStages(int displayRound, bool displayRoundGoalFilled)
    {
        var totalRound = EventDreamBalloonHelper.GetTotalRound();
        var curCoin = EventDreamBalloonHelper.GetCurrentCoin();

        var stages = new List<DreamBalloonStageData>(totalRound > 0 ? totalRound : 0);
        for (var round = 1; round <= totalRound; round++)
        {
            var goalCoin = EventDreamBalloonHelper.GetGoalCoin(round);
            var state = round < displayRound ? 2 : (round == displayRound ? 1 : 0);
            var currentStageCoin = displayRoundGoalFilled ? goalCoin : curCoin;
            var stageCoin = round < displayRound ? goalCoin : (round == displayRound ? currentStageCoin : 0);

            stages.Add(new DreamBalloonStageData
            {
                index = round - 1,   // 좌우 배치 기준(짝수 Left / 홀수 Right, 기획 "이벤트 단계 배치 예시")
                round = round,
                goalCoin = goalCoin,
                curCoin = stageCoin,
                state = state,
                isFinal = round == totalRound,
            });
        }

        return stages;
    }
}

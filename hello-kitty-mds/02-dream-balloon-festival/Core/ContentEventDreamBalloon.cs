using System;
using System.Collections.Generic;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using GameCore.Utils;

using GameLogic;
using GameLogic.Condition;
using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;
using GameLogic.Network;

using UnityEngine;

/// <summary>
/// 드림 벌룬 페스티벌 컨텐츠 컨트롤러 (구현 명세서 §7-1 · LiveEventType.DREAMBALLOON).
///
/// 서버 권위 = 진행 상태(EventBalloonInfo). 좌석 감소·순위·성공/실패 판정은 클라 시뮬(§7-3, <see cref="DreamBalloonSeatSimulator"/>).
/// 컨텐츠는 요청/적용(Request* → SyncFromServer)과 시뮬/노티파이어 오케스트레이션, 상태 전파(Message)를 담당한다.
///
/// ⚠️ 루트 팝업(UIPopupEventDreamBalloon*)·트랙 컨트롤러(DreamBalloonRoadController)는 프리팹 스크립트로 본 작업 범위에서 제외한다.
/// 팝업 오픈 지점은 로그 + TODO placeholder 로 두고, UI 제작 후 실제 UIManager 호출로 교체한다.
/// </summary>
public class ContentEventDreamBalloon : ContentLiveEventBase
{
    // 진행중 버튼 아이콘 어드레서블 주소(§3-8·§3-9). 아트: Resources_moved/UI/Images/Icon/MergeEventDreamBalloon_Progress.png
    // 쉬는중 아이콘은 Event_DreamBalloon_Setting.icon_rest 에서 주입(EventDreamBalloonHelper.GetRestIconPath).
    private const string PROGRESS_ICON_PATH = "MergeEventDreamBalloon_Progress";
    private const string RANK_SAVE_KEY_PREFIX = "DreamBalloonRank_";   // [ISSUE-06] 성공 확정 순간의 등수 영속화 키 — {prefix}{eventSeq}_{round}
    private const int FINAL_CLAIM_ROUND = 0;             // 최종 보상의 claim round — 패킷 정의상 0 (§8-2)
    private const float FINAL_CLAIM_TIMEOUT_SEC = 5f;    // 최종 보상 RS 대기 상한 — 무응답(서버 미구현) 시 연출이 막히지 않게 한다
    private const int LIDX_REST_NOTICE = 43510;   // "지금은 쉬는 중이에요! 다시 출발해 축제에 참여해봐요!"

    private readonly DreamBalloonSeatSimulator seatSimulator = new();
    private readonly DreamBalloonRestNotifier restNotifier = new();

    private DreamBalloonModel model;
    private int resolvedRound = -1;     // 라운드 결과 중복 전송 방지(라운드별 1회)
    private bool requestingResult;      // RQEventBalloonRoundResult in-flight guard
    private bool requestingInfo;        // RQEventBalloonInfo in-flight guard — 로그인 시 선조회·Initialize·OnOpenEvent 3중 발신을 1건으로 합친다
    private readonly List<Action> pendingInfoCallbacks = new();   // in-flight 중 들어온 onComplete — 응답 시 일괄 실행(각 호출부 후처리 보존)
    private bool listenersSubscribed;   // Initialize 재호출(UpdateActiveEvent 재활성화) 시 메시지/컨디션 리스너 재구독(누수) 방지
    private int unsentResultRound = -1; // 전송 실패로 서버 미확정인 라운드 — 재진입(EvaluateRoundOnOpen) 시 재전송
    private bool unsentResultSuccess;   // 미전송 결과의 성공/실패
    private int localFailRound = -1;    // 로컬에서 실패로 확정(전송은 팝업 진입까지 보류)한 라운드 — 이후 코인을 더 모아도 성공으로 뒤집지 않는다
    private int trophyProgressedRound = -1;   // [ISSUE-06] 트로피 진행도를 발행한 라운드 — resolvedRound 와 분리된 자체 1회 가드
    private StageResultMsg pendingResultPresentation;   // 팝업 미개방 중 확정된 결과 보관 → 팝업 진입 시 재생(§5-1 self-check, StageResultMsg 유실 대비)
    private readonly CancellationTokenSource contentCts = new();   // 컨텐츠 수명 토큰(Release 에서 취소) — 대기 태스크가 해제 후에도 도는 것 방지
    private bool roundStartRecruitPending;   // 라운드 시작 모집 연출(§4-3) 미소비 플래그 — 팝업 미개방(라운드 1) 시 진입 후 재생 대비
    private bool roundStartFromRest;         // 직전 쉬어가기(RequestRest) 경유 여부 — §4-7 재시작 놀람 연출 판정(모집 연출이 1회 소비)
    private bool roundStartPlayIn = true;    // 라운드 시작 시 열기구 등장(In) 여부 — 성공 후 바로 다음 라운드면 false(Idle 유지), 그 외 true(In). 모집 연출이 1회 소비
    private bool cloudIntroPending;          // 구름 등장(In) 연출을 이벤트 최초 시작에만 1회 재생하기 위한 플래그(ISSUE-13). RequestStart 에서 set, 첫 트랙 빌드에서 consume(그 외 라운드/재진입은 등장 스킵).
    private int pendingMergeCurrency;   // §4-2 머지 합치기 코인 연출 대기값 — 좌표는 콘텐츠 레이어에 없어 값만 캐시하고 UI 합치기 지점(PlayPendingCurrencyEffect)에서 재생한다.

    // 라운드 시작(경쟁자 모집) 연출 재생 구간 — 이 구간에는 라운드 결과를 확정하지 않고 보류한다(§4-3).
    // 연출 끝의 START 가 곧 라운드 시작이므로, 재생 중 좌석이 0이 돼도 판정을 미뤘다가 연출이 끝난 직후 처리한다.
    private bool roundStartPresenting;
    private bool deferredResultPending;   // 보류된 결과 존재
    private int deferredResultRound = -1;
    private bool deferredResultSuccess;

    /// <summary>현재 라운드 좌석 정원(§7-3 클라 권위 시뮬). 시뮬 미구동 시 0 — 테이블 폴백은 Helper 담당.</summary>
    public int SlotMax => seatSimulator.SlotMax;

    /// <summary>현재 라운드 잔여 좌석(§7-3). 좌석 감소는 `SeatChangedMsg` 로도 전파된다.</summary>
    public int RemainSeat => seatSimulator.RemainSeat;

    /// <summary>UI 데이터 진입점(SetInfo 대상). 상태 읽기는 캐싱된 서버 정보(EventDreamBalloonHelper)에 위임한다(§7-1a).</summary>
    public class DreamBalloonModel : IUIInfoData
    {
        public long eventId;
        public int eventSettingId;
        public int loadTableIdx;

        public int Difficulty => EventDreamBalloonHelper.GetDifficulty();
        public int CurrentRound => EventDreamBalloonHelper.GetCurrentRound();
        public int State => EventDreamBalloonHelper.GetState();
        public bool IsResting => EventDreamBalloonHelper.IsResting();
        public int LastRoundResult => EventDreamBalloonHelper.GetLastRoundResult();
        public int TotalRound => EventDreamBalloonHelper.GetTotalRound();
        public int CurCoin => EventDreamBalloonHelper.GetCurrentCoin();

        public int GetGoalCoin(int round) => EventDreamBalloonHelper.GetGoalCoin(round);
        public EventDreamBalloon_AiRoundData GetRoundInfo(int round) => EventDreamBalloonHelper.GetAiRound(round);
    }

    public DreamBalloonModel Model => model;

    #region 라이프사이클
    public override void Initialize(LiveEventData eventData)
    {
        base.Initialize(eventData);

        restNotifier.Bind(EventDreamBalloonHelper.GetEventSeq());
        restNotifier.StartCheck();   // 쉬는중 안내 조건 체크(UniTask 루프, 로비/머지 상주 §7-4)
        BuildModel();

        // 구독은 1회만 — Initialize 는 UpdateActiveEvent(재활성화·재로그인)에서 같은 인스턴스에 다시 호출될 수 있어,
        // 가드 없이 재구독하면 리스너가 누적돼 핸들러가 중복 실행된다(메시지 버스는 delegate 멀티캐스트). 해제는 Release 에서.
        if (!listenersSubscribed)
        {
            Message.AddListener<SeatChangedMsg>(OnSeatChanged);
            Message.AddListener<RestNotifyMsg>(OnRestNotify);

            // 쉬는중 안내 조건2(누적 사용 에너지 §7-4·§2-7) 집계 — 공용 컨디션 버스 구독.
            // 에너지 소모는 생성기 탭(CreateBlockController) 단일 지점이며, 그 자리에서 GeneratorEnergyUse 가 발행된다.
            ConditionDispatcher.Subscribe(OnGameCondition);

            listenersSubscribed = true;
        }

        // 게임 실행(활성 이벤트 초기화) 시 상태 복원(§8-3·§7-1a) + 상주 시뮬 구동(§7-3):
        // 이벤트 미진입(로비/머지)에서도 좌석 판정이 돌아, 화면 밖에서 성공/실패가 확정될 수 있게 한다.
        RequestInfo(OnInfoResolvedForLaunch);
    }

    public override void Release()
    {
        Message.RemoveListener<SeatChangedMsg>(OnSeatChanged);
        Message.RemoveListener<RestNotifyMsg>(OnRestNotify);
        ConditionDispatcher.Unsubscribe(OnGameCondition);
        listenersSubscribed = false;   // 재활성화 시 Initialize 가 다시 구독할 수 있도록 리셋
        seatSimulator.Stop();
        restNotifier.Stop();
        contentCts.Cancel();
        contentCts.Dispose();
        base.Release();
    }

    // 이벤트 비활성화 시 — 기간 종료(만료)일 때만 종료 연출(§4-6, 종료 기준: 기간 종료). 종료 팝업 노출(Carrot/Bingo/LuckyMatching 패턴).
    // 최종보상 획득 흐름(state 3 클리어 / 4 종료)은 Final→End 로 이미 처리되므로 제외한다. 만료 아닌 일반 비활성화는 조용히 종료(표준).
    public override void OnDeactiveEvent()
    {
        // 다른 컨텐츠(Carrot·ClawMachine 등)의 종료 처리와 동일하게 — 이벤트 비활성 = 종료로 간주한다.
        //  로그인/재접속 시 RegistLiveEventCloseTimer → CloseLiveEventTimerAsync → UpdateDeactiveEvent 로 호출된다.
        curEventState = LiveEventState.eventEnd;

        CloseOpenPopups();

        // 종료 팝업 노출(ISSUE-14). 완료(IsAllComplete = 최종 라운드까지 모든 보상 수령)한 유저는
        //  최종 연출 → _Final → _End 로 이미 종료 팝업을 봤으므로 중복 노출하지 않는다(Carrot 의 !IsAllComplete 가드와 동일).
        //  ⚠️ 구 구현의 `expired && state < 3` 가드는 운영툴 종료(서버 state=4)까지 함께 막아
        //     "재접속 시 종료 팝업 미노출"의 원인이었다 — 다른 컨텐츠처럼 IsAllComplete 기준으로 일원화한다.
        LiveEventData liveData = EventDataHelper.GetLiveEventData(CurEventType);
        if (EventDataHelper.IsExistEventData(liveData) && !IsAllComplete())
        {
            OpenEndPopupDeferredAsync().Forget();
        }

        restNotifier.Stop();
        restNotifier.ClearSavedData();      // 이벤트 종료 시 쉬는중 안내 저장 키 정리(§7-4 저장 계층)
        EventDreamBalloonHelper.ResetRoundCoin();   // 기획 §2 「이벤트 종료 시 보유중인 이벤트 재화 초기화」

        base.OnDeactiveEvent();
    }

    // UI 정리/전환과 겹치지 않게 한 박자 늦춰 종료 팝업을 연다(Carrot/Bingo 의 0.1s 지연 동일 취지).
    private async UniTaskVoid OpenEndPopupDeferredAsync()
    {
        await UniTask.Delay(TimeSpan.FromSeconds(0.1f));
        EventDreamBalloonHelper.OpenEndPopup();
    }

    // 비활성/종료 시 떠 있을 수 있는 드림벌룬 팝업을 닫는다(Carrot CloseAllCarrotPopups 패턴). 미오픈 시 null → ?. 안전 호출.
    private void CloseOpenPopups()
    {
        UIManager uiManager = UIManager.Instance;
        uiManager.FindUIWindow<UIPopupEventDreamBalloon>()?.Close();
        uiManager.FindUIWindow<UIPopupEventDreamBalloonSuccess>()?.Close();
        uiManager.FindUIWindow<UIPopupEventDreamBalloonFail>()?.Close();
        uiManager.FindUIWindow<UIPopupEventDreamBalloonFinal>()?.Close();
    }

    public override void Update()
    {
        base.Update();

        // 재화(코인) 획득 시 목표 달성(성공) 판정 — DreamBalloon 은 제네릭 이벤트 재화라 OnRefreshEventCurrencyMsg 미발신.
        // OnRefreshCurrencyAction 경로에만 의존하지 않고, 진행중 라운드에서 매 틱 목표 코인 도달을 폴링한다(가드: 시뮬 미구동 시 no-op).
        CheckRoundGoal();
    }

    public override void OnOpenEvent(Action MergeMoveCallback)
    {
        base.OnOpenEvent(MergeMoveCallback);
        // 표준 시작 라이프사이클(commonData.startTime 세팅) — 다른 라이브이벤트(LuckyMatching 등)와 동일.
        // 미호출 시 startTime 이 0으로 남아 IsBeforeInitEvent=true → IsActiveEvent=false →
        // HUD/머지 진입 버튼이 만료로 간주돼 SetActive(false) 된다.
        UpdateEventState();

        // [인트로 자동 오픈 — Carrot 동일 기준] 이벤트 미시작(before-init) = curEventState.eventReady 이면
        // 서버 응답(state)을 기다리지 않고 곧바로 인트로(시작 팝업)를 연다(당근 OpenEntryPopupByState 와 동일).
        // 자동 오픈은 컨트롤러(DefaultLiveEventController.UpdateActiveEvent)의 IsBeforeInitEvent 게이트로 최초 1회만 진입하며,
        // 위 UpdateEventState→UpdateData_SetStartEvent 가 startTime 을 박아 이후 진입부터는 eventReady 가 풀려 아래 서버 state 분기로 간다.
        if (curEventState == LiveEventState.eventReady)
        {
            OpenIntroPopup();       // 시작 팝업(자동안내 완료 시 난이도 선택 직행) — §5-1 #1
            // 여기서 RequestInfo 를 다시 보내지 않는다 — 이 진입(eventReady)은 활성화(UpdateActiveEvent) 경로이고,
            // 같은 호출에서 Initialize 가 방금 RequestInfo 를 보냈다(로그인 시 3중 발신의 세 번째 발신원). 진입 분기는 상태로 이미 결정된다.
            // (혹시 in-flight 라도 아래 RequestInfo 가드가 합쳐준다 — 표시 갱신은 그 응답의 SyncFromServer 로 처리된다.)
            return;
        }

        // 진행중/쉬는중/클리어/종료 등 '시작된' 상태의 진입(HUD·머지 명시적 진입 포함)은 서버 state 로 분기(§5·§8·§7-1a).
        RequestInfo(OnInfoResolvedForOpen);
    }

    // [시작 팝업 노출 제어] 드림 벌룬은 예약이 아니라 서버 state==0 이 인트로 관문이다(OnInfoResolvedForOpen).
    //  인트로를 닫기[X]로 나가면 state 가 0 으로 남아 다음 진입에도 다시 뜨므로, NEW 도 그때까지 유지한다.
    //  ※ 서버 정보 미수신은 GetState 가 기본값 0(미시작)을 돌려주므로 NEW 오점등이 된다 — 캐시 존재를 함께 확인한다.
    public override bool IsStartPopupPending()
    {
        return null != EventDreamBalloonHelper.GetServerInfo() && 0 == EventDreamBalloonHelper.GetState();
    }

    // 인트로(최초 진입) 팝업 — 자동안내 미완료면 시작 팝업(→설명→난이도), 완료(계정당 1회)면 난이도 선택 직행(§3-2·§5-1 #1).
    private void OpenIntroPopup()
    {
        // [시작 팝업 노출 제어 / 기획 991526937 · HL-1008] 드림 벌룬은 startTime 이 아니라 자체 관문(서버 state==0)으로
        //  인트로를 띄우므로, 머지판·로비 아이콘 진입처럼 오늘의 안내를 거치지 않은 노출도 여기서 원장을 소비한다.
        //  (소비하지 않으면 인트로를 이미 봤는데도 오늘의 안내 NEW 가 남는다)
        //  자동안내 완료로 난이도 선택 직행하는 경우도 소비한다 — 시작 팝업의 자리를 대체하는 진입 팝업이므로 노출로 본다.
        //  이 함수는 드림 벌룬 인트로의 단일 노출 지점이다 — OnOpenEvent(eventReady)·OnInfoResolvedForOpen(state 0) 양쪽이 여기로 모인다.
        LiveEventData introEventData = EventDataHelper.GetLiveEventData(CurEventType);
        if (null != introEventData)
            LiveEventStartPopupLedger.Consume(introEventData.eventID);

        if (DataManager.Instance.GetSkipShowInfo(UserFeatureFlag.SkipInfo_DreamBalloon, out _))
            EventDreamBalloonHelper.OpenSelectPopup();   // 자동안내 완료 → 바로 난이도 선택
        else
            EventDreamBalloonHelper.OpenStartPopup();    // 최초 → 시작 팝업(→ 설명 → 난이도)
    }

    // 이벤트 화면을 닫아도 좌석 시뮬은 멈추지 않는다(§7-3 상주) — 로비/머지에서 계속 판정.
    // 정지는 쉬어가기(RequestRest)·컨텐츠 해제(Release)·다음 라운드 시작(StartRound 내부 Stop) 시에만.
    // (OnCloseEvent 오버라이드 제거 — base 의 화면 비활성만 수행)

    public override void OnRefreshCurrencyAction()
    {
        base.OnRefreshCurrencyAction();
        CheckRoundGoal();   // 재화 게이지 갱신 시점마다 목표 달성(성공) 판정
    }

    /// <summary>
    /// 머지 진행(블록 합치기) 코인 수급(기획 §2 "머지 진행 시 소량의 코인 획득").
    /// 미션 달성 코인은 응답(rewardList)으로 클라 지갑에 반영되지만, 머지 합치기는 FakeServer 지갑에만 적립되고
    /// 응답에 재화가 실리지 않는다 → 여기서 클라 지갑으로 끌어온다. 동기화가 OnRefreshEventCurrencyMsg 를
    /// 발신하므로 게이지·구름 트랙이 함께 갱신되고, CheckRoundGoal 로 목표 달성 판정까지 이어진다.
    ///
    /// 재화 획득 연출(§4-2)은 합쳐진 블록 월드 좌표가 필요한데 콘텐츠 레이어엔 없다 → 획득값만 캐시하고
    /// 합치기 UI 서브콜백(<see cref="PlayPendingCurrencyEffect"/>, MergeBlockMain)에서 좌표와 함께 재생한다.
    /// </summary>
    protected override void OnGainEventCurrency(EEventCurrencyGainRoute gainRoute, List<RewardPacketData> rewardEventPackets)
    {
        if (rewardEventPackets.IsNullOrEmpty())
        {
            return;
        }

        DataManager.Instance.SynchronizeCurrency(CurrencyType.EventBalloon, false);

        int count = rewardEventPackets.Count;
        for (int i = 0; i < count; i++)
        {
            pendingMergeCurrency += rewardEventPackets[i].quantity;
        }
    }

    /// <summary>
    /// 머지 합치기 코인 연출 재생(§4-2) — 캐시된 획득값을 합쳐진 블록 위치에서 이벤트 버튼으로 날린다.
    /// 합치기 UI 서브콜백(MergeBlockMain, 서버 sim 이후)에서 호출한다. 대기값이 없으면 no-op.
    /// </summary>
    public void PlayPendingCurrencyEffect(Vector3 fromWorldPosition)
    {
        if (pendingMergeCurrency <= 0)
        {
            return;
        }

        // 머지 코인이 FakeServer 원장에 적립된 뒤(머지 sim 이후) 실행되는 시점 — 미러를 원장 기준으로 재동기화한다.
        // OnGainEventCurrency 의 SynchronizeCurrency 는 원장 적립 **전**에 발신돼 미러가 1 모자란 값으로 고착되므로, 여기서 바로잡는다(머지 버튼 게이지 -1 수정).
        DataManager.Instance.SynchronizeCurrency(CurrencyType.EventBalloon, false);

        int value = pendingMergeCurrency;
        pendingMergeCurrency = 0;
        EventResourceHelper.OnEffectLiveEventCurrency(fromWorldPosition, LiveEventType.DREAMBALLOON, EffectAcquire.GetGoodsDirectionCount(value), value, null);
    }

    protected override void OnProcessAfterInitalize()
    {
        base.OnProcessAfterInitalize();
        BuildModel();
    }

    // DreamBalloon 은 라운드/진행상태를 RQEventBalloonInfo·RoundStart 로 관리(§7-1a·§8)하므로,
    // 제네릭 라운드 초기화(base.OnStartEventRound → GetCurRound)는 생략한다.
    // (DreamBalloon 은 GetRoundChangeTargetCondition 미매핑 → None → "not found None" 로그만 유발.)
    // startTime 세팅(OnStartEvent)만 수행해 before-init 을 해제, 진입 버튼 노출에 필요한 부분만 태운다.
    protected override void UpdateData_SetStartEvent()
    {
        OnStartEvent();
    }

#if UNITY_EDITOR
    // 에디터(FakeServer) 진입 시퀀스 생략 경로 — 실서버 없이도 startTime 을 세팅해 진입 버튼이 노출되도록
    // 표준 시작 라이프사이클을 태운다(LuckyMatching 동일 패턴).
    public override void OnOpenEventWithoutUISequence()
    {
        base.OnOpenEventWithoutUISequence();
        LiveEventData liveEventData = EventDataHelper.GetLiveEventData(CurEventType);
        if (null == liveEventData)
        {
            DLogger.Error($"not found LiveEventData : {CurEventType}");
            return;
        }

        UpdateEventState();
    }
#endif
    #endregion

    #region 서버 요청/적용 (Request* → SyncFromServer)
    // 이벤트 정보 조회(게임 실행 시 §8-3). 응답 적용 후 콜백.
    public void RequestInfo(Action onComplete = null)
    {
        // 이벤트가 없거나 기간이 아니면 **송신하지 않는다**(인형뽑기·카페와 동일 가드, EventDreamBalloonHelper.RequestInfoAsync 와 같은 기준).
        //   완료(IsCompleteEvent)만 예외로 통과 — 미수령 보상·종료 처리에 최신 state 가 필요하다.
        //   ⚠️ eventSeq 만 보면 부족하다: 이벤트 데이터가 남아 있는 한 기간이 끝났거나 미운영이어도 seq 가 양수라 패킷이 나간다.
        //   onComplete 는 **반드시 호출**한다 — 로비 선조회·진입 흐름이 이 콜백을 기다리므로 끊으면 시퀀스가 멈춘다.
        LiveEventData liveEventData = EventDataHelper.GetLiveEventData(CurEventType);
        if (!EventDataHelper.IsValidData(liveEventData) && !EventDataHelper.IsCompleteEvent(liveEventData))
        {
            onComplete?.Invoke();
            return;
        }

        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        if (eventSeq <= 0)
        {
            onComplete?.Invoke();
            return;
        }

        // in-flight 합치기 — 이미 조회 중이면 패킷을 새로 보내지 않고 콜백만 큐에 얹는다(응답 시 일괄 실행).
        //   로그인 시 선조회(RequestInfoAsync)·Initialize·OnOpenEvent 가 같은 프레임에 연달아 요청해도 RQEventBalloonInfo 는 1건만 나간다.
        //   각 호출부의 onComplete 후처리(OnInfoResolvedForLaunch/ForOpen 등)는 응답 뒤에 그대로 보존·실행된다.
        if (requestingInfo)
        {
            if (null != onComplete)
            {
                pendingInfoCallbacks.Add(onComplete);
            }
            return;
        }

        // 로그인(활성 이벤트 초기화) 시 상태 저장 — 응답 EventBalloonInfo 의 state 가 클라 캐시의 최종 기준이다(§7-1a).
        // 런타임 전이(쉬어가기/라운드 시작)로 바뀐 state 도 다음 로그인 때 이 값으로 재확정된다.
        requestingInfo = true;
        WrapWebManager.Instance.RequestEventBalloonInfo(eventSeq, data =>
        {
            requestingInfo = false;

            // 실패 시 캐시가 갱신되지 않았으므로 view 를 새로 그리지 않는다. 진입 흐름(onComplete)은 끊지 않는다.
            if (IsResponseOk(data, "RQEventBalloonInfo"))
            {
                SyncFromServer();
                DropStalePendingResultOnInfo();   // [ISSUE-15] 갱신된 서버 상태와 모순되는 보관 결과를 끊는다
            }

            onComplete?.Invoke();

            // in-flight 중 합쳐진 콜백들을 응답 이후 일괄 실행(각 호출부 후처리 보존). 실행 중 재요청이 쌓일 수 있어 스냅샷 후 비운다.
            if (pendingInfoCallbacks.Count > 0)
            {
                Action[] snapshot = pendingInfoCallbacks.ToArray();
                pendingInfoCallbacks.Clear();

                int count = snapshot.Length;
                for (int i = 0; i < count; i++)
                {
                    snapshot[i]?.Invoke();
                }
            }
        });
    }

    /// <summary>
    /// [ISSUE-15] `RQEventBalloonInfo` 로 갱신된 서버 상태가 **보관 중인 결과**(<see cref="pendingResultPresentation"/>)와
    /// 모순되면 그 결과를 폐기한다.
    ///
    /// 재접속/재로그인(<c>WebManager.ReLoginProcessAsync</c>)은 컨텐츠 인스턴스를 새로 만들지 않으므로 pending 이 그대로
    /// 살아남는다. 그 사이 서버가 **클리어-이전 스냅샷**(state 2 · currentRound ≤ pending.round)을 돌려주면,
    /// 팝업 진입 시 그 stale 결과가 소비되며 "서버는 미완료로 보는 라운드"에 <c>RewardClaim</c> 이 나간다
    /// (ISSUE-15 로그 시그니처: `RSEventBalloonInfo state=2` → `RQEventBalloonRewardClaim`). 여기서 그 연결을 끊는다.
    ///
    /// ⚠️ **Info 응답 경로에서만 호출한다.** `RoundResult` 성공 콜백이 **갓 만든** pending 은 대상이 아니다 —
    ///    그 경로에서 함께 호출하면 서버가 currentRound 를 즉시 올려주지 않는 경우 정상 보상까지 막혀
    ///    **ISSUE-16(보상 미지급)이 재발**한다. 이 분리가 두 이슈를 동시에 만족시키는 핵심이다.
    ///
    /// 폐기 후에는 <see cref="resolvedRound"/> 를 열어 재확정 경로(EvaluateRoundOnOpen → ResolveRoundResult)가
    /// 서버로 클리어를 다시 커밋하고, 그 응답으로 연출·보상이 정상 흐름을 탄다.
    /// </summary>
    private void DropStalePendingResultOnInfo()
    {
        if (null == pendingResultPresentation)
        {
            return;
        }

        // 서버가 그 라운드를 통과 처리했으면(진행중이 아니거나 currentRound 가 넘어갔으면) 정상 결과 — 유지한다.
        int pendingRound = pendingResultPresentation.round;
        if (!EventDreamBalloonHelper.IsRoundProgressing() || EventDreamBalloonHelper.GetCurrentRound() > pendingRound)
        {
            return;
        }

        DLogger.Error($"[DreamBalloon] stale 결과 폐기 — 서버 미클리어(state:{EventDreamBalloonHelper.GetState()} currentRound:{EventDreamBalloonHelper.GetCurrentRound()} pendingRound:{pendingRound})");

        pendingResultPresentation = null;
        resolvedRound = -1;   // 재확정 허용 — 다음 판정에서 서버로 다시 커밋한다
        SendIconRefresh();    // 진급/실패 '확인 대기' 표시 해제(아이콘·레드닷 재평가)
    }

    // 난이도 선택 및 이벤트 시작(§3-3). difficulty 1: 쉬움 / 0: 보통 / 2: 어려움 (테이블과 동일 인코딩).
    // onStarted: 서버 확정(SyncFromServer) 후 호출 — 메인 팝업은 이 시점에 연다(응답 전 열면 미시작 데이터로 그려짐).
    // onFailed : 서버 실패 시 호출 — 난이도 선택 화면을 유지한다.
    public void RequestStart(int difficulty, Action onStarted = null, Action onFailed = null)
    {
        roundStartPlayIn = true;    // 최초 라운드 시작 — 열기구 등장(In)
        cloudIntroPending = true;   // 이벤트 최초 시작 — 구름 등장(In) 연출 1회 허용(ISSUE-13). 그 외 라운드 변경·재진입은 등장 스킵.
        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        if (eventSeq <= 0)
        {
            onStarted?.Invoke();   // 서버 미연결(에디터 등) — 진입 흐름은 끊지 않는다
            return;
        }

        WrapWebManager.Instance.RequestEventBalloonStart(eventSeq, difficulty, data =>
        {
            if (!IsResponseOk(data, "RQEventBalloonStart(난이도 결정)"))
            {
                onFailed?.Invoke();   // 난이도·이벤트 시작은 서버 확정 사항 — 시뮬·팝업 진행 안 함
                return;
            }

            SyncFromServer();
            ApplyLocalState(EventDreamBalloonHelper.PROGRESS_STATE);   // [ISSUE-17] 최초 라운드 시작도 진행 상태를 로컬 확정 — 응답 state 누락/지연(이벤트 종료>재시작 창)에도 게이지(showGauge) 미노출 방지. RequestNextRoundStart(:441)·RequestRest(:567) 와 동일 방어.
            EventDreamBalloonHelper.MarkRoundStartNow();   // ISSUE-18: roundStartAt 을 now 로 갱신 → 좌석 시뮬 만석 재개(stale 값 즉시 실패 방지)
            EventDreamBalloonHelper.ResetRoundCoin();   // 1라운드 시작 — 코인 0부터 센다
            trophyProgressedRound = -1;                // [ISSUE-06] 새 이벤트 참여 — 이전 참여의 발행 가드가 남지 않게 한다
            StartCurrentRoundSimulation();
            SendRoundStarted();   // 라운드 1도 경쟁자 모집 연출(§4-3) — 메인 팝업 진입 시 재생(pending 소비)
            onStarted?.Invoke();
        });
    }

    // 다음 단계 시작 / 다시 시작하기(진행중 진입, §3-5·§3-6).
    // 시작 버튼은 메인(쉬는중)·성공 팝업·실패 팝업 3곳에 있다. 게임 시작 연출(§4-3 경쟁자 모집)은
    // 버튼별 콜백이 아니라 **서버 확정 시점에 RoundStartedMsg 를 방송**해 메인 팝업이 단일 수신자로 재생한다
    // (결과 팝업 버튼은 콜백을 넘기지 않아 연출이 통째로 스킵되던 문제 해소).
    // onStarted: 서버 응답 반영(SyncFromServer) 후 호출 — 호출부 후처리용(연출 재생은 메시지가 담당).
    // onFailed : 서버 실패 시 호출 — 호출부가 버튼/입력 잠금을 되돌린다(연출·라운드 진행은 하지 않는다).
    public void RequestNextRoundStart(Action onStarted = null, Action onFailed = null)
    {
        // 열기구 등장(In) vs Idle 유지 판정 — 상태 전이 전(prior)에 캡처한다.
        //   성공(GetLastRoundResult==1) 후 바로 다음 라운드(쉬는 것 없이)면 열기구가 이미 도착 구름에 있어 Idle 유지,
        //   실패 재시작·쉬는 후 도전(메인 팝업)은 In(등장).
        roundStartPlayIn = roundStartFromRest || EventDreamBalloonHelper.GetLastRoundResult() != 1;

        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        if (eventSeq <= 0)
        {
            // 서버 미연결(에디터 등) — 연출 흐름은 끊지 않는다.
            SendRoundStarted();
            onStarted?.Invoke();
            return;
        }

        int startingRound = EventDreamBalloonHelper.GetCurrentRound();
        seatSimulator.ClearSavedSeat(eventSeq, startingRound);
        ClearAchievedRank(eventSeq, startingRound);   // [ISSUE-06] 재도전 시 이전 시도의 확정 등수가 남지 않게 한다
        restNotifier.OnNextRoundStart();

        // 진행 상태 전송 → 서버 저장 → 클라 캐싱(§7-1a). 쉬어가기(actionType 2)와 대칭.
        WrapWebManager.Instance.RequestEventBalloonRoundStart(eventSeq, 1, data =>
        {
            if (!IsResponseOk(data, "RQEventBalloonRoundStart(라운드 시작)"))
            {
                onFailed?.Invoke();   // 라운드는 서버가 확정한다 — 시작하지 않고 UI 잠금만 해제
                return;
            }

            SyncFromServer();
            ApplyLocalState(EventDreamBalloonHelper.PROGRESS_STATE);   // 서버 저장 확정 → 진행 상태 캐싱
            EventDreamBalloonHelper.MarkRoundStartNow();   // ISSUE-18: roundStartAt 을 now 로 갱신 → 좌석 시뮬 만석 재개(stale 값 즉시 실패 방지)
            EventDreamBalloonHelper.ResetRoundCoin();   // 다음 단계/재시작 — 이 라운드 코인 0부터 센다(실패 후 재도전 포함)
            StartCurrentRoundSimulation();
            SendIconRefresh();   // 진행중 아이콘으로 교체(§7-4)
            SendRoundStarted();  // 경쟁자 모집 연출(§4-3) — 메인 팝업이 수신해 재생한다
            onStarted?.Invoke();
        });
    }

    // 라운드 시작 확정 방송 — 어느 팝업의 시작 버튼을 눌렀든 연출 트리거는 이 한 곳이다(§4-3).
    // 메인 팝업이 열려 있으면(라운드 2+) 라이브 수신(OnRoundStarted), 닫혀 있으면(라운드 1 = 난이도 선택 직후)
    // 메시지를 놓치므로 pending 을 세워 진입 시 SetInfo 에서 소비·재생하게 한다.
    private void SendRoundStarted()
    {
        roundStartRecruitPending = true;
        Message.Send(new RoundStartedMsg { round = EventDreamBalloonHelper.GetCurrentRound() });

        // ISSUE-19: 라운드 시작(진행중 전이) 시 머지판 오더 미션을 전체 재빌드해 손님 위 이벤트 재화 노출을 복원한다.
        // 재화 노출(UIOrderMissionItem.SetEventRewardCurrency)은 전체 재빌드(SetOrderMissionItem) 시점의
        // IsRoundProgressing()(state==2) 게이트로만 결정된다. 쉬는중(state 1)에 만들어진 미션은 코인이 숨겨진 채 남고,
        // 진행중 복귀 시 재빌드 트리거가 없어(RefreshDataMergeMissionMsg 미발신) 계속 미노출됐다.
        // 이 지점은 상태가 진행중(2)으로 확정된 뒤이므로(RequestStart SyncFromServer / RequestNextRoundStart ApplyLocalState) 게이트를 통과한다.
        Message.Send(new RefreshDataMergeMissionMsg());
    }

    /// <summary>라운드 시작 모집 연출을 아직 재생하지 않았으면 true 를 돌려주고 플래그를 내린다(1회성). 라이브 수신·진입 self-check 공용.</summary>
    public bool ConsumeRoundStartRecruit()
    {
        if (!roundStartRecruitPending)
        {
            return false;
        }

        roundStartRecruitPending = false;
        return true;
    }

    /// <summary>직전에 쉬어가기(RequestRest)를 거쳤으면 true 를 돌려주고 플래그를 내린다(§4-7 재시작 놀람 연출 1회성).</summary>
    public bool ConsumeRoundStartFromRest()
    {
        bool fromRest = roundStartFromRest;
        roundStartFromRest = false;
        return fromRest;
    }


    /// <summary>이번 라운드 시작에서 열기구 등장(In)을 재생할지 여부(성공 후 바로 다음 라운드면 false=Idle 유지). 1회 소비 후 기본 true(In) 복원.</summary>
    public bool ConsumeRoundStartPlayIn()
    {
        bool playIn = roundStartPlayIn;
        roundStartPlayIn = true;
        return playIn;
    }

    /// <summary>구름 등장(In) 연출을 이번 트랙 빌드에서 재생할지 여부 — 이벤트 최초 시작에만 true. 1회 소비 후 false(그 외 라운드 변경·재진입은 등장 스킵, ISSUE-13).</summary>
    public bool ConsumeCloudIntro()
    {
        bool pending = cloudIntroPending;
        cloudIntroPending = false;
        return pending;
    }

    /// <summary>
    /// 라운드 시작(경쟁자 모집) 연출의 재생 구간을 알린다(§4-3) — 메인 팝업이 재생 시작/종료 시 호출한다.
    ///
    /// 이 구간에는 라운드 결과를 확정하지 않는다. 연출 끝의 START 가 곧 라운드 시작이므로, 재생 도중
    /// 좌석이 0이 돼도(재접속 직후처럼 좌석이 이미 소진된 경우) 실패를 바로 확정하면 시작 연출이 뚝 끊기고
    /// 실패 연출로 넘어가 버린다. 판정을 보류했다가 연출이 끝난 직후에 처리한다.
    ///
    /// 좌석 시뮬은 그대로 돌고(순위·잔여 좌석은 유지), **결과 확정(서버 전송)과 연출만** 미룬다.
    /// </summary>
    public void SetRoundStartPresenting(bool presenting)
    {
        roundStartPresenting = presenting;
        if (presenting)
        {
            return;
        }

        FlushDeferredRoundResult();
    }

    // 연출 중 보류해 둔 라운드 결과를 확정한다(연출 종료 직후 1회).
    private void FlushDeferredRoundResult()
    {
        if (!deferredResultPending)
        {
            return;
        }

        deferredResultPending = false;
        int round = deferredResultRound;
        bool success = deferredResultSuccess;
        deferredResultRound = -1;

        ResolveRoundResult(round, success);
    }

    // 쉬어가기(쉬는중 진입, §3-5·§3-6).
    public void RequestRest()
    {
        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        if (eventSeq <= 0)
        {
            return;
        }

        int prevState = EventDreamBalloonHelper.GetState();
        seatSimulator.Stop();

        // 쉬는 상태 전송 → 서버 저장 → 클라 캐싱(§7-1a). 서버가 확정하기 전에는 클라가 state 를 바꾸지 않는다.
        WrapWebManager.Instance.RequestEventBalloonRoundStart(eventSeq, 2, data =>
        {
            if (!IsResponseOk(data, "RQEventBalloonRoundStart(쉬어가기)"))
            {
                // 상태 전이 실패 — 미리 멈춘 좌석 시뮬을 원래대로 되돌린다(진행중이었다면 계속 감소해야 한다).
                if (prevState == EventDreamBalloonHelper.PROGRESS_STATE)
                {
                    StartCurrentRoundSimulation();
                }

                return;
            }

            SyncFromServer();
            ApplyLocalState(EventDreamBalloonHelper.REST_STATE);   // 서버 저장 확정 → 쉬는 상태 캐싱
            roundStartFromRest = true;   // §4-7 — 다음 라운드 시작 시 파트너 놀람 연출(RunRecruitAsync 가 1회 소비)
            restNotifier.OnEnterRest();
            SendIconRefresh();   // 쉬는중 아이콘으로 교체(§7-4)

            // [ISSUE-20] 쉬어가기(쉬는중) 전이 시 손님 오더 미션을 전체 재빌드해 코인 노출을 제거한다(기획 §5-3 "휴식 중 미션 달성 미획득").
            //  코인 노출은 재빌드(SetOrderMissionItem) 시점의 IsOnLiveEvent(=IsRoundProgressing, state==2) 게이트로만 결정되므로,
            //  쉬는중(state 1) 전이 후 재빌드 트리거가 없으면 진행중에 켜진 코인이 그대로 남는다(ISSUE-19 진행중 노출의 반대 방향).
            Message.Send(new RefreshDataMergeMissionMsg());
        });
    }

    /// <summary>
    /// 라운드 클리어 보상 지급(§3-4 ⑨ 단계 보상 · §3-7 최종 보상).
    ///
    /// **수령 기록의 권위 = 서버 `RQEventBalloonRewardClaim`**(패킷 정의서 기준, §8-2).
    ///   · 단계 보상: `rewardType 1` + `round N`
    ///   · 최종 보상: `rewardType 2` + `round 0`  ← **최종은 round 를 0 으로 보낸다**
    ///   · 응답 `claimedRewardList` 가 **수령 여부의 단일 소스**이며 `DataManager.EventBalloonClaimedRewards` 에 캐싱된다.
    /// 이벤트 **종료(state 4) 판정도 서버 몫**이다 — 클라가 로컬로 만들지 않는다(§7-1a).
    ///
    /// **아이템 지급은 클라(§8-5 재화 권위)** 가 공용 경로 `EventDataHelper.CompleteEventRewards` 로 수행한다.
    /// 수령 패킷 응답에는 지급 아이템(`Reward[]`)이 없고 `claimedRewardList` 만 오므로, 서버는 **기록**만 하고
    /// **지급은 클라 원장**이 한다(다른 라이브이벤트와 동일). 두 경로는 역할이 다르며 **둘 다 필요하다.**
    ///
    /// 🔴 **보상 아이템이 없는 라운드는 건너뛴다**(`HasStageReward` 게이트). `Event_RewardGroup` 에는 라운드 1~10 행이
    /// **모두** 있고(목표 코인 `goalValue2` 관문), 구 구현은 `rewardRow == null` 만 걸러내 **아이템 없는 라운드까지
    /// `CompleteEventRewards` 로 `Receive` 마킹**해 10 라운드 클리어 시 **그룹 10개 행이 전부 Receive** → 공용 완료 판정
    /// (`LiveEventData.IsCompleteEvent`)이 참 → `IsActiveEvent` false → `DefaultLiveEventController.OpenEvent` 가
    /// **로그도 없이 조용히 return** → **최종 미션 클리어 직후 진입 버튼 먹통**이 됐다(ISSUE-21/§10-2 35).
    ///
    /// ⚠️ **이 게이트만으로는 더 이상 막지 못한다(2026-07-19).** 당시엔 실제 아이템이 5·10 라운드에만 있어 8개 관문 행이
    /// 미수령으로 남는 데 의존한 방어였는데, **2026-07-16 CSV 재발행으로 10개 라운드 전부 보상이 발행**되면서
    /// `HasStageReward` 가 전 라운드에서 참이 되어 같은 증상이 재발했다(CSV 가 권위이므로 CSV 결함이 아니라 **전제가 낡은 것**).
    /// → 근본 차단은 <see cref="EventDataHelper.IsActiveEvent"/> 의 **DREAMBALLOON 전용 분기**(완료 판정 = 서버 `state 4`)가 담당한다.
    /// 이 게이트는 "아이템 없는 행에 헛수령 기록을 남기지 않는다"는 **본래 의미로만** 유지한다.
    /// </summary>
    private void GrantRoundReward(int round)
    {
        int difficulty = EventDreamBalloonHelper.GetDifficulty();
        if (!EventDreamBalloonHelper.HasStageReward(round, difficulty))
        {
            return;   // 보상 아이템이 없는 관문 라운드 — 지급도 수령 기록도 하지 않는다(위 🔴)
        }

        // 최종 라운드 = 최종 보상(rewardType 2, round 0). 그 외 = 단계 보상(rewardType 1, round N). (§8-2)
        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        bool isFinal = totalRound > 0 && round >= totalRound;

        // ⚠️ **최종 보상은 여기서 지급하지 않는다.** 기획 확정 순서(2026-07-19):
        //    마지막 라운드 클리어 → 메인 팝업 이동 → RQEventBalloonRewardClaim → **RS 수신 후 지급** → 최종 연출 → _Final → End.
        //    → 최종분은 <see cref="ClaimFinalRewardAsync"/> 가 팝업 진입 시점에 처리한다.
        //    (단계 보상도 ISSUE-22 으로 팝업 진입 시점 지급이 됐으나, 최종은 RS 대기가 필요해 경로가 다르다.)
        if (isFinal)
        {
            return;
        }

        // 중복 수령 방지 — 서버 기록(claimedRewardList)이 단일 소스다. 재접속·재판정으로 같은 라운드가 다시 들어와도 1회만.
        if (EventDreamBalloonHelper.IsRewardClaimed(EventDreamBalloonHelper.REWARD_TYPE_STAGE, round))
        {
            DLogger.Log($"[DreamBalloon] 보상 이미 수령됨 — 스킵 round:{round}");
            return;
        }

        // ⚠️ [ISSUE-16] 여기에 "서버 미클리어면 수령 보류" 가드를 두지 않는다(2026-07-23 제거).
        //   ISSUE-15(미완료 상태 보상 요청) 방지용으로 넣었으나, 서버가 RoundResult 응답에서 currentRound 를 즉시
        //   올려주지 않는 경우 **정상 클리어의 보상까지 차단**해 ISSUE-16(보상 미지급)을 재발시킬 수 있다.
        //   보상 지급 보장이 우선이므로 가드를 두지 않는다. ISSUE-15 은 클리어 커밋을 팝업 진입 시점으로 이연한
        //   ResolveRoundResult 의 `success && !isOnEvent` 게이트가 주 경로를 이미 차단한다.
        RequestRewardClaim(EventDreamBalloonHelper.REWARD_TYPE_STAGE, round);   // 서버 수령 기록(§8-2)
        GrantRewardItems(round, difficulty);                                    // 아이템 지급(클라 원장, §8-5)
    }

    /// <summary>
    /// 최종 보상 수령 — **RQEventBalloonRewardClaim 응답(RS)을 받은 뒤에 지급**한다(기획 확정 2026-07-19).
    /// 호출 시점은 "마지막 라운드 클리어 후 메인 팝업 진입", 즉 **최종 연출 재생 전**이다.
    ///
    /// 단계 보상(<see cref="GrantRoundReward"/>)과 달리 RS 를 기다리는 이유는 기획이 지정한 순서이기 때문이다.
    /// 이미 수령했거나(재진입) 보상 행이 없으면 즉시 완료 — 중복 지급 없음(단일 소스는 서버 claimedRewardList).
    ///
    /// ⚠️ 서버가 이 패킷을 미구현이면 응답이 오지 않는다(§10-2 서버 확인 대상) → **타임아웃으로 흐름을 풀어** 최종 연출·팝업이
    ///    영영 안 뜨는 것을 막는다. 타임아웃 시에는 지급하지 않으며(기획 "RS 오면 보상 지급"), 수령 기록이 없으므로
    ///    다음 진입에서 이 경로가 다시 시도한다.
    /// </summary>
    public UniTask ClaimFinalRewardAsync()
    {
        int totalRound = EventDreamBalloonHelper.GetTotalRound();
        int difficulty = EventDreamBalloonHelper.GetDifficulty();

        if (EventDreamBalloonHelper.IsRewardClaimed(EventDreamBalloonHelper.REWARD_TYPE_FINAL, FINAL_CLAIM_ROUND)
            || !EventDreamBalloonHelper.HasStageReward(totalRound, difficulty))
        {
            return UniTask.CompletedTask;
        }

        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        if (eventSeq <= 0)
        {
            // 서버 미연결(에디터 등) — 연출 흐름을 끊지 않도록 즉시 지급한다(다른 요청 경로와 동일 철학).
            GrantRewardItems(totalRound, difficulty);
            ApplyEventEndLocally();
            return UniTask.CompletedTask;
        }

        UniTaskCompletionSource claimed = new();
        WrapWebManager.Instance.RequestEventBalloonRewardClaim(eventSeq, EventDreamBalloonHelper.REWARD_TYPE_FINAL, FINAL_CLAIM_ROUND, data =>
        {
            if (IsResponseOk(data, "RQEventBalloonRewardClaim(최종)"))
            {
                GrantRewardItems(totalRound, difficulty);   // RS 수신 후 지급(§8-5)
                ApplyEventEndLocally();                     // 최종 보상 획득 = 이벤트 종료(클라 처리)
            }

            claimed.TrySetResult();
        });

        return WaitClaimOrTimeoutAsync(claimed.Task);
    }

    /// <summary>
    /// 최종 보상 획득 직후 **클라가 이벤트 종료(state 4)를 확정**한다(사용자 요구 2026-07-19).
    ///
    /// 종전에는 종료를 서버 권위로만 두어(`state 4` 수신 대기) 서버가 안 내려주면 머지/HUD 버튼이 완료 상태로 남았다.
    /// 진행 상태는 재접속 때 `RQEventBalloonInfo` 로 다시 받아오므로, **서버 값이 오면 그 값이 이 로컬 전이를 덮는다**
    /// (SyncFromServer). 즉 로컬 전이는 "서버 응답 전까지의 화면 정합"만 담당하고 데이터 권위를 뺏지 않는다.
    ///
    /// 지급이 성립한 경우에만 부른다 — RS 실패·무응답으로 미지급이면 종료로 찍지 않아야 다음 진입에서 재시도된다.
    /// </summary>
    private void ApplyEventEndLocally()
    {
        ApplyLocalState(EventDreamBalloonHelper.END_STATE);

        // ⚠️ [ISSUE-16] `EventDreamBalloonHelper.MarkAllRewardsReceived()` 호출을 제거한다(2026-07-23).
        //   그 보정은 **실제로 지급한 적 없는 보상행까지 전부 Receive 로 마킹**하고, 주석대로 Fs 백업까지 기록해
        //   재접속 후에도 유지된다. 그런데 Fs 의 수령 판정(FsEventDataHandlerBase.TryCompleteReward)은 이미
        //   Receive 인 행을 만나면 "Call Complete Reward twice" 로 **false 를 반환해 지급을 통째로 건너뛴다**
        //   (FsProcessEvent.DirectApi_CompleteEventRewards). 즉 한 번 완주한 계정은 같은 eventSeq 로 이벤트가
        //   재시작돼도 마킹이 남아 **이후 모든 라운드의 보상이 차단**된다(= ISSUE-16 "보상 미지급").
        //
        //   이 보정의 원래 목적(공용 완료 게이트 통과, ISSUE-23)은 이미 사라졌다 — 진입 게이트는
        //   EventDataHelper.IsActiveEvent 의 DREAMBALLOON 분기(완료 = 서버 state 4, ISSUE-17)로 이전됐고,
        //   완료 표시/레드닷도 EventButtonUIHelper.IsActiveReddot 에서 서버 state 기준(!IsEnded())으로 옮겼다.
        //   따라서 이 마킹을 보는 완료 판정 경로가 더 이상 없으므로 제거해도 회귀가 없고,
        //   실제 수령 기록의 단일 소스는 서버 claimedRewardList 이므로 중복 지급 위험도 없다.
        EventButtonUIHelper.ResfreshUIButtons();            // 로비/머지/오늘안내 아이콘 즉시 갱신(숨김 반영)
    }

    // 재진입 경로(연출 없이 _Final 직행) — 수령을 먼저 끝내고 팝업을 연다.
    private async UniTaskVoid ClaimFinalThenOpenPopupAsync()
    {
        await ClaimFinalRewardAsync();
        EventDreamBalloonHelper.OpenFinalPopup();
    }

    // RS 대기 — 무응답(서버 미구현)일 때 최종 연출이 막히지 않도록 상한을 둔다.
    private async UniTask WaitClaimOrTimeoutAsync(UniTask claimTask)
    {
        int result = await UniTask.WhenAny(claimTask,
            UniTask.Delay(TimeSpan.FromSeconds(FINAL_CLAIM_TIMEOUT_SEC), DelayType.UnscaledDeltaTime,
                PlayerLoopTiming.Update, contentCts.Token));

        if (result != 0)
        {
            DLogger.Error($"[DreamBalloon] 최종 보상 RS 무응답({FINAL_CLAIM_TIMEOUT_SEC}s) — 지급 없이 연출 진행. 다음 진입에서 재시도한다.");
        }
    }

    /// <summary>
    /// 보상 수령을 서버에 기록한다(`RQEventBalloonRewardClaim`, §8-2). 응답 `claimedRewardList` 는
    /// `WrapWebManager.OnResponseEventBalloonRewardClaim` 이 `DataManager` 에 캐싱한다 → 이후 중복 수령 판정의 소스.
    ///
    /// ⚠️ 지급(아이템)은 이 패킷이 하지 않는다(응답에 `Reward[]` 없음). 실패해도 **지급 흐름은 끊지 않는다** —
    /// 재화 권위가 클라이므로(§8-5) 아이템은 이미 지갑에 들어간다. 다만 기록이 유실되면 재접속 시 중복 지급 가능성이
    /// 있으므로 실패를 로그로 남긴다. **서버 미구현 시 이 호출은 무응답**이 될 수 있다(§10-2 — 서버팀 확인 필요).
    /// </summary>
    private void RequestRewardClaim(int rewardType, int round)
    {
        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        if (eventSeq <= 0)
        {
            return;   // 서버 미연결(에디터 등) — 지급 흐름은 끊지 않는다
        }

        WrapWebManager.Instance.RequestEventBalloonRewardClaim(eventSeq, rewardType, round, data =>
        {
            if (!IsResponseOk(data, "RQEventBalloonRewardClaim"))
            {
                return;   // 기록 실패 — 지급은 이미 완료(클라 원장). 다음 RQEventBalloonInfo 로 목록이 재확정된다
            }

            DLogger.Log($"[DreamBalloon] 보상 수령 기록 완료 rewardType:{rewardType} round:{round}");
        });
    }

    // 아이템 실지급 — 공용 표준 경로(Carrot/Bingo/Doughnut 동일). 보상 인덱스 = Event_RewardGroup 해당 행의 index(§2-2).
    private void GrantRewardItems(int round, int difficulty)
    {
        EventRewardGroupTableData rewardRow = EventDreamBalloonHelper.GetRewardRow(round, difficulty);
        LiveEventData liveEventData = EventDataHelper.GetLiveEventData(CurEventType);
        if (null == rewardRow || !EventDataHelper.IsExistEventData(liveEventData))
        {
            return;
        }

        EventDataHelper.CompleteEventRewards(liveEventData, out RewardPacketData[] rewards, true, rewardRow.index);
        DLogger.Log($"[DreamBalloon] 보상 지급 round:{round} rewardIndex:{rewardRow.index} count:{rewards?.Length ?? 0}");
    }

    /// <summary>
    /// 응답 성공 여부. **반드시 상태 전이 전에 검사한다.**
    ///
    /// ⚠️ `WebManager.ParsePacket` 은 등록 핸들러(`OnResponseEventBalloon*`)를 먼저 부르고 요청별 콜백을 나중에 부르는데,
    /// 등록 핸들러는 `code != 0` 이면 `DataManager` 를 갱신하지 않고 early return 한다. 그런데 요청별 콜백은 **실패해도 그대로 호출된다.**
    /// 따라서 콜백이 code 를 보지 않으면 서버 실패를 "성공한 척" 통과시켜, 갱신되지 않은 옛 상태로 view 를 그린다.
    /// </summary>
    private bool IsResponseOk(RSCallBackData data, string api)
    {
        // ⚠️ **code 만 보면 안 된다.** 네트워크 끊김(소켓 Close)·미도달을 감지하면 WebManager.RequestCallBack 이
        //    **빈 RSCallBackData(success=false, code=0)** 로 대기를 깨우기 때문에, code==0 만 보면 서버에 닿지도 않은
        //    패킷을 '성공'으로 오인한다(ContentEventClawMachine.cs:60 에 같은 이유의 선례가 있다).
        //    실응답 경로는 모두 RSCallBackData.OnResponse 로 success = (packet.code == 0) 을 채우므로
        //    (WebManager.RegisterHandler.ParsePacket · FsWebManager), success 를 함께 봐도 정상 응답이 막히지 않는다.
        //    [ISSUE-16] 라운드 보상 지급이 이 판정에 묶여 있어(RoundResult 성공 콜백), 오인하면
        //    **서버는 클리어를 기록하지 않았는데 아이템만 지급**된다. 반대로 클리어가 유실되는 경로도 같은 뿌리다.
        if (data.success && data.code == 0)
        {
            return true;
        }

        DLogger.Error($"[DreamBalloon] {api} 실패 — success:{data.success} code:{(ResponseError)data.code}");
        return false;
    }

    /// <summary>
    /// `state` 를 로컬 캐시(<see cref="EventBalloonInfoPacketData"/>)에 반영하고 view 를 갱신한다.
    ///
    /// **호출 시점은 "서버 저장 성공 이후" 뿐이다.** 런타임 상태 전이는 `쉬어가기/라운드 시작 버튼 클릭 → 서버 전송 →
    /// 서버 저장 → 클라 캐싱` 순서를 따르며(§7-1a), 서버가 확정하지 못한 상태를 클라가 먼저 만들지 않는다.
    /// 응답이 `state` 를 실어 오면 <see cref="SyncFromServer"/> 가 이미 반영하므로 이 호출은 대개 no-op이고,
    /// 응답에 누락/지연이 있어도 방금 확정된 전이가 캐시에 남도록 보장하는 역할을 한다.
    /// 로그인 시에는 `RQEventBalloonInfo` 응답의 state 가 캐시의 최종 기준이 된다.
    /// </summary>
    private void ApplyLocalState(int state)
    {
        EventBalloonInfoPacketData info = EventDreamBalloonHelper.GetServerInfo();
        if (null == info || info.state == state)
        {
            return;
        }

        info.state = state;
        BuildModel();
        SendIconRefresh();
        Message.Send(new OnRefreshEventCurrencyMsg { eventType = LiveEventType.DREAMBALLOON });
    }

    // 서버 응답(EventBalloonInfo) 반영 후 view 최신화. state 기준 아이콘/모델 갱신(§7-1a).
    private void SyncFromServer()
    {
        BuildModel();
        SendIconRefresh();
        Message.Send(new OnRefreshEventCurrencyMsg { eventType = LiveEventType.DREAMBALLOON });
    }

    // 게임 실행 시(상주) — 진행중이면 이벤트 미진입 상태에서도 좌석 시뮬을 구동(§7-3).
    // 화면 밖에서 성공(목표 달성)/실패(좌석 0)가 확정되면 결과를 서버에 저장하고, 진입 시 연출로 이어간다(§5-1 · EvaluateRoundOnOpen).
    private void OnInfoResolvedForLaunch()
    {
        if (EventDreamBalloonHelper.GetState() == 2)
        {
            StartCurrentRoundSimulation();
        }
    }

    private void OnInfoResolvedForOpen()
    {
        int state = EventDreamBalloonHelper.GetState();
        DLogger.Log($"[DreamBalloon] OnOpen — state:{state} round:{EventDreamBalloonHelper.GetCurrentRound()}");

        // 상태(state)별 진입 팝업 오픈(§5·§5-1). 시작/설명 팝업(_Start/_Info)은 자동 안내 진입에서 별도 처리.
        switch (state)
        {
            case 0:   // 미시작 → 인트로(시작 팝업/난이도 선택). 통상 eventReady 는 OnOpenEvent 가 선처리하며,
                      // 이 경로는 startTime 세팅됨(eventRun 아님)인데 서버 state 만 0 인 명시적 재진입 폴백(§5-1 #1).
                OpenIntroPopup();
                break;

            case 1:   // 쉬어가기(쉬는중) → main 팝업
                EventDreamBalloonHelper.OpenMainPopup();
                break;

            case 2:   // 진행중 → main 팝업. 진입 시 좌석 시뮬 시작 → 판정→연출(§7-3, StageResultMsg는 main 팝업 수신)
                StartCurrentRoundSimulation();
                EventDreamBalloonHelper.OpenMainPopup();
                break;

            case 3:   // 라운드 클리어 — **매 라운드 발생**한다(서버 스펙 2026-07-19). 완주와 반드시 구분할 것.
                      //
                      // ⚠️ 완주가 아니면 일반 라운드 통과 대기이므로 쉬는중(1)과 동일하게 **메인 팝업**으로 보낸다.
                      //    여기서 완주로 오판하면 1라운드만 깨도 최종 보상이 지급되고 이벤트가 종료 처리된다.
                      //
                      // ⚠️ 완주여도 `OpenFinalPopup()` 을 직접 열면 안 된다. 최종 라운드 성공은 머지판(화면 밖)에서 확정되므로
                      //    결과가 `pendingResultPresentation` 에 보관돼 있고, 이를 **소비해 연출로 이어가는 주체는 메인 팝업**이다
                      //    (`SetInfo` → `PeekPendingResultPresentation` → `PlayResultThenOpenAsync` → 최종 연출 → `_Final` 오픈).
                      //    메인을 건너뛰면 **열기구가 축제장으로 올라가는 최종 연출(§4-4 최종)이 통째로 스킵**되고 보상 팝업만 뜬다.
                if (null != pendingResultPresentation || !EventDreamBalloonHelper.IsCleared())
                {
                    EventDreamBalloonHelper.OpenMainPopup();
                }
                else
                {
                    // 완주 + 재진입(이미 연출을 본 뒤) — 연출 없이 최종 보상 팝업으로 직행한다.
                    //  단 최종 보상 수령이 아직 남아 있을 수 있으므로(직전 시도의 RS 실패·무응답) 여기서도 먼저 수령한다.
                    //  이미 수령했으면 ClaimFinalRewardAsync 가 즉시 완료되므로 중복 지급은 없다.
                    ClaimFinalThenOpenPopupAsync().Forget();
                }
                break;

            case 4:   // 종료 → 종료 팝업
                EventDreamBalloonHelper.OpenEndPopup();
                break;

            default:
                EventDreamBalloonHelper.OpenMainPopup();
                break;
        }
    }
    #endregion

    #region 좌석 시뮬 · 라운드 판정 (클라 권위 §7-3)
    private void StartCurrentRoundSimulation()
    {
        int round = EventDreamBalloonHelper.GetCurrentRound();
        var aiRound = EventDreamBalloonHelper.GetAiRound(round);
        if (null == aiRound)
        {
            DLogger.Error($"[DreamBalloon] AiRound not found. round:{round}");
            return;
        }

        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        long roundStartAt = EventDreamBalloonHelper.GetRoundStartAt();

        // 팝업을 여닫아 이 경로가 재호출돼도, 같은 라운드를 이미 구동 중이면 시뮬을 재시작하지 않는다
        // (resolvedRound 를 리셋해 확정된 결과가 다시 열리는 것도 방지). 판정 self-check 만 다시 돈다.
        // [ISSUE-16] **성공 확정(Success)도 "붙든 상태"로 본다** — Running 만 보던 구 가드는 머지판에서 동결해 둔
        //   클리어를 팝업 진입 때마다 StartRound 로 재계산해 파괴했고(judge → Running/Fail), 그 결과
        //   EvaluateRoundOnOpen 의 seatSimulator.IsSuccess 분기가 도달 불가능한 죽은 조건이 됐다.
        if (!seatSimulator.IsHoldingRound(eventSeq, round, roundStartAt))
        {
            resolvedRound = -1;
            // 실패 고정도 함께 해제한다 — 아래 StartRound 가 좌석을 roundStartAt 으로 다시 재현하며 판정을 새로 내리므로,
            // 실패 여부는 이 기동의 로컬 데이터(좌석·코인)로 재도출된다. 반드시 StartRound **앞**에서 해제할 것:
            // StartRound 는 NotifySeatChanged → OnSeatChanged → ResolveRoundResult 로 동기 재확정까지 흘러가므로,
            // 뒤에서 해제하면 방금 다시 세운 고정을 지워 버린다.
            localFailRound = -1;
            seatSimulator.StartRound(eventSeq, round, aiRound.slotMax, aiRound.decayMinSec, aiRound.decayMaxSec, roundStartAt);
        }

        // 팝업 호출 시점 판정(§7-3): 재접속/복귀로 이미 확정된 성공/실패도 여기서 연출로 이어진다.
        EvaluateRoundOnOpen();
    }

    // 라운드 진입 판정(§7-3·§5-1) — 시뮬 기준 이미 확정된 성공/실패면 결과 확정 → StageResultMsg 로 연출.
    // 진입 시(StartCurrentRoundSimulation) + 메인 팝업 SetInfo(self-check) 양쪽에서 호출. resolvedRound 로 중복 확정 방지. 시뮬 미구동 시 no-op.
    public void EvaluateRoundOnOpen()
    {
        // 전송 실패로 서버에 미확정된 결과가 있으면 먼저 재전송한다(claim 5).
        // Update 의 매 프레임 CheckRoundGoal 로는 재전송되지 않으므로(resolvedRound 가드), 재진입 시점에 여기서 재시도.
        if (unsentResultRound >= 0)
        {
            ResolveRoundResult(unsentResultRound, unsentResultSuccess);
            return;
        }

        int round = seatSimulator.CurRound;

        // [ISSUE-16] 목표를 이미 채운 라운드는 좌석 상태와 무관하게 성공으로 커밋한다(팝업 진입 = isOnEvent → 실제 전송).
        //   · 머지판에서 목표 달성 후 동결(IsSuccess)해 둔 클리어를, 팝업 진입 시점에 서버로 전송한다.
        //   · 팝업 미진입 재접속으로 좌석이 오프라인 감소해 IsFail 이 됐더라도, 목표를 채웠으면 실패보다 성공이 우선한다(클리어 소실 방지).
        //   ⚠️ 단 **이미 실패로 고정된 라운드(localFailRound)** 는 예외다 — 실패 확정 후에도 코인이 소각되지 않은 채
        //      머지가 계속되므로(소각은 전송 성공 콜백에서 일어난다), 이 예외가 없으면 나중에 목표를 넘긴 순간
        //      실패가 성공으로 뒤집힌다. 고정 여부는 실패 확정 시점의 목표 달성 여부로 판단했다(ResolveRoundResult 참조).
        if (seatSimulator.IsSuccess || (IsGoalReached(round) && localFailRound != round))
        {
            ResolveRoundResult(round, true);
            return;
        }

        if (!seatSimulator.IsRunning && !seatSimulator.IsFail)
        {
            return;
        }

        if (seatSimulator.IsFail)
        {
            ResolveRoundResult(round, false);
            return;
        }

        CheckRoundGoal();
    }

    // 재화 게이지 갱신·판정 시점에 목표 코인 달성(성공) 여부 확인.
    private void CheckRoundGoal()
    {
        if (!seatSimulator.IsRunning)
        {
            return;
        }

        int round = seatSimulator.CurRound;
        if (IsGoalReached(round))
        {
            ResolveRoundResult(round, true);
        }
    }

    #region 확정 등수 영속화 (ISSUE-06)
    /// <summary>
    /// 성공 확정 순간의 등수를 저장한다 — **최초 1회만** 기록하고 이후 재판정은 덮어쓰지 않는다.
    ///
    /// 좌석은 <c>roundStartAt</c> 기준 시각 함수라, 목표 달성 후 앱이 죽으면 재기동 시 경과 시간만큼 감소한 좌석으로
    /// 등수가 재계산된다. 이때 <c>OnGoalReached()</c> 는 <c>judge != Running</c>(Fail 복원)이면 **no-op** 이라 동결도 복원되지 않는다.
    /// 클리어 자체는 아직 소각되지 않은 코인이 근거가 되어 살아나지만(§ISSUE-16), **등수는 근거가 없어 그대로 유실**된다
    /// → "1위로 클리어" 같은 등수 미션이 실제 달성과 다르게 판정된다. 그래서 등수만 따로 남긴다.
    /// </summary>
    private void SaveAchievedRank(int round)
    {
        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        int rank = seatSimulator.Rank;
        if (eventSeq <= 0 || round <= 0 || rank <= 0)
        {
            return;
        }

        string key = $"{RANK_SAVE_KEY_PREFIX}{eventSeq}_{round}";
        if (PlayerPrefs.HasKey(key))
        {
            return;   // 최초 확정값 우선 — 재판정으로 악화된 등수가 덮어쓰지 못하게 한다
        }

        PlayerPrefs.SetInt(key, rank);
        PlayerPrefs.Save();
    }

    /// <summary>저장된 확정 등수. 없으면 0.</summary>
    private int LoadAchievedRank(int round)
    {
        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        if (eventSeq <= 0 || round <= 0)
        {
            return 0;
        }

        return PlayerPrefs.GetInt($"{RANK_SAVE_KEY_PREFIX}{eventSeq}_{round}", 0);
    }

    /// <summary>
    /// 저장된 확정 등수 제거. 라운드 시작 시 호출 — 같은 라운드를 재도전하면 이전 시도의 등수가 남아 있으면 안 된다.
    /// ⚠️ 삭제도 <c>Save()</c> 로 flush 한다 — 쓰기만 flush 하면 강제 종료 시 지운 키가 되살아난다.
    /// </summary>
    private void ClearAchievedRank(long eventSeq, int round)
    {
        if (eventSeq <= 0)
        {
            return;
        }

        PlayerPrefs.DeleteKey($"{RANK_SAVE_KEY_PREFIX}{eventSeq}_{round}");
        PlayerPrefs.Save();
    }
    #endregion

    // 현재 이벤트 코인(itemIdx 224)이 해당 라운드 목표 코인 이상인가(클리어 조건, §7-3). goalCoin 미로드(≤0)면 false.
    private bool IsGoalReached(int round)
    {
        int goalCoin = EventDreamBalloonHelper.GetGoalCoin(round);
        return goalCoin > 0 && EventDreamBalloonHelper.GetCurrentCoin() >= goalCoin;
    }

    // 공용 컨디션 버스(ConditionDispatcher) 수신 — 쉬는중 안내 조건2(§7-4) 누적 사용 에너지 집계.
    // GeneratorEnergyUse 는 생성기 탭에서 에너지를 실제 소모할 때만 발행되며(무료 생성 제외), multiplier = 소모량(부스터 배수).
    private void OnGameCondition(ConditionContext context)
    {
        if (context.type != (int)MergeTriggerType.GeneratorEnergyUse)
        {
            return;
        }

        // multiplier 0/미지정은 소비 측에서 1로 간주(ConditionContext 규약).
        int usedEnergy = context.multiplier <= 0 ? 1 : context.multiplier;
        restNotifier.AddUsedEnergy(usedEnergy);
    }

    // 쉬는중 안내 노출 조건 충족(§7-4, RestNotifier) → 마이드림파트너 안내창(파트너 + 쉬는중 텍스트 LIdx 43510) 오픈.
    // 쉬는중 안내(§7-4·§2-6) — **기존 마이드림파트너 안내창을 그대로 재사용**한다(신규 UI 아님).
    // 큐는 머지판이 열려 있을 때만 적재된다(내부 가드) — 기획 「머지판에서 쉬는 중임을 알려준다」와 동일 조건.
    //
    // ⚠️ 시퀀스 행을 **사전 선택(preset)해서 넘긴다** — 그룹만 넘기면 큐가 꺼낼 때 `PickRandomSequence` 로 조건을 재평가하는데,
    //    그룹 40 의 `ContextValue`(이벤트가 전달한 수치 비교) 조건은 큐 경로로 값을 실어보낼 방법이 없어 항상 탈락한다(연출 무음 스킵).
    //    발화 판정은 노티파이어가 이미 끝냈으므로(§7-4) 재평가 자체가 불필요하다. preset 은 큐/팝업이 재선택 없이 그대로 쓴다.
    private void OnRestNotify(RestNotifyMsg msg)
    {
        if (msg.sequenceGroup > 0)
        {
            MyDreamPartnerSequenceTableData sequence = EventDreamBalloonHelper.GetRestSequenceRow(msg.sequenceGroup);
            DLogger.Log($"[DreamBalloon] 쉬는중 안내 발화 — sequenceGroup:{msg.sequenceGroup} sequence:{sequence?.index ?? 0}");
            if (null != sequence)
            {
                MyDreamPartnerMergeMotionQueue.Enqueue(msg.sequenceGroup, sequence: sequence);
                return;
            }
        }

        // 시퀀스 미발행(Event_DreamBalloon_Setting.dreamballoon_Sequence 미export 등) 폴백 — 텍스트 토스트.
        DLogger.Log($"[DreamBalloon] 쉬는중 안내 발화 — sequenceGroup:{msg.sequenceGroup} → 토스트 폴백");
        ToastHelper.ShowToastPopup(TableManager.GetText(LIDX_REST_NOTICE));
    }

    private void OnSeatChanged(SeatChangedMsg msg)
    {
        // 좌석 0 도달 = 실패 확정(§7-3). 결과 저장·연출 트리거.
        if (msg.remainSeat <= 0 && seatSimulator.IsFail)
        {
            // [ISSUE-16] 목표를 이미 채웠다면 좌석 0(실패)보다 성공이 우선한다 — 오프라인 좌석 감소로 클리어가 실패로 뒤집히는 것을 막는다.
            // (EvaluateRoundOnOpen 과 동일 우선순위. 이미 실패로 고정된 라운드는 제외 — 실패 후 추가로 모은 코인이 결과를 뒤집지 않게 한다.)
            if (IsGoalReached(msg.round) && localFailRound != msg.round)
            {
                ResolveRoundResult(msg.round, true);
                return;
            }

            ResolveRoundResult(msg.round, false);
        }
    }

    // 팝업 미개방 상태에서 확정된 결과(치트/재접속 등, StageResultMsg 유실)를 팝업 진입 시 재생하기 위한 접근자(§5-1).
    public StageResultMsg PeekPendingResultPresentation()
    {
        return pendingResultPresentation;
    }

    public void ClearPendingResultPresentation()
    {
        // 실패 대기를 소비했다면 머지 버튼 레드닷(MergeCheck)을 내려야 한다 — 소비만으로는 이미 켜진 오브젝트가 그대로 남는다.
        // 재시작/쉬어가기 선택까지 가면 어차피 SyncFromServer 가 갱신하지만, 결과 팝업만 보고 닫는 경로에서는 갱신 신호가 없다.
        bool hadPendingFail = HasPendingRoundFail;

        pendingResultPresentation = null;

        if (hadPendingFail)
        {
            SendIconRefresh();
        }
    }

    // 라운드 클리어(성공)를 확정했으나 아직 팝업으로 진입·확인하기 전 상태(ISSUE-24).
    // 자동 성공확정으로 서버 state 가 쉬는중(1)으로 내려와도 이 동안은 '쉬는 상태'가 아니라 '진급 대기'이므로,
    // HUD·머지 아이콘은 진행중(_Progress) 스프라이트 + 레드닷을 유지한다. 실패 결과(success=false)는 대상이 아니다.
    public bool HasPendingRoundClear => null != pendingResultPresentation && pendingResultPresentation.success;

    // 라운드 실패를 확정했으나 아직 팝업으로 진입·확인하기 전 상태 — HasPendingRoundClear 의 대칭.
    // 실패는 좌석 소진(화면 밖 머지판)에서 확정되므로, 유저에게 "들어와서 재시작/쉬어가기를 선택하라"고 알릴 신호가 필요하다.
    // 성공은 완료 버튼(Btn_Ok, ISSUE-21)이, 실패는 머지 버튼 레드닷(MergeCheck)이 그 역할을 맡는다.
    // 팝업 진입 시 ClearPendingResultPresentation 으로 소비되므로 실패 연출을 본 뒤에는 자동으로 꺼진다.
    public bool HasPendingRoundFail => (null != pendingResultPresentation && !pendingResultPresentation.success) || HasUncommittedFail;

    /// <summary>
    /// 화면 밖에서 좌석 소진으로 실패가 확정됐으나 아직 서버로 커밋하지 못한 상태
    /// (팝업 미진입 → <see cref="ResolveRoundResult"/> 의 isOnEvent 게이트가 전송을 보류 중).
    ///
    /// 이 구간에는 <see cref="pendingResultPresentation"/> 이 아직 없다(전송 성공 후에만 생성) → 그것만 보면
    /// 유저는 재진입 신호(머지 버튼 레드닷)를 받지 못한 채 방치된다. 그 공백을 **로컬 데이터**로 메운다.
    ///
    /// 이는 성공 경로가 이미 쓰고 있는 방식의 **대칭**이다 — 완료 버튼(Btn_Ok)·파트너 모션은
    /// `EventButtonUIHelper`(:249)·`DreamBalloonRoadController`(:438)에서 `IsRoundGoalReached()`
    /// (= 보유 코인, 클라 원장 → 세이브)를 먼저 보므로 커밋 전에도 켜진다. 실패의 로컬 근거는 좌석이며,
    /// 좌석은 서버 roundStartAt 기준 결정론적 함수라 **재접속해도 동일하게 복원**된다(§7-3).
    ///
    /// 전송을 시작하면 <see cref="resolvedRound"/> 가 그 라운드로 채워져 false 가 되고,
    /// 성공하면 <see cref="pendingResultPresentation"/> 이 뒤를 잇는다(표시 공백 없음).
    /// 진행중(state 2)까지 확인한다 — 그 밖의 상태는 어차피 전송이 차단되므로 켜 둘 근거가 없다.
    /// </summary>
    /// ⚠️ <c>seatSimulator.IsFail</c> 만으로 판단하면 안 된다(ISSUE-16 재발 경로) — 목표를 채운 뒤 팝업 미진입 상태로
    ///    앱을 재실행하면 좌석이 오프라인 감소로 0 이 되어 <c>judge = Fail</c> 로 복원되는데, 이때 성공 우선 분기가
    ///    <see cref="OnGoalReached"/> 를 호출해도 <c>Running</c> 이 아니라 **no-op 이라 judge 는 Fail 로 남는다.**
    ///    그 상태를 실패 대기로 읽으면 완료 표시(Btn_Ok)가 꺼져 "아이콘에 완료 표시 미노출"이 된다(티켓 댓글 증상).
    ///    → **실패로 고정된 라운드(<see cref="localFailRound"/>)만** 실패 대기로 본다. 판정 주체를
    ///      <see cref="EvaluateRoundOnOpen"/>·<see cref="OnSeatChanged"/> 와 동일한 기준으로 맞춘다.
    private bool HasUncommittedFail => seatSimulator.IsFail
        && localFailRound == seatSimulator.CurRound
        && resolvedRound != seatSimulator.CurRound
        && EventDreamBalloonHelper.IsRoundProgressing();

    // 성공·실패를 가리지 않고 "결과를 확정했으나 아직 팝업으로 확인하기 전" — 진입 버튼 아이콘 판정용(SendIconRefresh).
    // 이 동안은 서버 state 가 쉬는중(1)이어도 유저 입장에선 '쉬는 중'이 아니라 '확인 대기'다.
    public bool HasPendingRoundResult => null != pendingResultPresentation || HasUncommittedFail;

    // 클라 판정 결과 확정 → 서버 저장(RQEventBalloonRoundResult) + 연출 트리거(StageResultMsg). 라운드별 1회.
    private void ResolveRoundResult(int round, bool success)
    {
        // 라운드 시작(경쟁자 모집) 연출 중에는 결과를 확정하지 않고 보류한다(§4-3) — 연출 끝의 START 가 곧 라운드 시작이다.
        // 재접속 직후처럼 좌석이 이미 소진된 상태로 라운드를 시작하면 연출 도중 좌석이 0이 되는데,
        // 그 자리에서 확정하면 시작 연출이 끊기고 실패 연출로 튄다 → 연출이 끝나면 SetRoundStartPresenting(false) 가 흘려보낸다.
        // 목표 달성 폴링(Update→CheckRoundGoal)이 매 프레임 들어오므로 최초 1건만 보관한다.
        if (roundStartPresenting)
        {
            if (!deferredResultPending)
            {
                deferredResultPending = true;
                deferredResultRound = round;
                deferredResultSuccess = success;
            }

            return;
        }

        // 이미 확정(전송 성공)한 라운드는 재전송하지 않는다. 단 전송 실패로 미확정(unsentResultRound)인 라운드는 재시도 허용.
        if ((resolvedRound == round && unsentResultRound != round) || requestingResult)
        {
            return;
        }

        // 라운드 결과는 **진행중(state 2)** 일 때만 유효하다. 쉬는중(1)·미시작(0)·클리어(3)·종료(4)에 보내면
        // 서버가 EVENT_NOT_VALID 로 거절하고 소켓을 닫아, 이후 쉬어가기/라운드 시작 응답이 전부 유실된다(실측).
        // 진입 판정(EvaluateRoundOnOpen)·목표 달성(CheckRoundGoal)·치트는 좌석 시뮬 상태만 보므로 여기서 서버 상태로 최종 차단한다.
        if (!EventDreamBalloonHelper.IsRoundProgressing())
        {
            DLogger.Error($"[DreamBalloon] 라운드 결과 전송 취소 — 진행중이 아님(state:{EventDreamBalloonHelper.GetState()} round:{round})");
            return;
        }

        // [ISSUE-16 / ISSUE-15] 라운드 결과 커밋은 **이벤트 메인 팝업이 열린 상태(isOnEvent)에서만** 서버로 보낸다.
        //   화면 밖(머지판·로그인 직후)에서 확정된 결과는 로컬에만 붙들고, 팝업 진입(EvaluateRoundOnOpen)에서 전송한 뒤
        //   서버가 확정(IsResponseOk)한 다음에야 연출(StageResultMsg)로 이어간다 = **연출은 항상 전송 성공 이후**.
        //   · ISSUE-16: 팝업 미진입 재접속 시 서버가 먼저 쉬는중(1)으로 전환돼 클리어 보상이 유실되던 불일치 제거(서버 state 는 진행중 2 유지).
        //   · ISSUE-15: RewardClaim(보상 수령)이 RoundResult(클리어)보다 먼저/미완료 상태에 나가는 순서 역전을 구조적으로 차단
        //     (RoundResult 서버 확정 → 그 응답으로 연출·보상 수령이 순서대로 흐른다).
        //
        //   실패도 성공과 **동일한 시점 규칙**을 따른다(2026-07-23 확정). 종전에는 화면 밖에서도 즉시 전송했으나,
        //   성공/실패가 서로 다른 시점 규칙을 갖는 것이 추적을 어렵게 해 통일했다.
        //   ⚠️ 맞바꾼 것: 팝업에 **재진입하지 않고 이벤트가 끝난 유저의 실패**는 서버·메타베이스 로그(ISSUE-25)에 남지 않는다.
        //      실패 확정 사실 자체는 좌석 시뮬이 roundStartAt 으로 언제든 재현하므로 클라 진행에는 영향이 없다.
        if (!isOnEvent)
        {
            if (success)
            {
                seatSimulator.OnGoalReached();   // 감소 정지 → 성공 순간의 잔여 좌석으로 순위 고정. 전송·resolvedRound 마킹은 팝업 진입 때.
                SaveAchievedRank(round);         // [ISSUE-06] 동결한 등수를 즉시 영속화 — 전송 전에 앱이 죽어도 등수가 살아남는다
            }
            else
            {
                // 실패는 좌석 소진으로 시뮬이 이미 확정(judge=Fail)했다. 전송만 미루되, 로컬에서 해야 할 두 가지를 지금 처리한다.
                //
                // ① 실패 결정을 **고정**한다. 전송을 미루면 코인 소각(ResetRoundCoin)도 함께 미뤄지므로, 유저가 실패를
                //    모른 채 머지판에서 계속 코인을 모아 목표를 넘길 수 있다 → 팝업 진입 시 EvaluateRoundOnOpen 의
                //    **성공 우선 분기가 실패한 라운드를 성공으로 뒤집는다**(종전에는 즉시 커밋+소각이 이 창을 막았다).
                //    📌 경합 판정 기준 = **좌석이 0이 된 순간의 목표 달성 여부**(기획 확정 2026-07-23).
                //       즉 실패 확정 후에 코인을 더 모아 목표를 넘겨도 클리어로 인정하지 않는다.
                //    ⚠️ 단 이 시점에 **이미 목표를 채운 상태면 고정하지 않는다** — 그건 "실패 전에 성공했다" 또는
                //       "성공 확정 후 강제종료로 좌석만 재계산돼 Fail 로 보인다"는 뜻이고, 남아 있는 코인이 그 클리어의
                //       유일한 로컬 근거다. 여기서 고정해 버리면 ISSUE-16(클리어 소실)이 재발한다.
                //    라운드가 새로 시작되면 StartCurrentRoundSimulation 이 해제하므로, 매 기동마다 로컬 데이터로 재판정된다.
                //
                if (!IsGoalReached(round))
                {
                    localFailRound = round;
                }
            }

            // 성공·실패 **모두** 아이콘/레드닷을 재평가한다 — 전송을 미루는 동안 유저에게 남는 유일한 신호다.
            //   · 성공 → 완료 버튼(Btn_Ok), 근거는 보유 코인(IsRoundGoalReached)
            //   · 실패 → 머지 버튼 레드닷, 근거는 HasUncommittedFail
            //     (레드닷 소스인 pendingResultPresentation 은 전송 성공 후에만 생기므로 그것만 믿으면 재진입 신호가 없다)
            // ⚠️ 재실행 직후 경로에서는 RequestInfo 의 SyncFromServer 가 **시뮬 시작보다 먼저** 끝난다(:356 → :360 onComplete).
            //    즉 그때의 아이콘 평가는 시뮬이 아직 None 인 상태를 본 것이므로, 판정이 선 지금 다시 알려야 표시가 맞는다.
            SendIconRefresh();

            return;
        }

        resolvedRound = round;
        unsentResultRound = -1;   // 전송 시도 시작 — 실패하면 콜백에서 다시 마킹

        if (success)
        {
            seatSimulator.OnGoalReached();   // 성공 확정 → 좌석 감소 중단(§7-3 감소 정지)
            SaveAchievedRank(round);         // [ISSUE-06] 위 보류 분기를 거치지 않고 바로 전송되는 경로(팝업 내 달성)도 동일하게 남긴다
        }
        else
        {
            seatSimulator.Stop();
        }

        // [ISSUE-06] 등수는 **최초 확정값**을 우선한다 — 저장분이 없을 때만 현재 시뮬 값을 쓴다.
        //  앱이 죽었다 살아나면 좌석이 경과 시간으로 재계산돼 등수가 악화되는데(OnGoalReached 는 judge!=Running 이면 no-op),
        //  클리어는 미소각 코인이 근거가 되어 복원되는 반면 **등수는 근거가 없어 그대로 유실**된다.
        int savedRank = LoadAchievedRank(round);
        int rank = savedRank > 0 ? savedRank : seatSimulator.Rank;
        int upperPercent = seatSimulator.GetUpperPercent(rank);   // 위에서 고른 확정 등수 기준 — 좌석 재계산으로 어긋나지 않게 한다

        // [ISSUE-06] 트로피 챌린지 미션 조건값 스케일(1쉬움 / 2보통 / 3어려움)의 달성 난이도.
        //  ⚠️ **요청 전에** 캡처한다 — 콜백은 SyncFromServer 뒤라 서버가 회차를 정리(난이도 리셋)한 상태를 읽을 수 있다.
        int missionDifficulty = EventDreamBalloonHelper.GetMissionDifficultyValue();

        long eventSeq = EventDreamBalloonHelper.GetEventSeq();
        int result = success ? 1 : 2;

        requestingResult = true;
        WrapWebManager.Instance.RequestEventBalloonRoundResult(eventSeq, round, result, data =>
        {
            requestingResult = false;

            if (!IsResponseOk(data, $"RQEventBalloonRoundResult(round:{round} result:{result})"))
            {
                // 라운드 결과는 서버가 기록해야 확정된다. 미확정 상태로 연출·보상·라운드 진행을 밀어붙이지 않는다.
                // resolvedRound 는 유지(매 프레임 CheckRoundGoal 폭주 방지)하되, 미전송으로 마킹해 재진입 시 EvaluateRoundOnOpen 이 재전송한다(claim 5).
                unsentResultRound = round;
                unsentResultSuccess = success;
                return;
            }

            // ⚠️ **결과를 먼저 보관**한다(SyncFromServer 보다 앞). SyncFromServer 가 발신하는 OnRefreshEventCurrencyMsg 로
            //    팝업이 트랙을 갱신(성공=재빌드+스크롤 / 실패=숨김 재판정)하는데, 그 갱신이 **경쟁자 포트레이트를 퇴장 연출 전에
            //    지워버린다**(성공=셀 재활용 / 실패=IsRoundProgressing false). 팝업의 OnRefreshCurrency 가 "결과 대기 중"
            //    (PeekPendingResultPresentation != null)이면 트랙 갱신을 건너뛰도록, 발신 전에 미리 세운다.
            pendingResultPresentation = new StageResultMsg { round = round, success = success, rank = rank, upperPercent = upperPercent };

            SyncFromServer();

            // 라운드 코인 소각 — 성공/실패 모두. 성공은 다음 단계를 0 부터 세기 위해, 실패는 기획 §2 「실패 시 현재 층 누적 코인 초기화」.
            // 반드시 보상 지급보다 **앞서** 호출한다(단계 보상에 코인이 포함되면 지급분까지 태워 버린다).
            EventDreamBalloonHelper.ResetRoundCoin();

            // [ISSUE-16] 단계 보상 **지급 + 서버 수령 기록을 여기(패킷 성공 직후)에서 수행**한다.
            //
            //   ISSUE-22 은 "팝업 진입 전에 보상이 올라간다"를 고치려고 지급을 팝업의 결과 소비 시점으로 이연했는데,
            //   그 뒤 도입된 `success && !isOnEvent` 게이트(위 참조) 때문에 **성공 패킷 자체가 이미 팝업 진입 시점에만 나간다.**
            //   즉 이연은 목적을 잃었고, 지급이 `pendingResultPresentation` 이라는 **메모리 객체의 생존**에 묶이는 부작용만 남았다.
            //   그 사이 DropStalePendingResultOnInfo 가 pending 을 폐기하면 **서버는 클리어를 기록했는데 아이템만 안 나간다**
            //   = ISSUE-16(보상 미지급) 재발 경로. 코인은 이미 소각돼 재확정 근거도 없다.
            //
            //   → 지급 기준을 "pending 이 소비될 때"가 아니라 **"패킷이 성공했을 때"** 로 되돌린다. pending 은 연출 트리거 전용이
            //     되고, 폐기되더라도 피해는 "연출 미재생"에 그친다. 수령 기록(RewardClaim)도 함께 옮겨 기록·지급이 갈라지지 않게 한다.
            //   ⚠️ 반드시 위 ResetRoundCoin() **뒤**에 둔다 — 순서가 뒤집히면 지급한 코인까지 소각된다.
            //   ⚠️ 최종 라운드는 GrantRoundReward 가 isFinal 로 걸러낸다(RS 대기가 필요해 ClaimFinalRewardAsync 가 따로 처리).
            if (success)
            {
                GrantRoundReward(round);
            }

            // [ISSUE-25] 메타베이스 라운드 결과 로그(Confluence 965935140) — 난이도별 클리어/실패 이벤트 발신.
            //  ⚠️ **발신 지점을 결과 확정(여기)으로 잡았다** — 문서 트리거는 클리어 "라운드 종료 보상 획득 시" / 실패 "실패 팝업 노출 시" 이지만:
            //    · 실패: 좌석 소진은 **머지판(화면 밖)에서 확정**되고 `_Fail` 팝업은 유저가 재진입해야 뜬다 → 팝업 기준이면
            //      재진입 없이 이벤트가 끝난 유저의 실패가 **통째로 유실**된다. 확정 시점은 유실이 없다.
            //    · 클리어: 최종 라운드는 `GrantRoundReward` 를 타지 않으므로(:568 isFinal return) 보상 지급 지점 한 곳에만 걸면
            //      **10라운드 클리어가 누락**된다. 이 지점은 전 라운드를 동일하게 커버한다.
            //  라운드당 정확히 1회 — `resolvedRound`·`requestingResult` 가드 + 서버 확정(IsResponseOk) 이후이므로 재전송 시에도 중복되지 않는다.
            EventDreamBalloonHelper.SendRoundResultLog(round, EventDreamBalloonHelper.GetDifficulty(), success);

            // [ISSUE-06] 트로피 챌린지 진행도 — **마지막 라운드 클리어**를 달성 난이도와 함께 집계한다
            //  (Condition_Setting.conditionType = MergeEventDreamBalloon_Progress).
            //  · conditionValue 는 "**이 난이도 이상**"(0 = 난이도 무관 / 1 쉬움 / 2 보통 / 3 어려움) 이며,
            //    비교는 TrophyChallengeManager.IsValidMissionGroup 이 담당한다.
            //  · 넘기는 값은 위에서 캡처한 `missionDifficulty` — **조건값 스케일로 변환된 난이도**다.
            //    내부 인코딩(1쉬움/0보통/2어려움)을 그대로 넘기면 보통(0)이 최하위로 뒤집혀 판정이 무너진다.
            //  · 등수(`rank`)는 넘기지 않는다 — 2026-07-20 기획 변경으로 조건 축이 등수 → 난이도로 바뀌었다(ISSUE-26 댓글).
            //  ⚠️ **최종 라운드에서만** 발행한다 — 티켓이 "마지막 라운드 클리어 조건 참조" 로 못박았다. 중간 라운드는 대상이 아니다.
            //  ⚠️ 이 지점은 서버 확정(IsResponseOk) 이후이고, resolvedRound·requestingResult 가드가 라운드당 1회를 보장한다.
            //  ⚠️ 1회 보장을 resolvedRound 에 기대지 않는다 — DropStalePendingResultOnInfo 가 재확정을 열려고
            //     resolvedRound = -1 로 되돌리므로, 늦게 도착한 stale Info 응답 뒤에 같은 라운드가 재전송될 수 있다.
            //     conditionCount ≥ 2 미션에서 초과 집계가 되므로 **발행 전용 가드**를 따로 둔다.
            //  ⚠️ 난이도를 못 읽으면(0) 발행하지 않는다 — 그대로 넘기면 "난이도 무관" 미션만 부당 충족시키고
            //     난이도 지정 미션은 어차피 미충족이라, 판정 근거가 없는 상태의 집계를 아예 막는 편이 안전하다.
            //     (구 등수 기준의 `slotMax > 0` 가드를 대체한다 — 좌석 정보는 더 이상 판정에 쓰이지 않는다.)
            if (success)
            {
                int totalRound = EventDreamBalloonHelper.GetTotalRound();
                if (totalRound > 0 && round >= totalRound
                    && trophyProgressedRound != round
                    && missionDifficulty > 0)
                {
                    trophyProgressedRound = round;
                    GameLogic.TrophyChallenge.TrophyChallengeManager.OnProgressTrophyChallenge(
                        MissionCommonConditionType.MergeEventDreamBalloon_Progress, missionDifficulty, 1);
                }
            }

            // [ISSUE-27] 라운드 결과 확정(성공·실패) 시 손님 오더 미션을 전체 재빌드해 코인 노출을 정리한다.
            //  코인 노출은 재빌드(SetOrderMissionItem) 시점의 IsOnLiveEvent(= IsRoundProgressing, state==2) 게이트로만 결정되므로,
            //  진행중(2)에서 벗어나도(성공→쉬는중 1 / 클리어 3, 실패→확인 대기) 재빌드 트리거가 없으면 **진행중에 켜둔 코인이 그대로 남는다**
            //  ("화면을 나갔다 들어오면 사라진다" = 재진입 시에만 재빌드돼 게이트가 재평가된다는 증상).
            //  ISSUE-19(라운드 시작=켜기)·ISSUE-20(쉬어가기=끄기)와 동일 계열의 세 번째 전이 지점이다.
            //  ⚠️ 반드시 SyncFromServer(state 확정) **뒤**에 둔다 — 재빌드가 최신 상태를 반영하도록.
            //  (단계 보상 지급은 위에서 이미 끝났다 — 이 재빌드는 코인 노출 게이트만 다루므로 서로 무관하다.)
            Message.Send(new RefreshDataMergeMissionMsg());

            // 팝업이 열려있으면 OnStageResult 가 라이브로 소비하고, 닫혀있으면(치트/재접속) 팝업 진입 시 재생하도록 보관.
            Message.Send(pendingResultPresentation);
        });
    }
    #endregion

    #region 상태 전파 (아이콘 교체 §7-4)
    // 진행중 ↔ 쉬는중 상태 전환 시 HUD/머지 버튼 아이콘 스프라이트 교체(수신부는 표준 완료 §3-8).
    private void SendIconRefresh()
    {
        // _Rest 스프라이트는 '쉬는 상태'일 때만 — **결과 확정 후 팝업으로 확인하기 전(HasPendingRoundResult)** 에는
        // 서버 state 가 쉬는중(1)이어도 진행중(_Progress)을 유지한다. 성공·실패 **둘 다** 대상이다.
        //   · 성공(ISSUE-24): 진급 대기 — 레드닷이 반짝인 직후 쉬는중 아이콘으로 바뀌어 버리는 것을 막는다.
        //   · 실패: 좌석 소진은 머지판(화면 밖)에서 확정되므로, 유저가 다시 들어와 재시작/쉬어가기를 고르기 전까지는
        //     '쉬는 중'이 아니라 '확인 대기'다. 아이콘은 _Progress 를 유지하고 레드닷(MergeCheck)으로 재진입을 알린다
        //     — 레드닷 토글은 UILiveEventItem.RefreshReddot 의 DREAMBALLOON 분기(IsRoundFailPending)가 담당하며,
        //     이 메시지 수신이 그 재평가 트리거다(같은 프레임에 아이콘·레드닷이 함께 확정된다).
        Message.Send(new OnRefreshEventIconMsg
        {
            eventType = LiveEventType.DREAMBALLOON,
            iconPath = EventDreamBalloonHelper.GetCurrentIconPath(),
        });
    }
    #endregion

    private void BuildModel()
    {
        if (null == cachedData)
        {
            return;
        }

        model = new DreamBalloonModel
        {
            eventId = cachedData.eventID,
            eventSettingId = cachedData.typeIndex,
            loadTableIdx = cachedData.GetLoadTableIndex(),
        };
    }

#if UNITY_EDITOR
    #region 치트
    // 치트 진입점은 단축키가 아니라 **치트 메뉴**뿐이다 — `Tools/Cheat/DreamBalloon/…`(DreamBalloonCheat.cs).

    // 현재 라운드 기준 재화 획득(목표 코인만큼) 후 성공 처리.
    // 지갑에 실제 코인을 지급하므로 게이지·판정이 실제 플레이와 동일한 경로를 탄다(표시용 오버라이드 불필요).
    public void Cheat_GrantCurrentRoundAndClear()
    {
        int round = EventDreamBalloonHelper.GetCurrentRound();
        int goalCoin = EventDreamBalloonHelper.GetGoalCoin(round);
        int lack = goalCoin - EventDreamBalloonHelper.GetCurrentCoin();   // 이미 모은 만큼 빼고 부족분만 지급
        EventDreamBalloonHelper.AddCoin(lack);
        ResolveRoundResult(round, true);   // 목표 달성 → 성공 연출
        DLogger.Log($"[DreamBalloon][치트] 현재 라운드 재화 획득 + 성공 round:{round} goal:{goalCoin} grant:{lack}");
    }

    // 현재 라운드 강제 실패.
    //  ⚠️ 결과(ResolveRoundResult)를 직접 부르지 않고 **좌석 시뮬을 좌석 0 종단 상태로 만든다** —
    //     그래야 실제 실패와 동일하게 OnSeatChanged → ResolveRoundResult 로 흘러 judge=Fail 이 남고,
    //     머지판이면 실패 고정(localFailRound)·레드닷·전송 보류까지 그대로 재현된다.
    //     결과만 직접 확정하면 judge 가 Running 으로 남아 레드닷이 안 켜지고 진입 시 판정도 어긋난다.
    //  시뮬이 진행중이 아니면(이미 확정/미구동) 종전처럼 결과만 확정한다.
    public void Cheat_FailCurrentRound()
    {
        int round = EventDreamBalloonHelper.GetCurrentRound();

        if (seatSimulator.IsRunning)
        {
            seatSimulator.Cheat_ForceFail();
        }
        else
        {
            ResolveRoundResult(round, false);
        }

        DLogger.Log($"[DreamBalloon][치트] 현재 라운드 강제 실패 round:{round} (isOnEvent:{isOnEvent})");
    }

    #endregion
#endif
}

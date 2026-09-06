using System;
using System.Collections.Generic;

using ACTGames.Content.Helper;     // RewardHelper.RefreshRewardData()

using Cysharp.Threading.Tasks;     // UniTask.Forget()

using GameContents._Helper;  // ShoppingRoadHelper (쇼핑로드 접근 seq)

using GameCore.Utils;       // Message (메시지 버스)

using GameLogic;
using GameLogic.Define;
using GameLogic.Extension;   // IsNullOrEmpty(), int[].GetWeightedRandomIndex()
using GameLogic.GameManagement;  // AnalyticsManager (메타베이스 로그 ISSUE-39)
using GameLogic.Management;
using GameLogic.Network;     // RewardPacketData, FsWebManager, FsProcessCommon (로컬 보상 지급)

using UnityEngine;

/// <summary>
/// 당근 수확 대소동 (두더지 잡기 컨셉변경) 컨텐츠 컨트롤러.
/// 기획서 886177888 — LiveEventType.CARROT (Event_Setting.eventType=29).
///
/// 인게임 등장 추첨은 <see cref="EventCarrotSpawnSelector"/> 에 위임한다(§6-4).
///
/// ⚠️ 전용 팝업/인게임 UI 클래스(예: UIPopupEventCarrot, UINotifyEventCarrot)가
/// 아직 제작되지 않아, 해당 UI 활성화 지점은 모두 로그 + TODO 주석 placeholder 로 둔다.
/// UI 제작 후 placeholder 를 실제 UIManager 호출로 교체할 것.
/// </summary>
public class ContentEventCarrot : ContentLiveEventBase
{
    // ── 인게임 보드 / 점수 ──────────────────────────────────────
    private EventCarrotBoardModel boardModel;   // 구멍 상태 + 스폰 스케줄(추첨기 소유)
    private EventCarrotScoreModel scoreModel;   // 점수 + 콤보
    private EventCarrotSettingTableData settingTable;   // Event_CarrotSetting 캐시(loadTableIdx 로 1회 조회)
    private int boardGrid = 3;                  // 인게임 보드 한 변(Event_CarrotSetting.grid) — CreateBoardModel 에서 설정, 보드 뷰가 N×N 생성에 사용

    // ── 결과/진행 상태 ──────────────────────────────────────────
    private int bestScore;                      // 최고 점수 — 서버 EventCarrotInfo.bestScore 로 동기화(로컬은 즉시 표시용)
    private int playCount;                       // 첫 도전 판정용(이번 '세션' 플레이 수) — 앱 재실행 시 0 초기화이므로 서버 누적 기록(HasPlayHistory)과 함께 본다(ISSUE-40)
    private EventCarrotGameResult lastResult;    // 직전 판 결과(결과 팝업이 조회)
    private readonly int[] caughtByType = new int[3];   // 종류별 잡은 수량 누적(이벤트 종료 팝업 §5-7) — 서버 EventCarrotInfo 로 동기화
    private readonly int[] caughtThisGame = new int[3];  // 이번 판 종류별 잡은 수량 — RQEventCarrotResult 전송용(판마다 초기화)
    private readonly List<int> pendingRewardIndices = new();   // 이번 판 슈퍼 레어 처치로 획득한 Event_Reward.idx 누적 — 판 종료 시 일괄 로컬 지급(§9-A)
    private long cumulativeScore;                // 모든 판 누적 점수(누적 보상 트래커) — 서버 EventCarrotInfo.cumulativeScore 로 동기화(§9-A-5)
    private readonly HashSet<int> claimedCumulativeRewardIndices = new();   // 지급 완료된 누적 보상 티어(Event_RewardGroup.index) — 진입 시 서버 누적 점수 기준 RestoreClaimedCumulativeRewards 로 복원(재지급 방지)
    private bool completeProcessed;              // OnCompleteEvent 의 표준 완료 처리(환급/완료 메시지) 1회 보장 — 종료 팝업 자체는 매번 노출(§5-7)
    private bool gameEndProcessed;               // OnGameEnd 1회 보장 — 타임아웃·그만두기 동시(예: 종료 직전 그만두기) 이중 호출 시 보상 누적 Clear+빈 재지급으로 신규 보상이 사라지는 것 방지. EnterInGame 에서 리셋
    private readonly List<RewardPacketData> lastGrantedKillRewards = new();        // 이번 판 슈퍼 레어 처치 보상만 — 결과 팝업 NewRewardBox 표시용(§5-6 5)
    private readonly List<RewardPacketData> lastGrantedCumulativeRewards = new();   // 이번 판 누적 점수 티어 보상만 — 결과 팝업 UIPopupRewardResult 표시용(§5-6)

    // ── 인게임 상태 조회 (HUD/연출용) ───────────────────────────
    public int CurrentScore => scoreModel?.CurrentScore ?? 0;
    public float RemainGameTime => boardModel?.RemainGameTime ?? 0f;
    public float RemainGameTimeRatio => boardModel?.RemainTimeRatio ?? 0f;   // 남은 시간 비율(1→0) — 인게임 타임 슬라이더(ISSUE-41)
    public float ComboGaugeRatio => scoreModel?.ComboGaugeRatio ?? 0f;
    public EventCarrotComboTier ComboTier => scoreModel?.ComboTier ?? EventCarrotComboTier.None;
    public EventCarrotGameResult LastResult => lastResult;
    public int BestScore => bestScore;          // 난이도별 최고 점수(이벤트 팝업 §5-4 4) — TODO[server] 서버 로드
    public long CumulativeScore => cumulativeScore;     // 모든 난이도 누적 점수(이벤트 팝업 §5-4 4) — TODO[server] 서버 로드
    public int StartCoin => GetStartCoin();     // 한 게임당 소모되는 입장 코인(Event_CarrotSetting.gameStartCoin) — 메인 팝업 시작 버튼 비용 표시(§5-4 8)
    public int BoardGridSize => boardGrid;              // 인게임 보드 한 변 N(Event_CarrotSetting.grid) — 보드 뷰가 N×N 슬롯 생성에 사용

    // ── 라이프사이클 ────────────────────────────────────────────
    public override void Initialize(LiveEventData eventData)
    {
        base.Initialize(eventData);

        // Carrot 팝업은 Hammer 의 static 이벤트 구독 방식이 아니라, 컨트롤러(this)를 Inform 으로 주입해
        // 팝업이 직접 호출하는 구조다(OnClickStartGame / OnClickStartPopupConfirm / OpenMainPopup 등).
        // 따라서 Initialize 에서 별도 콜백 구독은 없다(남은 시간 표시 등은 팝업이 controller 에서 pull).
    }

    public override void Release()
    {
        // Initialize 에서 구독한 콜백이 없으므로(컨트롤러 주입 구조) 해제할 것도 없다.
        base.Release();
    }

    public override void OnActiveEvent(Action callback)
    {
        // 표준 라이브이벤트 규약(Bingo/LuckyMatching 동일): 활성화 시 base 를 호출한다.
        // 자동 오픈은 ContentShoppingRoad/Default 컨트롤러의 IsBeforeInitEvent 게이트로 제어된다 —
        // 최초 1회(before-init)만 인트로가 뜨고, base.OnOpenEvent→UpdateEventState 가 startTime 을 박아
        // before-init 이 풀리면 이후 게임 실행 시엔 활성화 시퀀스 자체가 호출되지 않는다(메인 자동 오픈 없음).
        // (startTime 세팅은 FakeServer 의 FsCarrotDataHandler 등록으로 동작 — 핸들러 미등록이 'always before-init' 원인이었음.)
        base.OnActiveEvent(callback);
    }

    // [로그인 플로우] 시작 팝업 없이 활성화만 — 진입 팝업을 여는 OnOpenEvent 대신
    //  공통 전처리(ActivateEvent: ContentRoot 활성 + 데이터 동기화 + 시작 상태 기록)만 태운다.
    public override void OnActiveEventWithoutStartUI(bool deferRoundStart = false)
    {
        ActivateEvent(null);
    }

    public override void OnDeactiveEvent()
    {
        curEventState = LiveEventState.eventEnd;

        CloseAllCarrotPopups();     // 이벤트 비활성 시 떠 있는 Carrot 팝업 모두 닫기(Hammer: targetUI.Close()).

        // 쇼핑로드 재시작 등으로 진행 중이던 세션이 비활성화될 때 종료 팝업을 노출한다(ISSUE-42, Bingo/LuckyMatching 의 OnDeactiveEvent 종료 안내 패턴).
        // 완료(IsAllComplete)된 세션은 OnCompleteEvent 가 이미 종료 팝업을 띄우므로 중복 노출하지 않는다. 미진행/데이터 없음이면 건너뛴다.
        var liveData = EventDataHelper.GetLiveEventData(CurEventType);
        if (EventDataHelper.IsExistEventData(liveData) && !IsAllComplete())
            OpenEndPopupDeferredAsync().Forget();

        base.OnDeactiveEvent();
    }

    /// <summary>이벤트 종료 팝업(§5-7) 활성화 — 최고 점수/종류별 잡은 수량 표시.</summary>
    private void OpenEndPopup()
    {
        OpenEndPopupAsync().Forget();
    }

    // 쇼핑로드 재시작(난이도 재선택) 시 로드맵이 닫혔다 재오픈되며 같은 레이어(둘 다 TopMiddle) 맨 위(SetAsLastSibling)로 올라와 종료 팝업을 덮는다(ISSUE-42 댓글).
    // 로드맵보다 위 레이어가 없어(Top 은 에러/로딩창 영역) 타이밍 대기 대신, 종료 팝업을 즉시 열고 재오픈 구간 동안 매 프레임 맨 위로 끌어올린다(레이어 소팅).
    private async UniTaskVoid OpenEndPopupAsync()
    {
        var inform = new PopupCarrotEndInform { controller = this };
        var popup = await UIManager.OpenUIMsgAsync<UIPopupEventCarrotEnd>(inform);
        if (popup == null)
            return;

        const int KEEP_TOP_FRAME = 30;      // ~0.5초 — 로드맵 재오픈(약 0.2초) 구간을 충분히 덮는다
        var popupTransform = popup.transform;
        for (var frame = 0; frame < KEEP_TOP_FRAME && popup != null && !popup.mIsClose; frame++)
        {
            popupTransform.SetAsLastSibling();
            await UniTask.Yield();
        }
    }

    // CloseAllCarrotPopups 직후의 UI 정리/전환과 겹치지 않도록 한 박자 늦춰 종료 팝업을 연다(Bingo 의 0.1s 지연 동일 취지).
    private async UniTaskVoid OpenEndPopupDeferredAsync()
    {
        await UniTask.Delay(TimeSpan.FromSeconds(0.1f));
        OpenEndPopup();
    }

    // 이벤트 비활성/종료 시 떠 있을 수 있는 Carrot 전용 팝업을 모두 닫는다(Hammer 의 FindUIWindow + Close 패턴).
    // FindUIWindow 는 미오픈 시 null 을 반환하므로 ?. 로 안전 호출(런타임 조회 결과 — 가드 유지).
    private void CloseAllCarrotPopups()
    {
        var uiManager = UIManager.Instance;
        uiManager.FindUIWindow<UIPopupEventCarrotInGame>()?.Close();
        uiManager.FindUIWindow<UIPopupEventCarrot>()?.Close();
        uiManager.FindUIWindow<UIPopupEventCarrotResult>()?.Close();
        uiManager.FindUIWindow<UIPopupEventCarrotStart>()?.Close();
        uiManager.FindUIWindow<UIPopupEventCarrotEnd>()?.Close();
        uiManager.FindUIWindow<UIPopupEventCarrotNeedCoin>()?.Close();
    }

    public override void OnOpenEvent(Action mergeMoveCallback)
    {
        // 명시적 진입(② 쇼핑로드 '시작' 버튼 / ③ 라이브이벤트 HUD 아이콘 → EventCommonHelper.OpenEvent)에서만 호출된다.
        // 새 쇼핑로드 세션(난이도 재선택 등)이면 ActivateEvent→UpdateEventState→OnStartEventRound 에서 누적 상태를
        // 이미 0 으로 리셋한다(ISSUE-43). 따라서 아래 진입 팝업은 리셋된 로컬 값을 읽어 이전 누적 점수가 노출되지 않는다.
        ActivateEvent(mergeMoveCallback);

        // 이벤트 시작 시 지급된 초기 코인(Event_Setting.eventStartItem)을 클라 지갑에 즉시 동기화한다(ISSUE-44).
        // 표준 UpdateEventState 는 eventRun 분기에서만 RefreshEventCurrency 를 호출하고, 첫 진입(before-init)은
        // 시작 처리 후 return 하므로 동기화가 누락된다 → 시작 시 지급된 코인이 메인 팝업 재진입(eventRun) 전까지
        // 반영되지 않던 문제. eventReady(첫 진입, 시작 직후)일 때만 보강 동기화한다(eventRun 은 이미 동기화됨 → 중복 방지).
        if (curEventState == LiveEventState.eventReady)
            EventCurrencyHelper.RefreshEventCurrency(CurEventType);

        // 진입 팝업(시작/메인)은 '이벤트 상태(curEventState)'로 결정한다(Bingo/LuckyMatching 표준):
        //  · eventReady(미시작, before-init) → 시작 팝업(§4-1/§5-3)
        //  · eventRun(운영 중) → 이벤트(메인) 팝업(§4-2/§5-4)
        // ActivateEvent 의 UpdateEventState 가 curEventState 를 갱신하므로 동기 분기 가능.
        OpenEntryPopupByState();

        // 서버 기록(최고/누적 점수)은 표시용으로 요청 — 응답 시 SyncFromServerCarrotInfo→RefreshOpenPopups 가
        // 떠 있는 팝업의 점수를 갱신한다(팝업 분기는 상태로 이미 결정되어 응답을 기다리지 않음).
        RequestServerInfo();
    }

    // 진입 팝업을 열지 않고 이벤트만 활성화한다(ContentRoot 활성·재화 갱신 + 라이브 데이터/상태 동기화).
    // 활성화(OnActiveEvent)와 진입(OnOpenEvent) 공통 전처리 — base.OnOpenEvent 는 ContentRoot 활성화만 하고 팝업은 열지 않는다.
    private void ActivateEvent(Action mergeMoveCallback)
    {
        base.OnOpenEvent(mergeMoveCallback);

        SyncLiveEventData(CurEventType);
        UpdateEventState();
    }

    // 이벤트 상태(curEventState)로 진입 팝업을 연다(Bingo/LuckyMatching 표준 — eventReady→시작, eventRun→메인).
    private void OpenEntryPopupByState()
    {
        if (curEventState == LiveEventState.eventReady)
            OpenStartPopup();   // 미시작(before-init) — 시작 팝업(§4-1/§5-3)
        else if (curEventState == LiveEventState.eventRun)
            OpenMainPopup();    // 운영 중 — 이벤트(메인) 팝업(§4-2/§5-4)
    }

    /// <summary>시작 팝업(§5-3) 활성화 — 이벤트 미시작 상태에서 안내 후 머지판 이동을 유도.</summary>
    private void OpenStartPopup()
    {
        var inform = new PopupCarrotStartInform { controller = this };
        UIManager.OpenUIMsgAsync<UIPopupEventCarrotStart>(inform).Forget();
    }

    /// <summary>이벤트(메인) 팝업(§5-4) 활성화 — 누적 점수/보상/시작 버튼 허브. 결과 팝업 [메인]에서도 호출.</summary>
    public void OpenMainPopup()
    {
        // 결과 [메인]/닫기로 복귀 시, 결과 뒤에 유지하던 인게임 팝업을 닫는다(ISSUE-45 — 결과를 게임 위에 띄우는 대신
        // 인게임을 결과 뒤에 유지하므로, 메인/종료 복귀 시 여기서 정리). 미오픈 시 no-op.
        UIManager.Instance.FindUIWindow<UIPopupEventCarrotInGame>()?.Close();

        // 모든 누적 보상을 수령 완료했으면 메인 대신 이벤트 종료 팝업으로 전환한다(§5-7 — "마지막 보상 획득 후
        // 이벤트 팝업으로 돌아오면 이벤트 종료 팝업 등장"). LuckyMatching/Bingo 의 OnCompleteEvent 트리거에 대응.
        if (IsAllCumulativeRewardClaimed())
        {
            OnCompleteEvent();
            return;
        }

        var inform = new PopupCarrotMainInform { controller = this };
        UIManager.OpenUIMsgAsync<UIPopupEventCarrot>(inform).Forget();
    }

    /// <summary>모든 누적 보상 티어를 수령했는지(§5-7 종료 조건). 보상 티어가 없으면(미구성) 종료로 오판하지 않도록 false.
    /// 결과 팝업이 [다시] 버튼 비활성화 판정에도 사용한다(전부 수령 시 재도전 불가).</summary>
    public bool IsAllCumulativeRewardClaimed()
    {
        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return false;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return false;

        var tierCount = tiers.Count;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier == null)
                continue;

            // 지급 완료(claimed) 또는 누적 점수가 골에 도달 = 수령. 하나라도 미수령이면 미완료.
            if (!claimedCumulativeRewardIndices.Contains(tier.index) && cumulativeScore < tier.goalValue2)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 시작 팝업 [시작] → 이벤트(메인) 팝업 직행(플로우차트 '시작 팝업 → 이벤트 팝업', 사용자 결정 2026-06-21).
    /// 기록 없는 첫 진입자도 메인 팝업(허브)에 도달해 플레이할 수 있게 한다 — 메인 팝업의 [시작]으로 인게임 진입 →
    /// 한 판 종료 시 서버 기록(EventCarrotInfo)이 생성되어 이후 진입은 메인 팝업으로 자동 노출된다.
    /// </summary>
    public void OnClickStartPopupConfirm()
    {
        OpenMainPopup();
    }

    /// <summary>코인 부족 팝업 [수집하러 가기] → 머지 화면 입장(§5-4-1 4).</summary>
    public void OnRequestGatherCoin()
    {
        // 머지판으로 수집하러 가기 시, 뒤에 유지되던 당근 팝업(메인/코인부족 등)을 모두 닫고 이동한다.
        CloseAllCarrotPopups();
        OnMoveMerge();
    }

    /// <summary>종류별 잡은 수량 조회(이벤트 종료 팝업 §5-7).</summary>
    public int GetCaughtCount(EventCarrotType type)
    {
        return caughtByType[(int)type];
    }

    /// <summary>보유 이벤트 코인 잔량(§5-4) — 기존 이벤트 공용 재화 저장소(지갑) 조회. Event_Setting.itemIdx 기준(형제 이벤트와 동일 경로).</summary>
    public int HeldCoin => cachedData != null ? EventCurrencyHelper.GetEventCurrency(cachedData) : 0;

    /// <summary>플레이 가능(입장 코인 충분) 여부 — 메인 팝업 시작 버튼 상태(StateRole.On/Off, §5-4 8). 내부 게이트(HasEnoughStartCoin)와 동일 판정.</summary>
    public bool CanStartGame => HasEnoughStartCoin();

    /// <summary>입장 코인 충분 여부(§3, §5-4 8). 잔량은 HeldCoin(공용 재화), 차감은 게임 시작(진입) 시점에 로컬 처리(ConsumeStartCoin).</summary>
    private bool HasEnoughStartCoin()
    {
        if (cachedData == null)
            return true;

        // 입장 재화(Event_Setting.itemIdx) 미지정(0) 시 게이트 미적용 — 진입/디버그 비차단(§10-A-13).
        // itemIdx 적재 후에는 보유 코인 ≥ gameStartCoin 일 때만 진입 허용.
        // TODO[table]: 입장 재화 소모/게이트 활성화 — Event_Setting[12901].itemIdx = 222(당근 뽑기 재화, Item_Common 222),
        //   eventStartItem = 100(기획서 886177888 §5-1)을 기획팀(KF_LiveEvent.xlsm)에서 적재 필요. 현재 dev 테이블은
        //   itemIdx=0 이라 소모(ConsumeStartCoin)·부족 게이트가 no-op 이다. 기능 코드는 완성 — 테이블 값만 채우면 동작.
        var currencyIndex = cachedData.GetEventCurrencyIndex();
        if (currencyIndex <= 0)
            return true;

        return HeldCoin >= GetStartCoin();
    }

    /// <summary>코인 부족 팝업(§5-4-1) 활성화.</summary>
    private void OpenNeedCoinPopup()
    {
        var inform = new PopupCarrotNeedCoinInform { controller = this, requiredCoin = GetStartCoin() };
        UIManager.OpenUIMsgAsync<UIPopupEventCarrotNeedCoin>(inform).Forget();
    }

    // gameStartCoin(§8-4) — Event_CarrotSetting 조회. 미로드 시 기획 예시값 100 폴백.
    private int GetStartCoin()
    {
        var table = GetSettingTable();
        return table != null ? table.gameStartCoin : 100;
    }

    /// <summary>
    /// 입장 코인(gameStartCoin)을 게임 시작(인게임 진입) 시점에 로컬에서 차감·저장한다(EnterInGame 에서 호출).
    /// 재화 저장은 다른 컨텐츠와 동일한 로컬 경로(SetRewardItem(음수 Currency) → SaveDataStorage_PlayInfo)를 사용하며,
    /// itemIdx 미적재(0)면 차감 대상이 없으므로 no-op(§10-A-13).
    /// </summary>
    private void ConsumeStartCoin()
    {
        if (cachedData == null)
            return;

        var cost = GetStartCoin();
        if (cost <= 0)
            return;

        var currencyIndex = cachedData.GetEventCurrencyIndex();   // Event_Setting.itemIdx(= CurrencyType)
        if (currencyIndex <= 0)
            return;     // 입장 재화 미지정 — 차감 대상 없음(게이트도 미적용)

        // 보유 재화에서 입장 비용만큼 로컬 차감(음수 Currency) 후 저장(EventCurrencyHelper.ResetEventCurrency 와 동일 패턴).
        FsWebManager.GetProcess<FsProcessCommon>().SetRewardItem(ItemType.Currency, currencyIndex, -cost, LogItemTriggerType.None, 0);
        FsWebManager.Instance.SaveDataStorage_PlayInfo(DataSaveType.Server);
        EventCurrencyHelper.RefreshEventCurrency(CurEventType);   // 지갑/상단 재화 UI 갱신

        // 운영툴 코인 소모 로그(ISSUE-46) — 지갑 차감 직후 송신해야 lastValue(최종 보유량)가 정확하다.
        SendCarrotCurrencyLog(LogItemTriggerType.Spend_Carrot_Coin, currencyIndex, cost);

        // 메타베이스 코인 사용 로그(ISSUE-39) — Value=소모 코인 수. 게임 시작(진입) 시점 호출이라 진행할(현재 누적 점수 기준) 라운드 기준.
        SendCarrotMMP(AnalyticsEventName.EVENT_CARROT_USECOIN, GetCurrentRoundIndex(), cost);
    }

    /// <summary>이벤트 제목(§5-3 1·§5-4 1) — Event_Setting.eventName 우선, 미설정/빈 텍스트 시 기본 스트링(43210) 폴백.</summary>
    public string GetEventTitle()
    {
        var nameIndex = cachedData != null ? cachedData.GetEventName() : -1;
        if (nameIndex > 0)
        {
            var named = TableManager.GetText(nameIndex);
            if (!string.IsNullOrEmpty(named))
                return named;
        }

        return TableManager.GetText(EventCarrotStringDefine.START_TITLE);   // 43210 폴백
    }

    public override void OnCloseEvent()
    {
        base.OnCloseEvent();
    }

    // ── 인게임 진행 ─────────────────────────────────────────────

    /// <summary>이벤트 팝업의 [시작] 버튼 → 입장 코인 충분 시 인게임 진입(진입 시 코인 차감, EnterInGame → ConsumeStartCoin).</summary>
    public void OnClickStartGame()
    {
        if (!HasEnoughStartCoin())
        {
            OpenNeedCoinPopup();    // 코인 부족 팝업(§5-4-1)
            return;
        }

        // 입장 코인(gameStartCoin)은 게임 시작(인게임 진입) 시점에 차감한다(EnterInGame → ConsumeStartCoin).
        EnterInGame();
    }

    private void EnterInGame()
    {
        // [다시] 재진입 시 결과 팝업 뒤에 유지되던 이전 인게임 팝업을 먼저 닫는다(ISSUE-45 — 중복 인게임 방지). 미오픈 시 no-op.
        UIManager.Instance.FindUIWindow<UIPopupEventCarrotInGame>()?.Close();

        // 입장 코인(gameStartCoin)은 게임 시작(인게임 진입) 시점에 차감한다. 호출측(OnClickStartGame/OnClickRetry)이
        // HasEnoughStartCoin 으로 보유량을 검증한 뒤 진입하므로, 두 경로의 공통 진입 funnel 인 여기서 1회 차감한다.
        // 그만두기도 OnGameEnd → 결과 팝업으로 점수를 획득(중도 종료도 플레이로 인정)하므로 진입 차감에 별도 환급은 두지 않는다.
        ConsumeStartCoin();

        pendingRewardIndices.Clear();   // 이전 판 잔여 보상 누적 초기화(다시 시작 포함)
        gameEndProcessed = false;       // 새 판 시작 — OnGameEnd 1회 가드 해제
        System.Array.Clear(caughtThisGame, 0, caughtThisGame.Length);   // 이번 판 당근 카운트 초기화

        boardModel = CreateBoardModel();
        scoreModel = CreateScoreModel();
        boardModel.StartGame();

        // 팝업이 boardView.Init(this) → 3초 카운트 → StartGame, TickInGame/OnTouchHole 구동을 담당한다.
        var inform = new PopupCarrotInGameInform { controller = this };
        UIManager.OpenUIMsgAsync<UIPopupEventCarrotInGame>(inform).Forget();
    }

    /// <summary>
    /// 인게임 매 프레임 진행 — 보드 스폰/복귀 + 콤보 타이머 갱신.
    /// spawned/expired 는 호출측(보드 뷰) 재사용 버퍼: spawned 는 SetCarrotType→PlayAppear,
    /// expired 는 PlayReturn 으로 구동한다. 시간 만료로 종료되면 OnGameEnd 후 false 반환.
    /// </summary>
    public bool TickInGame(float deltaTime, List<EventCarrotSpawnInfo> spawned, List<int> expired)
    {
        var running = boardModel.Tick(deltaTime, spawned, expired);
        scoreModel.Tick(deltaTime);     // 콤보 유지 타이머(해제 시 게이지는 ComboGaugeRatio 로 반영)

        if (!running)
            OnGameEnd();

        return running;
    }

    /// <summary>
    /// 구멍 터치 1회 처리 — 보드 판정 + 점수/콤보 + 슈퍼 보상까지 묶어 연출 정보를 반환한다.
    /// 빈 구멍은 경직(콤보 해제), 슈퍼 레어는 매 탭 가산 후 HP 0 처치 시 보상 추첨.
    /// </summary>
    public EventCarrotTouchOutcome OnTouchHole(int holeIndex)
    {
        var kind = boardModel.TryHarvest(holeIndex, out var type, out var remainHp);
        if (kind == EventCarrotTouchKind.Empty)
        {
            scoreModel.OnMiss();    // 경직 → 콤보 해제(점수 감점 없음)
            return new EventCarrotTouchOutcome(kind, false, default, -1, 0f);
        }

        var hit = scoreModel.OnHit(type);

        // 잡은 수량 집계(이벤트 종료 팝업 §5-7): 일반/레어는 Hit, 슈퍼 레어는 처치(SuperKilled) 시 1회.
        // 누적(caughtByType)은 종료 팝업·서버 동기화용, 이번 판(caughtThisGame)은 RQEventCarrotResult 전송용.
        if (kind == EventCarrotTouchKind.Hit || kind == EventCarrotTouchKind.SuperKilled)
        {
            var typeIndex = (int)type;
            caughtByType[typeIndex]++;
            caughtThisGame[typeIndex]++;
        }

        var superRewardIndex = kind == EventCarrotTouchKind.SuperKilled ? SelectSuperRewardIndex() : -1;
        // 처치 보상은 즉시 지급하지 않고 누적 → 판 종료(OnGameEnd)에서 일괄 지급한다.
        // 그만두기/타임아웃 모두 OnGameEnd 결과에서 일괄 정산되므로, 판 도중이 아니라 종료 시점이 지급 기준이다.
        if (superRewardIndex >= 0)
            pendingRewardIndices.Add(superRewardIndex);

        var superHpRatio = kind == EventCarrotTouchKind.SuperDamaged && boardModel.SuperHpMax > 0
            ? (float)remainHp / boardModel.SuperHpMax
            : 0f;
        return new EventCarrotTouchOutcome(kind, true, hit, superRewardIndex, superHpRatio);
    }

    /// <summary>구멍 점유 여부(보드 뷰가 경직/수확 판정에 사용) — 모델상 수확 가능한 당근이 있는지.</summary>
    public bool IsHoleOccupied(int holeIndex)
    {
        return boardModel != null && boardModel.IsOccupied(holeIndex);
    }

    /// <summary>복귀(Return) 애니 완료 시(보드 뷰가 호출) 해당 구멍을 비운다.</summary>
    public void ReleaseHole(int holeIndex)
    {
        boardModel?.ReleaseHole(holeIndex);
    }

    /// <summary>슈퍼 레어 처치(HP 0) 시 드롭 보상 1건을 추첨한다(만분율 가중치).</summary>
    public int SelectSuperRewardIndex()
    {
        GetSuperRewardTable(out int[] rewardIndices, out int[] rewardRates);
        if (rewardIndices.IsNullOrEmpty() || rewardRates.IsNullOrEmpty())
            return -1;

        // 비율 기반 추첨 확정(§11-10): 각 superRewardIdx[i] 확률 = superRewardRate[i] / superRewardRate.Sum().
        // GetWeightedRandomIndex 가 합으로 정규화하므로 '미당첨' 구간 없음(idx/rate 개수 일치 전제 — A-11 테이블 정정).
        var pick = rewardRates.GetWeightedRandomIndex();
        // 가데이터에서 superRewardIdx(3) 와 superRewardRate(4) 개수가 다를 수 있어 인덱스 범위 가드(IndexOutOfRange 방지).
        return pick >= 0 && pick < rewardIndices.Length ? rewardIndices[pick] : -1;
    }

    /// <summary>
    /// 슈퍼 레어 처치 보상(Event_Reward.idx) → 표시용 (아이콘 타입/인덱스/수량) 해석(§7-2 보상 팝업).
    /// 보드 뷰가 처치 시 본 메서드로 보상을 풀어 구멍의 CommonRewardItem 에 넘긴다. 미존재 시 false.
    /// </summary>
    public bool TryGetSuperReward(int eventRewardIndex, out ItemType itemType, out int itemIndex, out int itemCount)
    {
        itemType  = ItemType.None;
        itemIndex = 0;
        itemCount = 0;

        if (eventRewardIndex < 0 || !TableManager.GetData(eventRewardIndex, out EventRewardTableData reward))
            return false;

        itemType  = (ItemType)reward.itemType;
        itemIndex = reward.itemIdx;
        itemCount = reward.itemValue;
        return true;
    }

    /// <summary>인게임 종료(20초 경과/그만두기) → 결과 도출 및 결과 팝업.</summary>
    public void OnGameEnd()
    {
        // 1회 보장 — 타임아웃(TickInGame)과 그만두기(OnConfirmQuit)가 같은 종료 시점에 겹쳐 호출될 수 있다(예: 종료 직전 그만두기 확인).
        // 이중 실행 시 두 번째가 lastGrantedKillRewards/lastGrantedCumulativeRewards 를 Clear 한 뒤(이미 비워진) pendingRewardIndices 로 재지급하여
        // 결과 팝업 신규 보상(슈퍼 레어)이 사라진다. 진입(EnterInGame)에서 리셋한 가드로 차단한다.
        if (gameEndProcessed)
            return;
        gameEndProcessed = true;

        var finalScore = scoreModel?.CurrentScore ?? 0;
        // 첫 도전 판정(ISSUE-40) — 이번 세션 플레이가 없고(playCount==0) 서버 누적 기록도 없을 때만 첫 도전.
        // playCount 는 앱 재실행 시 0 으로 리셋되므로, 진입 시 동기화된 서버 기록(HasPlayHistory)을 함께 봐야
        // 재실행 후 첫 게임에서 "첫 도전" 문구가 잘못 노출되지 않는다. cumulativeScore += finalScore 이전(서버 기록=이전 판들)에 판정.
        var isFirstPlay = playCount == 0 && !HasPlayHistory();
        playCount++;

        var previousBest = bestScore;       // 갱신 전 최고 점수(결과 팝업 "{0} → {1}" §5-6-1)
        var isNewBest = finalScore > bestScore;
        if (isNewBest)
            bestScore = finalScore;

        lastGrantedKillRewards.Clear();         // 슈퍼 레어 처치 보상(NewRewardBox) 초기화
        lastGrantedCumulativeRewards.Clear();   // 누적 점수 티어 보상(UIPopupRewardResult) 초기화

        // 슈퍼 레어 처치 보상(§6-1/§7-2)을 판 종료 시 일괄 로컬 지급(트로피 챌린지 GrantRewards 패턴 §9-A).
        // 보상은 오프라인 안전을 위해 서버 응답과 무관하게 로컬(테이블) 지급한다(§9-A-5).
        GrantRewards(pendingRewardIndices);
        pendingRewardIndices.Clear();

        // 메타베이스 라운드 종료(점수 획득) 로그(ISSUE-39) — Value=이번 판 획득 포인트. 점수 반영 전이라 진행 중 라운드 기준.
        SendCarrotMMP(AnalyticsEventName.EVENT_CARROT_GETPOINTS, GetCurrentRoundIndex(), finalScore);

        // 누적 점수 갱신 후 누적 점수 보상(Event_RewardGroup §5-4) 지급 — 새로 도달한 골 점수 티어만.
        // 반환값은 이번 판에 새로 달성한 티어 중 최고 골 점수(결과 팝업 "보상 획득" §5-6 5, 없으면 0).
        cumulativeScore += finalScore;
        var rewardGoalScore = GrantCumulativeRewards();

        lastResult = new EventCarrotGameResult(finalScore, bestScore, previousBest, isNewBest, isFirstPlay, rewardGoalScore);

        // 한 판 결과를 서버에 저장(RQEventCarrotResult §9-A) — 누적/최고치·당근 카운트 state 동기화.
        // 보상은 위에서 이미 로컬 지급했으므로, 응답은 서버 권위값으로 화면 표시(최고/누적/카운트)를 보정하는 용도.
        SendGameResult(finalScore);

        // 결과 팝업 활성화는 인게임 팝업(OnGameFinished)이 LastResult 로 담당(§5-6).
    }

    // 이전 플레이 기록 보유 여부(ISSUE-40 첫 도전 판정 보조) — 진입 시 서버 EventCarrotInfo 로 동기화되는
    // 누적 기록(최고/누적 점수·종류별 누적 당근 수) 중 하나라도 있으면 과거에 플레이한 적이 있는 것이다.
    // 앱 재실행으로 0 이 되는 playCount 와 달리 서버 권위값이라, 재실행 후에도 첫 도전을 올바로 가린다.
    // 단, caughtByType 은 이번 판 진행 중에도 증가(당근 잡을 때마다 ++)하므로 OnGameEnd 판정 시점엔 이번 판 캐치가 섞여 있다.
    // 이번 판 캐치(caughtThisGame)를 제외한 '이전 판들'의 누적만 봐야 한다 — 안 그러면 첫 판에서 당근만 잡아도
    // HasPlayHistory=true 가 되어 '첫 도전'이 최고갱신으로 잘못 분기된다. (bestScore/cumulativeScore 는 판정 시점에
    // 아직 이번 판 미반영이라 그대로 사용해도 안전.)
    private bool HasPlayHistory()
    {
        return bestScore > 0
            || cumulativeScore > 0
            || (caughtByType[(int)EventCarrotType.Normal]    - caughtThisGame[(int)EventCarrotType.Normal])    > 0
            || (caughtByType[(int)EventCarrotType.Rare]      - caughtThisGame[(int)EventCarrotType.Rare])      > 0
            || (caughtByType[(int)EventCarrotType.SuperRare] - caughtThisGame[(int)EventCarrotType.SuperRare]) > 0;
    }

    // 한 판 결과를 서버에 저장(RQEventCarrotResult). 응답의 EventCarrotInfo 로 최고/누적 점수·당근 카운트를 보정한다.
    // 한 판 결과를 서버에 저장(RQEventCarrotResult). 응답의 EventCarrotInfo 로 최고/누적 점수·당근 카운트를 보정한다.
    private void SendGameResult(int finalScore)
    {
        var eventSeq = GetCarrotEventSeq();
        if (eventSeq <= 0)
            return;

        WrapWebManager.Instance.RequestEventCarrotResult(eventSeq, finalScore,
            caughtThisGame[(int)EventCarrotType.Normal],
            caughtThisGame[(int)EventCarrotType.Rare],
            caughtThisGame[(int)EventCarrotType.SuperRare],
            _ => SyncFromServerCarrotInfo());
    }

    // 이벤트 진입 시 누적 정보·최고 점수 조회(RQEventCarrotInfo §9-A). 응답 도착 시 로컬 필드 동기화 + onComplete 호출.
    private void RequestServerInfo(Action onComplete = null)
    {
        var eventSeq = GetCarrotEventSeq();
        if (eventSeq <= 0)
        {
            // eventSeq 조회 불가(쇼핑로드/라이브 데이터 미준비) — 서버 기록 조회 불가.
            // 현재 캐시(없으면 null=기록 없음) 기준으로 진입 팝업을 진행한다.
            onComplete?.Invoke();
            return;
        }

        WrapWebManager.Instance.RequestEventCarrotInfo(eventSeq, _ =>
        {
            SyncFromServerCarrotInfo();
            onComplete?.Invoke();
        });
    }

    /// <summary>
    /// 서버 패킷(RQEventCarrotInfo/Result)의 eventSeq — 서버는 이 값으로 '라이브이벤트 접근'과 '쇼핑로드 접근'을 구분한다.
    /// 쇼핑로드로 접근 중이면 쇼핑로드 seq(shoppingRoadSeq), 라이브이벤트로 접근 중이면 라이브 event_master seq 를 보낸다.
    /// (당근은 공용이벤트+쇼핑로드 양쪽에 존재할 수 있어 cachedData.eventID 단독으로는 접근 경로 구분이 안 됨 — §9-A)
    /// </summary>
    private long GetCarrotEventSeq()
    {
        // '진행중(active)' 이 아니라 '처리주체(handle)' 로 판정한다 — 당근은 before-init(startTime=0, 미진행)이어도
        // 쇼핑로드 소속이면 쇼핑로드 seq 가 유효해야 기록 조회/저장(RQEventCarrotInfo/Result)이 동작한다.
        // (ActiveState 는 commonData.startTime!=0 을 요구해 미진행 당근이 None 으로 떨어져 seq=0 → 패킷 미전송 버그였음.)
        var handleState = EventCommonHelper.GetEventHandleState(CurEventType);
        if (handleState == EventCommonHelper.EventHandleState.OnlyShoppingRoad
            || handleState == EventCommonHelper.EventHandleState.Both)
        {
            var shoppingRoad = ShoppingRoadHelper.GetCurShoppingRoadData();
            if (shoppingRoad != null && shoppingRoad.packetData != null)
                return shoppingRoad.packetData.shoppingRoadSeq;
        }

        var liveData = EventDefaultControllerHelper.GetLiveEventData(CurEventType);
        return liveData != null ? liveData.eventID : 0;
    }

    /// <summary>
    /// 이벤트(메인) 팝업 남은 시간(§5-4 3) — 쇼핑로드 소속이면 쇼핑로드 남은 시간을, 아니면 라이브이벤트 만료 잔여 시간을 반환한다.
    /// 접근 경로(쇼핑로드/라이브) 구분은 GetCarrotEventSeq 와 동일하게 GetEventHandleState 로 판정한다.
    /// </summary>
    public TimeSpan GetRemainTime()
    {
        var handleState = EventCommonHelper.GetEventHandleState(CurEventType);
        if (handleState == EventCommonHelper.EventHandleState.OnlyShoppingRoad
            || handleState == EventCommonHelper.EventHandleState.Both)
        {
            var shoppingRoad = ShoppingRoadHelper.GetCurShoppingRoadData();
            if (shoppingRoad != null)
            {
                var remain = ShoppingRoadHelper.GetRemainTime(shoppingRoad);
                if (remain > TimeSpan.Zero)
                    return remain;
            }
        }

        if (cachedData != null)
            return EventDataHelper.GetExpireRemainTime(cachedData, DataManager.Instance.GetCurrentIntTimeStamp());

        return TimeSpan.Zero;
    }

    /// <summary>
    /// 새 쇼핑로드 세션 시작(before-init) 시 1회 호출 — 당근 누적 상태를 0 으로 리셋한다(ISSUE-43).
    /// 난이도 재선택/재시작은 같은 shoppingRoadSeq 로 UPDATE_TYPE.START 만 보내 seq 가 바뀌지 않으므로,
    /// 세션마다 새로 생성되는 curEventData(startTime=0=before-init) → UpdateData_SetStartEvent 의 본 훅에서 리셋한다.
    /// (Bingo/LuckyMatching 은 진행도가 전부 curEventData 에 있어 자동 리셋되지만, 당근만 seq별 서버 레코드+로컬을
    ///  별도로 들고 있어 표준 리셋에 묶이지 않는다.) 같은 세션 내 재진입은 before-init 이 풀려 호출되지 않는다.
    /// </summary>
    protected override void OnStartEventRound()
    {
        base.OnStartEventRound();
        ResetSessionProgress();
    }

    // 당근 세션 누적 상태를 로컬·클라캐시 2곳에서 0/비움 처리한다(ISSUE-43).
    // 서버-of-record(EventCarrotInfo) 레코드 리셋은 서버가 쇼핑로드 START(난이도 재선택) 수신 시 직접 처리한다(ISSUE-43 서버 합의) —
    // 당근 패킷이 실서버로 전송되도록 전환되어, 클라가 FakeServer 레코드를 비우던 방식은 더 이상 권위값에 영향을 주지 못한다.
    // 따라서 클라는 별도 리셋 패킷을 보내지 않고, 진입 시 RequestEventCarrotInfo 응답(서버가 리셋한 0/null)을 SyncFromServerCarrotInfo 로 확정한다.
    // 로컬/캐시 비움(①②)은 서버 응답 도착 전 즉시 0 으로 보이게 하는 선반영이다.
    private void ResetSessionProgress()
    {
        ResetLocalProgress();                           // ① 로컬 누적/최고/카운트/보상 티어
        DataManager.Instance.ResetEventCarrotCaching(); // ② 클라 캐시(EventCarrotInfo) 비움
    }

    // 누적 점수/최고 점수/종류별 당근 수·지급 완료 보상 티어를 빈 상태로 되돌린다(새 세션 진입·서버 기록 없음, ISSUE-43).
    // playCount(첫 도전 판정용 세션 카운터)는 건드리지 않는다 — 리셋된 0 기록상 HasPlayHistory 가 false 라 별도 영향 없음.
    private void ResetLocalProgress()
    {
        bestScore       = 0;
        cumulativeScore = 0;
        System.Array.Clear(caughtByType, 0, caughtByType.Length);
        claimedCumulativeRewardIndices.Clear();
    }

    // 서버 EventCarrotInfo(DataManager 캐시)로 최고/누적 점수·종류별 당근 누적 수를 보정한다(기록 없으면 0 으로 리셋).
    // 보상 지급은 로컬에서 이미 처리하므로, 본 동기화는 화면 표시/트래커 정합 용도.
    private void SyncFromServerCarrotInfo()
    {
        var info = DataManager.Instance.EventCarrotInfo;
        if (info == null)
        {
            // 서버 기록 없음(새 쇼핑로드 세션 등) — 이전 세션의 로컬 누적 상태를 0 으로 리셋한다(ISSUE-43).
            ResetLocalProgress();
            RefreshOpenPopups();
            return;
        }

        bestScore       = info.bestScore;
        cumulativeScore = info.cumulativeScore;
        caughtByType[(int)EventCarrotType.Normal]    = info.normalCarrotCount;
        caughtByType[(int)EventCarrotType.Rare]      = info.rareCarrotCount;
        caughtByType[(int)EventCarrotType.SuperRare] = info.superRareCarrotCount;

        // 서버 누적 점수를 권위값으로 받은 뒤, 이미 도달한 누적 보상 티어를 지급 완료로 복원한다.
        // claimedCumulativeRewardIndices 는 로컬 전용이라 재시작/재설치 시 비어 있어, 복원하지 않으면
        // 재시작 후 첫 게임 종료 시 이미 받은 누적 보상이 GrantCumulativeRewards 에서 재지급된다.
        RestoreClaimedCumulativeRewards();

        RefreshOpenPopups();    // 갱신된 최고/누적/카운트를 떠 있는 Carrot 팝업에 즉시 반영
    }

    /// <summary>
    /// 현재 누적 점수(서버 권위값) 기준으로 이미 달성한 누적 보상 티어를 지급 완료 집합에 복원한다.
    /// 누적 점수는 단조 증가하고 누적 보상은 티어 도달 즉시 지급되므로, goalValue2 &lt;= cumulativeScore 인
    /// 티어는 과거 세션에서 반드시 지급된 것이다. 서버에 클레임 티어를 별도 영속하지 않고 누적 점수만으로
    /// 재지급을 막는다(재시작/재설치 시 로컬 HashSet 이 비어 발생하던 재지급 방지). 멱등(여러 번 호출 안전).
    /// </summary>
    private void RestoreClaimedCumulativeRewards()
    {
        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return;

        var tierCount = tiers.Count;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier == null)
                continue;
            if (tier.goalValue2 <= cumulativeScore)
            {
                claimedCumulativeRewardIndices.Add(tier.index);     // 이미 도달 → 지급 완료로 표시
                MarkStandardCumulativeRewardReceived(tier.index);   // 표준 보상 데이터도 수령 처리(쇼핑로드 진행 게이트용)
            }
        }
    }

    /// <summary>
    /// 누적 보상 티어 지급/복원 시, 표준 LiveEvent 보상 데이터(eventRewardInfoes)의 해당 티어를 수령 완료로 표시한다.
    /// 쇼핑로드 진행 게이트(EventDataHelper.IsCompleteEvent → IsRewardAllRecievedCondition(EventCarrot))가 표준 수령
    /// 상태를 읽으므로, Carrot 자체 claimedCumulativeRewardIndices 와 별개로 표준 데이터도 동기화해야 모든 누적 보상
    /// 수령 후 쇼핑로드가 당근 뽑기를 '완료'로 인식해 다음 컨텐츠를 연다. tierIndex = Event_RewardGroup.index(=rewardIndex).
    /// </summary>
    private void MarkStandardCumulativeRewardReceived(int tierIndex)
    {
        var liveEventData = EventDataHelper.GetLiveEventData(CurEventType);
        if (!EventDataHelper.IsExistEventData(liveEventData))
            return;

        var rewardInfo = liveEventData.rewardData?.GetRewardInfo(tierIndex);
        if (rewardInfo == null)
            return;

        rewardInfo.recieveState = EventRewardState.Receive;
    }

    // 서버 응답(RQEventCarrotInfo/RQEventCarrotResult)으로 state 가 갱신된 뒤, 현재 떠 있는 Carrot 팝업을 즉시 리프레시한다.
    // FindUIWindow(active:true) 는 열린(activeSelf) 팝업만 반환하므로 미오픈 시 null → ?. 안전 호출(런타임 조회 결과 — 가드 유지).
    private void RefreshOpenPopups()
    {
        var uiManager = UIManager.Instance;
        uiManager.FindUIWindow<UIPopupEventCarrot>(true)?.RefreshFromController();
        uiManager.FindUIWindow<UIPopupEventCarrotResult>(true)?.RefreshFromController();
        uiManager.FindUIWindow<UIPopupEventCarrotEnd>(true)?.RefreshFromController();
    }

    /// <summary>
    /// 슈퍼 레어 처치 보상(§6-1/§7-2) — Event_Reward.idx 배열을 RewardPacketData 로 변환해 로컬 지급한다(§9-A-5).
    /// 보상은 서버 패킷(RQEventCarrotResult)이 아니라 테이블 기반으로 클라에서 지급한다는 방침에 따른 것으로,
    /// 트로피 챌린지(TrophyChallengeManager.GrantRewards)와 동일 경로(GrantRewardPackets)를 재사용한다.
    /// </summary>
    private void GrantRewards(List<int> eventRewardIndices)
    {
        if (eventRewardIndices.IsNullOrEmpty())
            return;

        // 메타베이스 슈퍼 레어 로그(ISSUE-39)용 '진행한(플레이한)' 포인트 라운드 — 점수 반영 전(GrantRewards 는 cumulativeScore += 이전 호출) 기준.
        var playedRoundIndex = GetCurrentRoundIndex();

        var indexCount = eventRewardIndices.Count;
        var rewards = new List<RewardPacketData>(indexCount);
        for (var i = 0; i < indexCount; ++i)
        {
            var rewardIndex = eventRewardIndices[i];
            if (rewardIndex <= 0)
                continue;
            if (!TableManager.GetData(rewardIndex, out EventRewardTableData rewardRow))
                continue;
            rewards.Add(new RewardPacketData((ItemType)rewardRow.itemType, rewardRow.itemIdx, rewardRow.itemValue));

            // 슈퍼 레어 보상 획득 처리 시(팝업 노출 X) 메타베이스 로그(ISSUE-39) — Value=보상 idx(Event_Reward.index).
            SendCarrotMMP(AnalyticsEventName.EVENT_CARROT_SUPERRARE, playedRoundIndex, rewardIndex);
        }

        GrantRewardPackets(rewards, LogItemTriggerType.Gain_Carrot_SRreward);   // 슈퍼 레어 보상 로그(ISSUE-46)
    }

    /// <summary>
    /// 누적 점수 보상(§5-4/§5-6) — Event_RewardGroup(rewardGroup = Event_Setting.rewardGroup)에서
    /// 골 점수(goalValue2)가 현재 누적 점수 이하이면서 아직 미지급인 티어를 표준 보상 수령 경로로 처리한다.
    ///
    /// 표준 경로 <see cref="EventDataHelper.CompleteEventRewards"/> 사용(ISSUE-47): 서버-of-record(FakeServer) 보상 수령 마킹 +
    /// 지급 + 클라 동기화 + 쇼핑로드 라운드 전환 메시지까지 일괄 처리한다. 쇼핑로드 완료 게이트(IsCompleteEvent)는
    /// 서버 수령 상태를 조회하므로, 과거의 '로컬 전용 지급 + 클라 전용 마킹' 방식으로는 완료가 인식되지 않아
    /// 다음 스텝으로 진행되지 않았다(ISSUE-47) — 다른 쇼핑로드 이벤트(Doughnut/ShoppingTown 등)와 동일한 표준 경로로 일원화한다.
    /// </summary>
    // 반환: 이번 호출에서 새로 달성(지급)한 티어 중 최고 골 점수(결과 팝업 "보상 획득" §5-6 5). 신규 달성 없으면 0.
    private long GrantCumulativeRewards()
    {
        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return 0;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return 0;

        var liveEventData = EventDataHelper.GetLiveEventData(CurEventType);
        if (!EventDataHelper.IsExistEventData(liveEventData))
            return 0;

        long highestNewGoal = 0;        // 이번 판 신규 달성 티어 중 최고 골 점수
        var tierCount = tiers.Count;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier == null)
                continue;
            if (tier.goalValue2 > cumulativeScore)
                continue;   // 아직 도달하지 못한 골 점수 티어
            if (!claimedCumulativeRewardIndices.Add(tier.index))
                continue;   // 이미 지급된 티어(Add 가 false → 기존재)

            // 표준 보상 수령 — 서버 마킹 + 지급 + 클라 동기화 + OnEventRoundChangeMsg 까지 일괄 처리(ISSUE-47).
            // (기존 MarkStandardCumulativeRewardReceived[클라 전용] + GrantRewardPackets[로컬 지급] + 수동 OnEventRoundChangeMsg 대체)
            var completed = EventDataHelper.CompleteEventRewards(liveEventData, out var tierRewards, true, tier.index);
            if (!completed)
            {
                claimedCumulativeRewardIndices.Remove(tier.index);   // 표준 처리 실패(서버 데이터 미해결 등) — 다음 기회 재시도하도록 되돌림(보상 유실 방지)
                continue;
            }

            if (tier.goalValue2 > highestNewGoal)
                highestNewGoal = tier.goalValue2;

            if (!tierRewards.IsNullOrEmpty())
            {
                lastGrantedCumulativeRewards.AddRange(tierRewards);   // 결과 팝업 누적 보상 표시(§5-6)

                // 운영툴 보상 획득 로그(ISSUE-46) — 재화 보상만 carrot 전용 키로 송신(표준 지급 직후라 lastValue 정확).
                var rewardCount = tierRewards.Length;
                for (var r = 0; r < rewardCount; ++r)
                {
                    var reward = tierRewards[r];
                    if (reward != null && reward.type == ItemType.Currency)
                        SendCarrotCurrencyLog(LogItemTriggerType.Gain_Carrot_TotalReward, reward.id, reward.quantity);
                }
            }

            // 포인트 라운드 클리어(누적 보상 획득) 메타베이스 로그(ISSUE-39) — 클리어한 라운드 index, Value 없음.
            SendCarrotMMP(AnalyticsEventName.EVENT_CARROT_REWARD, tier.index);
        }

        return highestNewGoal;
    }

    // EventRewardGroupTableData 의 보상 묶음(itemType/itemIdx/itemValue 배열)을 RewardPacketData 로 풀어 누적한다.
    private void AppendRewardBundle(EventRewardGroupTableData tier, List<RewardPacketData> rewards)
    {
        var itemTypes = tier.itemType;
        if (itemTypes.IsNullOrEmpty())
            return;

        // Event_RewardGroup 의 itemType/itemIdx/itemValue 는 고정 길이(예: 3칸) 번들이라 빈 칸("0,0,0")이 섞인다.
        // 빈 칸(타입 None 또는 수량 0)은 보상이 아니므로 건너뛴다 — 빈 보상 셀/아이콘 미해결 에러 방지.
        var bundleCount = itemTypes.Length;
        var idxCount = tier.itemIdx?.Length ?? 0;
        var valCount = tier.itemValue?.Length ?? 0;
        for (var i = 0; i < bundleCount; ++i)
        {
            if (itemTypes[i] == ItemType.None)
                continue;
            if (i >= idxCount || i >= valCount || tier.itemValue[i] <= 0)
                continue;

            rewards.Add(new RewardPacketData(itemTypes[i], tier.itemIdx[i], tier.itemValue[i]));
        }
    }

    // 누적 보상 그룹 번호(Event_Setting.rewardGroup) — cachedData.typeIndex 로 Event_Setting 조회. 미조회 시 -1.
    private int GetRewardGroup()
    {
        var typeIndex = cachedData != null ? cachedData.typeIndex : -1;
        return TableManager.GetData(typeIndex, out EventSettingTableData setting) ? setting.rewardGroup : -1;
    }

    // 메타베이스(서버 Analytics) 로그(ISSUE-39) — 짝 맞추기(LuckyMatchingHelper) 동일 구조.
    // Label=이벤트 idx(rewardGroup), Action=포인트 라운드 idx(Event_RewardGroup.index), Value=수치(없으면 생략). Level(유저레벨)은 CustomSendEvent 가 자동 추가.
    private void SendCarrotMMP(string eventName, int roundIndex, float? value = null)
    {
        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0 || roundIndex <= 0)
            return;

        AnalyticsManager.CustomSendEvent(eventName, label: rewardGroup.ToString(), action: roundIndex.ToString(), value: value);
    }

    // 현재 진행 중(다음 목표) 포인트 라운드의 Event_RewardGroup.index — 누적 점수 기준 첫 미달성 티어. 전부 달성 시 마지막 티어.
    // ※ 점수 반영(cumulativeScore += finalScore) "전"에 호출해야 '진행한/진행 중' 라운드가 되며, 클리어로 넘어간 다음 라운드가 선반영되지 않는다(ISSUE-39 주의).
    private int GetCurrentRoundIndex()
    {
        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return 0;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return 0;

        var tierCount = tiers.Count;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier != null && cumulativeScore < tier.goalValue2)
                return tier.index;      // 첫 미달성 = 진행 중
        }

        return tiers[tierCount - 1]?.index ?? 0;     // 전부 달성 — 최종 티어 폴백
    }

    /// <summary>
    /// 다음 누적 보상 티어까지의 진행도(이벤트 팝업 게이지/필요 점수 §5-4 4) 조회.
    /// Event_RewardGroup 에서 아직 도달 못 한 가장 가까운 골 점수 티어를 찾아,
    /// 남은 점수(requiredScore)와 직전 달성 티어 대비 진행 비율(gaugeRatio 0~1)을 산출한다.
    /// 모든 티어를 달성해 더 받을 보상이 없으면 false(팝업은 "완료" 표기 + 게이지 가득, §5-4 "다음 점수 없으면 제거").
    /// </summary>
    public bool TryGetNextRewardProgress(out long requiredScore, out float gaugeRatio)
    {
        requiredScore = 0;
        gaugeRatio = 1f;

        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return false;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return false;

        // 아직 도달 못 한 가장 가까운 골(nextGoal)과 이미 달성한 최대 골(prevGoal)을 한 번에 탐색.
        var nextGoal = long.MaxValue;
        long prevGoal = 0;
        var found = false;
        var tierCount = tiers.Count;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier == null)
                continue;

            long goal = tier.goalValue2;
            if (goal > cumulativeScore)
            {
                if (goal < nextGoal)
                {
                    nextGoal = goal;
                    found = true;
                }
            }
            else if (goal > prevGoal)
            {
                prevGoal = goal;    // 게이지 하한(이미 받은 직전 티어)
            }
        }

        if (!found)
            return false;   // 모든 티어 달성 — 다음 보상 없음

        requiredScore = nextGoal - cumulativeScore;
        var span = nextGoal - prevGoal;
        gaugeRatio = span > 0 ? Mathf.Clamp01((float)(cumulativeScore - prevGoal) / span) : 1f;
        return true;
    }

    // 게이지 Fill 위치는 UIPopupEventCarrot.ApplyRewardGauge 가 '실제 생성된 마커(보상 행) 위치 ↔ 게이지 Fill 영역
    // 실제 범위'로 직접 산출한다(사용자 결정 B, 2026-06-24). 컨트롤러는 누적 점수(CumulativeScore)와 티어 데이터
    // (BuildRewardSliderData)만 제공한다 — 과거의 비례/추상 비율 계산은 제거.

    /// <summary>
    /// RewardPacketData 목록을 기존 아이템 획득 로직으로 로컬 지급한다(트로피 챌린지 §9-A 공통 tail).
    /// ① SetRewardItems(실제 인벤토리 반영) → ② SaveDataStorage_PlayInfo(서버 백업, 재접속 유실 방지)
    /// → ③ RefreshRewardData(런타임 캐시 + 머지 인벤토리/상단 UI 갱신).
    /// </summary>
    private void GrantRewardPackets(List<RewardPacketData> rewards, LogItemTriggerType logType)
    {
        if (rewards.IsNullOrEmpty())
            return;


        // 결과 팝업 분리 노출용 — 슈퍼 레어 처치 보상은 NewRewardBox, 누적 점수 티어 보상은 UIPopupRewardResult 로 나눠 담는다(§5-6).
        if (logType == LogItemTriggerType.Gain_Carrot_SRreward)
            lastGrantedKillRewards.AddRange(rewards);
        else if (logType == LogItemTriggerType.Gain_Carrot_TotalReward)
            lastGrantedCumulativeRewards.AddRange(rewards);

        var rewardArray = rewards.ToArray();
        FsWebManager.GetProcess<FsProcessCommon>().SetRewardItems(rewardArray, LogItemTriggerType.None, 0);
        FsWebManager.Instance.SaveDataStorage_PlayInfo(DataSaveType.Server);
        RewardHelper.RefreshRewardData(rewardArray);

        // 운영툴 보상 획득 로그(ISSUE-46) — 지갑 반영 직후, 재화(Currency) 보상만 logType(SR/누적)으로 송신(lastValue 정확).
        for (var i = 0; i < rewardArray.Length; ++i)
        {
            var reward = rewardArray[i];
            if (reward == null || reward.type != ItemType.Currency)
                continue;
            SendCarrotCurrencyLog(logType, reward.id, reward.quantity);
        }
    }

    /// <summary>
    /// 운영툴 재화 로그(ISSUE-46) — 당근 코인 소모/보상 획득. logValue=당근 이벤트 idx, itemId=재화 idx,
    /// updatedValue=증감 수량(절대값), lastValue=지갑 반영 후 최종 보유량. 지갑 반영 "직후" 호출해야 lastValue 가 정확하다.
    /// 당근 코인은 이벤트 전용 신규 재화라 SetRewardItem 자동 로그(Diamond/GachaCoin 등) 대상이 아니므로 직접 송신한다.
    /// </summary>
    private void SendCarrotCurrencyLog(LogItemTriggerType logType, int currencyIdx, int changeAmount)
    {
        if (logType == LogItemTriggerType.None || currencyIdx <= 0 || changeAmount == 0)
            return;

        WebManager.Instance.SendPacketIgnoreProcess(new RQLog
        {
            logType      = logType.ToString(),
            logValue     = cachedData != null ? cachedData.eventID : 0,
            itemId       = currencyIdx,
            updatedValue = Mathf.Abs(changeAmount),
            lastValue    = DataManager.Instance.GetCurrencyCount(currencyIdx),
        });
    }

    /// <summary>이번 판에 지급된 보상(슈퍼 처치 + 누적 티어) 표시 정보 목록(결과 팝업 "신규 보상 획득" §5-6 5). 없으면 빈 목록.</summary>
    

    /// <summary>이번 판 슈퍼 레어 처치 보상만(결과 팝업 NewRewardBox §5-6 5). 없으면 빈 목록.</summary>
    public List<RewardInfo> GetLastGrantedKillRewardInfos()
    {
        return RewardHelper.GetRewardInfoByRewardPacketDatas(lastGrantedKillRewards);
    }

    /// <summary>이번 판 누적 점수 티어 보상만(결과 팝업 UIPopupRewardResult "획득 보상 전체" §5-6). 없으면 빈 목록.</summary>
    public List<RewardInfo> GetLastGrantedCumulativeRewardInfos()
    {
        return RewardHelper.GetRewardInfoByRewardPacketDatas(lastGrantedCumulativeRewards);
    }

    /// <summary>
    /// 현재 목표(다음 미달성) 누적 보상 티어의 보상 표시 정보 — 결과 팝업 목표 점수 보상 툴팁(UIEventCarrotRewardTooltip)용.
    /// 슬라이더가 가리키는 목표 골(targetScoreText)과 동일 기준: 누적 점수 기준 첫 미달성 티어(전부 달성 시 마지막 티어).
    /// 보상 그룹/티어 없으면 null.
    /// </summary>
    public List<RewardInfo> GetTargetGoalRewardInfos()
    {
        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return null;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return null;

        var tierCount = tiers.Count;
        EventRewardGroupTableData target = null;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier != null && cumulativeScore < tier.goalValue2)
            {
                target = tier;      // 첫 미달성 = 현재 목표(슬라이더가 가리키는 골)
                break;
            }
        }

        target ??= tiers[tierCount - 1];    // 전부 달성 — 마지막 티어 폴백
        if (target == null)
            return null;

        var rewards = new List<RewardPacketData>();
        AppendRewardBundle(target, rewards);
        return RewardHelper.GetRewardInfoByRewardPacketDatas(rewards);
    }

    /// <summary>
    /// 결과 팝업 누적 점수 슬라이더 연출용 — 누적 보상 티어 골 점수(goalValue2) 오름차순 목록(0 베이스라인 제외).
    /// 슬라이더는 이 골들을 구간 경계로 삼아 '직전 달성 골(0f) → 목표 골(1f)' 진행을 표현한다. 없으면 빈 목록.
    /// </summary>
    public List<long> GetRewardTierGoals()
    {
        var result = new List<long>();

        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return result;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return result;

        var tierCount = tiers.Count;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier != null && tier.goalValue2 > 0)
                result.Add(tier.goalValue2);
        }

        result.Sort();
        return result;
    }

    /// <summary>
    /// 누적 점수 보상 전 티어를 이벤트 팝업 세로 진행형 리스트(§5-4)용 데이터로 변환한다.
    /// height 는 정규화 비율(0~1, goalValue2 / 최대 골)이며, 표시 위치 픽셀 스케일은 팝업(리스트 영역 높이)에서 곱한다.
    /// isRewarded 는 지급 완료 티어 또는 현재 누적 점수가 골을 넘긴 티어.
    /// </summary>
    public List<CommonSliderProgressRewardData> BuildRewardSliderData()
    {
        var result = new List<CommonSliderProgressRewardData>();

        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
        {
            DLogger.Warning($"[Carrot] 보상 리스트 비어있음 — rewardGroup 미해결(cachedData={(cachedData == null ? "null" : cachedData.typeIndex.ToString())}). 이벤트 초기화(Initialize) 여부 확인");
            return result;
        }

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
        {
            DLogger.Warning($"[Carrot] 보상 리스트 비어있음 — Event_RewardGroup[{rewardGroup}] 행 없음. 테이블 적재(Assets/Resources/Tables/Event_RewardGroup.csv) 확인");
            return result;
        }

        var tierCount = tiers.Count;

        // 최고 골 점수(게이지 상단 기준) — 0 나눗셈 방지로 최소 1.
        long maxGoal = 1;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier != null && tier.goalValue2 > maxGoal)
                maxGoal = tier.goalValue2;
        }

        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier == null)
                continue;

            var rewards = new List<RewardPacketData>();
            AppendRewardBundle(tier, rewards);

            result.Add(new CommonSliderProgressRewardData
            {
                index      = tier.index,
                goalValue2 = tier.goalValue2,   // ScoreRewardText(필요 점수) 표시용
                height     = (float)tier.goalValue2 / maxGoal,   // 정규화 0~1 (위치는 RewardContainer VerticalLayoutGroup이 담당, 현재 미사용 — 게이지형 수동 배치 전환 대비 보존)
                rewards    = rewards,
                isRewarded = claimedCumulativeRewardIndices.Contains(tier.index) || cumulativeScore >= tier.goalValue2,
            });
        }

        // 진행중(현재 목표) 티어 표시(PDF p.2 ScrollPanelProgress) — 미보상 티어 중 최초(최저 골 점수) 하나만 Progress 로.
        // 티어는 테이블 행 순서(골 점수 오름차순)라 첫 미보상 항목이 다음 목표다.
        var rewardCount = result.Count;
        for (var i = 0; i < rewardCount; ++i)
        {
            if (!result[i].isRewarded)
            {
                result[i].isCurrent = true;
                break;
            }
        }

        return result;
    }

    /// <summary>결과 팝업의 [다시] — 동일 조건 재시작. 진입 시 코인 차감(EnterInGame → ConsumeStartCoin).</summary>
    public void OnClickRetry()
    {
        boardModel?.ResetGame();
        scoreModel?.ResetGame();

        if (!HasEnoughStartCoin())
        {
            OpenNeedCoinPopup();    // 코인 부족 팝업(재확인 팝업 생략 — §5-6 6)
            return;
        }

        // 입장 코인(gameStartCoin)은 게임 시작(인게임 진입) 시점에 차감한다(EnterInGame → ConsumeStartCoin).
        EnterInGame();
    }

    public override void OnCompleteEvent()
    {
        // 이벤트 종료 팝업(최고 점수/종류별 잡은 수량 — §5-7)은 완료 후 이벤트 팝업 복귀 시마다 노출.
        OpenEndPopup();

        // 표준 완료 처리(베이스 + 환급 + 완료 메시지)는 1회만 — 반복 진입 시 중복 환급/이벤트 정리 방지.
        if (completeProcessed)
            return;
        completeProcessed = true;

        base.OnCompleteEvent();

        // 미수령 보상 환급 처리 후 완료 메시지 전파(기존 베이스 패턴 재사용).
        OpenRefundPopoup(() =>
        {
            Message.Send(new OnEventCompleteMsg()
            {
                eventType = CurEventType
            });
        });
    }

#if UNITY_EDITOR
    /// <summary>
    /// 치트 — 당근 뽑기 컨텐츠 강제 클리어. 누적 점수를 전 티어 최고 골 이상으로 실서버에 올린 뒤,
    /// 서버 확정 누적 기준으로 모든 누적 보상을 로컬 지급하고 이벤트 종료 처리 + 종료 팝업(§5-7)으로 전환한다.
    /// (치트 에디터 CarrotCheatPanel 에서 호출)
    /// </summary>
    public void Cheat_CompleteContent()
    {
        // 누적 점수를 전 티어 최고 골까지 서버에 올린 뒤(서버 권위), 도달한 전 티어 보상 지급 + 종료 처리.
        SubmitCheatCumulativeScoreToServer(GetMaxCumulativeRewardGoal(), () =>
        {
            GrantCumulativeRewards();   // 도달한 전 티어 누적 보상 로컬 지급(미지급분만, claimedCumulativeRewardIndices 갱신)
            OnCompleteEvent();          // 이벤트 종료 처리 + 종료 팝업(§5-7)
        });
    }

    /// <summary>
    /// 치트 — 누적 점수를 "최종 보상 티어 클리어 직전"까지 실서버에 채운다(§5-4).
    /// 목적: 마지막 1회 플레이로 최종 골을 넘겨 컨텐츠가 클리어되는 흐름(§5-7 종료 팝업)을 확인하기 위한 셋업.
    /// 메인 팝업이 활성 상태일 때만 적용한다(false 반환=미적용). 최종 티어를 제외한 전 티어는 이때 지급한다.
    /// </summary>
    public bool Cheat_FillScoreNearClear()
    {
        // 요구사항: 메인 팝업이 활성 상태일 때만 적용.
        if (UIManager.Instance.FindUIWindow<UIPopupEventCarrot>(true) == null)
            return false;

        var maxGoal = GetMaxCumulativeRewardGoal();
        if (maxGoal <= 0)
            return true;

        // 최종 티어 골 직전(maxGoal - 1)까지 서버에 채운다 — 마지막 1회 플레이의 어떤 점수로도 최종 골을 넘기게 한다.
        SubmitCheatCumulativeScoreToServer(maxGoal - 1, () =>
        {
            GrantCumulativeRewards();   // 최종 티어를 제외한 전 티어 지급(claimed) — 최종 티어만 남긴다.
            RefreshOpenPopups();        // 떠 있는 메인 팝업 즉시 갱신(점수/게이지/보상 리스트)
        });
        return true;
    }

    // 누적 보상 티어 중 최고 골 점수(goalValue2). 보상 그룹/티어 없으면 0.
    private long GetMaxCumulativeRewardGoal()
    {
        var rewardGroup = GetRewardGroup();
        if (rewardGroup <= 0)
            return 0;

        var tiers = TableManager.Instance.GetEventRewardData(rewardGroup);
        if (tiers.IsNullOrEmpty())
            return 0;

        long maxGoal = 0;
        var tierCount = tiers.Count;
        for (var i = 0; i < tierCount; ++i)
        {
            var tier = tiers[i];
            if (tier != null && tier.goalValue2 > maxGoal)
                maxGoal = tier.goalValue2;
        }

        return maxGoal;
    }

    // [Cheat] 목표 누적 점수(targetCumulative)에 도달하도록 결과 패킷(RQEventCarrotResult)을 실서버에 제출한다.
    // 서버는 누적을 cumulativeScore += score 로 합산하므로 (목표 - 현재 누적)만큼을 한 판 점수로 보낸다.
    // 서버 응답으로 누적/최고를 권위값으로 보정(SyncFromServerCarrotInfo)한 뒤 afterSync 후처리를 실행한다.
    // ※ 서버가 bestScore = max(score) 로 갱신하므로 큰 델타를 한 번에 보내면 최고 점수도 그 값으로 올라간다(치트 한정 부작용).
    //   최고 점수 비오염 정확 세팅이 필요하면 서버에 치트 전용 패킷이 별도로 필요하다.
    private void SubmitCheatCumulativeScoreToServer(long targetCumulative, Action afterSync)
    {
        var eventSeq = GetCarrotEventSeq();
        var delta = targetCumulative - cumulativeScore;
        if (eventSeq <= 0 || delta <= 0)
        {
            // 서버 미가용(seq 없음) 또는 이미 목표 이상 — 로컬만 보정 후 후처리.
            if (delta > 0)
                cumulativeScore = targetCumulative;
            afterSync?.Invoke();
            return;
        }

        WrapWebManager.Instance.RequestEventCarrotResult(eventSeq, (int)delta, 0, 0, 0, _ =>
        {
            SyncFromServerCarrotInfo();   // 서버 권위 누적/최고로 보정
            afterSync?.Invoke();
        });
    }
#endif

    // ── 테이블 바인딩 (Event_CarrotSetting) ───────────────────────

    private EventCarrotBoardModel CreateBoardModel()
    {
        GetGameSetting(out int grid, out int maxSpawn, out float[] objLife, out float[] spawnGap,
            out float gameTime, out int superHp);
        boardGrid = grid;       // 보드 뷰(EventCarrotBoardView)가 BoardGridSize 로 읽어 N×N 슬롯을 생성한다.
        var selector = CreateSpawnSelector();
        return new EventCarrotBoardModel(selector, grid * grid, maxSpawn, objLife, spawnGap, gameTime, superHp);
    }

    private EventCarrotSpawnSelector CreateSpawnSelector()
    {
        GetSpawnSetting(out int[] appearRate, out int superPityCount, out int superMax);
        return new EventCarrotSpawnSelector(appearRate, superPityCount, superMax);
    }

    // Event_CarrotSetting 행을 loadTableIdx 로 조회·캐시(index == loadTableIdx). 미로드/미존재 시 null → 각 getter 가 기획 예시값으로 폴백.
    private EventCarrotSettingTableData GetSettingTable()
    {
        if (settingTable != null)
            return settingTable;

        var loadTableIdx = cachedData != null ? cachedData.GetLoadTableIndex() : -1;
        TableManager.GetData(loadTableIdx, out settingTable);
        return settingTable;
    }

    // grid/maxSpawn/objLife/spawnGap/gameTime/supreHP(§8-4) — Event_CarrotSetting 조회. 미로드 시 기획 예시값 폴백.
    private void GetGameSetting(out int grid, out int maxSpawn, out float[] objLife, out float[] spawnGap,
        out float gameTime, out int superHp)
    {
        var table = GetSettingTable();
        if (table != null)
        {
            grid     = table.grid;
            maxSpawn = table.maxSpawn;
            objLife  = table.objLife;
            spawnGap = table.spawnGap;
            gameTime = table.gameTime;
            superHp  = table.supreHP;       // supreHP(컬럼명 오타) — 슈퍼 레어 타격 수
            return;
        }

        grid     = 3;                       // N×N 구멍
        maxSpawn = 5;                       // 한 번에 최대 등장 수
        objLife  = new[] { 0.4f, 0.7f };    // 노출 시간 [최소, 최대]
        spawnGap = new[] { 0.4f, 0.7f };    // 등장 간격 [최소, 최대]
        gameTime = 20f;                     // 게임 시간(초)
        superHp  = 2;                       // 슈퍼 레어 타격 수
    }

    private EventCarrotScoreModel CreateScoreModel()
    {
        GetScoreSetting(out int[] scoreSet, out int[] comboCount, out int[] comboBonus, out float comboKeepTime);
        return new EventCarrotScoreModel(scoreSet, comboCount, comboBonus, comboKeepTime);
    }

    // scoreSet/comboCount/comboBonus(§8-4) — Event_CarrotSetting 조회. comboKeepTime 은 테이블 컬럼 미정(기획 §11) → 기본 2초.
    private void GetScoreSetting(out int[] scoreSet, out int[] comboCount, out int[] comboBonus, out float comboKeepTime)
    {
        comboKeepTime = 2f;                  // 콤보 유지 시간(초) — 테이블 컬럼 미정
        var table = GetSettingTable();
        if (table != null)
        {
            scoreSet   = table.scoreSet;
            comboCount = table.comboCount;
            comboBonus = table.comboBonus;
            return;
        }

        scoreSet   = new[] { 5, 10, 5 };     // [일반, 레어, 슈퍼 레어] 터치당 점수
        comboCount = new[] { 2, 10 };        // [일반 발동, 메가 발동] 연속 성공 수
        comboBonus = new[] { 1, 2 };         // [일반, 메가] 가산점
    }

    // appearRate/superPityCount/superMax(§8-4) — Event_CarrotSetting 조회. 미로드 시 기획 예시값 폴백.
    private void GetSpawnSetting(out int[] appearRate, out int superPityCount, out int superMax)
    {
        var table = GetSettingTable();
        if (table != null)
        {
            appearRate     = table.appearRate;
            superPityCount = table.superPityCount;
            superMax       = table.superMax;
            return;
        }

        appearRate     = new[] { 8000, 1500, 500 };  // 만분율 [일반, 레어, 슈퍼]
        superPityCount = 20;
        superMax       = 2;
    }

    // superRewardIdx / superRewardRate(§8-4) — Event_CarrotSetting 조회. 미로드 시 기획 예시값 폴백.
    private void GetSuperRewardTable(out int[] rewardIndices, out int[] rewardRates)
    {
        var table = GetSettingTable();
        if (table != null)
        {
            rewardIndices = table.superRewardIdx;
            rewardRates   = table.superRewardRate;
            return;
        }

        rewardIndices = new[] { 10112, 10113, 10114, 10115 };
        rewardRates   = new[] { 5000, 2000, 1000, 1000 };   // 합 9000 — 나머지 1000(미당첨) 처리 정책 확정 필요
    }
}

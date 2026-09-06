using System.Collections.Generic;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;
using GameCore.Utility;
using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;
using GameLogic.Network;
using GameLogic.Network.FakeServer.FsDataHandler.LiveEvent;

using StatefulUI.Runtime.Core;
using StatefulUISupport.Scripts.Components;

using DG.Tweening;

using UnityEngine;
using UnityEngine.UI;

// 캐릭터 카페 2차 서브콘텐츠 — 우사하나 풍선 게임 메인(인게임) 팝업 (상세 892698729 §3-2)
// 라운드 풍선 레이아웃(서버 권위)을 그리드로 표시하고, 풍선 터뜨리기를 Content.Try* 로 위임한다.
// 라운드 전환/상점/클리어 등 부가 팝업은 후속(스텁) — 본 팝업은 메인 게임판 + 풍선 그리드/터뜨리기를 담당한다.
public class UIPopupMiniGameBalloon : UIBasePopup
{
    private const LiveEventType EVENT_TYPE = LiveEventType.CHARACTERCAFE;

    // [사운드] 기획 892698729 §4 확정 인덱스(1141~1146, v17 2026-06-15) 적용.
    private const int SOUND_BALLOON_POP = 1141; // 풍선 팡(터뜨림) — §4-1 클릭 즉시
    private const int SOUND_REWARD_GET = 1142;  // 보상 획득(슬롯 도착) — §4-1
    private const int SOUND_KEY_FOUND = 1143;   // 열쇠 발견(터뜨림) — §4-2 풍선 팡 시점
    private const int SOUND_KEY_UNLOCK = 1144;  // 잠금 해제(도착) — §4-2
    private const int SOUND_GAME_CLEAR = 1146;  // 클리어 팡파레 — §4-4 연출 종료 시점

    // 인포 팝업(§3-3) — _Info 어드레서블 키 + 최초 진입 1회 강제 first-open 플래그.
    //  메인 카페 이벤트(LiveEventType.CHARACTERCAFE)의 인포 first-open 키와 충돌하지 않도록 미니게임 전용 pid 키를 사용한다.
    private const string INFO_POPUP_KEY = "UIPopupMiniGameBalloon_Info";
    private const string INFO_FIRST_OPEN_PREF_KEY = "CharacterCafeMiniGameBalloonInfo";

    // 열쇠 버튼(Btn_Next) Animator(MiniGameBalloon_Button) 상태명 — Enable/Disable 클립이 EnableObject/DisableObject 활성을 제어.
    private const string ANIM_KEY_BTN_ENABLE = "Enable";
    private const string ANIM_KEY_BTN_DISABLE = "Disable";
    private const string ANIM_KEY_BTN_ACTIVATE = "Activate"; // 트리거 — 잔여 트리거 클리어용

    // 라운드 전환 연출(4-3) — 루트 Animator(UIPopupMiniGameBalloon) NextRound 트리거 + 라운드 시작 사운드(0005).
    private const string ANIM_PARAM_NEXT_ROUND = "NextRound";
    private const int SOUND_ROUND_START = 1145; // 라운드 시작 배너 — 892698729 §4-3 배너 노출 시점

    // 라운드 시작 배너(TransitionToNextRound/TextDesc) — GetText(LIDX_ROUND_START) "라운드 {0} START!" 의 {0}=라운드 번호 치환.
    private const int LIDX_ROUND_START = 43302;

    // 열쇠 도착(자물쇠 열림) 후 팝업/다음 라운드 진행까지의 텀(ms) — 유저가 자물쇠 열림→팝업 플로우를 인지하도록 잠깐 대기.
    private const int KEY_FOUND_DELAY_MS = 1000;

    // ─────────────────────────────────────────────────────────────────────
    // [로컬라이징 LIdx] 기획 892698729 §3-2 이벤트 팝업 문구.
    //   제목(43161 "풍선 게임!")은 비가변 → 프리팹 "Title/UITextEx" 노드 UITextEx(mStringKey=43161)에 직접 바인딩 완료(코드 미설정).
    //   내용(43162 "풍선 속에 숨겨진…")도 비가변 → [완료 06-14] 헤더 컨테이너에 내용 UITextEx 노드 추가 + mStringKey=43162 인스펙터 바인딩(제목 노드 복제, 제목 아래 배치).
    //   규칙(⑦ "{0}개로 풍선 1개 터트리기!")은 popCost 치환이 있는 가변 → 코드에서 처리(LIDX_RULE). [채번 = LIdx 43301]
    private const int LIDX_RULE = 43301;    // 규칙 "{1} {0}개로 풍선 1개 터트리기!" — RefreshTexts 가 string.Format(GetText(43301), popCost, eventCurrencySprite) ({0}=popCost, {1}=재화 아이콘 sprite 태그)
    // ─────────────────────────────────────────────────────────────────────

    // 풍선 Spine 스킨(형태×색상) 풀 — 생성/재생성 시 슬롯별 랜덤 배정 (Spine_UIPopupCharacterCafe_Balloon_Ani)
    // 원형(Cirle) 6색 + 하트(Heart) 6색 = 12종
    private static readonly string[] BALLOON_SKINS =
    {
        "Cirle_Sky", "Cirle_Pink", "Cirle_Yellow", "Cirle_Green", "Cirle_Orange", "Cirle_Purple",
        "Heart_Sky", "Heart_Pink", "Heart_Yellow", "Heart_Green", "Heart_Orange", "Heart_Purple",
    };

    public class Info : IUIInfoData
    {
        public ContentEventCharacterCafe content; // 데이터 변경 위임 대상 (Try*)
        public int objectIdx;                     // 미니게임을 띄운 오브젝트 (objectEventIdx 연결)
    }

    [SerializeField] private UIMiniGameBalloonGrid balloonGrid;   // 풍선 그리드 컨트롤러 (프리팹 바인딩)
    [SerializeField] private StatefulComponent[] roundSteps;      // 라운드 스텝퍼 점(Round1~5, 인덱스=라운드-1)
    [SerializeField] private RectTransform roundSliderRect;       // 라운드 스텝퍼 바(RoundSlider) — 총 라운드 수에 비례해 폭 조정(ISSUE-69). ⚠️ 점 컨테이너(RoundInfo)의 부모라 GO 를 끄면 점까지 사라진다
    [SerializeField] private UIButtonEx treasureKeyButton;        // 보물상자 열기(열쇠) 버튼(Btn_Next) — 열쇠 발견 시 활성
    [SerializeField] private Animator treasureKeyAnimator;        // Btn_Next Animator(MiniGameBalloon_Button) — Enable/Disable 클립이 EnableObject/DisableObject 활성을 제어
    [SerializeField] private Animator popupTransitionAnimator;    // 루트 Animator(UIPopupMiniGameBalloon) — NextRound 트리거로 라운드 전환 연출(4-3)
    [SerializeField] private UITextEx roundStartBannerText;       // 라운드 시작 배너(TransitionToNextRound/TextDesc) — "라운드 {0} START!" {0}=라운드 번호 치환(프리팹 raw 텍스트, mTextType=Text)
    [SerializeField] private StatefulComponent[] rewardSlots;     // 보물상자 히든 보상 슬롯(Reward1~4)
    [SerializeField] private StatefulComponent treasureBoxInfo;   // 보물상자/열쇠버튼 라운드 타입(Info 노드) — 일반/마지막
    [SerializeField] private StatefulComponent treasureRewardBox; // 보석함 오브제(_Reward_Box) — 열쇠 도착 시 정화(DirtyOut), 마지막 라운드는 Final (§4-9)
    [SerializeField] private UIBalloonRewardItem rewardEffectItem;

    private Queue<UIBalloonRewardItem> rewardEffectItemQue = new Queue<UIBalloonRewardItem>();
    private Info curInfo;
    private int objectEventIdx;                 // curInfo.objectIdx(Event_CharacterCafeObject.index) → 미니게임 인덱스. 라운드 테이블 조회 키(첫 SetInfo 시 캐싱)
    private CommonRewardItem[] rewardSlotItems; // rewardSlots 와 평행한 보상 아이콘(첫 사용 시 캐싱)
    private bool keyFoundPopupShown;           // 열쇠 발견 팝업(RoundInfoPopup) 라운드당 1회 자동 노출 가드
    private int lastReadyRound;                // 라운드 전환 연출(4-3) 감지용 — 직전 OnRoundReady 라운드(0=최초). 증가 시 전환 연출 재생
    // [ISSUE-70] 결과 비행 이펙트는 풀링되는 rewardEffectItem(UIBalloonRewardItem) 인스턴스로 표시한다 — 각 인스턴스가 결과 1개만 들고 있어
    //  공유 노드 오염(열쇠인데 이전 아이템 노출 등)이 원천 없음. 여러 보상이 동시에 각자 독립 비행하며, 도착 시 각자 풀로 반환된다.
    private CancellationTokenSource resultFlyCts; // 결과 비행 IsReady 대기 취소용 — 라운드 전환/팝업 종료 시 전체 취소(+재생성)
    private CancellationTokenSource keyFoundDelayCts; // 열쇠 도착 후 팝업/진행 지연(OpenKeyFoundAfterDelayAsync) 취소용 — 라운드 전환/정리 시 취소(직전 라운드 스테일 콜백이 다음 라운드 자동 노출 가드를 오염시키는 것 방지)
    private readonly List<UIBalloonRewardItem> activeRewardItems = new(); // 동시 비행 중인 결과 아이템들 — 각자 독립 비행, 정리 시 전부 풀 반환
    private bool keyResultFlying;              // 열쇠 비행 중 풍선 조작 차단 — 열쇠가 자물쇠로 들어가 팝업이 열리는 플로우를 유저가 인지하도록(비행 시작~도착)
    private Transform rewardEffectParent;      // 결과 아이템 인스턴스 부모(레이어 유지용 — 기존 Effect 노드 부모)
    private UIInfoPopupController infoPopupController; // 인포 팝업(_Info) 인스턴스 — 첫 오픈 시 로드·캐싱(§3-3)
    private bool infoPopupLoading;             // 인포 팝업 비동기 로드 중복 가드

    protected override void OnEnable()
    {
        base.OnEnable();
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.AddButtonListener(ButtonRole.Close, Close);
        if (Stateful.HasButton(ButtonRole.Info))
            Stateful.AddButtonListener(ButtonRole.Info, OnClickInfo);
        if (null != treasureKeyButton)
            treasureKeyButton.onClick.AddListener(OnClickTreasureBox);

        // 서브 컨텐츠 라이브 갱신(재화 변동 fan-out 등) 통지를 수신해 재화 표시를 갱신한다.
        Message.AddListener<CharacterCafeMiniGameSubContent.RefreshMsg>(OnRefreshMsg);
        // [이벤트 기반 분리] 송수신/적용 완료 통지 → 응답 기반 UI 재구성/연출.
        Message.AddListener<CharacterCafeMiniGameSubContent.RoundReadyMsg>(OnRoundReady);     // 라운드 준비/전환 → RefreshAll
        Message.AddListener<CharacterCafeMiniGameSubContent.BalloonPoppedMsg>(OnBalloonPopped); // 풍선 터뜨림 → 결과 연출
        Message.AddListener<CharacterCafeMiniGameSubContent.ObjectClearedMsg>(OnMiniGameCleared); // 미니게임 클리어 → 팝업 닫기
    }

    protected override void OnDisable()
    {
        // [단일 송신 2026-06-17] 라운드를 진행하지 않고 닫는 경우, 로컬에만 누적된 풍선 터뜨림 진행을 1회 영속한다(재접속 시 보드 상태 유실/재팝 방지).
        //  라운드 진행/클리어로 이미 단일 송신됐으면 dirty 가 비어 무동작이다(중복 송신 없음).
        if (null != curInfo?.content?.MiniGameSubContent)
            curInfo.content.MiniGameSubContent.FlushPendingPops(curInfo.objectIdx);

        ReleaseAllResultFlies(); // [ISSUE-70] 동시 비행 중인 결과 전부 정리(트윈 Kill + 대기 취소 + 풀 반환)

        // 인포 팝업이 열려 있으면 닫아 메인 팝업 재오픈 시 잔상이 남지 않게 한다(인스턴스는 캐싱 유지).
        if (null != infoPopupController)
            infoPopupController.gameObject.SetActive(false);

        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.RemoveButtonAllListener(ButtonRole.Close);
        if (Stateful.HasButton(ButtonRole.Info))
            Stateful.RemoveButtonAllListener(ButtonRole.Info);
        if (null != treasureKeyButton)
            treasureKeyButton.onClick.RemoveListener(OnClickTreasureBox);

        Message.RemoveListener<CharacterCafeMiniGameSubContent.RefreshMsg>(OnRefreshMsg);
        Message.RemoveListener<CharacterCafeMiniGameSubContent.RoundReadyMsg>(OnRoundReady);
        Message.RemoveListener<CharacterCafeMiniGameSubContent.BalloonPoppedMsg>(OnBalloonPopped);
        Message.RemoveListener<CharacterCafeMiniGameSubContent.ObjectClearedMsg>(OnMiniGameCleared);
        base.OnDisable();
    }

    // 서브 컨텐츠 라이브 갱신 통지 — 외부 재화 변동 등으로 보유 재화 표시를 최신화한다.
    // 풍선 그리드/라운드 레이아웃은 풍선 터뜨리기·라운드 전환 시점에만 변하므로 여기서 재생성하지 않는다(셔플 깜빡임 방지).
    private void OnRefreshMsg(CharacterCafeMiniGameSubContent.RefreshMsg msg)
    {
        RefreshCurrency();
    }

    // [ISSUE-71] 풍선게임 팝업은 뒤로가기(ESC)로 닫지 않는다 — 라운드 전환 연출 중 비정상 닫힘(보상 슬롯 1칸/더미 노출) 방지.
    //  ESC 를 소비(true 반환)해 아래 카페 메인 팝업으로 fall-through 되어 닫히는 것도 막는다. 닫기는 X 버튼(ButtonRole.Close)·게임 플로우로만.
    public override bool OnKeyEscapeExcute()
    {
        return true;
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);
        curInfo = data as Info;
        if (null == curInfo || null == curInfo.content)
        {
            DLogger.Error($"[{GetType().Name}] invalid info");
            return;
        }

        // 미니게임 라운드 테이블(GetCharacterCafeMiniGameData*) 조회 키 = objectEventIdx.
        // 진입 오브젝트(Event_CharacterCafeObject)에서 미니게임 인덱스를 해석해 캐싱한다.
        objectEventIdx = ResolveObjectEventIdx(curInfo.objectIdx);

        lastReadyRound = 0; // 재오픈 시 첫 라운드를 전환으로 오인하지 않도록 초기화(4-3)

        // 라운드 시작 보장을 서브컨텐츠 진입점에 위임(시작점=서브컨텐츠). 레이아웃 준비 완료(RoundReadyMsg) 시 UI 구성.
        InitRound();
    }

    // 라운드 시작 보장 — 입력만 서브컨텐츠 진입점(RequestEnsureRoundStarted)에 위임한다(시작점=서브컨텐츠).
    //  레이아웃 준비 완료는 RoundReadyMsg 로 통지되며, 그때 UI 전체를 구성한다(OnRoundReady → RefreshAll).
    private void InitRound()
    {
        if (null != curInfo?.content?.MiniGameSubContent)
            curInfo.content.MiniGameSubContent.RequestEnsureRoundStarted(curInfo.objectIdx);
    }

    // 라운드 레이아웃 준비/전환 완료 통지(RoundReadyMsg) → UI 전체 재구성(응답 기반).
    private void OnRoundReady(CharacterCafeMiniGameSubContent.RoundReadyMsg msg)
    {
        if (null == msg || this == null) return;
        if (null == curInfo || msg.objectIdx != curInfo.objectIdx) return;

        // 라운드 전환 연출(4-3) 감지 — 직전 라운드에서 전진했을 때만(최초 진입/재오픈 제외).
        int curRound = GetMiniGameData()?.curRound ?? 1;
        bool isRoundAdvance = lastReadyRound > 0 && curRound > lastReadyRound;
        lastReadyRound = curRound;

        RefreshAll();

        // 최초 미니게임 진입 1회 강제 인포 노출(§3-3) — pid 플래그로 1회만 자동 노출(이후 ? 버튼으로 재호출).
        TryForceFirstOpenInfo();

        // 라운드 전환 연출(4-3) — 보상 솟구침→배너+스윕→새 라운드 폭죽 + 라운드 시작 사운드(0005).
        //  주요 보상 획득 연출(§4-5)은 라운드 진입이 아니라, 중요 보상 풍선을 터뜨려 "획득하는 시점"에 재생한다(OnBalloonPopped → PlayImportantRewardObtain).
        if (isRoundAdvance)
            PlayNextRoundTransition(curRound);
    }

    // 라운드 전환 연출(4-3) — 루트 Animator NextRound 트리거(Ani_Clip_TransitionToNextRound) + 라운드 시작 사운드(0005).
    private void PlayNextRoundTransition(int round)
    {
        // 라운드 시작 배너 "라운드 {0} START!" — {0} 자리표시자를 현재 라운드 번호로 치환.
        if (null != roundStartBannerText)
            roundStartBannerText.SetText(string.Format(TableManager.GetText(LIDX_ROUND_START), round));
        if (null != popupTransitionAnimator && popupTransitionAnimator.gameObject.activeInHierarchy)
            popupTransitionAnimator.SetTrigger(ANIM_PARAM_NEXT_ROUND);
        PlayResultSound(SOUND_ROUND_START);
    }

    private void RefreshAll()
    {
        RefreshCurrency();
        RefreshTexts();
        RefreshRoundStepper();
        RefreshTreasureKey();
        RefreshTreasureBox();
        RefreshRewardSlots();
        BuildGrid();

        // [ISSUE-70] 라운드 전환/재오픈 시 동시 비행 중인 결과 전부 정리 + 풀 반환(잔상 방지).
        ReleaseAllResultFlies();

        keyFoundPopupShown = false; // 라운드 시작/재오픈 시 열쇠 발견 팝업 자동 노출 가드 초기화
        RefreshGuide(false);        // 라운드 시작/재오픈 시 손가락 유도 숨김(§4-6)
    }

    // 보물상자 보상 슬롯(Reward1~4) — 라운드 히든 보상별 상태: 미획득(RewardBefore)/중요(RewardImportant)/획득(RewardGet)
    private void RefreshRewardSlots()
    {
        if (rewardSlots.IsNullOrEmpty())
            return;

        MiniGameModel data = GetMiniGameData();
        EventCharacterCafeMiniGameTableData table = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, data?.curRound ?? 1);
        int[] rewards = table?.roundReward;
        int[] signs = table?.roundRewardSign;

        Dictionary<int, int> poppedCount = CountPoppedRewards(data);
        Dictionary<int, int> seen = new();

        int slotCount = rewardSlots.Length;
        int rewardCount = (rewards != null) ? rewards.Length : 0;
        for (int i = 0; i < slotCount; i++)
        {
            StatefulComponent slot = rewardSlots[i];
            if (null == slot)
                continue;

            // 라운드 보상 수보다 많은 슬롯은 비활성
            if (i >= rewardCount || rewards[i] <= 0)
            {
                slot.gameObject.SetActive(false);
                continue;
            }
            slot.gameObject.SetActive(true);

            int reward = rewards[i];
            bool important = signs != null && i < signs.Length && signs[i] == 1;

            // 보상 아이콘 표시 (Event_Reward.index → 아이템 아이콘/수량)
            CommonRewardItem rewardItem = GetSlotRewardItem(i);
            if (null != rewardItem)
                rewardItem.SetInfo(ResolveRewardInfo(reward));

            // 슬롯 터치 → 아이템 타입별 인포(§3-1/§D: 1=없음 / 7=아이템 인포·확률표 / 10=뱃지 말풍선).
            BindRewardSlotInfo(slot, rewardItem);

            // 보상 슬롯 상태 — 중요/일반 모두 "풍선을 터뜨려 획득(popped)"한 시점에만 획득 상태로 전환한다.
            //  중요 보상도 라운드 진입 시점에 RewardImportant(획득 이펙트)로 표시하지 않고, 획득 전에는 RewardBefore 로 둔다.
            //  (중요 보상은 BuildMiniGameBalloonContents 에서 풍선 콘텐츠로 배치되어 터뜨려 획득 — 획득 연출은 §4-5 PlayImportantRewardObtain.)
            int occurrence = (seen.TryGetValue(reward, out int v) ? v : 0) + 1;
            seen[reward] = occurrence;
            int collected = poppedCount.TryGetValue(reward, out int c) ? c : 0;
            bool obtained = collected >= occurrence;

            StateRole state;
            if (important)
                state = obtained ? StateRole.RewardImportant : StateRole.RewardBefore;
            else
                state = obtained ? StateRole.RewardGet : StateRole.RewardBefore;

            if (slot.HasState((int)state))
                slot.SetState((int)state);
        }
    }

    // 슬롯의 보상 아이콘(CommonRewardItem) — 중첩 프리팹이라 첫 사용 시 자식에서 캐싱
    private CommonRewardItem GetSlotRewardItem(int index)
    {
        if (rewardSlots.IsNullOrEmpty() || index < 0 || index >= rewardSlots.Length)
            return null;

        rewardSlotItems ??= new CommonRewardItem[rewardSlots.Length];
        if (null == rewardSlotItems[index] && null != rewardSlots[index])
            rewardSlotItems[index] = rewardSlots[index].GetComponentInChildren<CommonRewardItem>(true);
        return rewardSlotItems[index];
    }

    // 보상 슬롯 터치 → 타입별 인포 호출(§3-1/§D). 슬롯 StatefulComponent 의 CommonRewardItemInfo 버튼에 핸들러를 재바인딩한다.
    //  타입별 분기(Block 7=인포/확률표, BadgePack 10=뱃지 말풍선, Facility 12=시설, 그 외=무동작="1:노출 없음")는
    //  공용 CommonRewardItem.OnClickShowInfo 가 처리한다. 클리어 팝업(§3-5) 보상 터치와 동일 기능.
    private void BindRewardSlotInfo(StatefulComponent slot, CommonRewardItem rewardItem)
    {
        if (null == slot || !slot.HasButton(ButtonRole.CommonRewardItemInfo))
            return;

        slot.RemoveButtonAllListener(ButtonRole.CommonRewardItemInfo);
        if (null == rewardItem)
            return;

        slot.AddButtonListener(ButtonRole.CommonRewardItemInfo, () => CommonRewardItem.OnClickShowInfo(rewardItem, null));
    }

    // Event_Reward.index → RewardInfo(아이템 타입/인덱스/수량). 미정의/0 이면 null(아이콘 숨김)
    private RewardInfo ResolveRewardInfo(int rewardEventIndex)
    {
        if (rewardEventIndex <= 0)
            return null;

        if (TableManager.GetData(rewardEventIndex, out EventRewardTableData rewardTable))
            return new RewardInfo((ItemType)rewardTable.itemType, rewardTable.itemIdx, rewardTable.itemValue);
        return null;
    }

    // 라운드 히든 보상별 획득(터뜨림) 수 — content(보상 idx)별 popped 카운트
    private Dictionary<int, int> CountPoppedRewards(MiniGameModel data)
    {
        Dictionary<int, int> count = new();
        IReadOnlyList<int> contents = data?.balloonContents;
        if (null == contents)
            return count;

        IReadOnlyList<bool> popped = data.balloonPopped;
        int n = contents.Count;
        for (int i = 0; i < n; i++)
        {
            bool isPopped = (null != popped) && i < popped.Count && popped[i];
            int content = contents[i];
            if (isPopped && content > 0)
                count[content] = (count.TryGetValue(content, out int v) ? v : 0) + 1;
        }
        return count;
    }

    // 보물상자/열쇠 버튼 — 라운드 타입(라벨·화려 UI) + 열쇠 발견 시 활성(잠금 해제). 재오픈 시 데이터로 복원.
    //  animateActivate=true(열쇠 비행 "도착" 시점) → 정적 Enable 대신 잠금→활성 "변경 연출"(BtnActivate, §4-12) 재생.
    private void RefreshTreasureKey(bool animateActivate = false)
    {
        MiniGameModel data = GetMiniGameData();

        // 라운드 타입: 마지막 라운드면 RoundLast("라운드 클리어"·화려), 아니면 RoundDefault("다음 라운드")
        if (null != treasureBoxInfo)
        {
            int round = data?.curRound ?? 1;
            StateRole boxState = IsLastRound(round) ? StateRole.RoundLast : StateRole.RoundDefault;
            if (treasureBoxInfo.HasState((int)boxState))
                treasureBoxInfo.SetState((int)boxState);
        }

        bool keyFound = (null != data) && data.keyFound;

        // 열쇠 버튼: 열쇠 발견 시 클릭 가능(잠금 해제)
        if (null != treasureKeyButton)
            treasureKeyButton.interactable = keyFound;

        // 열쇠 버튼 활성/비활성 비주얼 — Btn_Next 의 Animator(MiniGameBalloon_Button)가 Enable/Disable/Activate 클립으로
        //  EnableObject/DisableObject 의 m_IsActive 를 매 프레임 제어한다. (SetActive/Stateful 로는 안 되고 애니 상태를 직접 구동.)
        if (null == treasureKeyAnimator || !treasureKeyAnimator.gameObject.activeInHierarchy)
            return;

        // 열쇠 도착 시점(animateActivate) → 잠금→활성 "변경 연출"(BtnActivate) 트리거.
        //  그 외(라운드 시작/재오픈 복원) → 정적 Enable/Disable 재생(BtnEnable=EnableObject 활성 / BtnDisable=DisableObject 활성).
        if (animateActivate && keyFound)
        {
            treasureKeyAnimator.SetTrigger(ANIM_KEY_BTN_ACTIVATE);
        }
        else
        {
            treasureKeyAnimator.ResetTrigger(ANIM_KEY_BTN_ACTIVATE);
            treasureKeyAnimator.Play(keyFound ? ANIM_KEY_BTN_ENABLE : ANIM_KEY_BTN_DISABLE, 0, 0f);
        }
    }

    // 보석함 정화(§4-9) — 열쇠가 자물쇠로 들어갈 때 보석함이 열리는(정화) 연출. 마지막 라운드면 Final, 아니면 Basic.
    //  treasureRewardBox 미바인딩 시 graceful no-op(보물상자 오브제 프리팹 연동 후 동작).
    private void PlayTreasureBoxUnlock()
    {
        if (null == treasureRewardBox)
            return;

        MiniGameModel data = GetMiniGameData();
        StateRole state = IsLastRound(data?.curRound ?? 1) ? StateRole.FinalDirtyOut : StateRole.BasicDirtyOut;
        if (treasureRewardBox.HasState((int)state))
            treasureRewardBox.SetState((int)state);
    }

    // 보석함 기본 상태 복원(§4-9) — 라운드 시작/재오픈 시. 열쇠 발견 후면 정화(DirtyOut), 아니면 잠김 기본(Round).
    //  개봉(Open) 상태로 라운드를 마친 뒤 다음 라운드 진입 시 잠김 기본으로 되돌리는 역할도 겸한다.
    private void RefreshTreasureBox()
    {
        if (null == treasureRewardBox)
            return;

        MiniGameModel data = GetMiniGameData();
        bool last = IsLastRound(data?.curRound ?? 1);
        bool keyFound = (null != data) && data.keyFound;
        StateRole state = keyFound
            ? (last ? StateRole.FinalDirtyOut : StateRole.BasicDirtyOut)
            : (last ? StateRole.FinalRound : StateRole.BasicRound);
        if (treasureRewardBox.HasState((int)state))
            treasureRewardBox.SetState((int)state);
    }

    // 보석함 개봉(§4-9 BasicOpen/FinalOpen) — 보상을 수령(라운드 진행/클리어)하는 시점에 상자가 열리는 연출.
    //  treasureRewardBox 미바인딩/상태 미정의 시 graceful no-op.
    private void PlayTreasureBoxOpen()
    {
        if (null == treasureRewardBox)
            return;

        StateRole state = IsLastRound(GetMiniGameData()?.curRound ?? 1) ? StateRole.FinalOpen : StateRole.BasicOpen;
        if (treasureRewardBox.HasState((int)state))
            treasureRewardBox.SetState((int)state);
    }

    // 손가락 유도(§4-6, §4-12) — 열쇠+히든 전부 수집 시 보물상자(열쇠) 버튼으로 손가락 유도 표시(GuideOn)/숨김(GuideOff).
    //  유도 상태는 열쇠 버튼 Info 노드 stateful(treasureBoxInfo, §4-12)이 보유한다. 미배선/상태 미정의 시 graceful no-op.
    private void RefreshGuide(bool show)
    {
        if (null == treasureBoxInfo)
            return;

        StateRole state = show ? StateRole.GuideOn : StateRole.GuideOff;
        if (treasureBoxInfo.HasState((int)state))
            treasureBoxInfo.SetState((int)state);
    }

    // 라운드 스텝퍼(④) — 총 라운드 수(Event_CharacterCafeMiniGame evnetGIdx 그룹 행 수)만큼만 점을 노출하고(ISSUE-69),
    //  현재 라운드 기준 각 점 상태 전환: 완료(RoundDone)/진행중(RoundNow)/미진입(RoundBefore).
    //  중앙 정렬은 점 컨테이너(RoundInfo)의 HorizontalLayoutGroup(MiddleCenter)이 담당하므로 점 토글만으로 충분하다.
    private void RefreshRoundStepper()
    {
        if (roundSteps.IsNullOrEmpty())
            return;

        MiniGameModel data = GetMiniGameData();
        int curRound = data?.curRound ?? 1;

        int count = roundSteps.Length;
        int totalRounds = TableManager.Instance.GetCharacterCafeMiniGameDatas(objectEventIdx)?.Count ?? 0;
        if (totalRounds <= 0 || totalRounds > count)
            totalRounds = count;   // 미발행/점 초과 시 폴백 — 전 점 노출(현행 유지)

        for (int i = 0; i < count; i++)
        {
            StatefulComponent step = roundSteps[i];
            if (null == step)
                continue;

            bool used = i < totalRounds;
            step.gameObject.SetActive(used);
            if (!used)
                continue;

            int roundNo = i + 1;
            StateRole state = roundNo < curRound ? StateRole.RoundDone
                : roundNo == curRound ? StateRole.RoundNow
                : StateRole.RoundBefore;

            if (step.HasState((int)state))
                step.SetState((int)state);
        }

        RefreshRoundSliderWidth(totalRounds);
    }

    // 라운드 바(RoundSlider) 폭 — 첫/마지막 활성 점의 **실제 배치 위치**로 산출한다(바 양끝 = 양끝 점 중심, ISSUE-69).
    //  점 간격은 HorizontalLayoutGroup 이 Image preferredWidth(스프라이트 원본 폭)로 정하므로 상수/비례식으로 유추하면 어긋난다.
    //  방금 SetActive 를 토글했으므로 레이아웃을 즉시 확정한 뒤 읽는다. 점·바 미바인딩/라운드 1개면 폭 0(바 숨김 효과).
    //  ⚠️ 바 GO 는 끄지 않는다 — 점 컨테이너(RoundInfo)가 자식이라 점까지 사라진다.
    private void RefreshRoundSliderWidth(int totalRounds)
    {
        RectTransform first = roundSteps[0]?.transform as RectTransform;
        RectTransform last = roundSteps[totalRounds - 1]?.transform as RectTransform;
        if (null == first || null == last)
        {
            return;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(first.parent as RectTransform);

        Vector2 size = roundSliderRect.sizeDelta;
        size.x = Mathf.Abs(last.anchoredPosition.x - first.anchoredPosition.x);
        roundSliderRect.sizeDelta = size;
    }

    // 테이블 연동 — 규칙 안내(⑦)를 MiniGame 테이블 값으로 구성 (기획 892698729 §3-2)
    // 제목/내용 등 고정 문구는 KF_String LIdx 미정의(Confluence 857473046 §7-2 미작성)라 후속 TableManager.GetText 로 교체.
    private void RefreshTexts()
    {
        if (!Stateful.HasText(TextRole.GuideText1))
            return;

        MiniGameModel data = GetMiniGameData();
        int round = data?.curRound ?? 1;
        EventCharacterCafeMiniGameTableData roundTable = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, round);
        if (null == roundTable)
            return;

        string format = TableManager.GetText(LIDX_RULE);
        // 규칙 안내(⑦, LIdx 43301) — {0}=popCost, {1}=이벤트 재화 아이콘(sprite 태그). 아이콘은 활성 카페 Setting.eventIcon 으로 캐릭터별 자동 매칭(ISSUE-72).
        //  재화 아이콘 소스 = ResourceUtils.GetSpritePath(Currency, CharacterCafeEventCurrency)(→ Setting.eventIcon, 미입력 시 Item 테이블 폴백).
        string eventIcon = ResourceUtils.GetSpritePath(ItemType.Currency, (int)CurrencyType.CharacterCafeEventCurrency);
        string eventIconSprite = string.Format(EventCharacterCafeHelper.EVENT_CURRENCY_SPRITE_TAG_FORMAT, eventIcon);
        Stateful.SetText(TextRole.GuideText1, string.Format(format, roundTable.popCost, eventIconSprite));

        // 제목(43161)·내용(43162) 모두 헤더 UITextEx 노드에 인스펙터 StringKey 바인딩 완료(코드 미설정, 06-14 내용 노드 추가).
    }

    private void RefreshCurrency()
    {
        if (Stateful.HasText(TextRole.EventCurrencyText))
            Stateful.SetText(TextRole.EventCurrencyText, EventCurrencyHelper.GetEventCurrency(EVENT_TYPE));
    }

    private void BuildGrid()
    {
        if (null == balloonGrid)
        {
            DLogger.Error($"[{GetType().Name}] balloonGrid not bound");
            return;
        }

        MiniGameModel data = GetMiniGameData();
        if (null == data)
            return;

        UIMiniGameBalloonGrid.Info gridInfo = new()
        {
            balloonContents = data.balloonContents,
            balloonPopped = data.balloonPopped,
            balloonImportant = BuildImportantFlags(data.balloonContents, data.curRound),
            balloonSkins = BuildRandomBalloonSkins(data.balloonContents?.Count ?? 0),
            columns = GetCurrentColumns(data.curRound),
            onBalloonClick = OnClickBalloon,
        };
        balloonGrid.SetData(gridInfo);
    }

    // 풍선 Spine 스킨을 슬롯별 랜덤 배정 — 6색 한 묶음을 셔플해 소진 후 다음 묶음을 이어붙여
    // 색이 최대한 겹치지 않게(인접·전체 균등) 분포시킨다. 생성/재생성 시마다 새로 섞는다.
    private List<string> BuildRandomBalloonSkins(int count)
    {
        List<string> skins = new(count > 0 ? count : 0);
        int skinKinds = BALLOON_SKINS.Length;
        if (count <= 0 || skinKinds == 0)
            return skins;

        List<string> bag = new(skinKinds);
        for (int i = 0; i < count; i++)
        {
            if (bag.Count == 0)
            {
                bag.AddRange(BALLOON_SKINS);
                bag.ShuffleRandom();
                // 묶음 경계 인접 중복 최소화: 직전 색과 다음 뽑을 색(맨 뒤)이 같으면 다른 위치와 교환
                int tail = bag.Count - 1;
                if (skinKinds > 1 && skins.Count > 0 && bag[tail] == skins[^1])
                    (bag[tail], bag[0]) = (bag[0], bag[tail]);
            }

            int last = bag.Count - 1;
            skins.Add(bag[last]);
            bag.RemoveAt(last);
        }
        return skins;
    }

    // 슬롯별 중요 보상 여부 — content(보상 idx)가 현재 라운드 중요 보상(roundRewardSign=1)에 속하는지
    private List<bool> BuildImportantFlags(IReadOnlyList<int> contents, int round)
    {
        List<bool> flags = new();
        if (null == contents)
            return flags;

        HashSet<int> importantIds = GetImportantRewardIds(round);
        int count = contents.Count;
        for (int i = 0; i < count; i++)
        {
            int content = contents[i];
            flags.Add(content > 0 && importantIds.Contains(content));
        }
        return flags;
    }

    // 현재 라운드 중요 보상(roundRewardSign=1) idx 집합. roundReward 와 roundRewardSign 은 평행 배열.
    private HashSet<int> GetImportantRewardIds(int round)
    {
        HashSet<int> set = new();
        EventCharacterCafeMiniGameTableData table = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, round);
        int[] rewards = table?.roundReward;
        int[] signs = table?.roundRewardSign;
        if (rewards == null || signs == null)
            return set;

        int count = rewards.Length < signs.Length ? rewards.Length : signs.Length;
        for (int i = 0; i < count; i++)
        {
            if (signs[i] == 1 && rewards[i] > 0)
                set.Add(rewards[i]);
        }
        return set;
    }

    // 단일 content 의 중요 보상 여부 (터뜨린 직후 갱신용)
    private bool IsImportantContent(int content)
    {
        if (content <= 0)
            return false;

        MiniGameModel data = GetMiniGameData();
        int round = data?.curRound ?? 1;
        return GetImportantRewardIds(round).Contains(content);
    }

    private int GetCurrentColumns(int round)
    {
        EventCharacterCafeMiniGameTableData roundTable = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, round);
        int[] colsRows = roundTable?.roundColsRows;
        if (colsRows != null && colsRows.Length >= 1)
            return colsRows[0];
        return 0;
    }

    // 현재 라운드 풍선 1회 비용(popCost)을 보유 이벤트 재화로 지불할 수 있는지 (popCost<=0 이면 항상 가능)
    private bool HasEnoughCurrencyToPop()
    {
        MiniGameModel data = GetMiniGameData();
        int round = data?.curRound ?? 1;
        EventCharacterCafeMiniGameTableData roundTable = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, round);
        int popCost = roundTable?.popCost ?? 0;
        if (popCost <= 0)
            return true;

        return EventCurrencyHelper.GetEventCurrency(EVENT_TYPE) >= popCost;
    }

    // [이벤트 기반 분리 2026-06-14] 중복 터뜨리기 가드는 서브컨텐츠(CharacterCafeMiniGameSubContent._popping)로 이전 — 팝업 가드 제거.
    // private bool _popping; // 풍선 터뜨리기 네트워크 왕복 중 중복 클릭 방지(서버 권위 비동기 전환)

    // 풍선 터뜨리기 — 재화 부족 선검사(UI 가드)만 팝업이 하고, popCost 차감/내용 공개 송수신/적용은 서브컨텐츠 진입점(RequestPopBalloon)에 위임한다(§3-2).
    //  결과 연출은 BalloonPoppedMsg 수신(OnBalloonPopped)에서 재생한다(이벤트 기반 분리).
    private void OnClickBalloon(int balloonIndex)
    {
        if (null == curInfo?.content) return;

        // 플로우차트 B: "최초 보상(열쇠+히든)을 다 찾은 상태인가?" YES → 풍선 조작 차단(보물상자 버튼만 동작).
        //  서버 판정(IsAllBalloonRewardsCollected, 꽝 제외 전부 popped)과 동치: 열쇠 발견 && 남은 히든 0.
        MiniGameModel data = GetMiniGameData();
        if (null != data && data.keyFound && CountRemainingHidden(data) == 0)
            return;

        // 열쇠 비행 중에는 다른 풍선 조작 차단 — 열쇠가 자물쇠를 열어 팝업이 활성화되는 플로우를 유저가 인지하도록(비행 도착 시 해제).
        if (keyResultFlying)
            return;

        // 플로우차트 B: "꽃이 충분한가?" NO → 재화 부족 팝업(§3-6) 노출 후 중단
        if (!HasEnoughCurrencyToPop())
        {
            curInfo.content.OpenItemPurchasePopup(curInfo.objectIdx, RefreshCurrency);
            return;
        }

        curInfo.content.MiniGameSubContent?.RequestPopBalloon(curInfo.objectIdx, balloonIndex);
    }

    // 풍선 터뜨리기 데이터 적용 완료 통지(BalloonPoppedMsg) → 결과 연출 재생(응답 기반).
    private void OnBalloonPopped(CharacterCafeMiniGameSubContent.BalloonPoppedMsg msg)
    {
        if (null == msg || this == null || null == msg.result) return;
        if (null == curInfo || msg.objectIdx != curInfo.objectIdx) return;

        PopBalloonResult result = msg.result;

        RefreshCurrency();

        // 터뜨림 사운드(기획 §4) — 열쇠=0003 열쇠 발견 / 그 외(히든·꽝)=0001 풍선 팡
        PlayResultSound(result.isKey ? SOUND_KEY_FOUND : SOUND_BALLOON_POP);

        bool isImportant = IsImportantContent(result.content);
        if (null != balloonGrid)
            balloonGrid.RefreshItem(result.balloonIndex, result.content, true, isImportant);

        // 중요 보상(roundRewardSign=1) 풍선 획득 → §4-5 주요 보상 획득 연출(중앙 등장→슬롯). 그 외(열쇠/일반 히든)는 풍선 위치→목적지 비행(§4-15).
        bool resultFlying = (result.content > 0 && isImportant)
            ? PlayImportantRewardObtain(result.content)
            : PlayResultEffect(result.balloonIndex, result.content, isImportant);

        // 열쇠 비행 시작 → 비행 동안 다른 풍선 조작 차단(도착 시 PlayResultEffect onArrive 에서 해제). 비행 스킵(폴백) 시엔 차단 없음.
        if (result.isKey)
            keyResultFlying = resultFlying;

        // 히든 보상 획득 시 보물상자 슬롯을 획득 상태로 갱신
        RefreshRewardSlots();

        // 열쇠 발견 → 보물상자(열쇠) 버튼 활성. 잠금→활성 변경 연출(BtnActivate, §4-12)은 열쇠 비행 "도착" 시점에 재생한다.
        //  비행 연출이 스킵되는 경우(이펙트 미바인딩 등)엔 도착 콜백이 없으므로 여기서 즉시 활성 폴백.
        if (result.isKey && !resultFlying)
            RefreshTreasureKey(animateActivate: true);

        // 열쇠+히든 전부 수집 → 손가락 유도 연출(§4-6). 진행은 보물상자 버튼 터치로.
        if (result.isAllCollected)
            OnAllCollected();
    }

    // 풍선 터뜨림 결과(열쇠/히든/중요보상)를 풍선 슬롯 위치 → 목적지로 비행시키는 연출(§4-15 Effect Stateful).
    // 프리팹에 좌표 애니가 없어 시작점(풍선)→목적지(열쇠 버튼/보상 슬롯) 이동을 코드로 구동한다.
    //  - content == -1(열쇠) → TreasureKey, 목적지 = 열쇠(다음 라운드) 버튼
    //  - content >  0(히든)  → 중요보상=SpecialReward / 일반=TreasureHiddenReward, 목적지 = 해당 보상 슬롯
    //  - content == 0(꽝)     → 연출 없음
    // 반환: 비행 트윈을 시작했으면 true(도착 OnComplete 에서 후처리). 조기 반환(미바인딩/꽝 등)이면 false.
    private bool PlayResultEffect(int balloonIndex, int content, bool isImportant)
    {
        if (null == rewardEffectItem || content == 0)
            return false;

        if (null == balloonGrid || !balloonGrid.TryGetBalloonWorldPosition(balloonIndex, out Vector3 from))
            return false;

        Vector3 to;
        if (content == -1) // 열쇠 → 열쇠(다음 라운드) 버튼
        {
            if (null == treasureKeyButton)
                return false;
            to = treasureKeyButton.transform.position;
            // 열쇠 비행과 동시에 보석함 정화(§4-9) — 마지막 라운드는 Final, 아니면 Basic.
            PlayTreasureBoxUnlock();
        }
        else // 히든 보상 → 보상 슬롯 (중요보상도 슬롯 위치 동일, 표시만 다름)
        {
            if (!TryGetRewardSlotPosition(content, out to))
                to = (null != treasureKeyButton) ? treasureKeyButton.transform.position : from; // 슬롯 못 찾으면 상자 쪽으로
        }

        // 풀에서 결과 아이템을 꺼내 이번 결과 1개만 표시(열쇠/히든/중요 + 아이콘). 여러 보상이 동시에 각자 비행한다(직전 비행 정리 안 함).
        UIBalloonRewardItem item = GetRewardEffectItem();
        if (null == item)
            return false;
        activeRewardItems.Add(item);

        item.transform.position = from;
        item.ShowResult(content, isImportant, content > 0 ? ResolveRewardInfo(content) : null);

        bool isKeyEffect = content == -1;
        // 비행은 비동기 — DOMove 전 item.IsReady + 애니메이터 클립 재생을 await(비행 시간 = 클립 잔여 시간). 도착 시 onArrive.
        FlyRewardItemAsync(item, to, () =>
        {
            if (isKeyEffect)
            {
                PlayResultSound(SOUND_KEY_UNLOCK);          // 0004 잠금 해제(도착)
                RefreshTreasureKey(animateActivate: true);  // 열쇠 도착 → 버튼 잠금→활성 "변경 연출"(BtnActivate, §4-12)
                OpenKeyFoundAfterDelayAsync().Forget();     // 자물쇠 열림 후 1초 텀 → 팝업/다음 라운드 진행(텀 동안 조작 차단 유지)
            }
            else if (content > 0)
            {
                PlayResultSound(SOUND_REWARD_GET);          // 0002 보상 획득(슬롯 도착)
            }
        }).Forget();
        return true;
    }

    // 주요 보상 획득 연출(§4-5, ISSUE-73) — 중요 보상(roundRewardSign=1)이 화면 중앙에서 크게 등장 → 해당 보상 슬롯으로 비행 → 안착(RewardImportant).
    //  중요 보상 풍선을 터뜨려 "획득하는 시점"에 호출(OnBalloonPopped). 라운드 진입 자동 재생이 아니다(획득 전 연출 노출 방지).
    //  결과 비행(§4-15)과 동일한 풀링 UIBalloonRewardItem(SpecialReward) 인프라를 재사용한다. 반환: 비행을 시작했으면 true.
    private bool PlayImportantRewardObtain(int content)
    {
        if (null == rewardEffectItem || rewardSlots.IsNullOrEmpty() || content <= 0)
            return false;

        MiniGameModel data = GetMiniGameData();
        EventCharacterCafeMiniGameTableData table = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, data?.curRound ?? 1);
        int[] rewards = table?.roundReward;
        if (rewards == null)
            return false;

        // 획득한 content 가 채울 보상 슬롯(roundReward 와 평행)을 찾아 그 슬롯으로 비행한다.
        int slotCount = rewardSlots.Length;
        int count = Mathf.Min(rewards.Length, slotCount);
        for (int i = 0; i < count; i++)
        {
            if (rewards[i] != content)
                continue;

            StatefulComponent slot = rewardSlots[i];
            if (null == slot)
                return false;

            UIBalloonRewardItem item = GetRewardEffectItem();
            if (null == item)
                return false;
            activeRewardItems.Add(item);

            Vector3 center = (null != rewardEffectParent) ? rewardEffectParent.position : transform.position;
            item.transform.position = center;
            item.ShowResult(content, true, ResolveRewardInfo(content));

            // 중앙 등장 확대→축소 스케일은 아이템 Animator 가 구동 — 코드는 슬롯 비행(이동)만 처리한다. 중요 보상=SpecialReward 비행 시간.
            Vector3 slotPos = slot.transform.position;
            StatefulComponent targetSlot = slot;
            // 비행은 비동기 — DOMove 전 item.IsReady 를 await 한다(특별 보상 등장/스케일 애니의 OnStartFly 가 준비 완료를 알림).
            FlyRewardItemAsync(item, slotPos, () =>
            {
                PlayResultSound(SOUND_REWARD_GET); // 슬롯 도착(§4-5 안착)
                if (targetSlot.HasState((int)StateRole.RewardImportant))
                    targetSlot.SetState((int)StateRole.RewardImportant);
            }).Forget();
            return true; // 중요 보상은 통상 1개 — 첫 매칭 슬롯만 연출(단일 active item 인프라)
        }
        return false;
    }

    // 결과 아이템 비행(DOMove) — 비행 시간 = 아이템 애니메이터의 현재 재생 클립 잔여 시간(애니와 비행 동기화). 여러 보상이 동시에 각자 비행한다.
    //  DOMove 전, "IsReady && 애니메이터가 결과 클립 재생 중(잔여>0)" 을 await 한다:
    //   - 특별 보상: 등장/스케일 애니의 OnStartFly 가 IsReady=true 를 알린 시점(클립 중반)부터 잔여 시간만큼 비행.
    //   - 열쇠/일반: IsReady 는 즉시 true 지만 트리거 직후엔 기본 상태(모션 없음)라, 다음 프레임 클립 전환(잔여>0) 후 비행.
    //  스케일/등장은 애니메이터가 구동하므로 코드는 위치 이동(DOMove)만 담당한다.
    private async UniTaskVoid FlyRewardItemAsync(UIBalloonRewardItem item, Vector3 to, System.Action onArrive)
    {
        if (null == item)
            return;

        // 공유 취소 토큰(라운드 전환/팝업 종료 시 ReleaseAllResultFlies 가 일괄 취소) — 첫 비행 시 생성.
        resultFlyCts ??= new CancellationTokenSource();

        // IsReady + 결과 클립 재생(잔여>0) 대기. 정리(ReleaseAllResultFlies)·파괴 시 resultFlyCts 로 취소.
        bool canceled = await UniTask
            .WaitUntil(() => null == item || (item.IsReady && item.GetCurrentAnimationRemainingTime() > 0f), cancellationToken: resultFlyCts.Token)
            .SuppressCancellationThrow();
        // 대기 중 취소(정리)·파괴·풀 반환(이미 정리됨) 시 중단.
        if (canceled || this == null || null == item || !activeRewardItems.Contains(item))
            return;

        item.transform.DOMove(to, item.GetCurrentAnimationRemainingTime())
            .SetEase(Ease.InOutQuad)
            .OnComplete(() =>
            {
                onArrive?.Invoke();
                ReleaseRewardEffectItem(item); // 도착 후 결과 아이템 풀 반환(잔상 방지)
            });
    }

    // 동시 비행 중인 결과 전부 정리 — 대기 중 비행 일괄 취소(토큰) + 각 아이템 풀 반환(DOKill 로 트윈 중단). 라운드 전환/팝업 종료용.
    private void ReleaseAllResultFlies()
    {
        resultFlyCts = resultFlyCts.CancelAndDispose(); // 대기 중(IsReady/클립) 비행 일괄 취소
        // 열쇠 도착 지연 콜백(OpenKeyFoundAfterDelayAsync)도 함께 취소 — 빠른 수동 진행(확인 버튼) 후 라운드가 전환되면
        //  직전 라운드의 지연 콜백이 뒤늦게 발화해 다음 라운드의 keyFoundPopupShown 가드를 오염(자동 노출 차단)시키는 것을 방지.
        keyFoundDelayCts = keyFoundDelayCts.CancelAndDispose();
        for (int i = activeRewardItems.Count - 1; i >= 0; i--)
            ReleaseRewardEffectItem(activeRewardItems[i]); // 비행 중 트윈 Kill + 풀 반환 (Release 가 리스트에서 제거)
        keyResultFlying = false; // 라운드 전환/팝업 종료 시 열쇠 비행 차단 해제(스턱 방지)
    }

    // [ISSUE-70] 결과 아이템 풀에서 하나 꺼낸다 — 큐(rewardEffectItemQue)에 있으면 재사용, 없으면 rewardEffectItem 프리팹에서 생성.
    private UIBalloonRewardItem GetRewardEffectItem()
    {
        UIBalloonRewardItem item = rewardEffectItemQue.Count > 0 ? rewardEffectItemQue.Dequeue() : CreateRewardEffectItem();
        if (null == item)
            return null;

        item.gameObject.SetActive(true);
        item.transform.localScale = Vector3.one; // 풀 재사용 시 잔여 스케일 초기화(등장 확대→축소는 아이템 Animator 가 구동)
        item.transform.SetAsLastSibling(); // 다른 UI 위로 렌더
        return item;
    }

    // rewardEffectItem(프리팹) 인스턴스 생성 — 부모는 rewardEffectParent(기존 Effect 노드 부모, 레이어 유지).
    private UIBalloonRewardItem CreateRewardEffectItem()
    {
        if (null == rewardEffectItem)
            return null;

        Transform parent = (null != rewardEffectParent) ? rewardEffectParent : transform;
        return Instantiate(rewardEffectItem, parent);
    }

    // 결과 아이템을 풀로 반환 — 비행 트윈 정리 + Move 노드 초기화(Hide) + 비활성화 후 큐에 적재.
    private void ReleaseRewardEffectItem(UIBalloonRewardItem item)
    {
        if (null == item)
            return;

        item.transform.DOKill();
        item.Hide();
        item.gameObject.SetActive(false);
        if (!rewardEffectItemQue.Contains(item))
            rewardEffectItemQue.Enqueue(item);
        activeRewardItems.Remove(item);
    }

    // 결과 사운드 재생(기획 §4). soundKey<=0 이면 미재생(전용 사운드 미확정 대비).
    private void PlayResultSound(int soundKey)
    {
        if (soundKey > 0)
            SoundManager.Instance.PlaySound(soundKey);
    }

    // 히든 보상(content = Event_Reward.index)이 채울 보상 슬롯의 월드 좌표. roundReward 배열에서 같은 보상의 첫 슬롯을 찾는다.
    private bool TryGetRewardSlotPosition(int content, out Vector3 pos)
    {
        pos = default;
        if (rewardSlots.IsNullOrEmpty())
            return false;

        MiniGameModel data = GetMiniGameData();
        EventCharacterCafeMiniGameTableData table = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, data?.curRound ?? 1);
        int[] rewards = table?.roundReward;
        if (rewards == null)
            return false;

        int slotCount = rewardSlots.Length;
        int count = slotCount < rewards.Length ? slotCount : rewards.Length;
        for (int i = 0; i < count; i++)
        {
            if (rewards[i] == content && null != rewardSlots[i])
            {
                pos = rewardSlots[i].transform.position;
                return true;
            }
        }
        return false;
    }

    // 열쇠 도착(자물쇠 열림) 후 KEY_FOUND_DELAY_MS 텀을 두고 팝업/다음 라운드 진행 — 유저가 자물쇠 열림→팝업 활성화 플로우를 인지하도록.
    //  텀 동안 keyResultFlying 유지로 풍선 조작 차단. 팝업 파괴(닫힘) 시 취소.
    private async UniTaskVoid OpenKeyFoundAfterDelayAsync()
    {
        // 파괴 토큰과 연동한 전용 취소원으로 대기 — 라운드 전환/정리(ReleaseAllResultFlies)에서 이 대기를 취소해
        //  빠른 수동 진행 시 직전 라운드의 지연 콜백이 다음 라운드 자동 노출 가드(keyFoundPopupShown)를 오염시키는 것을 막는다.
        keyFoundDelayCts = keyFoundDelayCts.CancelAndDispose();
        keyFoundDelayCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);

        bool canceled = await UniTask.Delay(KEY_FOUND_DELAY_MS, cancellationToken: keyFoundDelayCts.Token).SuppressCancellationThrow();
        if (canceled || this == null)
            return;

        // 팝업 활성화(남은 히든 있으면 RoundInfoPopup) 또는 팝업 없이 다음 라운드/클리어 진행.
        //  풍선 조작 차단(keyResultFlying) 해제 시점:
        //   - 팝업 활성화 시: 팝업 종료 후(OpenRoundInfoPopup 의 onClosed)
        //   - 팝업 없이 진행 시: 라운드 전환/클리어 정리(ReleaseAllResultFlies)
        TryAutoOpenKeyFoundPopup();
    }

    // 열쇠 비행 도착 후 "열쇠를 찾았어요!" 다음 라운드 팝업(§3-2 "도착 시 자동 노출")을 라운드당 1회만 자동으로 띄운다.
    // 자동 노출을 닫은 뒤 재오픈은 보물상자(열쇠) 버튼으로 처리(OnClickTreasureBox 와 동일 플로우 재사용).
    private void TryAutoOpenKeyFoundPopup()
    {
        if (keyFoundPopupShown)
            return;
        keyFoundPopupShown = true;
        OnClickTreasureBox(); // 남은 히든 있으면 RoundInfoPopup, 없으면 진행 — 버튼 클릭과 동일
    }

    // 보물상자(열쇠) 버튼 터치 — 남은 히든 보상 있으면 다음 라운드 팝업(§3-4), 없으면 바로 진행(§3-2 C)
    private void OnClickTreasureBox()
    {
        MiniGameModel data = GetMiniGameData();
        if (null == data || !data.keyFound)
            return;

        RefreshGuide(false); // 보물상자 버튼을 눌렀으면 손가락 유도 숨김(§4-6)

        // 풍선게임 결과 창은 RoundInfoPopup 으로 일원화(UIPopupMiniGameBalloonClear 미사용).
        //  - 마지막 라운드: 항상 RoundInfoPopup — 모두 수집이면 §3-2 5) 클리어 모드, 아니면 §3-2 4) "바로 완료".
        //  - 그 외 라운드: 남은 히든 있을 때만 RoundInfoPopup(§3-2 4), 다 모았으면 바로 다음 라운드 진행.
        int remainingHidden = CountRemainingHidden(data);
        if (remainingHidden > 0 || IsLastRound(data.curRound))
            OpenRoundInfoPopup(data, remainingHidden);
        else
            ProceedToNextRoundOrClear();
    }

    // 다음 라운드 팝업(§3-4) 노출: "바로 다음 라운드/완료" 또는 "보상 찾고 가기" + 아직 못 받은(남은) 보상 표시
    private void OpenRoundInfoPopup(MiniGameModel data, int remainingHidden)
    {
        bool lastRound = IsLastRound(data.curRound);
        bool isClear = lastRound && remainingHidden <= 0; // 마지막 라운드 + 모든 보물 수집 → §3-2 5) 클리어 모드("보물 찾기 성공!")
        UIPopupMiniGameBalloonRoundInfoPopup.Info info = new()
        {
            remainingHidden = remainingHidden,
            isLastRound = lastRound,
            isClear = isClear,
            onProceed = ProceedToNextRoundOrClear,
            uncollectedRewards = BuildUncollectedRewards(data),
            clearRewards = isClear ? BuildClearRewards() : null,
            onClosed = () => keyResultFlying = false, // 팝업 종료 후 풍선 조작 차단 해제(열쇠 도착→팝업활성화→팝업 종료 후 클릭 가능)
        };
        UIManager.OpenUIMsgAsync<UIPopupMiniGameBalloonRoundInfoPopup>(info).Forget();
    }

    // 아직 수령하지 못한(미터뜨린 히든) 보상 목록 — RoundInfoPopup Reward1~4 표시용. content(Event_Reward.index) → RewardInfo.
    // [ISSUE-67] 추첨/사전 배치 양 모드 공용 판정에 위임 — 확률 추첨 모드(ISSUE-66)는 미터뜨림 슬롯이 미정(0)이라
    //  슬롯 스캔으로는 잔여 보상이 항상 0건이 돼 아이콘이 비고, 잔여 수 문구(CountRemainingHidden=재고 기반)와도 어긋난다.
    private List<RewardInfo> BuildUncollectedRewards(MiniGameModel data)
    {
        List<RewardInfo> list = new();
        if (null == data)
            return list;

        // 원본(사전 배치 전용 슬롯 스캔) — 동일 동작은 공용 헬퍼의 사전 배치 분기가 그대로 수행한다.
        //  IReadOnlyList<int> contents = data?.balloonContents;
        //  if (null == contents) return list;
        //  IReadOnlyList<bool> popped = data.balloonPopped;
        //  int total = contents.Count;
        //  for (int i = 0; i < total; i++)
        //  {
        //      bool isPopped = (null != popped) && i < popped.Count && popped[i];
        //      int content = contents[i];
        //      if (content > 0 && !isPopped) { ... }
        //  }
        List<int> contents = CharacterCafeMiniGameSubContent.GetRemainingHiddenRewardContents(
            data.objectIdx, data.curRound, data.balloonContents, data.balloonPopped);

        int total = contents.Count;
        for (int i = 0; i < total; i++)
        {
            RewardInfo info = ResolveRewardInfo(contents[i]);
            if (null != info)
                list.Add(info);
        }
        return list;
    }

    // 남은 히든 보상(미터뜨린 1~ 슬롯) 수
    // [ISSUE-66] 추첨/사전 배치 양 모드 공용 판정에 위임 — 확률 추첨 모드는 미터뜨림 슬롯이 미정(0)이라
    //  슬롯 검사로는 잔여 히든이 항상 0 으로 오판돼 풍선 조작이 잘못 잠긴다(진행 게이트 오류).
    private int CountRemainingHidden(MiniGameModel data)
    {
        return CharacterCafeMiniGameSubContent.CountRemainingHiddenRewards(data.objectIdx, data.curRound, data.balloonContents, data.balloonPopped);
    }

    private bool IsLastRound(int round)
    {
        int totalRounds = TableManager.Instance.GetCharacterCafeMiniGameDatas(objectEventIdx)?.Count ?? 0;
        return totalRounds > 0 && round >= totalRounds;
    }

    // 전 보상(열쇠+히든) 수집 완료 — 보물상자 버튼은 이미 활성. 진행은 버튼 터치로(§3-2 C).
    private void OnAllCollected()
    {
        RefreshTreasureKey();
        // 전 보상 수집 → 보물상자(열쇠) 버튼으로 손가락 유도(§4-6).
        RefreshGuide(true);
    }

    // [이벤트 기반 분리 2026-06-14] 중복 진행 가드는 서브컨텐츠(CharacterCafeMiniGameSubContent._proceeding)로 이전 — 팝업 가드 제거.
    // private bool _proceeding; // 라운드 진행/클리어 네트워크 왕복 중 중복 진행 방지(서버 권위 비동기 전환)

    // 라운드 진행/클리어 — 입력만 서브컨텐츠 진입점(RequestProceedMiniGame)에 위임한다(시작점=서브컨텐츠, 클리어→다음라운드 시퀀스 소유).
    //  클리어면 ObjectClearedMsg(메인 맵 재구성 + 본 팝업 닫기 OnMiniGameCleared), 다음 라운드면 RoundReadyMsg(RefreshAll)로 통지된다.
    private void ProceedToNextRoundOrClear()
    {
        // 보상 수령(라운드 진행/클리어) 시점에 보석함 개봉 연출(§4-9 BasicOpen/FinalOpen).
        PlayTreasureBoxOpen();

        if (null != curInfo?.content?.MiniGameSubContent)
            curInfo.content.MiniGameSubContent.RequestProceedMiniGame(curInfo.objectIdx);
    }

    // 미니게임 클리어 통지(ObjectClearedMsg) — 진입 오브젝트가 이 미니게임이면 팝업을 닫아 메인 카페로 복귀한다.
    //  (맵/오브젝트 재구성은 메인 카페 팝업이 동일 메시지로 처리)
    private void OnMiniGameCleared(CharacterCafeMiniGameSubContent.ObjectClearedMsg msg)
    {
        if (null == msg || this == null) return;
        if (null == curInfo || msg.objectIdx != curInfo.objectIdx) return;
        OnGameClear();
    }

    // 미니게임 클리어 (마지막 라운드 열쇠 획득, §3-5) — "보물 찾기 성공!" 클리어 축하 팝업 노출 후 메인 카페로 복귀
    private void OnGameClear()
    {
        DLogger.Log($"[{GetType().Name}] 미니게임 클리어");

        // 클리어 팡파레(§4 0006)
        PlayResultSound(SOUND_GAME_CLEAR);

        // [결과 창 일원화] 풍선게임 결과/클리어 UI는 RoundInfoPopup 으로만 노출한다(UIPopupMiniGameBalloonClear 미사용).
        //  마지막 라운드 결과(§3-2 4·5)는 이미 RoundInfoPopup 에서 처리되므로, 클리어 통지 시엔 바로 닫아 메인 카페로 복귀.
        Close();

        // 오브젝트 상호작용 완료(4-7) 연결은 데이터 계층에서 처리됨:
        //   ProceedToNextRoundOrClear → TryClearMiniGame(핸들러 ClearMiniGameObject: 진입 오브젝트 Done + 다음 오브젝트 활성화)
        //   → CharacterCafeMiniGameSubContent.ObjectClearedMsg → 메인 카페 팝업이 맵/오브젝트 상태 재적용.
        // 우사하나 장식물(중요 보상) 클리어 Spine 연출(§4-4)은 클리어 팝업(UIPopupMiniGameBalloonClear.PlayClearSpineAsync, ObjectRole.Spine)에서 처리.
        //  [내부 빌드 태그] 코드 훅 + 프리팹 바인딩(SkeletonGraphic·ObjectRole.Spine) 완료 — 전용 클리어 연출 아트 미빌드라 임시 우사하나(Idle_Front) 로드. 전용 아트 확정 시 경로만 교체(TODO[art]).
    }

    // "보물 찾기 성공!" 클리어 축하 팝업(§3-5) 노출 — 마지막 라운드 보상(최대 8) 미리보기.
    //  "좋아요!"(또는 화면 터치) 시 onClosed 콜백으로 본 팝업을 닫아 생일 이벤트(메인 카페)로 복귀한다.
    //  클리어 팝업 로드 실패 시에도 메인 팝업은 닫아 복귀를 보장한다.
    private async UniTaskVoid OpenClearPopupAsync()
    {
        UIPopupMiniGameBalloonClear.Info clearInfo = new()
        {
            rewards = BuildClearRewards(),
            onClosed = Close,
        };

        UIPopupMiniGameBalloonClear ui = await UIManager.OpenUIMsgAsync<UIPopupMiniGameBalloonClear>(clearInfo);
        if (this == null)
            return;
        if (null == ui)
            Close();
    }

    // 클리어 보상 미리보기(§3-5, 최대 8) — 마지막 라운드(roundReward)를 RewardInfo 목록으로 변환.
    private List<RewardInfo> BuildClearRewards()
    {
        List<RewardInfo> list = new();

        int totalRounds = TableManager.Instance.GetCharacterCafeMiniGameDatas(objectEventIdx)?.Count ?? 0;
        int lastRound = totalRounds > 0 ? totalRounds : (GetMiniGameData()?.curRound ?? 1);
        EventCharacterCafeMiniGameTableData table = TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, lastRound);
        int[] rewards = table?.roundReward;
        if (rewards == null)
            return list;

        int count = rewards.Length;
        for (int i = 0; i < count; i++)
        {
            RewardInfo info = ResolveRewardInfo(rewards[i]);
            if (null != info)
                list.Add(info);
        }
        return list;
    }

    // 인포(?) 버튼(§3-3) — 인포 팝업을 (필요 시 로드 후) 노출한다.
    private void OnClickInfo()
    {
        OpenInfoPopupAsync().Forget();
    }

    // 최초 미니게임 진입 1회 강제 인포 노출(§3-3) — 미니게임 전용 first-open 플래그가 아직 없을 때만 1회 자동 노출.
    //  메인 카페 이벤트 인포(LiveEventType.CHARACTERCAFE)와 분리된 pid 키를 사용해 중복/충돌을 피한다.
    private void TryForceFirstOpenInfo()
    {
        if (LocalPrefs.GetByPid(INFO_FIRST_OPEN_PREF_KEY, 0) != 0)
            return;
        LocalPrefs.SetByPid(INFO_FIRST_OPEN_PREF_KEY, 1);
        OpenInfoPopupAsync().Forget();
    }

    // 인포 팝업(_Info, UIInfoPopupController) 노출 — 첫 호출 시 어드레서블에서 자식으로 로드·캐싱한 뒤 오픈 시퀀스를 재생한다.
    private async UniTaskVoid OpenInfoPopupAsync()
    {
        if (null == infoPopupController)
        {
            if (infoPopupLoading)
                return;
            infoPopupLoading = true;

            var ct = gameObject.GetCancellationTokenOnDestroy();
            GameObject infoObject = await this.InstantiateScopedAsync(INFO_POPUP_KEY, transform, ct: ct);
            infoPopupLoading = false;
            if (this == null || null == infoObject)
                return;

            infoObject.TryGetComponent(out infoPopupController);
            if (null == infoPopupController)
            {
                DLogger.Error($"[{GetType().Name}] {INFO_POPUP_KEY} has no UIInfoPopupController");
                return;
            }
            infoPopupController.gameObject.SetActive(false);
        }

        infoPopupController.gameObject.SetActive(true);
        infoPopupController.SetInfo();
        infoPopupController.OnSequenceOpenPopup();
    }

    // 진입 오브젝트(Event_CharacterCafeObject.index = objectIdx)에서 미니게임 인덱스(objectEventIdx)를 해석한다.
    // 미니게임 라운드 테이블 조회(GetCharacterCafeMiniGameData*) 의 키로 사용된다. 미정의 시 0(폴백).
    private int ResolveObjectEventIdx(int objectIdx)
    {
        if (TableManager.GetData<EventCharacterCafeObjectTableData>(objectIdx, out EventCharacterCafeObjectTableData objectTable))
            return objectTable.objectEventIdx;

        DLogger.Error($"[{GetType().Name}] not found object table : objectIdx {objectIdx}");
        return 0;
    }

    // [소스 이원화 2026-06-14] 미니게임 진행 데이터는 접근자(실서버=DataManagement miniGameList, 치트=미러)에서 조회.
    private MiniGameModel GetMiniGameData()
    {
        if (null == curInfo) return null;
        return EventCharacterCafeHelper.GetMiniGameData(curInfo.objectIdx);
    }
}

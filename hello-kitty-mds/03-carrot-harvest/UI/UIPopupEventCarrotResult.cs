using System.Collections.Generic;

using Cysharp.Threading.Tasks;  // UniTaskVoid / GetCancellationTokenOnDestroy (UIPopupRewardResult 오픈)

using GameCore.Utils;       // DLogger

using ACTGames.Content.Helper;  // RewardHelper.OnClickShowInfoPop (보상 인포 팝업)

using GameLogic.Define;     // IUIInfoData, RewardInfo, RewardInfoData, ShopSuccessType
using GameLogic.Management; // TableManager, UIManager

using DG.Tweening;          // 점수 카운트업/보상 등장 연출(DOVirtual/DOPunchScale/Sequence)
using Spine.Unity;          // SkeletonGraphic(결과 연출 스파인)

using UnityEngine;
using UnityEngine.UI;       // LoopScrollRect / LoopScrollPrefabSource / LoopScrollDataSource (me.qiankanglai.loopscrollrect)

/// <summary>
/// 당근 수확 대소동 결과 팝업의 데이터 — 컨트롤러(다시/메인 처리)와 이번 판 결과를 전달한다.
/// </summary>
public struct PopupCarrotResultInform : IUIInfoData
{
    public ContentEventCarrot controller;
    public EventCarrotGameResult result;
}

/// <summary>
/// 당근 수확 대소동 결과 팝업(골격). <see cref="UIBasePopup"/> 기반.
///
/// 인게임 종료(시간 만료/그만두기) 시 인게임 팝업이 닫히며 본 팝업을 연다.
/// 상태별 제목/내용(첫 도전·최고 점수 갱신·보상·일반 §5-6-1), 획득/누적/최고 점수, 신규 보상 박스(가로 LoopScroll),
/// 등장 연출(점수 카운트업 → 결과 스파인 Basic/Max → 신규 보상 박스 등장)을 모두 표시한다(§5-6 구현 완료).
/// </summary>
public class UIPopupEventCarrotResult : UIBasePopup, LoopScrollPrefabSource, LoopScrollDataSource
{
    private const string INCREMENT_COLOR = "#5BC236";   // 누적 점수 증가분 (+N) 초록색(§5-6 4) — 리치텍스트, 톤은 조정 가능

    // [다시] 필요 코인 텍스트 색(ISSUE-52 댓글) — 충분: 기본(흰색) / 부족: 붉은색(메인 팝업 시작 버튼 disable 코인 텍스트와 동일 값).
    private static readonly Color RETRY_COST_COLOR_NORMAL = Color.white;
    private static readonly Color RETRY_COST_COLOR_INSUFFICIENT = new Color(1f, 0.57899255f, 0.5707547f, 1f);

    // 결과 연출 스파인 트랙명(Spine_UIPopupEventCarrotResult): 일반(Basic) / 최고 점수 갱신(Max).
    private const string SPINE_START_NORMAL = "In_Basic";
    private const string SPINE_IDLE_NORMAL  = "Idle_Basic";
    private const string SPINE_START_BEST   = "In_Max";
    private const string SPINE_IDLE_BEST    = "Idle_Max";
    private const string COMPLETE_CONFETTI_ANIM = "Fx_Complete_Confetti";   // 뒷판 컨페티 스파인 애니(Loop, ISSUE-53)

    private const float SCORE_COUNT_DURATION   = 0.5f;      // 점수 카운트업 시간
    private const float REWARD_REVEAL_DELAY     = 0.15f;    // 점수 카운트업 후 보상 박스 등장까지 지연
    private const float REWARD_REVEAL_DURATION  = 0.35f;    // 보상 박스 스케일 등장 시간
    private const float BEST_EMPHASIS_DURATION  = 0.4f;     // 최고 점수 갱신 강조 펀치 시간
    private const float BEST_EMPHASIS_PUNCH     = 0.3f;     // 최고 점수 갱신 강조 펀치 스케일 강도
    private const float BEST_FX_LEAD_TIME       = 0.3f;     // 최고갱신 점수판 이펙트(펀치/라인 FX)를 카운트업 완료보다 앞당기는 시간(ISSUE-54)

    private const float SLIDER_FILL_DURATION   = 0.5f;      // 누적 슬라이더 한 구간(직전 골→목표 골) 채움 시간
    private const float SLIDER_STEP_INTERVAL   = 0.25f;     // 구간 사이 간격(파티클 재생/다음 구간 리셋 텀)

    // 등장 연출 출력 순서(ISSUE-55): 팝업 본체 오픈 → 우사하나 스파인 → 꽃가루 파티클.
    private const float POPUP_BOX_OPEN_DURATION = 0.75f;    // 팝업 본체(PopupBoxFrameBg) 오픈 애니(HighScoreBoardBox_Open) 길이
    private const float SPINE_REVEAL_INTERVAL   = 0.5f;     // 우사하나 스파인 등장(In→상단 이동) 후 꽃가루까지 간격(연출 보정용, 조정 가능)

    [Header("Texts")]
    // 팝업 제목/타이틀 아웃라인은 StatefulComponent 가 상태별 타이틀 오브젝트(TitleText_Common/TitleText_HighScore,
    // 각 아웃라인 머테리얼 베이크)를 토글해 처리한다. 제목 텍스트도 오브젝트에 베이크되어 코드 세팅 불필요.
    [SerializeField] private UITextEx contentText;      // 팝업 내용(상태별)
    [SerializeField] private UITextEx scoreCountText;       // 획득 점수(일반) — ScoreNumCountText. 일반/누적보상 결과에 노출(ISSUE-56)
    [SerializeField] private UITextEx scoreCountTextHigh;    // 획득 점수(신기록/첫 플레이 강조) — ScoreNumCountTextHigh. 신기록·첫 플레이 결과에 노출(ISSUE-56)
    [SerializeField] private UITextEx cumulativeScoreText;  // 누적 점수 "{0} (+{1})" — CumulativeScoreCountNumText
    [SerializeField] private UITextEx highScoreText;        // 최고 점수 값(HighScoreNumCountText, §5-6-1 하단)
    [SerializeField] private UITextEx heldCoinText;         // 보유 이벤트 코인 수량 — CurrencyBox_CarrotCoin/StartCount(ISSUE-52)

    [Header("Buttons")]
    [SerializeField] private UIButtonEx againButton;    // 다시
    [SerializeField] private UITextEx retryCostText;            // [다시] 버튼 한 게임당 필요 코인(EnableObject 측) — Btn_Again/EnableObject/EventCarrot_CoinText(ISSUE-52)
    [SerializeField] private UITextEx retryCostTextDisabled;    // [다시] 버튼 필요 코인(DisableObject 측) — 코인 부족 시 노출되는 비활성 상태에서도 동일 표시(ISSUE-52 댓글)
    [SerializeField] private UIButtonEx mainButton;     // 메인(이벤트 팝업)
    [SerializeField] private UIButtonEx closeButton;    // 닫기 — Btn_Close

    [Header("Spine")]
    [SerializeField] private SkeletonGraphic resultSpine;       // 결과 연출 스파인 — 일반(Basic)/최고갱신(Max)
    [SerializeField] private GameObject confettiFx;             // 결과 컨페티(Fx_Carrot_Confetti, 파티클 1회) — 결과창 오픈 시 항상 재생(첫 도전/게임 종료 공통)
    [SerializeField] private GameObject bestConfettiFx;         // 최고 점수 갱신 컨페티(Fx_Carrot_Confetti_2, 파티클 3회) — 최고갱신 시 추가 재생(앞)
    [SerializeField] private SkeletonGraphic completeConfettiSpine;  // 뒷판 컨페티 스파인(Fx_Complete_Confetti) — 모든 결과(첫종료/일반종료 포함) Loop 재생(ISSUE-53)
    [SerializeField] private Animator popupBoxAnimator;         // 팝업 본체 오픈 애니(HighScoreBoardBox_Ani) — PopupBoxFrameBg. 등장 시 0프레임부터 재생.

    [Header("Reward")]
    [SerializeField] private GameObject newRewardBox;           // 신규 보상 박스(NewRewardBox) — SetActive 로만 노출/숨김(슈퍼 레어 처치 보상만, §5-6 5)
    [SerializeField] private Transform newReward;               // 신규 보상 등장 DOTween 애니 대상(NewRewardBox 하위 NewReward) — 박스는 SetActive 만, 스케일 애니는 이 오브젝트가 동작
    [SerializeField] private LoopScrollRect newRewardScroll;    // 신규 보상 가로 LoopScroll(LoopHorizontalScrollRect) — RewardBG 부착(좌우 스크롤·중앙 정렬 §5-6 5)
    [SerializeField] private GameObject newRewardItemPrefab;    // 가로 셀로 쓸 CommonRewardItem 프리팹

    [Header("Cumulative Slider")]
    [SerializeField] private Slider cumulativeSlider;       // 누적 점수 진행 슬라이더 — TimeSlider(0f=직전 달성 골, 1f=목표 골)
    [SerializeField] private UITextEx targetScoreText;      // 목표 점수(goalValue2) — TimeSlider/CarrotScoreIcon/Text(구간 전환 시 갱신)
    [SerializeField] private GameObject rewardReachedFx;    // 목표(1.0f) 도달 파티클 — Fx_CharacterCafe_reward(구간 도달마다 재생)
    [SerializeField] private UISimpleRewardTooltip rewardTooltip;    // 목표 점수 보상 툴팁(UIEventCarrotRewardTooltip) — 게이지 연출 완료 후 노출, 이동 전 비활성
    [SerializeField] private UIButtonEx cumulativeScoreBoardButton;  // 오각형 누적 점수판 터치 → 목표 보상 말풍선 재등장(ISSUE-57)
    [SerializeField] private UIBlocker rewardTooltipBlocker;         // 보상 말풍선 표시 중 외부 터치 시 닫기용 블로커(ISSUE-57)

    private ContentEventCarrot controller;
    private UITextEx activeScoreText;            // 이번 결과에 노출 중인 획득 점수 텍스트(일반/강조) — 카운트업 대상(ISSUE-56)
    private EventCarrotGameResult lastResult;    // 이번 판 결과 스냅샷 — 서버 응답 리프레시 시 재사용
    private List<RewardInfo> lastRewardInfos;    // 현재 표시 중인 신규 보상 목록(LoopScroll 데이터 소스)
    private readonly Stack<Transform> newRewardPool = new();    // CommonRewardItem 셀 풀(LoopScrollPrefabSource)
    private bool entrancePlayed;                 // 등장 연출 1회 보장(서버 리프레시 RefreshFromController 시 재생 방지)
    private Sequence entranceSequence;           // 등장 연출 시퀀스(점수 카운트업 → 보상 박스 등장 → 누적 슬라이더 연출)

    // 누적 슬라이더 한 구간 연출 정보 — '직전 달성 골(0f) → 목표 골(1f)' 사이를 채운다.
    private struct CumulativeSliderStep
    {
        public long targetGoal;     // 목표 골 점수(슬라이더 1f, CarrotScoreIcon/Text 표시)
        public float startFill;     // 구간 시작 시 슬라이더 값(0~1)
        public float endFill;       // 구간 종료 시 슬라이더 값(0~1)
        public bool reachesGoal;    // 1.0f(목표) 도달 구간인지 — 파티클 재생/보상 획득 여부
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);

        if (data == null)
        {
            Close();
            return;
        }

        var inform = (PopupCarrotResultInform)mData;
        controller = inform.controller;
        lastResult = inform.result;

        SoundManager.Instance.PlaySound(EventCarrotSoundDefine.RESULT_POPUP);    // 결과 팝업 등장 사운드(ISSUE-49)

        // 팝업 오픈 시퀀스 SO(uIPopupSequenceSO) 미할당이라 AfterOpenPopupSequence 가 호출되지 않으므로,
        // 등장 연출(점수 카운트업·스파인·보상 등장)은 여기서 직접 트리거한다(매 오픈마다 1회).
        entrancePlayed = false;
        Refresh(lastResult);             // 최종값 세팅(서버 리프레시·연출 미동작 시 폴백 공통)
        PlayEntranceAnimation(lastResult);   // 시작값으로 되돌린 뒤 최종값까지 카운트업/스파인/보상 연출
    }

    /// <summary>서버 응답(최고 점수)으로 컨트롤러 state 가 갱신됐을 때 컨트롤러가 호출 — 이번 판 결과로 재표시(§9-A).</summary>
    public void RefreshFromController()
    {
        Refresh(lastResult);
    }

    protected override void Start()
    {
        base.Start();

        againButton.onClick.AddListener(OnClickAgain);
        mainButton.onClick.AddListener(OnClickMain);
        closeButton.onClick.AddListener(OnClickClose);

        // 오각형 점수판 터치 → 말풍선 재등장 / 말풍선 표시 중 블로커 터치 → 닫기(ISSUE-57).
        if (cumulativeScoreBoardButton != null)
            cumulativeScoreBoardButton.onClick.AddListener(OnClickScoreBoard);
        if (rewardTooltipBlocker != null)
            rewardTooltipBlocker.SetInfo(HideRewardTooltip);
    }

    protected override void OnDestroy()
    {
        againButton.onClick.RemoveListener(OnClickAgain);
        mainButton.onClick.RemoveListener(OnClickMain);
        closeButton.onClick.RemoveListener(OnClickClose);

        if (cumulativeScoreBoardButton != null)
            cumulativeScoreBoardButton.onClick.RemoveListener(OnClickScoreBoard);
        if (rewardTooltipBlocker != null)
            rewardTooltipBlocker.RemoveClickAction(HideRewardTooltip);

        entranceSequence?.Kill();
        entranceSequence = null;

        base.OnDestroy();
    }

    // 상태별 제목/내용(§5-6-1). 노출 우선순위: 첫 도전 → 최고 점수 갱신 → 누적 보상 획득 → 일반.
    // 제목/내용은 결과에 따라 바뀌는 가변 텍스트라 코드에서 LIdx 를 조회·포맷한다(인스펙터 고정 불가).
    private void Refresh(EventCarrotGameResult result)
    {
        // 획득 점수 텍스트 — 신기록/첫 플레이는 강조(ScoreNumCountTextHigh), 그 외는 일반(ScoreNumCountText)으로 노출한다(ISSUE-56).
        var useHighScore = result.IsNewBest || result.IsFirstPlay;
        activeScoreText = useHighScore ? scoreCountTextHigh : scoreCountText;
        activeScoreText?.SetText($"{result.GainedScore}");

        // 최고 점수(§5-6-1 하단) — 서버 동기화 권위값 우선(controller.BestScore), 미주입 시 스냅샷 폴백.
        var bestScore = controller != null ? controller.BestScore : result.BestScore;
        highScoreText.SetText($"{bestScore}");

        // 보유 이벤트 코인 + [다시] 버튼 한 게임당 필요 코인(ISSUE-52). 코인은 직전 게임 시작 시 이미 차감되어, 결과 진입 시점의 보유량을 표시.
        if (controller != null)
        {
            heldCoinText.SetText($"{controller.HeldCoin}");

            // [다시] 필요 코인 — EnableObject/DisableObject 양쪽 코인 텍스트를 동일하게 갱신한다(ISSUE-52 댓글).
            // 코인 부족 시 노출되는 DisableObject 에서도 같은 값/색으로 표시되도록 둘 다 세팅. 부족 시 붉은색, 충분 시 기본(흰색).
            var retryCost = controller.StartCoin;
            var affordable = controller.CanStartGame;
            ApplyRetryCostText(retryCostText, retryCost, affordable);
            ApplyRetryCostText(retryCostTextDisabled, retryCost, affordable);

            // [다시] 버튼 관리 기준:
            //  - 누적 만렙(보상 전부 수령) → 오브젝트 자체를 숨긴다(SetActive false, 재진입 불가, ISSUE-58). 노출은 SetActionButtonsVisible 이 CanRetry 로 처리.
            //  - 그 외 → 코인 충분 여부로 EnableObject/DisableObject 토글(ISSUE-52). 코인 부족은 Disable(딤드)지만 클릭 유지(→ 코인 부족 팝업).
            if (againButton != null)
            {
                if (!CanRetry())
                    againButton.gameObject.SetActive(false);
                else
                    againButton.EnableButton = affordable;
            }
        }

        // 상태 전환(ApplyResultState)이 StatefulComponent 로 상태별 타이틀 오브젝트(제목 텍스트·아웃라인 포함)를 토글한다.
        // 코드는 내용 텍스트(contentText)만 동적 세팅한다.
        if (result.IsFirstPlay)
        {
            // 1순위: 첫 도전 — 상태는 일반과 동일(Resultcommon), 로컬라이징만 다름(기획 3-3 6)).
            ApplyResultState(StateRole.Resultcommon);
            contentText.SetText(TableManager.GetText(EventCarrotStringDefine.RESULT_CONTENT_FIRST));    // "첫 도전 수고하셨습니다!"
        }
        else if (result.IsNewBest)
        {
            // 2순위: 최고 점수 갱신 — "{0} → {1}!\n최고 점수 갱신! 대단해요!" (0=이전 최고, 1=현재 획득)
            ApplyResultState(StateRole.ResultHighScore);
            contentText.SetText(string.Format(
                TableManager.GetText(EventCarrotStringDefine.RESULT_CONTENT_NEW_BEST),
                result.PreviousBest, result.GainedScore));
        }
        else if (result.RewardGained)
        {
            // 3순위: 누적 보상 획득 — "누적 {0}점 달성으로 보상을 받았어요!" (0=이번 판 달성한 골 점수)
            ApplyResultState(StateRole.ResultRewardsGet);
            contentText.SetText(string.Format(
                TableManager.GetText(EventCarrotStringDefine.RESULT_CONTENT_REWARD),
                result.RewardGoalScore));
        }
        else
        {
            // 4순위: 일반 — 첫 도전과 동일 상태(Resultcommon), 로컬라이징만 다름(기획 3-3 6)).
            // "최고 점수까지 {0}점! 한번 더 도전해보세요!" (0=최고 점수 - 현재 획득 점수). bestScore 는 위에서 산출.
            ApplyResultState(StateRole.Resultcommon);
            contentText.SetText(string.Format(
                TableManager.GetText(EventCarrotStringDefine.RESULT_CONTENT_NORMAL),
                Mathf.Max(0, bestScore - result.GainedScore)));
        }

        // 점수 아래 라인 이펙트(Fx_bg_eff_Line_2)를 StatefulUI 전용 상태로 토글(ISSUE-59).
        // 일단 끄고(Off→On 재토글로 파티클을 처음부터 재생), 최고 갱신 라인 FX 는 점수 카운트업이 멈추는
        // 시점(PlayEntranceAnimation)에서 켠다(ISSUE-60, 기존엔 팝업 오픈과 동시 출력).
        // 등장 연출이 없는 경로(RefreshFromController, entrancePlayed=true)는 카운트업이 없으므로 즉시 최종 상태로 켠다.
        // 첫 도전은 항상 최고 갱신(0→점수)이지만 '첫 도전'이 우선이라 라인 FX 도 끈다(IsBestCelebration, ISSUE-61).
        ApplyResultState(StateRole.HighScoreFxOFF);
        if (entrancePlayed && IsBestCelebration(result))
            ApplyResultState(StateRole.HighScoreFxOn);

        // 일반/강조 획득 점수 텍스트 중 하나만 노출(상태 토글 뒤에 적용해 우선)(ISSUE-56).
        SetActiveScoreText(useHighScore);

        RefreshCumulativeScore(result);
        RefreshRewardBox();

        // 누적 슬라이더는 등장 연출(PlayEntranceAnimation)이 직전 골→목표 골로 채워 보여준다.
        // 연출이 없는 경로(서버 리프레시 RefreshFromController)에서는 최종 도달 위치로 즉시 스냅한다.
        var cumulativeTotal = controller != null ? controller.CumulativeScore : result.GainedScore;
        ApplyCumulativeSliderImmediate(cumulativeTotal);
    }

    // [다시] 필요 코인 텍스트 갱신(EnableObject/DisableObject 공용, ISSUE-52) — 값 세팅 + 코인 부족 시 붉은색, 충분 시 기본(흰색).
    private void ApplyRetryCostText(UITextEx text, int cost, bool affordable)
    {
        if (text == null)
            return;

        text.SetText($"{cost}");
        if (text.TextComponent != null)
            text.TextComponent.color = affordable ? RETRY_COST_COLOR_NORMAL : RETRY_COST_COLOR_INSUFFICIENT;
    }

    // 결과 팝업 상태 전환(기획 3-3 6) — StatefulUI 상태(Resultcommon/ResultHighScore/ResultRewardsGet).
    // 첫 도전·일반은 동일 상태(Resultcommon)를 공유하고 로컬라이징(제목/내용)만 코드에서 다르게 세팅한다.
    // 프리팹에 미정의 상태면 Has* 가드로 안전 no-op(디자이너 셋업 대기 대비).
    private void ApplyResultState(StateRole role)
    {
        if (Stateful != null && Stateful.HasState((int)role))
            Stateful.SetState((int)role);
    }

    // 최고 점수 갱신 '전용 연출'(Max 스파인/컨페티/라인 FX/점수 강조)을 적용할지 여부(ISSUE-61).
    // 첫 도전은 항상 최고 갱신(0→점수)이지만 결과 문구/연출 우선순위상 '첫 도전'이 최고 갱신보다 앞서므로(§5-6-1),
    // 첫 도전일 때는 최고 갱신 연출을 끈다. 내용 텍스트(Refresh)는 이미 IsFirstPlay 분기가 우선이라 별도 처리 불필요.
    private bool IsBestCelebration(EventCarrotGameResult result)
    {
        return result.IsNewBest && !result.IsFirstPlay;
    }

    // 일반(ScoreNumCountText)/강조(ScoreNumCountTextHigh) 획득 점수 텍스트 중 하나만 노출한다(ISSUE-56).
    private void SetActiveScoreText(bool useHigh)
    {
        if (scoreCountText != null)
            scoreCountText.gameObject.SetActive(!useHigh);
        if (scoreCountTextHigh != null)
            scoreCountTextHigh.gameObject.SetActive(useHigh);
    }

    // 누적 점수 현황판(§5-6 4) — "{0} (+{1})". 0=판 종료 후 누적 점수(컨트롤러), 1=이번 판 증가분(획득 점수, 초록).
    // 증가분이 0 이하면 (+N) 없이 누적 점수만 표시.
    private void RefreshCumulativeScore(EventCarrotGameResult result)
    {
        // 바인딩 완료(2026-06-17): cumulativeScoreText ← CumulativeScoreCountNumText(UITextEx, 리치텍스트). 미주입 대비 null 가드 유지.
        if (cumulativeScoreText == null)
            return;

        var total = controller != null ? controller.CumulativeScore : result.GainedScore;
        var increment = result.GainedScore;     // 이번 판 획득 = 누적 증가분
        if (increment > 0)
            cumulativeScoreText.SetText($"{total} <color={INCREMENT_COLOR}>(+{increment})</color>");
        else
            cumulativeScoreText.SetText($"{total}");
    }

    // 이번 판 지급 보상(슈퍼 처치 + 누적 티어)을 신규 보상 박스의 가로 LoopScroll 로 표시(§5-6 5). 보상 없으면 박스 숨김.
    // 트로피 챌린지 최종 보상(PanelTrophyChallenge_Main.finalRewardScroll)과 동일한 LoopHorizontalScrollRect 패턴.
    private void RefreshRewardBox()
    {
        // 바인딩 완료: newRewardBox ← NewRewardBox, newRewardScroll ← RewardBG(LoopHorizontalScrollRect), newRewardItemPrefab ← CommonRewardItem 셀 프리팹.
        // NewRewardBox 는 슈퍼 레어 처치 보상만 노출한다(누적 점수 티어 보상은 슬라이더 연출 후 UIPopupRewardResult 로 별도 노출 §5-6).
        lastRewardInfos = controller != null ? controller.GetLastGrantedKillRewardInfos() : null;
        var infoCount = lastRewardInfos?.Count ?? 0;

        if (newRewardBox != null)
            newRewardBox.SetActive(infoCount > 0);

        if (newRewardScroll != null)
        {
            newRewardScroll.prefabSource = this;
            newRewardScroll.dataSource = this;
            newRewardScroll.totalCount = infoCount;
            newRewardScroll.RefillCells();
        }
    }

    // 등장 연출(§5-6): 결과 스파인(일반/최고갱신) + 점수 카운트업(획득→누적→최고) + 신규 보상 박스 등장.
    // Refresh 가 최종값을 이미 세팅했으므로, 시작값으로 되돌린 뒤 최종값까지 트윈한다. 매 오픈 1회만 재생한다.
    private void PlayEntranceAnimation(EventCarrotGameResult result)
    {
        if (entrancePlayed)
            return;
        entrancePlayed = true;

        // ① 팝업 본체(PopupBoxFrameBg) 오픈 애니를 0프레임부터 재생(스케일/투명도). 스파인/파티클은 아래 시퀀스에서 순차 등장.
        ReplayPopupBoxOpen();

        // 우사하나 스파인은 팝업 본체가 열린 뒤 등장하므로(ISSUE-55 팝업>우사하나>파티클), 등장 전까지 숨긴다.
        // (스파인 startingAnimation=Idle_Basic 이라 활성 상태면 오픈 즉시 Idle 가 보여 등장 순서가 뒤집힌다.)
        if (resultSpine != null)
            resultSpine.gameObject.SetActive(false);

        // 컨페티 FX(Fx_Carrot_Confetti/Fx_Carrot_Confetti_2)도 프리팹 기본 활성+playOnAwake 라 팝업 오픈 즉시
        // 모든 결과에서 터진다. 등장 순서(팝업>스파인>파티클, ISSUE-55)와 최고갱신 전용 분기(Fx_Carrot_Confetti_2(3회)는
        // 최고갱신 팝업에서만, ISSUE-62)를 지키도록 오픈 시 모두 숨겨, 아래 시퀀스의 PlayConfetti 시점에만 재생한다.
        if (confettiFx != null)
            confettiFx.SetActive(false);
        if (bestConfettiFx != null)
            bestConfettiFx.SetActive(false);
        if (completeConfettiSpine != null)
            completeConfettiSpine.gameObject.SetActive(false);

        var bestScore = controller != null ? controller.BestScore : result.BestScore;
        var cumulativeTotal = controller != null ? (int)controller.CumulativeScore : result.GainedScore;
        var cumulativeBase = Mathf.Max(0, cumulativeTotal - result.GainedScore);

        // 슬라이더 연출 전 [다시]/[메인] 버튼을 숨긴다 — 연출이 모두 끝난 뒤 OnComplete 에서 다시 노출한다.
        SetActionButtonsVisible(false);

        // 누적 슬라이더 구간(직전 달성 골→목표 골) 계산 + 시작(플레이 전) 위치로 스냅.
        var sliderSteps = BuildCumulativeSliderSteps(cumulativeBase, cumulativeTotal);
        SnapCumulativeSliderToStart(sliderSteps, cumulativeBase);

        // 카운트업 시작값으로 스냅(획득=0, 최고=이전 최고, 누적=이번 판 제외 누적).
        // 획득 점수는 이번 결과에 노출 중인 텍스트(일반/강조)를 대상으로 한다(ISSUE-56, Refresh 에서 activeScoreText 결정).
        var scoreText = activeScoreText ?? scoreCountText;
        scoreText.SetText("0");
        highScoreText.SetText($"{result.PreviousBest}");
        if (cumulativeScoreText != null)
            cumulativeScoreText.SetText($"{cumulativeBase}");

        // 보상 박스는 SetActive 로만 노출/숨김하고, 등장 애니(스케일)는 하위 NewReward 오브젝트가 담당한다.
        // 점수 카운트업 이후 등장하므로 NewReward 를 미리 숨김(스케일 0).
        var hasRewardBox = newRewardBox != null && newRewardBox.activeSelf;
        if (hasRewardBox && newReward != null)
            newReward.localScale = Vector3.zero;

        entranceSequence?.Kill();
        var seq = DOTween.Sequence();

        // 첫 도전은 항상 최고 갱신(0→점수)이지만 '첫 도전'이 우선이라 최고갱신 전용 연출은 끈다(IsBestCelebration, ISSUE-61).
        var bestCelebration = IsBestCelebration(result);

        // 등장 연출 출력 순서(ISSUE-55): ① 팝업 본체 오픈(위에서 0프레임부터 재생) → ② 우사하나 스파인 → ③ 꽃가루 파티클.
        seq.AppendInterval(POPUP_BOX_OPEN_DURATION);
        seq.AppendCallback(() => PlayResultSpine(bestCelebration));
        seq.AppendInterval(SPINE_REVEAL_INTERVAL);
        seq.AppendCallback(() => PlayConfetti(bestCelebration));

        // 1) 획득 점수 카운트업(이번 결과에 노출 중인 일반/강조 텍스트 대상, ISSUE-56).
        seq.AppendCallback(() => scoreText.IncreaseNumberForEffect(0, result.GainedScore, SCORE_COUNT_DURATION));
        seq.AppendInterval(SCORE_COUNT_DURATION);

        // 2) 누적·최고 점수 카운트업(동시).
        seq.AppendCallback(() =>
        {
            PlayCumulativeCountUp(cumulativeBase, cumulativeTotal, result.GainedScore);
            if (result.PreviousBest != bestScore)
                highScoreText.IncreaseNumberForEffect(result.PreviousBest, bestScore, SCORE_COUNT_DURATION);
        });

        // 최고갱신 점수판 이펙트(펀치/라인 FX)를 카운트업 완료보다 BEST_FX_LEAD_TIME(0.3초) 앞당겨 발동한다(ISSUE-54).
        // 이후 단계(보상 등장/슬라이더)의 절대 타이밍은 보존하기 위해 카운트업 인터벌을 분할한다.
        var bestFxLead = bestCelebration ? Mathf.Min(BEST_FX_LEAD_TIME, SCORE_COUNT_DURATION) : 0f;
        seq.AppendInterval(SCORE_COUNT_DURATION - bestFxLead);

        // 3) 최고 점수 갱신 강조(펀치 스케일) + 점수 라인 FX. 첫 도전은 제외(IsBestCelebration, ISSUE-61).
        // 라인 FX(Fx_bg_eff_Line_2)는 점수 카운트업이 끝나기 0.3초 전에 켠다(ISSUE-60/ISSUE-54).
        if (bestCelebration)
            seq.AppendCallback(() =>
            {
                EmphasizeNewBest();
                ApplyResultState(StateRole.HighScoreFxOn);
            });

        seq.AppendInterval(bestFxLead);     // 분할한 나머지 — 이후 단계 절대 타이밍 보존

        // 4) 신규 보상(하위 NewReward) 등장(스케일 OutBack). 박스 자체는 SetActive 만 담당하고 애니는 NewReward 가 동작.
        if (hasRewardBox && newReward != null)
        {
            seq.AppendInterval(REWARD_REVEAL_DELAY);
            seq.Append(newReward.DOScale(1f, REWARD_REVEAL_DURATION).From(0f).SetEase(Ease.OutBack));
        }

        // 5) 누적 점수 슬라이더 연출(직전 골→목표 골). 보상 티어를 넘긴 만큼(2단계 이상이면 2회+) 채움이 반복된다.
        AppendCumulativeSliderAnimation(seq, sliderSteps);

        // 연출 종료 — 버튼 재노출 + 누적 보상 획득 시 UIPopupRewardResult 로 획득 보상 전체 노출.
        seq.OnComplete(OnEntranceAnimationComplete);

        entranceSequence = seq;
    }

    // 등장 연출(슬라이더 포함) 완료 — [다시]/[메인] 버튼 재노출 후, 이번 판 누적 보상이 있으면 전체 보상 팝업을 연다.
    private void OnEntranceAnimationComplete()
    {
        SetActionButtonsVisible(true);
        HideRewardReachedFx();
        ShowTargetGoalRewardTooltip();
        ShowCumulativeRewardPopup();
    }

    // 게이지(누적 슬라이더) 연출 완료 후, 현재 목표 골의 보상을 목표 점수 보상 툴팁(UIEventCarrotRewardTooltip)에 노출한다(요구사항).
    // 표시할 목표 보상이 없으면(보상 그룹/티어 없음) 툴팁은 비활성 상태로 둔다.
    private void ShowTargetGoalRewardTooltip()
    {
        if (rewardTooltip == null || controller == null)
            return;

        var rewardInfos = controller.GetTargetGoalRewardInfos();
        if (rewardInfos == null || rewardInfos.Count == 0)
        {
            HideRewardTooltip();
            return;
        }

        // 위치는 프리팹 배치(슬라이더 목표 아이콘 인근) 그대로 유지. 목표 보상 뱃지 터치 시 아이템 인포(ISSUE-57 — 뱃지=뱃지 정보, ISSUE-63 핸들러 재사용).
        var displayContext = CommonRewardItem.CountDisplayContext.ShowCurrencyDefault();
        rewardTooltip.ShowTooltipWithCustomAction(displayContext, rewardTooltip.transform.position, rewardInfos, -1,
            rewardItem => RewardHelper.OnClickShowInfoPop(rewardItem, null), null);

        // 말풍선 표시 중 외부 터치로 닫기(ISSUE-57) — 블로커 활성화. 같은 탭의 재디스패치로 점수판이 곧장 재오픈되지 않도록 점수판 버튼은 제외한다.
        if (rewardTooltipBlocker != null)
        {
            rewardTooltipBlocker.gameObject.SetActive(true);
            if (cumulativeScoreBoardButton != null)
                rewardTooltipBlocker.IgnoreNextClick(cumulativeScoreBoardButton.gameObject);
        }
    }

    // 오각형 누적 점수판 터치 → 목표 보상 말풍선 재등장(ISSUE-57). 표시할 보상이 없으면 ShowTargetGoalRewardTooltip 내부에서 닫힘 처리.
    private void OnClickScoreBoard()
    {
        ShowTargetGoalRewardTooltip();
    }

    // 보상 말풍선 닫기(ISSUE-57) — 외부 터치 블로커 콜백 및 보상 없음 처리 공용. 말풍선과 블로커를 함께 끈다.
    private void HideRewardTooltip()
    {
        if (rewardTooltip != null)
            rewardTooltip.HideTooltip();
        if (rewardTooltipBlocker != null)
            rewardTooltipBlocker.gameObject.SetActive(false);
    }

    // 결과 스파인 재생 — 최고 점수 갱신이면 Max(축하), 아니면 Basic. 시작 애니(1회) 후 대기(Idle) 루프로 연결.
    private void PlayResultSpine(bool isNewBest)
    {
        if (resultSpine == null || resultSpine.AnimationState == null)
            return;

        // 팝업 본체가 열린 뒤 이 시점에 우사하나 스파인을 노출(등장 전까지 PlayEntranceAnimation 에서 숨겨둠).
        resultSpine.gameObject.SetActive(true);

        var startAnim = isNewBest ? SPINE_START_BEST : SPINE_START_NORMAL;
        var idleAnim  = isNewBest ? SPINE_IDLE_BEST  : SPINE_IDLE_NORMAL;
        resultSpine.AnimationState.SetAnimation(0, startAnim, false);
        resultSpine.AnimationState.AddAnimation(0, idleAnim, true, 0f);
    }

    // 팝업 본체(PopupBoxFrameBg)의 오픈 애니(HighScoreBoardBox_Ani)를 0프레임부터 재생 — 풀 재사용 시에도 매 오픈 등장 보장.
    private void ReplayPopupBoxOpen()
    {
        if (popupBoxAnimator == null)
            return;

        popupBoxAnimator.Rebind();
        popupBoxAnimator.Update(0f);
    }

    // 결과 컨페티 — Fx_Carrot_Confetti(파티클 1회) + Fx_Complete_Confetti(뒷판 스파인 Loop)는 항상 재생(첫종료/일반종료 포함, ISSUE-53).
    // 최고 점수 갱신 시 추가로 Fx_Carrot_Confetti_2(파티클 3회, 앞)를 켠다.
    private void PlayConfetti(bool isNewBest)
    {
        ReplayEffect(confettiFx);   // 항상 재생(첫 도전/게임 종료 공통, 앞)
        PlayCompleteConfetti();     // 뒷판 컨페티(Fx_Complete_Confetti) 스파인 Loop — 첫종료/일반종료 포함 항상 재생(ISSUE-53)

        if (!isNewBest)
        {
            // 최고갱신 전용 앞 3회 컨페티만 꺼둔다(재오픈/풀 재사용 대비). 뒷판 컨페티는 위에서 공통 재생.
            if (bestConfettiFx != null)
                bestConfettiFx.SetActive(false);
            return;
        }

        ReplayEffect(bestConfettiFx);       // 최고갱신 추가 — 파티클 3회(앞)
    }

    // 뒷판 컨페티(Fx_Complete_Confetti) 스파인을 Loop 로 재생(ISSUE-53) — 첫종료/일반종료 포함 모든 결과 공통.
    // SetActive 만으로는 풀 재사용/재오픈 시 애니가 멈춰 있을 수 있어, 활성화 후 명시적으로 Loop 재생한다(resultSpine 패턴 동일).
    private void PlayCompleteConfetti()
    {
        if (completeConfettiSpine == null || completeConfettiSpine.AnimationState == null)
            return;

        completeConfettiSpine.gameObject.SetActive(true);
        completeConfettiSpine.AnimationState.SetAnimation(0, COMPLETE_CONFETTI_ANIM, true);   // Loop
    }

    // 이펙트 오브젝트를 off→on 재토글해 파티클/스파인을 처음부터 재생한다.
    private void ReplayEffect(GameObject fx)
    {
        if (fx == null)
            return;

        fx.SetActive(false);
        fx.SetActive(true);
    }

    // 누적 점수 카운트업 — 숫자(base→total)를 올린 뒤 증가분 "(+N)"(초록)을 덧붙인다. 증가분이 없으면 숫자만.
    private void PlayCumulativeCountUp(int from, int to, int increment)
    {
        if (cumulativeScoreText == null)
            return;

        if (increment <= 0)
        {
            cumulativeScoreText.SetText($"{to}");
            return;
        }

        DOVirtual.Int(from, to, SCORE_COUNT_DURATION, value => cumulativeScoreText.SetText($"{value}"))
            .OnComplete(() => cumulativeScoreText.SetText($"{to} <color={INCREMENT_COLOR}>(+{increment})</color>"));
    }

    // 최고 점수 갱신 강조 — 최고 점수 텍스트에 펀치 스케일(§5-6-1 2순위 "최고 점수 갱신!").
    private void EmphasizeNewBest()
    {
        if (highScoreText == null)
            return;

        highScoreText.transform.DOPunchScale(Vector3.one * BEST_EMPHASIS_PUNCH, BEST_EMPHASIS_DURATION, 6, 0.6f);
    }

    // [다시]/[메인] 버튼 오브젝트(Btn_Again/Btn_Main) 노출 토글 — 슬라이더 연출 중에는 숨기고 연출 종료 후 노출(요구사항).
    private void SetActionButtonsVisible(bool visible)
    {
        // [다시]/[메인]은 슬라이더 연출 중 숨기고 연출 종료 후 노출. 코인 충분 여부의 Enable/Disable 표시는 Refresh 의 EnableButton 이 담당.
        // 단 [다시]는 누적 만렙이면 연출 종료 후에도 노출하지 않는다(재진입 불가, ISSUE-58).
        if (againButton != null)
            againButton.gameObject.SetActive(visible && CanRetry());
        if (mainButton != null)
            mainButton.gameObject.SetActive(visible);
    }

    // 재도전 가능 여부 — 누적 보상을 모두 수령(누적 만렙)하면 재도전 불가([다시] 버튼 SetActive false, ISSUE-58).
    private bool CanRetry()
    {
        return controller != null && !controller.IsAllCumulativeRewardClaimed();
    }

    // 누적 슬라이더 구간 계산 — 0f=직전 달성 골, 1f=목표 골. 이번 판에 새로 넘긴 골마다 가득 채움 구간(파티클),
    // 남은 점수는 다음 목표 골을 향한 부분 채움 구간(파티클 없음)을 만든다. 골이 없으면 빈 목록.
    private List<CumulativeSliderStep> BuildCumulativeSliderSteps(long preScore, long postScore)
    {
        var steps = new List<CumulativeSliderStep>();
        if (controller == null)
            return steps;

        var goals = controller.GetRewardTierGoals();    // 오름차순, goalValue2 > 0
        if (goals == null || goals.Count == 0)
            return steps;

        var goalCount = goals.Count;

        // 직전 달성 골(x 이하 최대 골, 없으면 0).
        long FloorGoal(long x)
        {
            long floor = 0;
            for (var i = 0; i < goalCount; ++i)
            {
                var g = goals[i];
                if (g <= x && g > floor)
                    floor = g;
            }
            return floor;
        }

        // 다음 목표 골(x 초과 최소 골 — 오름차순이라 첫 초과 골, 없으면 -1).
        long CeilGoal(long x)
        {
            for (var i = 0; i < goalCount; ++i)
            {
                var g = goals[i];
                if (g > x)
                    return g;
            }
            return -1;
        }

        var cur = preScore;

        // 1) 이번 판에 새로 넘긴 골(pre < g <= post)마다 '직전 골→해당 골'을 가득(1.0f) 채우는 구간.
        for (var i = 0; i < goalCount; ++i)
        {
            var g = goals[i];
            if (g <= preScore)
                continue;
            if (g > postScore)
                break;

            var segFrom = FloorGoal(cur);
            var span = g - segFrom;
            var startFill = span > 0 ? Mathf.Clamp01((float)(cur - segFrom) / span) : 0f;
            steps.Add(new CumulativeSliderStep { targetGoal = g, startFill = startFill, endFill = 1f, reachesGoal = true });
            cur = g;
        }

        // 2) 남은 점수(다음 목표 골 미달)는 '직전 골→다음 목표 골'의 부분 채움(목표 미도달, 파티클 없음).
        if (cur < postScore)
        {
            var ceil = CeilGoal(cur);
            if (ceil > 0)
            {
                var segFrom = FloorGoal(cur);
                var span = ceil - segFrom;
                var startFill = span > 0 ? Mathf.Clamp01((float)(cur - segFrom) / span) : 0f;
                var endFill = span > 0 ? Mathf.Clamp01((float)(postScore - segFrom) / span) : 0f;
                steps.Add(new CumulativeSliderStep { targetGoal = ceil, startFill = startFill, endFill = endFill, reachesGoal = false });
            }
        }

        return steps;
    }

    // 연출 시작 전 — 슬라이더를 첫 구간 시작값(플레이 전 위치)으로 스냅하고 목표 점수 텍스트를 세팅한다.
    private void SnapCumulativeSliderToStart(List<CumulativeSliderStep> steps, long preScore)
    {
        // 게이지가 움직이기 전(연출 시작)에는 목표 점수 보상 툴팁을 비활성화한다(요구사항). 게이지 완료 후 OnEntranceAnimationComplete 에서 노출.
        // 표시 중 닫기용 블로커(ISSUE-57)도 함께 끈다(연출 도중 잔류 방지).
        if (rewardTooltip != null)
            rewardTooltip.gameObject.SetActive(false);
        if (rewardTooltipBlocker != null)
            rewardTooltipBlocker.gameObject.SetActive(false);

        if (cumulativeSlider == null)
            return;

        HideRewardReachedFx();

        if (steps != null && steps.Count > 0)
        {
            var first = steps[0];
            cumulativeSlider.value = first.startFill;
            if (targetScoreText != null)
                targetScoreText.SetText($"{first.targetGoal}");
        }
        else
        {
            ApplyCumulativeSliderImmediate(preScore);   // 연출 구간 없음(획득 0/전부 달성) — 현재 위치 그대로.
        }
    }

    // 누적 슬라이더 연출을 등장 시퀀스에 이어 붙인다 — 구간마다 목표 텍스트 갱신 → 채움 → (목표 도달 시)파티클 → 텀.
    private void AppendCumulativeSliderAnimation(Sequence seq, List<CumulativeSliderStep> steps)
    {
        if (cumulativeSlider == null || steps == null || steps.Count == 0)
            return;

        var stepCount = steps.Count;
        for (var i = 0; i < stepCount; ++i)
        {
            var step = steps[i];    // 루프 내부 지역 변수 — 콜백 캡처 안전(구간별 고정).

            // 구간 시작 — 목표 점수(CarrotScoreIcon/Text) 갱신 + 시작값 스냅 + 직전 파티클 정리.
            seq.AppendCallback(() =>
            {
                if (targetScoreText != null)
                    targetScoreText.SetText($"{step.targetGoal}");
                cumulativeSlider.value = step.startFill;
                HideRewardReachedFx();
            });

            // 채움(직전 골 → 목표).
            seq.Append(DOVirtual.Float(step.startFill, step.endFill, SLIDER_FILL_DURATION,
                value => cumulativeSlider.value = value).SetEase(Ease.OutQuad));

            // 목표(1.0f) 도달 구간이면 파티클(Fx_CharacterCafe_reward) 재생.
            if (step.reachesGoal)
                seq.AppendCallback(PlayRewardReachedFx);

            seq.AppendInterval(SLIDER_STEP_INTERVAL);
        }
    }

    // 연출 없이 슬라이더를 현재 누적 점수의 도달 위치로 즉시 스냅(서버 리프레시 등). 0f=직전 골, 1f=다음 목표 골.
    private void ApplyCumulativeSliderImmediate(long score)
    {
        if (cumulativeSlider == null || controller == null)
            return;

        var goals = controller.GetRewardTierGoals();
        if (goals == null || goals.Count == 0)
            return;

        var goalCount = goals.Count;
        long floor = 0;
        long ceil = -1;
        for (var i = 0; i < goalCount; ++i)
        {
            var g = goals[i];
            if (g <= score)
            {
                if (g > floor)
                    floor = g;
            }
            else
            {
                ceil = g;   // 오름차순 첫 초과 = 다음 목표
                break;
            }
        }

        if (ceil < 0)
        {
            // 모든 골 달성 — 가득.
            cumulativeSlider.value = 1f;
            if (targetScoreText != null)
                targetScoreText.SetText($"{goals[goalCount - 1]}");
            return;
        }

        var span = ceil - floor;
        cumulativeSlider.value = span > 0 ? Mathf.Clamp01((float)(score - floor) / span) : 0f;
        if (targetScoreText != null)
            targetScoreText.SetText($"{ceil}");
    }

    // 목표 도달 파티클(Fx_CharacterCafe_reward) — off→on 재토글로 매 구간 처음부터 재생.
    private void PlayRewardReachedFx()
    {
        if (rewardReachedFx == null)
            return;

        rewardReachedFx.SetActive(false);
        rewardReachedFx.SetActive(true);
    }

    private void HideRewardReachedFx()
    {
        if (rewardReachedFx != null)
            rewardReachedFx.SetActive(false);
    }

    // 누적 보상 획득 시 — 이번 판 누적 티어 보상 전체를 UIPopupRewardResult 로 노출(요구사항). 없으면 미노출.
    private void ShowCumulativeRewardPopup()
    {
        if (controller == null)
            return;

        var rewardInfos = controller.GetLastGrantedCumulativeRewardInfos();
        if (rewardInfos == null || rewardInfos.Count == 0)
            return;

        OpenCumulativeRewardPopupAsync(rewardInfos).Forget();
    }

    private async UniTaskVoid OpenCumulativeRewardPopupAsync(List<RewardInfo> rewardInfos)
    {
        var rewardInfoData = new RewardInfoData();
        var count = rewardInfos.Count;
        for (var i = 0; i < count; ++i)
            rewardInfoData.AddRewardInfo(rewardInfos[i]);

        var popup = await UIManager.OpenUIMsgAsync<UIPopupRewardResult>(rewardInfoData, ct: this.GetCancellationTokenOnDestroy());
        if (popup == null)
            return;

        popup.SetTitle(ShopSuccessType.RewardAcquired);
    }

    // LoopScrollPrefabSource — CommonRewardItem 셀 풀링.
    GameObject LoopScrollPrefabSource.GetObject(int index)
    {
        if (newRewardPool.Count == 0)
            return Instantiate(newRewardItemPrefab);

        var candidate = newRewardPool.Pop();
        candidate.gameObject.SetActive(true);
        return candidate.gameObject;
    }

    void LoopScrollPrefabSource.ReturnObject(Transform trans)
    {
        trans.gameObject.SetActive(false);
        trans.SetParent(newRewardScroll != null ? newRewardScroll.transform : transform, false);
        newRewardPool.Push(trans);
    }

    // LoopScrollDataSource — 셀(CommonRewardItem)에 보상 아이콘/수량 주입. lastRewardInfos 는 이미 RewardInfo 로 해석된 목록.
    void LoopScrollDataSource.ProvideData(Transform trans, int index)
    {
        if (lastRewardInfos == null || index < 0 || index >= lastRewardInfos.Count)
            return;

        var item = trans.GetComponent<CommonRewardItem>();
        if (item == null)
            return;

        // 아이템 터치 시 인포 팝업(뱃지는 뱃지 정보, ISSUE-63) — 범용 핸들러를 clickAction 으로 전달.
        // RewardHelper.OnClickShowInfoPop 는 (CommonRewardItem, Action) 시그니처라 람다로 감싼다(후속 닫기 액션 불필요 → null).
        item.SetInfo(lastRewardInfos[index], rewardItem => RewardHelper.OnClickShowInfoPop(rewardItem, null));
    }

    private void OnClickAgain()
    {
        if (controller == null)
            return;

        // 누적 보상을 모두 수령했으면 재도전 불가(ISSUE-58) — 버튼 interactable=false 로도 막히지만 방어적으로 차단.
        if (controller.IsAllCumulativeRewardClaimed())
            return;

        // 코인 충분 → 인게임 재진입(결과 닫음) / 부족 → 코인 부족 팝업만 오픈하고 결과는 뒤에 유지.
        // 입장 코인은 게임 시작(진입) 시점에 차감되어 호출 후 CanStartGame 이 바뀔 수 있으므로, 호출 전 값으로 진입 여부를 판정한다.
        var willEnterGame = controller.CanStartGame;
        controller.OnClickRetry();      // 동일 조건 재시작 → 인게임 재진입(코인 검증 포함)
        if (willEnterGame)
            Close();
    }

    private void OnClickMain()
    {
        // 결과 팝업 닫고 이벤트(메인) 팝업 재호출(§5-6 7).
        Close();
        controller?.OpenMainPopup();
    }

    private void OnClickClose()
    {
        OnClickMain();      // 닫기(X)도 [메인]과 동일하게 결과 팝업 닫고 이벤트(메인) 팝업으로 복귀(§5-6 7).
    }

    // ESC/뒤로가기 키 처리(ISSUE-51) — 결과 팝업에서도 무반응(인게임 팝업과 동일).
    // 베이스(UIBasePopup.OnKeyEscapeExcute)는 백키 시 팝업을 즉시 Close 하나, [닫기]/[메인]과 달리 메인 팝업
    // 복귀(OnClickMain) 없이 그냥 닫혀 당근 팝업이 모두 사라진다. true 를 반환해 백키를 '소비'하되 아무 동작도
    // 하지 않아, 베이스 Close 와 하위 UI·앱 종료 다이얼로그로의 전파를 모두 차단한다(이동은 버튼으로만).
    public override bool OnKeyEscapeExcute()
    {
        return true;
    }
}

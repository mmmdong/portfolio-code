using System;

using Cysharp.Threading.Tasks;  // UniTask
using DG.Tweening;              // DOTweenAnimation (CountText3 카운트 펑핑)

using GameCore.Utils;       // DLogger

using GameLogic.Define;     // IUIInfoData
using GameLogic.Management;  // UIManager

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 당근 수확 대소동 인게임 팝업의 데이터.
/// 보드/점수 모델을 소유한 컨트롤러 참조를 전달한다(보드 뷰가 이 컨트롤러를 매 프레임 구동).
/// </summary>
public struct PopupCarrotInGameInform : IUIInfoData
{
    public ContentEventCarrot controller;
}

/// <summary>
/// 당근 수확 대소동 인게임 팝업(골격). <see cref="UIBasePopup"/> 기반.
///
/// 역할: 자식 <see cref="EventCarrotBoardView"/> 를 호스팅하고 컨트롤러를 주입(Init)한 뒤,
///       3초 카운트 → StartGame, 매 프레임 HUD(점수/남은 시간/콤보 게이지) 갱신,
///       보드 뷰의 Harvested/GameEnded 를 받아 연출/종료 처리, 그만두기 버튼 처리.
///
/// [SerializeField] 바인딩은 프리팹(UIPopupEventCarrotInGame)에 연결 완료 — null 가드 없이 직접 접근한다.
///    (컨트롤러 EnterInGame 에서 UIManager.OpenUIMsgAsync 로 본 팝업을 PopupCarrotInGameInform 과 함께 연다.)
/// </summary>
public class UIPopupEventCarrotInGame : UIBasePopup
{
    private const int COUNT_START_NUMBER = 3;       // 카운트 시작 숫자(3 → 2 → 1)
    private const float COUNT_STEP_SECONDS = 1f;    // 숫자 전환 간격
    private const float COUNT_GO_SECONDS = 0.7f;    // "GO!" 유지 시간
    private const float STUN_SHOW_SECONDS = 1f;     // 경직 UI 노출 시간(§6-2, 보드 입력 잠금과 동일)
    // 콤보 펑핑은 트리거(SetTrigger)가 아니라 상태를 직접 Play(state, 0, 0f) 로 재생한다 — 같은 상태에 이미 있으면
    // 트리거는 전이가 안 일어나 재생이 안 되므로, 콤보 갱신마다 normalizedTime 0 으로 처음부터 강제 재생.
    private static readonly int COMBO_SCALE_STATE = Animator.StringToHash("Ani_ComboScale_Basic");       // 일반 콤보 펑핑 상태
    private static readonly int COMBO_SCALE_MEGA_STATE = Animator.StringToHash("Ani_ComboScale_Maga");   // 메가 콤보 펑핑 상태(컨트롤러 상태명 오타 Maga)
    private const float COMBO_NOTICE_SECONDS = 1f;  // 콤보 취소/시간 초과 안내 노출 시간(§7-4, PDF p.3)
    private const float END_COUNTDOWN_SECONDS = 5f; // 종료 카운트다운 시작 시점(남은 시간 5초, ISSUE-48)
    private const float END_NOTICE_SECONDS = 2f;    // 종료 딤드 안내(PopupBox "당근 수확 종료!") 노출 후 결과 팝업까지 지연(Jira ISSUE-48: 2초)
    // 타임 게이지(TimerSlider) 색상(ISSUE-41) — 평소 #89C3FF, 종료 임박(≤END_COUNTDOWN_SECONDS) #FF4E4E.
    private static readonly Color TIME_GAUGE_NORMAL_COLOR = new(0.5372549f, 0.7647059f, 1f);      // #89C3FF
    private static readonly Color TIME_GAUGE_LOW_COLOR = new(1f, 0.30588236f, 0.30588236f);       // #FF4E4E
    // TimerSlider 애니메이터(Carrot_Timer, ISSUE-41) — 기본 Idle(loop), 종료 임박 시 TimeAttack 트리거로 Idle→Move(loop) 전이.
    private static readonly int TIMER_IDLE_STATE = Animator.StringToHash("Idle");
    private static readonly int TIMER_TIME_ATTACK_TRIGGER = Animator.StringToHash("TimeAttack");

    [Header("Board")]
    [SerializeField] private EventCarrotBoardView boardView;

    [Header("HUD")]
    [SerializeField] private UITextEx scoreText;
    [SerializeField] private UITextEx timeText;         // 레거시 타임 텍스트(RemainingTimeBox — 프리팹 비활성). 표시는 timeSlider 가 대체(ISSUE-41)
    [SerializeField] private Slider timeSlider;          // 남은 시간 게이지(value 1→0) — TimerSlider(ISSUE-41, 타임 간판 대체)
    [SerializeField] private Image timeSliderFill;       // 타임 게이지 Fill 이미지 — 잔여 시간에 따라 색 전환(평소 #89C3FF / 임박 #FF4E4E, ISSUE-41)
    [SerializeField] private Animator timeSliderAnimator; // TimerSlider 애니메이터(Carrot_Timer) — 기본 Idle, 종료 임박 시 Move 전이(ISSUE-41)
    [SerializeField] private Slider comboGauge;         // 콤보 유지 게이지(Slider value 0~1) — TimeSlider

    [Header("Combo")]
    [SerializeField] private GameObject comboBox;       // 콤보 배너 루트(Animator) — ComboBoxBasic
    [SerializeField] private UITextEx comboTextDefault; // 일반 콤보 텍스트 — "COMBO! ×N"(43220), ComboText_Default
    [SerializeField] private UITextEx comboTextMega;    // 메가 콤보 텍스트 — "MEGA COMBO! ×N"(43222), ComboText_Mega
    [SerializeField] private UITextEx comboBonusText;   // "(+N점)"
    [SerializeField] private UITextEx comboNoticeText;  // 콤보 취소/시간 초과 안내(비-그라데이션 ComboText) — 43223/43224

    [Header("Buttons")]
    [SerializeField] private UIButtonEx quitButton;     // 그만두기

    [Header("Count")]
    [SerializeField] private GameObject countRoot;          // 카운트 루트(UIPopupEventCarrotStartCount)
    [SerializeField] private UITextEx countNumberText;      // 숫자(3/2/1) — CountText3
    [SerializeField] private GameObject countGoObject;      // "GO!" — CountTextGO

    [Header("Stun")]
    [SerializeField] private GameObject stunBox;            // 경직 아이콘+"경직!"+딤 (EventCarrot_StunBox)
    [SerializeField] private Slider missTimeSlider;         // 경직 잔여 시간 게이지(1→0) — EventCarrot_StunBox/MissTimeSlider

    [Header("End Step")]
    [SerializeField] private GameObject endStepRoot;        // 종료 스텝 루트(UIPopupEventCarrotMainEnd) — 종료 딤드 안내 컨테이너(ISSUE-48). 카운트다운 토스트는 제거됨(ISSUE-41)
    [SerializeField] private GameObject endNoticeBox;       // 종료 딤드 안내(PopupBox) — "당근 수확 종료!"(43248, 프리팹 baked) 노출 후 결과 팝업

    private ContentEventCarrot controller;
    private bool playing;
    private bool comboShown;
    private int comboNoticeGen;          // 콤보 취소/초과 안내의 지연 숨김 무효화용 세대 카운터
    private int stunGen;                  // 경직 연출 세대 — 연속 경직 시 이전 슬라이더 루프 무효화용
    private bool ended;         // 결과 전환 1회 보장(시간 만료/그만두기 동시 방지)
    private int endCountdownLastSecond;   // 종료 카운트다운 마지막 표시 초 — 초 변경 시에만 사운드 1회(ISSUE-49)
    private bool timeGaugeLow;             // 타임 게이지 임박 색상 적용 여부 — 임계 교차 시 1회만 색 전환(ISSUE-41)
    private Animator comboBoxAnimator;      // comboBox(ComboBoxBasic) Animator 캐시 — 콤보 펑핑 트리거용
    private DOTweenAnimation countNumberTween;  // CountText3 의 DOTweenAnimation 캐시 — 카운트 숫자 펑핑 재생용

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);

        if (data == null)
        {
            Close();
            return;
        }

        var inform = (PopupCarrotInGameInform)mData;
        controller = inform.controller;
        if (controller == null)
        {
            DLogger.Error($"[{GetType().Name}] controller 미전달 — 인게임 팝업 종료");
            Close();
            return;
        }

        boardView.Init(controller);

        // 콤보 배너(ComboBoxBasic = ComboText/ComboBonusText 포함)와 게이지는 콤보 발동 시에만 노출 — 시작 시 숨김(팝업 재사용 대비 매 진입 보장).
        HideComboBanner();
        ResetEndStep();         // 종료 스텝(딤드 안내) 초기화 — 팝업 재사용(다시 시작) 대비 매 진입 보장.
        ResetTimeGauge();       // 타임 게이지 색/값 초기화(다시 시작 대비) — 평소색·가득(ISSUE-41)

        RefreshHud();
        PlayStartCountThenBeginAsync().Forget();
    }

    protected override void Awake()
    {
        base.Awake();

        // 콤보 박스 펑핑(scale 트리거)용 Animator 캐시 — 바인딩만(구독은 Start).
        comboBoxAnimator = comboBox.GetComponent<Animator>();

        // 카운트 숫자(CountText3) 펑핑용 DOTweenAnimation 캐시 — 바인딩만.
        countNumberTween = countNumberText.GetComponent<DOTweenAnimation>();
    }

    protected override void Start()
    {
        base.Start();

        quitButton.onClick.AddListener(OnClickQuit);

        boardView.Harvested += OnHarvested;
        boardView.GameEnded += OnGameFinished;
    }

    protected override void OnDestroy()
    {
        quitButton.onClick.RemoveListener(OnClickQuit);

        boardView.Harvested -= OnHarvested;
        boardView.GameEnded -= OnGameFinished;

        base.OnDestroy();
    }

    private void Update()
    {
        if (playing)
        {
            RefreshHud();
            UpdateEndCountdown();
        }
    }

    // 종료 5초 전부터 남은 초가 바뀔 때마다(5→1) 카운트다운 사운드 1회(ISSUE-49).
    // 시각 카운트다운(토스트)은 제거됐고, 임박 표시는 타임 슬라이더 임박색(#FF4E4E)이 대체한다(ISSUE-41).
    private void UpdateEndCountdown()
    {
        var remain = controller.RemainGameTime;
        if (remain <= 0f || remain > END_COUNTDOWN_SECONDS)
            return;

        var second = Mathf.CeilToInt(remain);
        if (second == endCountdownLastSecond)
            return;

        endCountdownLastSecond = second;
        SoundManager.Instance.PlaySound(EventCarrotSoundDefine.END_COUNTDOWN);
    }

    // 3초 카운트(3 → 2 → 1 → GO!) 후 게임 시작. 팝업 파괴 시 토큰으로 자동 취소.
    private async UniTaskVoid PlayStartCountThenBeginAsync()
    {
        var token = gameObject.GetCancellationTokenOnDestroy();

        countRoot.SetActive(true);
        countGoObject.SetActive(false);

        for (var n = COUNT_START_NUMBER; n >= 1; n--)
        {
            ShowCountNumber(n);
            SoundManager.Instance.PlaySound(EventCarrotSoundDefine.START_COUNT);     // 카운트 숫자(3·2·1)마다 1초 간격 재생(ISSUE-49)
            await UniTask.Delay(TimeSpan.FromSeconds(COUNT_STEP_SECONDS), cancellationToken: token);
        }

        // GO!
        countNumberText.gameObject.SetActive(false);
        countGoObject.SetActive(true);
        SoundManager.Instance.PlaySound(EventCarrotSoundDefine.START_COUNT);     // GO! 비트 — 카운트다운 마지막 1초 비트(ISSUE-49)

        await UniTask.Delay(TimeSpan.FromSeconds(COUNT_GO_SECONDS), cancellationToken: token);

        countRoot.SetActive(false);

        BeginGame();
    }

    // 숫자(3/2/1)를 갱신하고 CountText3 의 DOTweenAnimation(From 스케일 팝)을 매 카운트마다 되감아 재생한다.
    // ※ RecreateTween(kill+재생성)은 From 트윈의 'to' 베이스라인을 직전 확대 스케일(1.2)로 다시 캡처해
    //    1.2→1.2(무변화)가 되며 스케일이 1.2 로 고정된다. autoKill=false 인 원본 트윈을 DORestart 로
    //    되감으면 항상 1.2→1.0 팝이 재생된다(최초 진입의 첫 1회는 DOTweenAnimation autoPlay 가 담당).
    private void ShowCountNumber(int number)
    {
        countNumberText.SetText($"{number}");
        countNumberTween.DORestart();
    }

    private void BeginGame()
    {
        boardView.StartGame();
        playing = true;
    }

    // 구멍 터치 1회 결과 → HUD 콤보 배너 갱신. (점수/MISS/아이템 텍스트·뽑힘 연출은 슬롯 단위로 보드 뷰가 처리)
    private void OnHarvested(EventCarrotTouchOutcome outcome)
    {
        RefreshHud();

        if (!outcome.Scored)
        {
            // 빈 구멍(경직) — 콤보 진행 중이었다면 "COMBO 취소!"(43223) 안내, 아니면 단순 숨김.
            if (comboShown)
                ShowComboNotice(EventCarrotStringDefine.INGAME_COMBO_CANCEL);
            else
                HideComboBanner();
            ShowStun();             // 경직 UI 1초 노출(보드는 입력 잠금)
            return;
        }

        if (outcome.Hit.ComboTier != EventCarrotComboTier.None)
            ShowComboBanner(outcome.Hit);   // 콤보 발동(2연속~) §7-4
        else
            HideComboBanner();              // 첫 터치(콤보 미발동) — 배너 없음
    }

    // 콤보 배너 표시 — COMBO!/MEGA COMBO ×N + 가산점. 펑핑은 Animator 트리거(일반 scale / 메가 scale_Mega)로 발동.
    private void ShowComboBanner(EventCarrotHitResult hit)
    {
        comboNoticeGen++;       // 진행 중 취소/초과 안내의 지연 숨김 무효화(이 콤보가 우선)

        var mega = hit.ComboTier == EventCarrotComboTier.Mega;

        comboBox.SetActive(true);       // 재생을 위해 활성 보장
        comboBoxAnimator.Play(mega ? COMBO_SCALE_MEGA_STATE : COMBO_SCALE_STATE, 0, 0f);  // 콤보 갱신마다 처음부터 재생(일반 Ani_ComboScale_Basic / 메가 Ani_ComboScale_Maga)
        comboGauge.gameObject.SetActive(true);      // 콤보 게이지 노출

        // 일반/메가 콤보 텍스트는 별도 노드(ComboText_Default / ComboText_Mega)로 분리 — 티어에 맞는 하나만 노출. 안내(ComboText)는 숨김.
        comboTextDefault.gameObject.SetActive(!mega);
        comboTextMega.gameObject.SetActive(mega);
        comboNoticeText.gameObject.SetActive(false);
        comboBonusText.gameObject.SetActive(true);

        var comboText = mega ? comboTextMega : comboTextDefault;
        comboText.SetText(EventCarrotInGameText.Combo(hit.ComboStreak, mega));      // 43220 / 43222 "x{0}"
        comboBonusText.SetText(EventCarrotInGameText.ComboBonus(hit.ComboBonus));   // 43221 "콤보 보너스 점수 +{0}"
        comboShown = true;
    }

    // 콤보 취소(빈 구멍 경직)/시간 초과(게이지 만료) 안내(§7-4, PDF p.3) — 비-그라데이션 ComboText 노드로
    // 펑핑 후 1초 노출 뒤 배너 제거. 새 콤보/숨김이 끼어들면 comboNoticeGen 으로 지연 숨김을 무효화한다.
    private void ShowComboNotice(int lidx)
    {
        var gen = ++comboNoticeGen;

        comboBox.SetActive(true);
        comboBoxAnimator.Play(COMBO_SCALE_STATE, 0, 0f);   // 안내 등장 펑핑(처음부터 재생)

        comboTextDefault.gameObject.SetActive(false);
        comboTextMega.gameObject.SetActive(false);
        comboBonusText.gameObject.SetActive(false);         // 취소/초과는 가산점 없음
        comboNoticeText.gameObject.SetActive(true);
        comboNoticeText.SetText(TableManager.GetText(lidx));    // 43223 "COMBO 취소!" / 43224 "시간 초과"

        comboGauge.gameObject.SetActive(false);     // 콤보 종료 — 게이지 숨김
        comboShown = false;                          // 콤보 상태 종료(중복 안내 방지)

        HideComboNoticeAfterAsync(gen).Forget();
    }

    private async UniTaskVoid HideComboNoticeAfterAsync(int gen)
    {
        await UniTask.Delay(TimeSpan.FromSeconds(COMBO_NOTICE_SECONDS), cancellationToken: gameObject.GetCancellationTokenOnDestroy());

        if (gen != comboNoticeGen)      // 더 최근의 콤보/안내/숨김이 발생 → 이 지연 숨김은 무효
            return;

        comboNoticeText.gameObject.SetActive(false);
        comboBox.SetActive(false);
    }

    private void HideComboBanner()
    {
        comboNoticeGen++;       // 진행 중 안내의 지연 숨김 무효화
        comboBox.SetActive(false);
        comboNoticeText.gameObject.SetActive(false);
        comboGauge.gameObject.SetActive(false);     // 콤보 게이지 숨김
        comboShown = false;
    }

    // 경직 UI(아이콘+"경직!"+딤) 노출 — MissTimeSlider 를 경직 시간 동안 1→0 으로 줄이고, 0 도달 시 경직 오브젝트를 숨긴다(§6-2, 보드 입력 잠금과 동기).
    private void ShowStun()
    {
        stunBox.SetActive(false);
        stunBox.SetActive(true);

        var gen = ++stunGen;        // 연속 경직 시 이전 슬라이더 루프 무효화(새 루프가 1부터 다시 구동)
        RunStunSliderAsync(gen).Forget();
    }

    // 경직 잔여 시간 게이지 — 매 프레임 value 를 1→0 으로 보간(UniTask). 0 도달 시 경직 오브젝트 Active Off(요구사항).
    private async UniTaskVoid RunStunSliderAsync(int gen)
    {
        var ct = gameObject.GetCancellationTokenOnDestroy();

        if (missTimeSlider != null)
            missTimeSlider.value = 1f;

        var elapsed = 0f;
        while (elapsed < STUN_SHOW_SECONDS)
        {
            await UniTask.Yield(PlayerLoopTiming.Update, ct);

            if (gen != stunGen)     // 새 경직이 끼어듦 — 이 루프는 무효(중복 구동 방지)
                return;

            elapsed += Time.deltaTime;
            if (missTimeSlider != null)
                missTimeSlider.value = Mathf.Clamp01(1f - elapsed / STUN_SHOW_SECONDS);
        }

        if (missTimeSlider != null)
            missTimeSlider.value = 0f;

        stunBox.SetActive(false);
    }

    // 게임 종료(시간 만료 또는 그만두기) → HUD 정지 후 결과 팝업으로 전환(§5-6, ISSUE-45).
    // 인게임 팝업은 닫지 않고 결과 팝업 뒤에 유지해, 결과가 쇼핑로드가 아닌 '게임 화면 위'에 뜨도록 한다.
    // 실제 인게임 닫기는 결과의 [다시](ContentEventCarrot.EnterInGame) / [메인](OpenMainPopup) 전환 시점에 처리된다.
    private void OnGameFinished()
    {
        if (ended)
            return;

        ended = true;
        playing = false;
        HideComboBanner();
        timeSlider.value = 0f;       // 타임 게이지도 0 으로 고정(ISSUE-41 — 남은 시간 표시 일관)

        // 게임 종료 시 남은 시간 HUD 를 0 으로 확정(ISSUE-50) — CeilToInt 는 0 초과 잔여(0<remain≤1)를 1 로 올려,
        // playing 이 종료로 false 가 되면서 잔여 0 의 마지막 갱신이 그려지지 않아 표시가 "1" 에서 멈춘다.
        // 종료 funnel(시간 만료/그만두기 공통)에서 한 번 0 으로 고정해 "1" 잔상을 제거한다.
        timeText.SetText("0");

        ShowEndNoticeThenResultAsync().Forget();
    }

    // 게임 종료 — 결과 팝업을 바로 열지 않고 딤드 안내(PopupBox "당근 수확 종료!")를 노출한 뒤 결과 팝업으로 전환(ISSUE-48 ②).
    private async UniTaskVoid ShowEndNoticeThenResultAsync()
    {
        var token = gameObject.GetCancellationTokenOnDestroy();

        // 종료 딤드 안내(PopupBox) 노출 — 등장 연출(DOTween From) 재생을 위해 재토글.
        if (endStepRoot != null)
            endStepRoot.SetActive(true);
        if (endNoticeBox != null)
        {
            endNoticeBox.SetActive(false);
            endNoticeBox.SetActive(true);
        }
        SoundManager.Instance.PlaySound(EventCarrotSoundDefine.END_MESSAGE);     // "당근 수확 종료!" 메세지 사운드(ISSUE-49)

        await UniTask.Delay(TimeSpan.FromSeconds(END_NOTICE_SECONDS), cancellationToken: token);

        if (controller == null)
            return;

        var inform = new PopupCarrotResultInform { controller = controller, result = controller.LastResult };
        await UIManager.OpenUIMsgAsync<UIPopupEventCarrotResult>(inform);

        // 결과 팝업이 위로 올라온 뒤 종료 딤드는 정리(결과 [다시] 재진입 시 SetInfo 의 ResetEndStep 가 다시 보장).
        ResetEndStep();
    }

    // 종료 스텝 초기화 — 딤드 안내 숨김 + 카운트다운 사운드 재트리거 보장(매 진입/결과 전환 후).
    private void ResetEndStep()
    {
        if (endStepRoot != null)
            endStepRoot.SetActive(false);
        if (endNoticeBox != null)
            endNoticeBox.SetActive(false);
        endCountdownLastSecond = 0;      // 종료 카운트다운 사운드 재트리거 보장(다시 시작 대비, ISSUE-49)
    }

    // 남은 시간 게이지(TimerSlider) 갱신 — 잔여 비율로 fill, 종료 임박(≤END_COUNTDOWN_SECONDS)이면 Fill 색을 임박색(#FF4E4E)으로 전환(ISSUE-41).
    // 색은 임계 교차 시에만 바꿔 매 프레임 재할당을 피한다(timeGaugeLow).
    private void ApplyTimeGauge(float remainTime)
    {
        timeSlider.value = controller.RemainGameTimeRatio;

        var low = remainTime <= END_COUNTDOWN_SECONDS;
        if (low == timeGaugeLow)
            return;

        timeGaugeLow = low;
        timeSliderFill.color = low ? TIME_GAUGE_LOW_COLOR : TIME_GAUGE_NORMAL_COLOR;
        if (low)
            timeSliderAnimator.SetTrigger(TIMER_TIME_ATTACK_TRIGGER);   // 종료 임박 — Idle→Move(loop) 전이(Carrot_Timer)
    }

    // 타임 게이지 초기화 — 평소색(#89C3FF)·가득(1)·기본 애니(Idle)로 되돌린다(다시 시작 시 직전 판 임박색/0/Move 잔상 제거, ISSUE-41).
    private void ResetTimeGauge()
    {
        timeGaugeLow = false;
        timeSliderFill.color = TIME_GAUGE_NORMAL_COLOR;
        timeSlider.value = 1f;

        // Move→Idle 전이가 컨트롤러에 없어, 재시작 시 코드로 기본 상태(Idle, loop)로 강제 복귀하고 임박 트리거 잔류를 제거한다.
        timeSliderAnimator.ResetTrigger(TIMER_TIME_ATTACK_TRIGGER);
        timeSliderAnimator.Play(TIMER_IDLE_STATE, 0, 0f);
    }

    // 그만두기 버튼 → 전용 확인 팝업(§5-5, 기획 3-2 1). [예] 시 종료/결과 전환, [아니오] 시 닫고 인게임 복귀.
    private void OnClickQuit()
    {
        UIManager.OpenUIMsgAsync<UIPopupEventCarrotNotify>(new PopupCarrotNotifyInform
        {
            onConfirm = OnConfirmQuit,
        }).Forget();
    }

    private void OnConfirmQuit()
    {
        boardView.StopGame();

        controller?.OnGameEnd();    // 그만두기 종료 — 결과 도출/저장
        OnGameFinished();
    }

    // ESC/뒤로가기 키 처리(ISSUE-51) — 인게임 진행 중에는 무반응.
    // 베이스(UIBasePopup.OnKeyEscapeExcute)는 백키 시 팝업을 즉시 Close 해, 그만두기 확인/결과 저장(OnGameEnd)을
    // 건너뛰고 게임이 비정상 종료됐다. true 를 반환해 백키를 '소비'하되 아무 동작도 하지 않아, 베이스 Close 와
    // 하위 UI·앱 종료 다이얼로그로의 전파를 모두 차단한다(기획 확정: 종료는 그만두기 버튼으로만).
    public override bool OnKeyEscapeExcute()
    {
        return true;
    }

    private void RefreshHud()
    {
        if (controller == null)
            return;

        scoreText.SetText($"{controller.CurrentScore}");

        var remainTime = controller.RemainGameTime;
        timeText.SetText($"{Mathf.CeilToInt(remainTime)}");
        ApplyTimeGauge(remainTime);     // 남은 시간 슬라이더 + 종료 임박 색상(ISSUE-41)

        comboGauge.value = controller.ComboGaugeRatio;

        // 콤보가 시간 초과(게이지 만료)로 끊기면 "시간 초과"(43224) 안내 후 배너 제거(매 명중 시 ShowComboBanner 가 다시 켠다).
        if (comboShown && controller.ComboTier == EventCarrotComboTier.None)
            ShowComboNotice(EventCarrotStringDefine.INGAME_COMBO_TIMEOUT);
    }
}

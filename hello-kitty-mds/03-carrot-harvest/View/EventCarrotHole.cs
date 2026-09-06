using System;

using Cysharp.Threading.Tasks;  // UniTask

using DG.Tweening;              // DOTweenAnimation(점수 팝업 페이드 재생)

using GameLogic.Define;         // ItemType

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 인게임 보드의 구멍 1칸(뷰). 고정 9슬롯에서는 당근=구멍=뷰=터치가 한 GameObject에 모인다.
/// 자신의 당근 뷰(<see cref="EventCarrotView"/>)와 터치 버튼을 보유하고, 클릭을 보드 뷰로 전달한다.
/// 구멍 인덱스는 <see cref="EventCarrotBoardView"/> 가 배열 순서로 부여하므로 본 클래스는 갖지 않는다.
/// 미바인딩 시 같은 GameObject 의 컴포넌트를 <c>GetComponent</c> 로 해석한다(Find 미사용).
/// </summary>
[DisallowMultipleComponent]
public sealed class EventCarrotHole : MonoBehaviour
{
    private const float MISS_SHOW_SECONDS = 0.7f;       // MISS! 노출 후 자동 숨김(DOTween 미부착 대응)
    private const float REWARD_SHOW_SECONDS = 1.6f;     // 슈퍼 보상 팝업 노출 시간(§7-2 super-pop 1.6초)
    private const float COMBO_SHOW_SECONDS = 0.7f;      // 콤보 배수 텍스트 노출 후 자동 숨김(펀치 연출 후 잔상 제거, 슬라이드10)

    [SerializeField] private Button touchButton;        // 구멍 전체를 덮는 터치 영역(UIButtonEx 등)
    [SerializeField] private EventCarrotView carrotView; // 이 슬롯에 영구 배치된 당근 뷰
    [SerializeField] private UITextEx scoreText;        // 터치 성공 시 "+{0}" (Texts/ScoreText)
    [SerializeField] private DOTweenAnimation scoreTextAnim; // ScoreText 페이드 연출(매 표시마다 DORestart — autoKill 트윈은 재활성만으론 재생 안 됨)
    [SerializeField] private UITextEx comboText;        // 일반 콤보 배수 "x{N}" 그라데이션 텍스트 (Texts/ComboText)
    [SerializeField] private DOTweenAnimation comboTextAnim; // ComboText 펀치 스케일(0.6) — autoPlay off, DORestart 로 재생
    [SerializeField] private UITextEx comboMegaText;    // 메가 콤보 배수 "x{N}" 그라데이션 텍스트 (Texts/ComboMegeText)
    [SerializeField] private DOTweenAnimation comboMegaTextAnim; // ComboMegeText 펀치 스케일(1.0) — autoPlay off, DORestart 로 재생
    [SerializeField] private UITextEx missText;         // 빈 구멍 터치 시 "MISS!" (Texts/MissText)
    [SerializeField] private UITextEx itemText;         // 슈퍼 레어 처치 보상 텍스트(레거시 — 아이콘 팝업 rewardItem 으로 대체)
    [SerializeField] private CommonRewardItem rewardItem; // 슈퍼 레어 처치 보상 아이콘 팝업(아이콘+수량, §7-2)
    [SerializeField] private EventCarrotVerdictView verdictView; // 슈퍼 레어 박스/HP 바 (EventCarrotVerdictBox)

    private Action clicked;
    private int comboGen;       // 콤보 배수 텍스트 자동 숨김 세대(연속 표시 시 최신 호출만 유효)

    public EventCarrotView View => carrotView;
    public EventCarrotVerdictView Verdict => verdictView;

    private void Awake()
    {
        if (touchButton == null)
            touchButton = GetComponent<Button>();

        if (carrotView == null)
            carrotView = GetComponent<EventCarrotView>();

        if (verdictView == null)
            verdictView = GetComponentInChildren<EventCarrotVerdictView>(true);
    }

    private void Start()
    {
        if (touchButton != null)
            touchButton.onClick.AddListener(OnClickButton);
    }

    private void OnDestroy()
    {
        if (touchButton != null)
            touchButton.onClick.RemoveListener(OnClickButton);
    }

    /// <summary>보드 뷰가 구멍 터치 콜백을 등록한다(인덱스는 보드 뷰가 배열 순서로 관리).</summary>
    public void SetClickListener(Action onClicked)
    {
        clicked = onClicked;
    }

    private void OnClickButton()
    {
        clicked?.Invoke();
    }

    /// <summary>
    /// 터치 성공 점수 팝업 "+{0}"(콤보 시 ★) 표시(§7-3) + 콤보 단계별 배수 텍스트 연출(슬라이드10).
    /// 매 터치 생성되는 가변 텍스트라 StringBuilder 로 빌드.
    /// </summary>
    public void ShowScore(int gainedScore, EventCarrotComboTier comboTier, int comboStreak)
    {
        var combo = comboTier != EventCarrotComboTier.None;
        PlayText(scoreText, EventCarrotInGameText.Score(gainedScore, combo), scoreTextAnim);
        ShowComboMultiplier(comboTier, comboStreak);
    }

    // 콤보 단계별 그라데이션 배수 텍스트(슬라이드10): None=미표시, Combo=ComboText, Mega=ComboMegeText.
    // 각 텍스트의 펀치 스케일값이 다르며(0.6/1.0) autoPlay off 이므로 PlayText 의 DORestart 로 재생한다.
    private void ShowComboMultiplier(EventCarrotComboTier comboTier, int comboStreak)
    {
        var target = comboTier switch
        {
            EventCarrotComboTier.Combo => comboText,
            EventCarrotComboTier.Mega => comboMegaText,
            _ => null
        };

        // 이전/반대 단계 배수 텍스트는 숨겨 중복 노출 방지.
        if (target != comboText)
            HideTextObject(comboText);
        if (target != comboMegaText)
            HideTextObject(comboMegaText);

        if (target == null)
            return;

        var anim = comboTier == EventCarrotComboTier.Mega ? comboMegaTextAnim : comboTextAnim;
        PlayText(target, EventCarrotInGameText.ComboMultiplier(comboStreak), anim);
        HideComboAfterAsync(++comboGen).Forget();
    }

    private async UniTaskVoid HideComboAfterAsync(int generation)
    {
        await UniTask.Delay(TimeSpan.FromSeconds(COMBO_SHOW_SECONDS), cancellationToken: gameObject.GetCancellationTokenOnDestroy());

        if (generation != comboGen)     // 더 최근 배수 표시가 떠 있으면 이 숨김은 무시.
            return;

        HideTextObject(comboText);
        HideTextObject(comboMegaText);
    }

    private void HideTextObject(UITextEx text)
    {
        if (text != null && text.gameObject.activeSelf)
            text.gameObject.SetActive(false);
    }

    /// <summary>빈 구멍 터치 시 "MISS!" 표시(§6-2). 고정 문구라 텍스트는 인스펙터(UITextEx StringKey = 43245)에서 주입,
    /// 코드는 노출/자동 숨김만 담당(ScoreText/ItemText 와 달리 DOTween 이 없어 코드로 자동 숨김).</summary>
    public void ShowMiss()
    {
        PlayText(missText, null);   // LIdx 43245 (EventCarrotStringDefine.INGAME_MISS) — 인스펙터 고정
        HideMissAfterAsync().Forget();
    }

    private async UniTaskVoid HideMissAfterAsync()
    {
        if (missText == null)
            return;

        await UniTask.Delay(TimeSpan.FromSeconds(MISS_SHOW_SECONDS), cancellationToken: gameObject.GetCancellationTokenOnDestroy());

        if (missText != null)
            missText.gameObject.SetActive(false);
    }

    /// <summary>슈퍼 레어 처치 보상 텍스트 표시(레거시). 아이콘 보상 팝업은 <see cref="ShowReward"/> 사용.</summary>
    

    /// <summary>
    /// 슈퍼 레어 처치 보상 아이콘 팝업 표시(§7-2 super-pop). 공용 <see cref="CommonRewardItem"/> 에
    /// (아이콘 타입/인덱스/수량)을 넘겨 아이콘+수량을 띄우고 1.6초 후 자동 숨김.
    /// </summary>
    public void ShowReward(ItemType type, int index, int count)
    {
        // TODO[binding]: 구멍 프리팹에 CommonRewardItem(보상 팝업) 자식 배치 후 rewardItem 인스펙터 바인딩 필요.
        if (rewardItem == null)
            return;

        rewardItem.gameObject.SetActive(true);
        rewardItem.SetInfo(type, index, count);
        HideRewardAfterAsync().Forget();
    }

    private async UniTaskVoid HideRewardAfterAsync()
    {
        if (rewardItem == null)
            return;

        await UniTask.Delay(TimeSpan.FromSeconds(REWARD_SHOW_SECONDS), cancellationToken: gameObject.GetCancellationTokenOnDestroy());

        if (rewardItem != null)
            rewardItem.gameObject.SetActive(false);
    }

    // 텍스트를 노출하고, 연출 트윈(anim)이 있으면 매 호출마다 처음부터 재생한다. value 가 null 이면 프리셋 텍스트 유지.
    // DOTweenAnimation 은 OnEnable 재시작이 없고 트윈을 Awake 에서 1회만 생성하므로, SetActive 재토글로는 재생되지 않는다.
    // → 최초 1회만 활성화로 트윈을 생성시키고, 이후 표시는 DORestart 로 명시적으로 되돌려 재생한다(prefab autoKill=false 전제).
    private void PlayText(UITextEx text, string value, DOTweenAnimation anim = null)
    {
        if (text == null)
            return;

        var textObject = text.gameObject;
        textObject.SetActive(true);

        if (value != null)
            text.SetText(value);

        if (anim != null)
            anim.DORestart();
    }
}

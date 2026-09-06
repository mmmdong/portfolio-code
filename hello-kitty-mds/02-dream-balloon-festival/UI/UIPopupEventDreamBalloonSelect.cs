using System;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using GameLogic.Define;
using GameLogic.Extension;
using StatefulUI.Runtime.Core;
using StatefulUISupport.Scripts.Components;

using UnityEngine;

// 드림 벌룬 페스티벌 — 난이도 선택 팝업 (구현 명세서 §3-3·§5-1 #1, 프리팹 UIPopupEventDreamBalloon_Select)
// 난이도 카드(쉬움/보통/어려움) 선택 → 시작 버튼 → RequestStart → main 팝업. 미선택 시 시작 비활성.
// 카드별 최종보상 미리보기 + 선택 연출은 UIDreamBalloonDifficultyButton 이 담당(ShoppingRoad UIPopupEventShoppingRoadStart 참고).
public class UIPopupEventDreamBalloonSelect : UIBasePopup
{
    public const string PREFAB_PATH = "UIPopupEventDreamBalloon_Select";

    // 풍선 로딩커튼(아트 952860690 §3) — 난이도 확정 후 커튼이 화면을 덮은 뒤 메인으로 전환한다.
    // 전환 시점 = 커튼 클립에 아트가 심어 둔 Animation Event(OnCurtainFinish) 수신 시점 — 아트가 이벤트 위치로 타이밍을 제어한다.
    // 클립 길이 조회는 이벤트 유실(아트 재발행 등) 대비 폴백 전용.
    private const string CURTAIN_CLIP_NAME = "DreamBalloonFestival_Finish_Balloon";
    private const float CURTAIN_EVENT_FALLBACK_MARGIN_SEC = 1f;   // 이벤트 미수신 폴백 = 클립 길이 + 여유
    // 난이도 값은 <b>계약 단일 소스</b>(EventDreamBalloonHelper)에서 가져온다 — 이 파일에 숫자를 다시 적지 않는다.
    //  이름을 남기는 이유는 가독성이다. 이 팝업의 언어는 "선택/미선택" 이라 그 말로 읽히는 편이 낫고,
    //  값은 파생이라 계약이 바뀌면 여기도 따라 바뀐다.
    private const int DEFAULT_DIFFICULTY = EventDreamBalloonHelper.DIFFICULTY_EASY;   // 오픈 시 기본 선택 = 쉬움(§4-1, ShoppingRoad 방식)
    private const int NOT_SELECTED = EventDreamBalloonHelper.DIFFICULTY_UNSPECIFIED;  // 미선택 = 미지정

    // 배열 순서는 프리팹 저작 순서다(실측: [0]=Easy · [1]=Normal · [2]=Hard).
    //  ⚠️ 각 카드가 들고 있는 값은 프리팹의 [SerializeField] difficulty 이고 <b>순서와 무관</b>하다 —
    //     실측 1 / 0 / 2 다. 개정 전 주석은 (1)/(2)/(3) 으로 적었는데 그것은 2026-07-20 이전의
    //     구 패킷 인코딩(1쉬움/2보통/3어려움) 잔재였다.
    [SerializeField] private UIDreamBalloonDifficultyButton[] difficultyCards;
    [SerializeField] private UILiveEventTimer eventTimer;   // 이벤트 종료까지 남은 시간(§3-3 ③)
    [SerializeField] private Animator curtainAnimator;   // 풍선 로딩커튼(중첩 EventDreamBalloon_Loading_Curtain)의 Animator — 폴백용 클립 길이 조회
    [SerializeField] private DreamBalloonCurtainEventRelay curtainEventRelay;   // 커튼 Animation Event(OnCurtainFinish) 수신기 — 같은 GO 의 Animator 가 호출
    [SerializeField] private GameObject imgBackground;   // 배경 딤(ImgBackground) — 커튼 전환 시점에 꺼서 아래에 열리는 메인이 가리지 않게

    // ⚠️ 난이도 인코딩 = **1쉬움 / 0보통 / 2어려움 / -1미지정**(패킷·테이블 공통, 2026-09-04 서버·클라 통일).
    //    `0` 이 보통이라 미선택 센티널로 쓸 수 없어 -1 을 쓴다. 그 -1 은 이 팝업의 로컬 약속이 아니라
    //    <b>서버와 공유하는 계약값</b>이다(EventDreamBalloonHelper.DIFFICULTY_UNSPECIFIED).
    private int selectedDifficulty = NOT_SELECTED;
    private bool startRequesting;     // 서버 응답 대기 중 중복 클릭 방지(claim 4)

    public class Info : IUIInfoData
    {
        public ContentEventDreamBalloon.DreamBalloonModel model;
    }

    protected override void OnEnable()
    {
        base.OnEnable();

        if (Stateful.HasButton(ButtonRole.BtnStart))
            Stateful.AddButtonListener(ButtonRole.BtnStart, OnClickStart);
        if (Stateful.HasButton(ButtonRole.BtnConfirm))
            Stateful.AddButtonListener(ButtonRole.BtnConfirm, OnClickStart);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.AddButtonListener(ButtonRole.Close, Close);

        BindDifficultyCards();
    }

    protected override void OnDisable()
    {
        if (Stateful.HasButton(ButtonRole.BtnStart))
            Stateful.RemoveButtonListener(ButtonRole.BtnStart, OnClickStart);
        if (Stateful.HasButton(ButtonRole.BtnConfirm))
            Stateful.RemoveButtonListener(ButtonRole.BtnConfirm, OnClickStart);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.RemoveButtonListener(ButtonRole.Close, Close);

        base.OnDisable();
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);

        EventDreamBalloonHelper.BindEventTimer(eventTimer);   // 남은 시간(§3-3 ③)

        RefreshDifficultyCards();      // 카드별 최종보상 미리보기 채우기 + 기본 상태

        // 기본 난이도 = 쉬움(§4-1, ShoppingRoad 방식) — 오픈 시 쉬움 카드를 선택(SetOn) 상태로 두고 시작 버튼 활성.
        // 사용자가 다른 난이도를 고르면 OnSelectDifficulty(ChangeOn 전이 연출)로 교체된다.
        selectedDifficulty = DEFAULT_DIFFICULTY;
        GetCard(selectedDifficulty)?.SetOn();
        SetStartInteractable(true);
    }

    // 각 난이도 카드에 선택 콜백 연결(§3-3). 카드 자체 버튼 구독은 카드 컴포넌트가 담당.
    private void BindDifficultyCards()
    {
        if (difficultyCards.IsNullOrEmpty())
        {
            return;
        }

        foreach (UIDreamBalloonDifficultyButton card in difficultyCards)
        {
            if (null != card)
            {
                card.SetButtonAction(OnSelectDifficulty);
            }
        }
    }

    // 카드별 최종보상 미리보기 갱신 + 기본(미선택) 상태 초기화.
    private void RefreshDifficultyCards()
    {
        if (difficultyCards.IsNullOrEmpty())
        {
            return;
        }

        foreach (UIDreamBalloonDifficultyButton card in difficultyCards)
        {
            if (null != card)
            {
                card.SetInfo();
            }
        }
    }

    // 난이도 카드 선택(§3-3) — 이전 선택 카드만 off, 새 카드 on (ShoppingRoad 방식).
    // ※ 미선택(default) 카드에 off 트리거를 남기면, 이후 그 카드를 선택할 때 소비되지 않은 pending off 가
    //    on→off 전이를 즉시 발동해 되뒤집는 버그가 생긴다. 그래서 '이전 선택 카드'만 off 한다.
    public void OnSelectDifficulty(int difficulty)
    {
        if (selectedDifficulty == difficulty)
        {
            return;   // 동일 난이도 재클릭 무시
        }

        GetCard(selectedDifficulty)?.ChangeOff();   // 이전 선택만 off (없으면 null → no-op)

        selectedDifficulty = difficulty;
        SetStartInteractable(true);                 // 선택됨 → 시작 버튼 활성화(§3-3)

        GetCard(selectedDifficulty)?.ChangeOn();
    }

    // 지정 난이도의 카드 컴포넌트 조회. 미선택(NOT_SELECTED)·미바인딩 시 null.
    // ⚠️ `difficulty <= 0` 으로 거르면 **보통(0)** 카드가 통째로 조회되지 않는다(선택 연출 미재생).
    private UIDreamBalloonDifficultyButton GetCard(int difficulty)
    {
        if (NOT_SELECTED == difficulty || difficultyCards.IsNullOrEmpty())
        {
            return null;
        }

        foreach (UIDreamBalloonDifficultyButton card in difficultyCards)
        {
            if (null != card && card.IsSameDifficulty(difficulty))
            {
                return card;
            }
        }

        return null;
    }

    // 시작/확인 버튼 활성 토글(§3-3 미선택 시 비활성). 버튼 role 미바인딩 시 no-op.
    private void SetStartInteractable(bool interactable)
    {
        if (Stateful.HasButton(ButtonRole.BtnStart))
        {
            Stateful.GetButton(ButtonRole.BtnStart).Button.interactable = interactable;
        }
        if (Stateful.HasButton(ButtonRole.BtnConfirm))
        {
            Stateful.GetButton(ButtonRole.BtnConfirm).Button.interactable = interactable;
        }
    }

    private void OnClickStart()
    {
        if (NOT_SELECTED == selectedDifficulty)
        {
            // 미선택 시 시작 불가(§3-3 시작 버튼 비활성). 방어.
            // ⚠️ `<= 0` 으로 거르면 **보통(0)** 선택이 미선택으로 오판돼 시작 자체가 막힌다.
            return;
        }

        if (startRequesting)
        {
            return;   // 응답 대기 중 중복 클릭 방지
        }

        // 메인 팝업은 서버 확정 후에 연다 — 응답 전에 열면 미시작(난이도 0/state 0) 데이터로 그려진다(claim 4).
        // 실패 시에는 난이도 선택 화면을 유지해 재시도할 수 있게 한다.
        startRequesting = true;
        EventDreamBalloonHelper.GetContent()?.RequestStart(
            selectedDifficulty,
            () => PlayCurtainThenEnterAsync().Forget(),
            () => startRequesting = false);
    }

    // 난이도 확정(서버 성공) → 풍선 로딩커튼(State LoadingCurtain) 재생 → 커튼 클립의 Animation Event(OnCurtainFinish)
    // 수신 시점에 메인 팝업으로 전환(아트 952860690 §3 — 전환 타이밍은 아트가 클립 이벤트 위치로 제어, 현재 2초 지점).
    // 선택 팝업 종료(Close)는 **커튼 클립이 끝난 뒤** — 클립 후반(커튼 걷힘)까지 커튼을 보유한 이 팝업이 화면을 유지해야 한다.
    // 커튼 State 미발행이면 기존과 동일하게 즉시 전환·종료. 팝업 파괴 시 전환은 취소된다(이벤트 종료 등 예외 상황).
    private async UniTaskVoid PlayCurtainThenEnterAsync()
    {
        float closeDelaySec = 0f;
        if (Stateful.HasState((int)StateRole.LoadingCurtain))
        {
            Stateful.SetState((int)StateRole.LoadingCurtain);
            float curtainStartTime = Time.time;

            bool entered = await WaitCurtainFinishAsync();
            if (!entered)
            {
                return;   // 팝업 파괴 — 전환하지 않는다(데이터 조작 없음)
            }

            closeDelaySec = ResolveCurtainClipLength() - (Time.time - curtainStartTime);   // 클립 잔여 시간만큼 Close 유예
        }

        // 배경 딤 제거(사용자 요구) — 커튼 아래(한 칸 밑 형제)에서 열리는 메인이 이 팝업의 딤에 가리지 않게.
        imgBackground.SetActive(false);

        UIBase mainPopup = await EventDreamBalloonHelper.OpenMainPopupAsync();
        PlaceBelowSelf(mainPopup);   // 메인을 이 팝업 바로 아래 형제 순서로 — 커튼이 클립 종료까지 메인 위에 보이도록(사용자 요구)

        if (closeDelaySec > 0f)
        {
            try
            {
                await UniTask.Delay(TimeSpan.FromSeconds(closeDelaySec), cancellationToken: gameObject.GetCancellationTokenOnDestroy());
            }
            catch (OperationCanceledException)
            {
                return;   // 팝업 파괴 — Close 불필요
            }
        }

        Close();
    }

    // 커튼 전환 시점 대기 — 클립 Animation Event(OnCurtainFinish) 수신 시 완료.
    // 릴레이 미바인딩 프리팹은 기존 방식(클립 길이 대기)으로 폴백하고, 릴레이가 있어도 이벤트가 유실된 경우
    // (아트 클립 재발행 등) 클립 길이 + 여유 시간 후 진행해 화면이 멈추지 않게 한다. 파괴 시 false.
    private async UniTask<bool> WaitCurtainFinishAsync()
    {
        CancellationToken ct = gameObject.GetCancellationTokenOnDestroy();
        float clipSec = ResolveCurtainClipLength();

        try
        {
            if (null == curtainEventRelay)
            {
                if (clipSec > 0f)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(clipSec), cancellationToken: ct);
                }

                return true;
            }

            var finished = new UniTaskCompletionSource();
            Action onFinish = () => finished.TrySetResult();
            curtainEventRelay.Finished += onFinish;
            try
            {
                await UniTask.WhenAny(
                    finished.Task.AttachExternalCancellation(ct),
                    UniTask.Delay(TimeSpan.FromSeconds(clipSec + CURTAIN_EVENT_FALLBACK_MARGIN_SEC), cancellationToken: ct));
            }
            finally
            {
                curtainEventRelay.Finished -= onFinish;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // 새로 연 팝업을 이 팝업 바로 아래 형제 순서로 배치 — 같은 레이어(부모)일 때만(다르면 형제 정렬 무의미라 스킵).
    // 커튼(이 팝업 보유)이 클립이 끝날 때까지 메인 팝업 위에 그려지게 한다.
    private void PlaceBelowSelf(UIBase popup)
    {
        if (null == popup)
        {
            return;
        }

        Transform popupTrans = popup.transform;
        if (popupTrans.parent != transform.parent)
        {
            return;
        }

        popupTrans.SetSiblingIndex(transform.GetSiblingIndex());
    }

    // 커튼 클립 실제 길이(초) — **폴백 전용**(전환 시점의 원천은 클립 Animation Event). 애니 미바인딩/클립 미존재 시 0.
    private float ResolveCurtainClipLength()
    {
        RuntimeAnimatorController controller = null != curtainAnimator ? curtainAnimator.runtimeAnimatorController : null;
        if (null == controller)
        {
            return 0f;
        }

        AnimationClip[] clips = controller.animationClips;
        int count = clips.Length;
        for (int i = 0; i < count; i++)
        {
            if (clips[i].name == CURTAIN_CLIP_NAME)
            {
                return clips[i].length;
            }
        }

        return 0f;
    }
}

using ACTGames.Content.Helper;

using GameCore.Utils;

using GameLogic.Define;

using UnityEngine;

// 드림 벌룬 — 머지 진입 버튼(MergeEventBalloonItem)의 상태별 표시 (구현 명세서 §3-8 · §7-3).
//
// | state          | 표시                                                        |
// | 미시작(0)·쉬는중(1) | 쉬는중 레이아웃 — 쉬는중 타이틀(Rest)만 추가(게이지·좌석 숨김)      |
// | 진행중(2)        | 진행중 레이아웃 — 코인 게이지 + 「남은 좌석」 + 진행중 타이틀. 목표 도달 시 완료 버튼(Btn_Ok)도 노출(구 레드닷 대체, ISSUE-21) |
// | 클리어(3)        | **완료 레이아웃(Btn_Ok)** — 클릭 시 최종 연출 → 최종 보상 팝업(§5-1 #5) |
// | 종료(4)         | **버튼 전체 숨김** — 이벤트가 끝났다                              |
//
// (기획 §3-2 「2) 버튼」 — 머지 버튼은 진행중·쉬는중 모두 "하단에 종료까지 남은 시간 표시", 재화 게이지는 진행중 전용)
//
// ⚠️ 남은 기간 타이머(`Timer`)는 **상시 노출**(어느 배열에도 넣지 않는다, ISSUE-37) — 공용 네비(`TimerObject`=UITimerSimple)가
//    구동하는 유일하게 동작하는 타이머다. 프리팹의 `Timer_rest` 는 텍스트 자식·바인딩이 없는 빈 껍데기(아트 미완성)라
//    켜도 아무것도 그려지지 않는다 → 쉬는중 전용 타이머가 필요해지면 아트가 Timer_rest 를 완성한 뒤 restOnly 에 재등록할 것.
//
// ⚠️ **클리어(3)에서 버튼을 내리면 안 된다.** 최종 라운드 성공은 머지판(화면 밖)에서 확정되고, 유저는 이 버튼으로
//    다시 들어와야 최종 연출과 보상 팝업을 볼 수 있다. 완료를 알리되 진입은 열어 두는 것이 `Btn_Ok` 레이아웃이다.
// ⚠️ 공용 완료 판정(`LiveEventData.IsCompleteEvent`)에 기대지 않는다 — 드림벌룬은 보상 행 10개 중 실제 아이템이
//    5·10 라운드에만 있어(§2-2) 그 판정이 참이 되지 않는다. **완료 표시의 단일 소스는 서버 `EventBalloonInfo.state`**(§7-1a).
//
// 좌석 값의 단일 소스는 클라 좌석 시뮬(SeatChangedMsg, §7-3).
// 아이콘 스프라이트 교체는 공용 경로(OnRefreshEventIconMsg → UILiveEventItem)가 담당한다.
// 완료 버튼(Btn_Ok)의 단일 소유자다 — 클리어(3) + 진행중 목표 도달(구 레드닷 조건)에 노출한다(ISSUE-21).
//  머지판 레드닷(MergeCheck) 자체는 공용 UILiveEventItem.RefreshReddot 의 DREAMBALLOON 분기에서 상시 OFF 처리한다
//  (레드닷 억제는 그 컴포넌트에서만 가능 — 로비 레드닷은 건드리지 않기 위해 공용 base 가 아니라 머지 아이템에서 분기).
public class MergeDreamBalloonSeatView : MonoBehaviour
{
    [SerializeField] private GameObject contentRoot;   // 버튼 표시 전체(ScaleBase) — 종료(4) 시 통째로 숨긴다
    [SerializeField] private GameObject seatRoot;   // 좌석 표시 루트(PersonnelText) — 상태에 따라 토글
    [SerializeField] private UITextEx seatText;     // 좌석 수 텍스트 (프리팹 authored "33" 은 더미값)

    [SerializeField] private GameObject[] progressOnly;   // 진행중에만 노출 — 코인 게이지(SliderBase)·타이틀(TitleImage). 타이머는 상시(ISSUE-37)
    [SerializeField] private GameObject[] restOnly;       // 쉬는중에만 노출 — 쉬는중 타이틀(Rest). Timer_rest 는 빈 껍데기라 미등록(ISSUE-37)
    [SerializeField] private GameObject[] completeOnly;   // 클리어에만 노출 — 완료 표시(Btn_Ok, 아트 authored)

    private void Start()
    {
        Message.AddListener<SeatChangedMsg>(OnSeatChanged);
        Message.AddListener<OnRefreshEventIconMsg>(OnRefreshEventIcon);
        // ISSUE-21: 목표 코인 도달(=구 레드닷 조건)은 재화 갱신 시점에 성립하므로 재화 갱신도 구독해 Btn_Ok 를 갱신한다.
        Message.AddListener<OnRefreshEventCurrencyMsg>(OnRefreshEventCurrency);

        Refresh(GetRemainSeat());
    }

    private void OnDestroy()
    {
        Message.RemoveListener<SeatChangedMsg>(OnSeatChanged);
        Message.RemoveListener<OnRefreshEventIconMsg>(OnRefreshEventIcon);
        Message.RemoveListener<OnRefreshEventCurrencyMsg>(OnRefreshEventCurrency);
    }

    // 좌석 감소(시뮬은 1석씩 발신) — 값만 갱신한다.
    private void OnSeatChanged(SeatChangedMsg msg)
    {
        Refresh(msg.remainSeat);
    }

    // 진행 상태 전이(진행중 ↔ 쉬는중 등)는 아이콘 갱신 메시지로 전파된다 → 노출 여부 재판정.
    private void OnRefreshEventIcon(OnRefreshEventIconMsg msg)
    {
        if (null == msg || msg.eventType != LiveEventType.DREAMBALLOON)
        {
            return;
        }

        Refresh(GetRemainSeat());
    }

    // 재화 갱신(=현재 라운드 목표 코인 도달 판정 시점)마다 완료 버튼(Btn_Ok) 노출을 재평가한다.
    // 구 레드닷과 동일한 cadence 로 갱신해, "레드닷 대신 Btn_Ok" 전환이 지연 없이 반영되도록 한다(ISSUE-21).
    private void OnRefreshEventCurrency(OnRefreshEventCurrencyMsg msg)
    {
        if (null == msg || msg.eventType != LiveEventType.DREAMBALLOON)
        {
            return;
        }

        Refresh(GetRemainSeat());
    }

    private void Refresh(int remainSeat)
    {
        // 종료(4) — 버튼 전체를 내린다. 이후 상태 전이가 없으므로 이 프레임 이후 갱신할 것도 없다.
        // contentRoot 는 미바인딩 가능성이 있어 가드한다(프리팹 수기 필드가 리임포트에 유실되는 사례 실측).
        if (EventDreamBalloonHelper.IsEnded())
        {
            if (null != contentRoot)
            {
                contentRoot.SetActive(false);
            }
            return;
        }

        if (null != contentRoot)
        {
            contentRoot.SetActive(true);
        }

        bool cleared = EventDreamBalloonHelper.IsCleared();          // 이벤트 완주 — 완료 표시(진입은 열어 둔다)
        bool progressing = EventDreamBalloonHelper.IsRoundProgressing();   // 2

        // 완료 버튼(Btn_Ok) 노출: 완주(최종 보상 진입) 또는 **진급 대기**(진행중 목표 도달 / 결과 확인 대기).
        //  진급 대기는 IsActiveReddot(= IsRoundGoalReached || IsRoundClearPending, 기획 §3-2)이 그대로 판정한다(ISSUE-21).
        //  ⚠️ **라운드 클리어(state 3) '자체'로는 켜지 않는다** — 강제 종료 후 재접속 등으로 결과가 이미 정착(확인할 연출 없음)됐으면
        //     '완료'가 아니라 '다음 라운드 대기(쉬는중)'다 → 쉬는 레이아웃으로 둔다. 확인할 연출이 남아 있으면
        //     IsRoundClearPending 이 위 IsActiveReddot 로 완료 버튼을 켜 준다(ISSUE-29 후속 — 팝업 파트너/게이지 처리와 동일 원칙).
        bool showComplete = cleared || EventButtonUIHelper.IsActiveReddot(LiveEventType.DREAMBALLOON);

        // 진행 레이아웃(남은 자리 + 재화 게이지)은 **결과가 아직 확정되지 않은 진짜 진행중**에만 노출한다.
        //  기획(Confluence 920223827) — 실패: "이벤트 진행 아이콘에 레드닷 출력 (**남은 자리는 출력 되지 않음**)".
        //
        //  ⚠️ 서버 state(IsRoundProgressing) 만으로 판단하면 안 된다. 라운드 결과 전송이 **메인 팝업 진입 시점까지
        //     보류**되므로(ContentEventDreamBalloon.ResolveRoundResult 의 isOnEvent 게이트), 실패·성공을 로컬로
        //     확정한 뒤에도 서버 state 는 진행중(2)으로 남는다 → 좌석·게이지가 그대로 켜져 있게 된다.
        //     종전에는 결과를 즉시 커밋해 state 가 바뀌면서 자연히 꺼졌던 부분이라, 보류 도입으로 드러난 구멍이다.
        //  · 실패 대기 → IsRoundFailPending
        //  · 성공/완주 대기 → showComplete(완료 버튼 레이아웃으로 전환되므로 진행 표시와 공존하지 않는다)
        bool failPending = EventDreamBalloonHelper.IsRoundFailPending();
        bool showProgress = progressing && !failPending && !showComplete;

        seatRoot.SetActive(showProgress);
        if (showProgress)
        {
            seatText.SetText($"{remainSeat}");
        }

        SetActiveAll(progressOnly, showProgress);
        // 쉬는 레이아웃: 진행중·완료(완주/진급 대기) 어디에도 안 걸리는 대기 상태 = 미시작·쉬는중 + **정착된 라운드 클리어(state 3)**.
        SetActiveAll(restOnly, !progressing && !showComplete);
        SetActiveAll(completeOnly, showComplete);
    }

    private void SetActiveAll(GameObject[] targets, bool active)
    {
        int count = targets.Length;
        for (int i = 0; i < count; i++)
        {
            if (null != targets[i])
            {
                targets[i].SetActive(active);
            }
        }
    }

    // 좌석 시뮬 미구동(미시작·쉬는중 등)이면 0 — 이 경우 표시 자체가 숨겨진다.
    private int GetRemainSeat()
    {
        return EventDreamBalloonHelper.GetContent()?.RemainSeat ?? 0;
    }
}

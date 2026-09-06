using System;
using System.Collections.Generic;
using System.Threading;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using UnityEngine;
using UnityEngine.UI;

// 드림 벌룬 — 스테이지(구름) 트랙 (구현 명세서 §7-2).
// 표준 ScrollRect 기반 세로 트랙. 단계 수(≤10)가 적어 가상화 없이 전량 셀을 생성한다(풀링 없음).
//   - 셀 프리팹(Item) = EventDreamBalloon_Stage (DreamBalloonStageCell 보유, index 홀짝으로 좌우 구름 배치)
//   - 셀 바인딩: Instantiate → SendMessage("SetInfo", DreamBalloonStageData)
//   - 스크롤 이동("카메라 이동") = verticalNormalizedPosition 조정(즉시/DOTween)
//   - Content 는 VerticalLayoutGroup + ContentSizeFitter 로 셀을 세로 배치(표준 ScrollRect 구성).
// ※ 기존 LoopVerticalScrollRect(가상화) 대응 구조를 걷어내고 일반 ScrollRect 로 전환한 버전.

// 단계(구름) 1건 데이터.
public class DreamBalloonStageData
{
    public int index;        // 0-base 배열 인덱스 (좌우 배치 기준: 짝수 Left / 홀수 Right, 기획 "이벤트 단계 배치 예시")
    public int round;        // 구름(단계) 1~totalRound
    public int goalCoin;     // 목표 코인(난이도별 §2-2)
    public int curCoin;      // 현재 획득 코인
    public int state;        // 0: 예정(잠김) / 1: 현재 진행 / 2: 성공(별 녹색+깃발)
    public bool isFinal;     // 최종 단계 구름
    // 이번 배치가 구름 등장(In) 재생 대상인지 — 이벤트 최초 시작 1회(ISSUE-13). 트랙 컨트롤러(SetStages)가 채운다.
    // 셀은 이 값이 true 면 게이지 상태 적용을 등장 애니가 끝난 뒤로 미룬다(등장을 덮어쓰지 않도록).
    public bool cloudIntro;
}

public class DreamBalloonStageLoopScroll : MonoBehaviour
{
    // [ISSUE-34] Content VLG 상/하 여백 고정값(기획 지정: 상단 200 / 하단 130).
    // 기존에는 셀 높이 비례(Top=셀높이 / Bottom=셀높이/2)로 잡았으나, 셀 프리팹 높이가 바뀌면
    // 여백이 함께 흔들려 상/하단 노출 영역을 기획 의도대로 고정할 수 없었다.
    private const int CONTENT_PADDING_TOP = 200;
    private const int CONTENT_PADDING_BOTTOM = 130;

    [SerializeField] private ScrollRect scrollRect;   // Scroll View 의 일반 ScrollRect
    [SerializeField] private RectTransform content;   // 셀이 배치되는 Content (VerticalLayoutGroup + ContentSizeFitter)
    [SerializeField] private GameObject item;         // 셀 프리팹(EventDreamBalloon_Stage)

    private readonly List<DreamBalloonStageData> dataList = new();
    private readonly List<GameObject> cells = new();
    private readonly List<DreamBalloonStageCell> cellDispatchers = new();   // 셀 루트 디스패처(파트너 앵커 조회용)
    private Tween scrollTween;
    private RectTransform balloonAnchor;   // 열기구 — 현재 라운드 셀을 이 화면 위치에 정렬(§7-2)
    // 열기구가 idle 에 구름 자식으로 SetParent(스크롤 추종)되면 라이브 위치가 변해 정렬 기준으로 못 쓴다.
    // → SetBalloonAnchor 시점(열기구 홈)에 월드 위치를 1회 캐싱해 정렬 기준으로 고정한다.
    private Vector3 cachedBalloonWorldPos;
    private bool hasBalloonWorldPos;
    private float cellHeight;   // 셀(구름) 1칸 높이 = 라운드 길이(ISSUE-32 최종 구름 하강 비례 기준 · AlignFinalCloud). BuildCells 에서 실측 캐싱.
    private VerticalLayoutGroup layoutGroup;   // Content VLG(Awake 캐싱) — 상/하 패딩을 고정값으로 설정(ApplyContentPadding).
    private Action scrollAction;   // 스크롤 위치가 바뀔 때마다 호출(파트너 위치 안내 프로필 갱신 §3-2). 드래그·코드 스크롤 공통.

    /// <summary>
    /// 해당 단계(1-base) 구름의 Transform — 파트너/친구 캐릭터를 SetParent 할 앵커(§3-2).
    /// 좌우 지그재그 중 **실제 노출된 구름**을 돌려준다. 셀 미생성/범위 밖이면 null.
    /// </summary>
    public Transform GetStageAnchor(int round)
    {
        DreamBalloonStageCell cell = GetCell(round);
        return null != cell ? cell.ActiveAirBalloonAnchor : null;
    }

    /// <summary>구름 위 파트너 기준 위치(§3-2 우) — 파트너를 SetParent 할 앵커. 셀 미생성/범위 밖이면 null.</summary>
    public Transform GetPartnerAnchor(int round)
    {
        DreamBalloonStageCell cell = GetCell(round);
        return null != cell ? cell.ActivePartnerAnchor : null;
    }

    /// <summary>해당 단계의 실제 노출된 구름 뷰(경쟁자 모집 포트레이트 연출 §4-3 대상). 셀 미생성/범위 밖이면 null.</summary>
    public DreamBalloonStageItem GetStageItem(int round)
    {
        DreamBalloonStageCell cell = GetCell(round);
        return null != cell ? cell.ActiveItem : null;
    }

    /// <summary>셀(구름) 1칸 높이 = 라운드 길이(ISSUE-32 최종 구름 하강 비례 기준). 셀 미생성 시 0.</summary>
    public float CellHeight => cellHeight;

    /// <summary>
    /// 스크롤 위치 변경 콜백 등록 — 파트너가 화면 밖으로 나갔는지 재판정하는 용도(기획 §3-2 "파트너의 위치 안내 표시").
    /// 쇼핑 로드(<c>UIShoppingRoadLoopScroll.SetScrollAction</c>)와 같은 계약이다.
    /// 드래그(onValueChanged)뿐 아니라 **코드 스크롤**(연출의 content 직접 이동)에서도 발화한다 —
    /// 연출이 트랙을 옮긴 뒤에도 안내 표시가 최신이어야 하기 때문. 매 프레임 올 수 있으므로 수신부에서 상태 변화 시에만 반영할 것.
    /// </summary>
    public void SetScrollAction(Action action)
    {
        scrollAction = action;
    }

    public void RemoveScrollAction()
    {
        scrollAction = null;
    }

    /// <summary>
    /// 해당 단계 구름이 뷰포트 **위쪽 밖**에 있는지(= 위로 스크롤해야 보임). 셀 미생성/미바인딩이면 false.
    /// 기준점은 파트너 앵커(없으면 구름 앵커) — 안내 대상이 "파트너의 위치"이기 때문(§3-2).
    /// </summary>
    public bool IsStageAboveViewport(int round)
    {
        return TryGetStageViewportY(round, out float localY, out Rect viewRect) && localY > viewRect.yMax;
    }

    /// <summary>해당 단계 구름이 뷰포트 **아래쪽 밖**에 있는지(= 아래로 스크롤해야 보임). 셀 미생성/미바인딩이면 false.</summary>
    public bool IsStageBelowViewport(int round)
    {
        return TryGetStageViewportY(round, out float localY, out Rect viewRect) && localY < viewRect.yMin;
    }

    // 해당 단계 앵커의 뷰포트 로컬 Y 와 뷰포트 Rect. rect 로 비교하므로 뷰포트 pivot 에 무관하다.
    private bool TryGetStageViewportY(int round, out float localY, out Rect viewRect)
    {
        localY = 0f;
        viewRect = default;

        RectTransform viewport = null == scrollRect ? null : scrollRect.viewport;
        if (null == viewport)
        {
            return false;
        }

        Transform anchor = GetPartnerAnchor(round) ?? GetStageAnchor(round);
        if (null == anchor)
        {
            return false;
        }

        localY = viewport.InverseTransformPoint(anchor.position).y;
        viewRect = viewport.rect;
        return true;
    }

    /// <summary>모든 구름 셀의 등장(In) 애니를 건너뛰고 정착 상태로 스냅한다(ISSUE-13 — 이벤트 최초 시작이 아닐 때 트랙 컨트롤러가 호출).</summary>
    public void SnapAllCloudsToIdle()
    {
        int count = cellDispatchers.Count;
        for (int i = 0; i < count; i++)
        {
            cellDispatchers[i]?.ActiveItem?.SnapCloudToIdle();
        }
    }

    private DreamBalloonStageCell GetCell(int round)
    {
        int index = round - 1;
        if (index < 0 || index >= cellDispatchers.Count)
        {
            return null;
        }

        return cellDispatchers[index];
    }

    private void Awake()
    {
        if (null == scrollRect)
        {
            scrollRect = GetComponent<ScrollRect>();
        }

        if (null == content && null != scrollRect)
        {
            content = scrollRect.content;
        }

        if (null != content)
        {
            layoutGroup = content.GetComponent<VerticalLayoutGroup>();   // 상/하 패딩을 셀 높이 비례로 설정(ApplyContentPadding)
        }

        // 평상시 유저 드래그 허용(Interactable = true). 성공/실패 등 연출 재생 동안에만 잠근다
        // (DreamBalloonRoadController.OnPlayAsync 에서 SetInteractable(false) → OnCleanup 에서 복원).
        // ScrollRect.enabled 토글은 드래그/휠 입력만 막고, 코드 스크롤(content.DOAnchorPosY /
        // verticalNormalizedPosition setter)은 enabled 와 무관하게 동작한다.
        SetInteractable(true);

        // 드래그 경계(기획 "이벤트 화면 스크롤"): ScrollRect 는 Clamped(프리팹 m_MovementType:2)라 콘텐츠가 뷰포트를 덮는
        // 범위 안에서만 스크롤된다(상단 빈 공간 최소화 — 사용자 선택). 코드 스크롤도 동일 범위(ClampContentY)로 맞춰 연출 중/후 튐을 없앤다.
        // 상·하단 여백은 Content VLG 패딩(Top=셀높이 / Bottom=셀높이/2)으로 준다.
        if (null != scrollRect)
        {
            scrollRect.onValueChanged.AddListener(OnScrollValueChanged);
        }
    }

    private void OnDestroy()
    {
        if (null != scrollRect)
        {
            scrollRect.onValueChanged.RemoveListener(OnScrollValueChanged);
        }

        scrollAction = null;
    }

    /// <summary>
    /// 스크롤뷰 드래그 입력 허용/차단(Interactable). 연출(성공/실패/최종/모집) 재생 동안 false 로 잠근다(§7-2).
    /// enabled=false 는 드래그/휠(IDragHandler·IScrollHandler)만 막고, 코드 스크롤은 그대로 동작한다.
    /// </summary>
    public void SetInteractable(bool interactable)
    {
        if (null != scrollRect)
        {
            scrollRect.enabled = interactable;
        }
    }

    // 유저 드래그로 스크롤될 때만 호출된다(연출의 코드 스크롤은 content 를 직접 이동하므로 onValueChanged 미발화).
    private void OnScrollValueChanged(Vector2 _)
    {
        ClampContentToBounds();
        scrollAction?.Invoke();
    }

    /// <summary>
    /// content.anchoredPosition.y 를 [1단계 정렬 위치 ~ 최종단계 정렬 위치] 안으로 제한한다(기획 "이벤트 화면 스크롤" 경계 처리).
    /// 최하단 = 1단계가 열기구에 정렬된 위치(1단계 보임) / 최상단 = 최종단계가 열기구에 정렬된 위치(축제장과 미겹침).
    /// 좌표 산출은 enabled=false 로 content 를 고정하던 시점과 동일한 정렬 함수(<see cref="TryGetTargetContentY"/>)를 재사용한다.
    /// </summary>
    /// <summary>
    /// content.anchoredPosition.y 를 [하단 한계 ~ 상단 한계] 안으로 제한한다(기획 "이벤트 화면 스크롤" 경계 처리).
    ///  - 상단 한계 = 최종단계가 열기구에 정렬된 위치(<see cref="TryGetTargetContentY"/>) — 축제장과 미겹침 여백 유지(기획 최상단).
    ///  - 하단 한계 = **1단계를 뷰포트 하단에서 bottomY 만큼 띄운 위치**(아래 여백 확보, 기획 최하단 "1단계 보임").
    ///    Content 는 뷰포트 하단 앵커 + bottom pivot(0.5,0) + ReverseArrangement 라, anchoredPosition.y = 0 이
    ///    하단 셀(1단계)을 뷰포트 하단에 붙이는 지점이고, 양수만큼 올릴수록 그만큼 아래 여백이 된다.
    /// </summary>
    private void ClampContentToBounds()
    {
        if (null == content)
        {
            return;
        }

        // ScrollRect Clamped 가 드래그를 이미 경계로 제한하지만, 코드 스크롤과 동일 기준(ClampContentY)으로 한 번 더 맞춰
        // 드래그/연출 두 경로의 경계를 일치시킨다. 상·하단 여백은 Content VLG 패딩(Top=셀높이 / Bottom=셀높이/2)이 담당한다.
        float y = content.anchoredPosition.y;
        float clamped = ClampContentY(y);
        if (!Mathf.Approximately(y, clamped))
        {
            SetContentY(clamped);
        }
    }

    // 열기구 정렬 기준을 지정 — 스크롤이 현재 라운드 셀을 이 화면 위치에 맞춘다. null 이면 선형 폴백.
    // ⚠️ 호출 시점의 열기구는 **홈(오버레이) 위치**여야 한다(RoadController 가 ResetBalloonHomeY 로 보장).
    //    그 월드 위치를 캐싱해, 이후 열기구가 구름 자식으로 스크롤을 따라 움직여도 정렬 기준은 고정 유지한다.
    public void SetBalloonAnchor(RectTransform anchor)
    {
        balloonAnchor = anchor;
        if (null != anchor)
        {
            cachedBalloonWorldPos = anchor.position;
            hasBalloonWorldPos = true;
        }
    }

    // 정렬 기준 열기구 월드 위치 — 캐싱값 우선(구름 자식화 이후에도 고정), 미캐싱 시 라이브 폴백.
    private Vector3 BalloonAlignWorldPos => hasBalloonWorldPos ? cachedBalloonWorldPos : (null != balloonAnchor ? balloonAnchor.position : Vector3.zero);

    // 스테이지 데이터 세팅 + 셀 전량 생성 + 현재 단계 위치로 즉시 배치.
    public void SetStages(List<DreamBalloonStageData> stages, int currentRound)
    {
        dataList.Clear();
        if (null != stages)
        {
            dataList.AddRange(stages);
        }

        BuildCells();
        ScrollToStage(currentRound);
    }

    /// <summary>
    /// 값만 갱신 — 셀을 재생성하지 않고 기존 셀에 데이터를 재주입한다(재화 획득 시 게이지 실시간 갱신, §3-4 ②).
    ///
    /// ⚠️ 값 변동에 <see cref="SetStages"/> 를 쓰면 안 된다. 셀 전량 재생성은 ① 구름 등장(In) 애니를 다시 재생시키고
    /// ② 셀에 SetParent 된 파트너 GO 까지 함께 파괴하며(§3-2 앵커 배치) ③ 스크롤 위치를 되돌린다.
    ///
    /// 단계 수가 바뀌었거나 셀이 아직 없으면 false — 호출부가 전량 재생성으로 폴백한다.
    /// </summary>
    public bool TryRefreshStages(List<DreamBalloonStageData> stages)
    {
        int count = cells.Count;
        if (null == stages || count == 0 || stages.Count != count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (null == cells[i])
            {
                return false;   // 셀이 파괴된 상태 → 전량 재생성 필요
            }
        }

        dataList.Clear();
        dataList.AddRange(stages);

        for (int i = 0; i < count; i++)
        {
            cells[i].SendMessage("SetInfo", stages[i], SendMessageOptions.DontRequireReceiver);
        }

        return true;
    }

    // 셀 전량 재생성 — Content 의 기존 자식을 모두 제거 후 Item 으로 단계 수만큼 생성·바인딩.
    private void BuildCells()
    {
        cells.Clear();
        cellDispatchers.Clear();

        if (null == content)
        {
            return;
        }

        // 기존 자식(정적 셀 잔재 포함) 전량 제거. Destroy 는 프레임 끝까지 지연되므로, 즉시 detach(SetParent null)해
        // 직후의 Instantiate + ForceRebuildLayoutImmediate 레이아웃 계산에서 이전 셀이 이중 집계되지 않도록 한다(인플레이스 갱신 트랙 붕괴 방지).
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            GameObject child = content.GetChild(i).gameObject;
            child.transform.SetParent(null, false);
            Destroy(child);
        }

        if (null == item)
        {
            return;
        }

        int count = dataList.Count;
        for (int i = 0; i < count; i++)
        {
            GameObject cell = Instantiate(item, content);
            cell.SetActive(true);
            cell.SendMessage("SetInfo", dataList[i], SendMessageOptions.DontRequireReceiver);
            cells.Add(cell);
            cellDispatchers.Add(cell.GetComponent<DreamBalloonStageCell>());   // SetInfo 이후 = 좌우 노출 확정 상태
        }

        // ContentSizeFitter/LayoutGroup 즉시 반영 — 이후 스크롤 위치 계산이 올바른 Content 크기 기준이 되도록.
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);

        // 셀 높이(라운드 길이) 실측 캐싱 — 최종 구름 라운드 비례 하강(ISSUE-32)의 기준값. 레이아웃 확정 후 첫 셀에서 읽는다.
        if (cells.Count > 0 && null != cells[0] && cells[0].transform is RectTransform cellRect)
        {
            cellHeight = cellRect.rect.height;
        }

        // 스크롤 상/하 여백을 고정값으로 설정(Top=200 / Bottom=130, ISSUE-34) 후 재빌드 — Clamped 경계·1단계/최종단계 정렬 기준.
        if (ApplyContentPadding())
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        }
    }

    // [ISSUE-34] Content VerticalLayoutGroup 상/하 패딩을 기획 지정 고정값으로 설정한다:
    //   Top = CONTENT_PADDING_TOP(200, 최종 단계 위 여유 — 축제장 연결, ISSUE-32)
    //   Bottom = CONTENT_PADDING_BOTTOM(130, 1단계 열기구 정렬 여백)
    // Clamped 스크롤 경계가 이 패딩을 포함한 Content 높이 기준으로 잡히므로, 1단계·최종단계 정렬이 이 패딩값으로 맞춰진다.
    // 값이 이미 같으면 false(재빌드 생략). left/right 는 보존한다.
    private bool ApplyContentPadding()
    {
        if (null == layoutGroup)
        {
            return false;
        }

        RectOffset cur = layoutGroup.padding;
        if (cur.top == CONTENT_PADDING_TOP && cur.bottom == CONTENT_PADDING_BOTTOM)
        {
            return false;
        }

        layoutGroup.padding = new RectOffset(cur.left, cur.right, CONTENT_PADDING_TOP, CONTENT_PADDING_BOTTOM);
        return true;
    }

    // 카메라 이동(즉시) — 해당 단계 구름을 열기구 높이에 맞춘다.
    public void ScrollToStage(int round)
    {
        KillTween();

        if (TryGetTargetContentY(round, out float targetY))
        {
            SetContentY(targetY);
            return;
        }

        SetVerticalNormalized(RoundToNormalized(round));   // 앵커 미확보 폴백
    }

    // 카메라 이동(연출) — time 동안 해당 단계로 스크롤. 완료까지 await(스킵/파괴 시 ct 취소).
    public async UniTask ScrollToStageAsync(int round, float time, CancellationToken ct)
    {
        if (time <= 0f)
        {
            ScrollToStage(round);
            return;
        }

        KillTween();

        bool hasAnchor = TryGetTargetContentY(round, out float targetY);
        if (hasAnchor)
        {
            targetY = ClampContentY(targetY);   // Clamped 범위로 제한 — 트윈 종료 위치가 ScrollRect 자체 클램프와 어긋나 튀지 않도록
        }
        float targetNormalized = hasAnchor ? 0f : RoundToNormalized(round);

        if (hasAnchor)
        {
            scrollTween = content.DOAnchorPosY(targetY, time).SetEase(Ease.InOutSine).SetLink(gameObject);
        }
        else if (null != scrollRect)
        {
            scrollTween = DOTween.To(() => scrollRect.verticalNormalizedPosition,
                    v => scrollRect.verticalNormalizedPosition = v, targetNormalized, time)
                .SetEase(Ease.InOutSine).SetLink(gameObject);
        }

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(time), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }
        catch (OperationCanceledException)
        {
            // [ISSUE-35] 트윈만 킬한다 — 여기서 content 를 옮기면 안 된다.
            //   SkipableBase 는 OnSkipToEnd() → skipCts.Cancel() 순서라, 이 catch 는 스냅 핸들러
            //   (SnapStageSuccessEnd → SnapToStage → ScrollToStage)가 이미 확정한 위치를 **나중에 덮어쓴다**.
            //   끝 상태는 스냅 핸들러가 단독으로 확정한다(열기구 이동 헬퍼와 동일 계약).
            KillTween();
            throw;
        }
    }

    /// <summary>
    /// 최종 연출 전용 — Content 를 현재 위치에서 <paramref name="deltaY"/> 만큼 이동(카메라 하강). 완료까지 await.
    ///
    /// ⚠️ <see cref="ClampContentY"/> 를 **적용하지 않는다.** 최종 라운드에서는 트랙이 이미 Clamped 상한에 닿아 있어
    /// 클램프를 걸면 이동량이 0 으로 죽는다 — 그래서 <see cref="ScrollToStageAsync"/> 로는 최종 하강을 만들 수 없다.
    /// 연출 구간은 ScrollRect.enabled=false 라 Unity 자체 클램프도 꺼져 있고, 최종 연출 뒤에는 스크롤을 다시 켜지 않으므로
    /// (DreamBalloonRoadController.OnCleanup 참조 — 켜면 Clamped 가 content 를 범위로 되돌려 하강이 튕겨 올라간다)
    /// 범위를 벗어난 위치가 유지된다. 스킵/파괴 시 목표로 스냅한 뒤 재throw(순수 view).
    /// </summary>
    public async UniTask MoveContentByAsync(float deltaY, float duration, CancellationToken ct)
    {
        if (null == content || Mathf.Approximately(deltaY, 0f))
        {
            return;
        }

        float targetY = content.anchoredPosition.y + deltaY;

        if (duration <= 0f)
        {
            MoveContentBy(deltaY);
            return;
        }

        KillTween();
        scrollTween = content.DOAnchorPosY(targetY, duration).SetEase(Ease.InOutSine).SetLink(gameObject);

        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(duration), DelayType.DeltaTime, PlayerLoopTiming.Update, ct);
        }
        catch (OperationCanceledException)
        {
            KillTween();
            SetContentYUnclamped(targetY);
            throw;
        }
    }

    /// <summary>
    /// 최종 연출 전용 — Content 를 즉시 <paramref name="deltaY"/> 만큼 이동(끝 상태 스냅).
    /// <see cref="MoveContentByAsync"/> 와 같은 이유로 Clamped 범위를 적용하지 않는다.
    /// </summary>
    public void MoveContentBy(float deltaY)
    {
        if (null == content || Mathf.Approximately(deltaY, 0f))
        {
            return;
        }

        KillTween();
        SetContentYUnclamped(content.anchoredPosition.y + deltaY);
    }

    private void SetContentYUnclamped(float value)
    {
        Vector2 pos = content.anchoredPosition;
        pos.y = value;
        content.anchoredPosition = pos;
        scrollAction?.Invoke();   // 코드 스크롤(연출)은 onValueChanged 를 태우지 않는다 — 안내 표시 갱신은 여기서
    }

    /// <summary>
    /// 목표 `content.anchoredPosition.y` — **실제 노출된 구름의 화면 Y 를 열기구의 화면 Y 에 맞춘다.**
    ///
    /// ⚠️ `verticalNormalizedPosition`(Clamp01) 대신 content.anchoredPosition 을 직접 계산해 정렬한 뒤,
    /// 최종 위치는 <see cref="ClampContentY"/> 로 ScrollRect Clamped 범위에 맞춘다. 1단계 정렬(열기구 높이)은
    /// Content VLG 하단 패딩(셀높이/2)으로 Clamped 하단과 일치시켜 열기구가 구름 위에 뜨지 않게 한다.
    /// </summary>
    private bool TryGetTargetContentY(int round, out float targetY)
    {
        targetY = 0f;

        // 미시작(state 0)이면 currentRound == 0 으로 내려온다 → 1단계 기준으로 보정(열기구 X 정렬과 동일 규칙).
        Transform anchor = GetStageAnchor(Mathf.Max(1, round));
        if (null == anchor || null == content || null == balloonAnchor || content.parent is not RectTransform viewport)
        {
            return false;
        }

        // 뷰포트 로컬 기준 Y 차이만큼 콘텐츠를 이동 — pivot/anchor 설정에 의존하지 않는다.
        float anchorY = viewport.InverseTransformPoint(anchor.position).y;
        float balloonY = viewport.InverseTransformPoint(BalloonAlignWorldPos).y;   // 캐싱된 홈 기준(구름 자식화 무관)
        targetY = content.anchoredPosition.y + (balloonY - anchorY);
        return true;
    }

    private void SetContentY(float value)
    {
        Vector2 pos = content.anchoredPosition;
        pos.y = ClampContentY(value);
        content.anchoredPosition = pos;
        scrollAction?.Invoke();   // 코드 스크롤(연출·정렬)도 안내 표시를 갱신한다
    }

    // ScrollRect Clamped(프리팹 m_MovementType:2) 유효 범위로 content.anchoredPosition.y 를 제한한다(Unity ScrollRect 자체 클램프와 동일 계산).
    // Content 는 뷰포트 하단 앵커(anchorMin/Max y=0)·bottom pivot 이라, 콘텐츠가 뷰포트를 덮는 범위 = [-(콘텐츠H − 뷰포트H), 0].
    // ⚠️ 연출 중엔 ScrollRect.enabled=false 로 자체 클램프가 꺼져 코드가 직접 이동하므로, 이 클램프로 Clamped 와 동일 위치를 유지해야
    //    enabled 복원 시 ScrollRect 가 위치를 되돌리는 튐(1라운드 실패 연출 등)이 없다. 상·하단 여백은 Content VLG 패딩으로 준다.
    private float ClampContentY(float value)
    {
        if (null == content || null == scrollRect || null == scrollRect.viewport)
        {
            return value;
        }

        float scrollable = content.rect.height - scrollRect.viewport.rect.height;
        if (scrollable <= 0f)
        {
            return 0f;   // 콘텐츠가 뷰포트보다 작거나 같으면 하단 고정(스크롤 불가)
        }

        return Mathf.Clamp(value, -scrollable, 0f);
    }

    // 단계(1-base) → 세로 정규화 위치. 열기구 기준이 지정되면 해당 셀 중심을 열기구의 화면 위치에 정렬,
    // 아니면 선형 폴백(round 1 하단 ~ round N 상단). ※ round 1 하단 배치는 Content VLG ReverseArrangement=true.
    private float RoundToNormalized(int round)
    {
        int count = dataList.Count;
        if (count <= 1)
        {
            return 0f;
        }

        int idx = Mathf.Clamp(round - 1, 0, count - 1);

        // 열기구 기준 정렬: (셀 중심 높이 - 열기구 뷰포트 높이) / 스크롤 가능 높이.
        RectTransform viewport = null == scrollRect ? null : scrollRect.viewport;
        if (null != balloonAnchor && null != viewport && null != content && idx < cells.Count && null != cells[idx])
        {
            float viewHeight = viewport.rect.height;
            float contentHeight = content.rect.height;
            float scrollable = contentHeight - viewHeight;
            if (scrollable > 0f)
            {
                // 정렬 기준 = 구름 위 열기구 위치(ActiveAirBalloonAnchor). 이 앵커를 열기구 오버레이 Y 에 맞춰
                // 스크롤하면 열기구가 해당 구름 위(AirBalloonTrans)에 정확히 놓인다.
                // 앵커의 content-로컬 Y 는 스크롤과 무관(content 와 함께 이동)하므로 목표 정규화 계산에 사용 가능.
                Transform anchor = idx < cellDispatchers.Count && null != cellDispatchers[idx]
                    ? cellDispatchers[idx].ActiveAirBalloonAnchor
                    : null;
                float targetFromBottom = null != anchor
                    ? content.InverseTransformPoint(anchor.position).y
                    : (cells[idx].transform as RectTransform).localPosition.y;
                float balloonFromBottom = viewHeight + viewport.InverseTransformPoint(BalloonAlignWorldPos).y;   // 뷰포트 하단 기준 열기구 높이(캐싱된 홈 기준)
                return Mathf.Clamp01((targetFromBottom - balloonFromBottom) / scrollable);
            }
        }

        return (float)idx / (count - 1);
    }

    private void SetVerticalNormalized(float value)
    {
        if (null == scrollRect)
        {
            return;
        }

        scrollRect.verticalNormalizedPosition = Mathf.Clamp01(value);
    }

    private void KillTween()
    {
        if (null == scrollTween)
        {
            return;
        }

        scrollTween.Kill();
        scrollTween = null;
    }
}

using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UI;       // LayoutRebuilder, VerticalLayoutGroup

/// <summary>
/// 당근 뽑기 메인 팝업 누적 보상 리스트(§5-4)의 LoopVerticalScrollRect 데이터 소스.
/// 가시 셀만 인스턴스화/리사이클하므로 티어 수에 맞춰 콘텐츠/스크롤이 자동 사이징된다(기존 전체 인스턴스화 ScrollRect 대체).
/// 셀은 기존 <see cref="CommonSliderProgressRewardItem"/> 를 그대로 재사용한다(0번 '0' 베이스라인 행은 미생성 — 게이지 계산에서만 가상 0 으로 가정).
/// </summary>
public class CarrotRewardLoopScroll : LoopScrollBase<CommonSliderProgressRewardData>
{
    // 마커(셀) 범위를 세로로 스팬하는 게이지 바(뷰포트 자식). 등록 시, 첫(0번째)~마지막 셀 중심 사이로 높이를 맞추고
    // 스크롤에 맞춰 추적한다(LoopScrollRect 는 content 자식을 셀로 관리하므로 게이지는 content 가 아닌 뷰포트에 둔다).
    private Slider gaugeSlider;
    private RectTransform gaugeRect;
    private long gaugeScore;     // 누적 점수(진행도 fill 계산용) — 팝업이 SetGaugeScore 로 갱신.

    protected override void Awake()
    {
        base.Awake();

        if (loopScrollRect != null)
            loopScrollRect.onValueChanged.AddListener(OnScrollValueChanged);
    }

    private void OnDestroy()
    {
        if (loopScrollRect != null)
            loopScrollRect.onValueChanged.RemoveListener(OnScrollValueChanged);
    }

    /// <summary>게이지 슬라이더 등록 — 이후 SetData/스크롤 시 셀 중심 범위로 높이(Span)와 진행도(value)를 맞춘다.</summary>
    public void SetGauge(Slider slider)
    {
        gaugeSlider = slider;
        gaugeRect = slider != null ? (RectTransform)slider.transform : null;
        ApplyGaugeSpan();
    }

    /// <summary>누적 점수 갱신 — 진행도(fill value) 재계산. 점수 변동(서버 동기화/게임 종료) 시 팝업이 호출.</summary>
    public void SetGaugeScore(long score)
    {
        gaugeScore = score;
        ApplyGaugeValue();
    }

    /// <summary>데이터 세팅 + 리필. startIndex 를 맨 위에 배치(진행중 티어로 자동 이동 — 기존 ScrollToCurrentTier 대체).</summary>
    public void SetData(List<CommonSliderProgressRewardData> data, int startIndex = 0)
    {
        dataList = data ?? new List<CommonSliderProgressRewardData>();

        if (loopScrollRect == null)
            loopScrollRect = GetComponent<LoopScrollRect>();

        // 활성화/오픈 직후 호출 시 viewport rect 가 아직 0이면 RefillCells 가 셀을 0개로 계산한다(가시 셀 기반 리사이클).
        // 캔버스 + 뷰포트 레이아웃을 강제 확정해 뷰포트 높이를 보장한 뒤 리필한다.
        Canvas.ForceUpdateCanvases();
        if (loopScrollRect != null && loopScrollRect.viewport != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(loopScrollRect.viewport);

        BindLoopScroll(dataList.Count, Mathf.Clamp(startIndex, 0, Mathf.Max(0, dataList.Count - 1)));

        ApplyGaugeSpan();   // 셀 범위 확정 후 게이지 높이 갱신
    }

    public override void ProvideData(Transform trans, int index)
    {
        if (dataList == null || index < 0 || index >= dataList.Count)
            return;

        if (trans.TryGetComponent(out CommonSliderProgressRewardItem cell))
        {
            // 마지막 티어는 FinalItemDisplay(있으면)로 — 기존 CommonRewardItemController.SetRewardItems 의 isFinal 규칙과 동일.
            var isFinal = index >= dataList.Count - 1;
            cell.SetData(dataList[index], isFinal);
        }
    }

    private void OnScrollValueChanged(Vector2 _)
    {
        ApplyGaugeSpan();
    }

    // 게이지 바를 '스크롤뷰에 실제 생성된 셀(ScrollPanelEventCarrotReward)들의 첫(최상단)~마지막(최하단) 중심' 범위로 맞춘다.
    // 셀 '중심'(펜타곤 마커 위치) 기준이라 0번째 셀 위/마지막 셀 아래로 넘치지 않는다(높이 = (갯수−1) × (셀높이 + spacing)).
    // 생성 셀의 실제 위치를 읽어 게이지 부모 로컬로 변환해 중심/높이를 설정 → 스크롤(셀 생성/리사이클) 시 갱신.
    private void ApplyGaugeSpan()
    {
        if (gaugeRect == null)
            return;

        var content = loopScrollRect != null ? loopScrollRect.content : null;
        if (content == null)
            return;
        if (gaugeRect.parent is not RectTransform parent)
            return;

        var topWorldY = float.NegativeInfinity;     // 생성 셀 중 최상단 '중심'(펜타곤 마커 위치)
        var bottomWorldY = float.PositiveInfinity;  // 생성 셀 중 최하단 '중심'
        var topCellGoal = 0;                        // 최상단 셀의 목표 점수(첫 티어 판별용)
        var corners = new Vector3[4];
        var childCount = content.childCount;
        var cellCount = 0;
        for (var i = 0; i < childCount; ++i)
        {
            var child = content.GetChild(i);
            if (!child.TryGetComponent(out CommonSliderProgressRewardItem cell))
                continue;   // 생성된 보상 셀만 집계(다른 자식 제외)

            ((RectTransform)child).GetWorldCorners(corners);   // [0]=좌하 [1]=좌상 [2]=우상 [3]=우하
            var cellCenterY = (corners[0].y + corners[1].y) * 0.5f;   // 셀 중심 Y(펜타곤 마커 기준 — 셀 끝이 아님)
            if (cellCenterY > topWorldY)
            {
                topWorldY = cellCenterY;
                topCellGoal = cell.GoalValue2;
            }
            if (cellCenterY < bottomWorldY)
                bottomWorldY = cellCenterY;
            cellCount++;
        }
        if (cellCount == 0)
            return;

        // 0번 '0' 베이스라인 행 제거 후에도 게이지 길이는 '0 이 있다고 가정'해 계산한다(요청).
        // 최상단 셀이 첫 티어(최소 goal)면, 그 위 한 셀 간격에 가상 0 기준점이 있다고 보고 상단을 한 칸 연장한다(첫 티어보다 위쪽에서 게이지 시작).
        if (cellCount >= 2 && IsFirstTierGoal(topCellGoal))
        {
            var cellStep = (topWorldY - bottomWorldY) / (cellCount - 1);
            topWorldY += cellStep;
        }

        var topLocalY = parent.InverseTransformPoint(new Vector3(0f, topWorldY, 0f)).y;
        var bottomLocalY = parent.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

        var height = topLocalY - bottomLocalY;     // 첫~마지막 셀 '중심' 거리 = (생성 셀 갯수−1) × (셀높이 + spacing)
        var centerY = (topLocalY + bottomLocalY) * 0.5f;

        // 세로 앵커를 중앙 고정(스트레치 해제)으로 두고 중심/높이 설정. 좌우(X)는 인스펙터 설정 유지.
        gaugeRect.anchorMin = new Vector2(gaugeRect.anchorMin.x, 0.5f);
        gaugeRect.anchorMax = new Vector2(gaugeRect.anchorMax.x, 0.5f);

        var anchored = gaugeRect.anchoredPosition;
        anchored.y = centerY;
        gaugeRect.anchoredPosition = anchored;

        var size = gaugeRect.sizeDelta;
        size.y = height;
        gaugeRect.sizeDelta = size;

        ApplyGaugeValue();   // 바 범위가 바뀌면 진행도(fill)도 같이 갱신
    }

    // 게이지 진행도(fill value) — 누적 점수가 마커(셀) 사이 어디에 위치하는지를 '생성 셀 중심' 기준으로 계산해 슬라이더 value 로 설정.
    // 점수를 bracket 하는 두 마커(작은 goal=위, 큰 goal=아래)의 중심 Y 를 점수 비율로 보간 → 바 범위[상단,하단]에 정규화.
    // 슬라이더 Direction=TopToBottom(상단 0%, 하단 100%) 기준: value = (상단 - target) / (상단 - 하단).
    private void ApplyGaugeValue()
    {
        if (gaugeSlider == null)
            return;

        var content = loopScrollRect != null ? loopScrollRect.content : null;
        if (content == null)
            return;

        var topWorldY = float.NegativeInfinity;     // 최상단 셀 중심
        var bottomWorldY = float.PositiveInfinity;  // 최하단 셀 중심
        var topCellGoal = 0;                        // 최상단 셀의 목표 점수(첫 티어 판별용)
        var lowerGoal = long.MinValue;              // 점수 이하 마커 중 가장 큰 goal(위쪽)
        var upperGoal = long.MaxValue;              // 점수 초과 마커 중 가장 작은 goal(아래쪽)
        var lowerGoalY = 0f;
        var upperGoalY = 0f;
        var hasLower = false;
        var hasUpper = false;
        var found = 0;
        var corners = new Vector3[4];
        var childCount = content.childCount;
        for (var i = 0; i < childCount; ++i)
        {
            var child = content.GetChild(i);
            if (!child.TryGetComponent(out CommonSliderProgressRewardItem cell) || !cell.HasData)
                continue;

            ((RectTransform)child).GetWorldCorners(corners);
            var cellCenterY = (corners[0].y + corners[1].y) * 0.5f;
            if (cellCenterY > topWorldY)
            {
                topWorldY = cellCenterY;
                topCellGoal = cell.GoalValue2;
            }
            if (cellCenterY < bottomWorldY)
                bottomWorldY = cellCenterY;

            var goal = cell.GoalValue2;
            if (goal <= gaugeScore && goal > lowerGoal)
            {
                lowerGoal = goal;
                lowerGoalY = cellCenterY;
                hasLower = true;
            }
            if (goal > gaugeScore && goal < upperGoal)
            {
                upperGoal = goal;
                upperGoalY = cellCenterY;
                hasUpper = true;
            }
            found++;
        }
        if (found == 0)
            return;

        // 0번 '0' 베이스라인 제거 후에도 게이지는 '0 이 있다고 가정'(요청): 최상단 셀이 첫 티어면 그 위 한 칸에 가상 0(goal 0) 기준점을 둔다.
        // → 바 상단(0%)을 첫 티어보다 한 칸 위(가상 0)로 연장(Span 과 동일 기준)하고, 첫 티어 미만 점수도 0~첫티어 구간을 보간한다.
        if (found >= 2 && IsFirstTierGoal(topCellGoal))
        {
            var cellStep = (topWorldY - bottomWorldY) / (found - 1);
            var virtualZeroY = topWorldY + cellStep;
            topWorldY = virtualZeroY;       // 바 상단을 가상 0 위치로 연장
            if (0L > lowerGoal)             // 점수 < 첫 티어면 실제 lower 마커가 없으므로 가상 0 을 lower 로 편입(gaugeScore ≥ 0)
            {
                lowerGoal = 0L;
                lowerGoalY = virtualZeroY;
                hasLower = true;
            }
        }

        float targetY;
        if (hasLower && hasUpper)
        {
            var frac = upperGoal > lowerGoal ? (float)(gaugeScore - lowerGoal) / (upperGoal - lowerGoal) : 0f;
            targetY = Mathf.Lerp(lowerGoalY, upperGoalY, Mathf.Clamp01(frac));   // 위(작은 goal) → 아래(큰 goal)
        }
        else if (hasLower)
        {
            targetY = bottomWorldY;     // 점수가 보이는 모든 마커 이상 → 하단(가득)
        }
        else
        {
            targetY = topWorldY;        // 점수가 보이는 모든 마커 미만 → 상단(빈)
        }

        var denom = topWorldY - bottomWorldY;
        gaugeSlider.value = Mathf.Abs(denom) < 0.0001f ? 0f : Mathf.Clamp01((topWorldY - targetY) / denom);
    }

    // 최상단 셀의 목표 점수가 데이터의 첫(최소 goal) 티어와 같은지 — 첫 티어가 화면 최상단에 보일 때만 가상 0 기준점을 적용한다.
    private bool IsFirstTierGoal(int goal)
    {
        return dataList != null && dataList.Count > 0 && goal == dataList[0].goalValue2;
    }
}

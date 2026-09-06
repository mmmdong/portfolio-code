using System;
using System.Collections.Generic;

using GameCore.Utils;
using GameLogic.Management;

using UnityEngine;
using UnityEngine.UI;

// 캐릭터 카페 2차 서브콘텐츠 — 우사하나 풍선 게임 그리드 컨트롤러 (상세 892698729 §3-2)
// 라운드 풍선 레이아웃(서버 권위)을 받아 템플릿 풍선을 동적 생성하고, 터뜨리기 클릭을 팝업으로 전달한다.
// 생성 로직만 담당 — 비용 차감/내용 공개 등 데이터 변경은 팝업이 Content.Try* 로 위임한다.
public class UIMiniGameBalloonGrid : MonoBehaviour
{
    public class Info
    {
        public IReadOnlyList<int> balloonContents;  // 슬롯별 내용 (-1=열쇠, 0=꽝, 1~=보상 idx)
        public IReadOnlyList<bool> balloonPopped;
        public IReadOnlyList<bool> balloonImportant; // 슬롯별 중요 보상 여부 (balloonContents 평행)
        public IReadOnlyList<string> balloonSkins;   // 슬롯별 풍선 Spine 스킨(색상) — 생성 시 랜덤 배정 (balloonContents 평행)
        public int columns;                         // 라운드 가로 풍선 수 (roundColsRows[0])
        public Action<int> onBalloonClick;
    }

    [SerializeField] private Transform balloonContainer;  // 풍선이 배치되는 부모 (GridLayoutGroup)
    [SerializeField] private GameObject balloonTemplate;  // 복제 대상 풍선 템플릿
    [SerializeField] private int wideColumnThreshold = 4;        // 이 컬럼 수 이상이면 가로 간격을 넓힌다(4·5열 빽빽함 완화)
    [SerializeField] private float wideColumnExtraSpacingX = 40f; // 넓힐 가로 간격(디자인 기준, 셀/스케일과 함께 비례 적용)
    [SerializeField] private float inStaggerInterval = 0.05f; // 등장(In) 애니 staggered 딜레이 간격(초) — 전체 생성 후 랜덤 순번 × 이 값으로 In 재생

    private Info curInfo;
    private readonly List<UIMiniGameBalloonItem> spawnedItems = new();
    // 동적 생성 풍선의 수명을 귀속시키는 스코프. SetData 재호출/파괴 시 일괄 Release.
    private readonly ResourceScope resourceScope = new();

    private GridLayoutGroup gridLayout;   // balloonContainer 의 GridLayoutGroup (최초 1회 캐싱)
    private Vector2 baseCellSize;         // 디자인 기준 셀 크기 (스케일 누적 방지를 위해 최초 값 보관)
    private Vector2 baseSpacing;          // 디자인 기준 간격 (겹침 연출 위해 음수 가능)
    private Vector3 baseItemScale = Vector3.one; // 풍선 템플릿(UIBalloonItem) 디자인 기준 localScale
    private bool gridBaseCached;
    private float gridScale = 1f;         // 그리드 정렬 배율 (셀/간격 + 풍선 아이템 transform.localScale 공통 적용)

    public void SetData(Info info)
    {
        curInfo = info;
        Build();
    }

    // 풍선 그리드 생성 — 풍선은 전체를 즉시 생성하고, Spine 등장(In) 애니만 슬롯별 딜레이로 staggered 재생(딜레이는 아이템이 처리).
    //  In 재생 순서를 셔플해 랜덤 순번 × inStaggerInterval 을 슬롯 딜레이로 부여한다. spawnedItems[index] 는 항상 해당 인덱스 아이템.
    private void Build()
    {
        if (null == balloonContainer || null == balloonTemplate)
        {
            DLogger.Error($"[{GetType().Name}] balloonContainer/balloonTemplate not bound");
            return;
        }

        ClearItems();

        // 컨테이너에 사전 배치된 목업 풍선(Balloon_1~6 등)을 전부 비활성화한다.
        // 템플릿도 자식이라 함께 비활성화되며, 동적 생성한 클론만 활성으로 표시된다.
        int existingCount = balloonContainer.childCount;
        for (int i = 0; i < existingCount; i++)
            balloonContainer.GetChild(i).gameObject.SetActive(false);

        ApplyGridLayout();

        var contents = curInfo.balloonContents;
        if (null == contents)
            return;

        var popped = curInfo.balloonPopped;
        var important = curInfo.balloonImportant;
        var skins = curInfo.balloonSkins;
        int count = contents.Count;

        float[] inDelays = BuildInDelays(count); // 등장(In) 애니 슬롯별 staggered 딜레이(랜덤 순번)

        for (int i = 0; i < count; i++)
        {
            var instance = resourceScope.Instantiate(balloonTemplate, balloonContainer);
            instance.SetActive(true);

            if (!instance.TryGetComponent(out UIMiniGameBalloonItem item))
            {
                DLogger.Error("Not found UIMiniGameBalloonItem on template");
                continue;
            }

            bool isPopped = (null != popped) && i < popped.Count && popped[i];
            bool isImportant = (null != important) && i < important.Count && important[i];
            UIMiniGameBalloonItem.Info itemInfo = new()
            {
                index = i,
                content = contents[i],
                isPopped = isPopped,
                isImportant = isImportant,
                skin = (null != skins && i < skins.Count) ? skins[i] : null,
                inDelay = inDelays[i],
                onClick = curInfo.onBalloonClick,
            };
            item.SetData(itemInfo);
            instance.transform.localScale = baseItemScale * gridScale; // 그리드 배율에 맞춰 풍선 시각 크기 정렬
            spawnedItems.Add(item);
        }
    }

    // 등장(In) 애니 슬롯별 딜레이 — 인덱스를 셔플해 랜덤 순번(0,1,2…) × inStaggerInterval 을 해당 인덱스 위치에 부여(전체 생성, In 만 staggered).
    private float[] BuildInDelays(int count)
    {
        float[] delays = new float[count > 0 ? count : 0];
        if (count <= 0)
            return delays;

        List<int> order = new(count);
        for (int i = 0; i < count; i++)
            order.Add(i);
        for (int i = count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int pos = 0; pos < count; pos++)
            delays[order[pos]] = pos * inStaggerInterval;
        return delays;
    }

    // 단일 풍선 갱신 (터뜨린 직후 결과 반영) — Pop 1회 재생
    public void RefreshItem(int index, int content, bool isPopped, bool isImportant)
    {
        int count = spawnedItems.Count;
        if (index < 0 || index >= count)
            return;

        var skins = curInfo?.balloonSkins; // 색상 유지 — 생성 시 배정한 스킨을 그대로 전달
        UIMiniGameBalloonItem.Info itemInfo = new()
        {
            index = index,
            content = content,
            isPopped = isPopped,
            isImportant = isImportant,
            skin = (null != skins && index < skins.Count) ? skins[index] : null,
            onClick = curInfo?.onBalloonClick,
        };
        spawnedItems[index].PlayPop(itemInfo);
    }

    // 풍선 슬롯의 월드 좌표 (보상/열쇠 비행 연출의 시작점). index 가 범위 밖이면 false.
    public bool TryGetBalloonWorldPosition(int index, out Vector3 worldPos)
    {
        worldPos = default;
        int count = spawnedItems.Count;
        if (index < 0 || index >= count || null == spawnedItems[index])
            return false;
        worldPos = spawnedItems[index].transform.position;
        return true;
    }

    // 라운드별 가로 풍선 수(컬럼)를 GridLayoutGroup 에 반영하고, 셀 크기/간격을 컨테이너 영역에 맞게 정렬한다.
    // 풍선 수가 3x3 → 4x4 → 5x5 로 늘어도 디자인 기준 셀/간격(겹침 포함)을 비례 축소해 컨테이너 안에 가운데로 채운다.
    private void ApplyGridLayout()
    {
        int columns = curInfo.columns;
        if (columns <= 0)
            return;

        CacheGridBase();
        if (null == gridLayout)
            return;

        // 총 풍선 수에서 행 수 산출(정사각이 아니어도 동작). contents 미설정 시 정사각(cols×cols) 가정.
        int count = (null != curInfo.balloonContents) ? curInfo.balloonContents.Count : columns * columns;
        int rows = Mathf.CeilToInt((float)count / columns);
        if (rows <= 0)
            rows = 1;

        gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        gridLayout.constraintCount = columns;
        gridLayout.childAlignment = TextAnchor.MiddleCenter; // 그리드 전체를 컨테이너 가운데로 정렬

        // 4·5열은 가로가 빽빽해 보이므로 컬럼 수가 임계 이상이면 가로 간격을 추가한다(디자인 기준값, 아래 scale 과 함께 비례 적용).
        float spacingX = baseSpacing.x + (columns >= wideColumnThreshold ? wideColumnExtraSpacingX : 0f);

        // 컨테이너 가용 영역에 맞춰 셀/간격/아이템 스케일을 비례 축소(넘칠 때만). 기준은 항상 baseCellSize/baseSpacing.
        float scale = 1f; // 1 이하로만 축소(작은 그리드는 풍선 크기 유지, 넘칠 때만 줄임)
        if (balloonContainer is RectTransform containerRect)
        {
            Vector2 area = containerRect.rect.size;
            // 기준 셀+간격으로 그리드가 차지하는 자연 크기 (간격이 음수면 겹쳐서 줄어든다)
            float baseWidth = columns * baseCellSize.x + (columns - 1) * spacingX;
            float baseHeight = rows * baseCellSize.y + (rows - 1) * baseSpacing.y;

            if (baseWidth > 0f && area.x > 0f)
                scale = Mathf.Min(scale, area.x / baseWidth);
            if (baseHeight > 0f && area.y > 0f)
                scale = Mathf.Min(scale, area.y / baseHeight);

            gridLayout.cellSize = baseCellSize * scale;
            gridLayout.spacing = new Vector2(spacingX, baseSpacing.y) * scale;
        }

        // 셀뿐 아니라 풍선 시각 크기(transform.localScale)도 같은 배율로 맞춘다(Spine 은 셀 크기를 따르지 않으므로).
        gridScale = scale;
    }

    // GridLayoutGroup 과 디자인 기준 셀/간격을 최초 1회 캡처한다.
    // (스케일 적용 후 값을 다시 기준으로 읽으면 라운드마다 누적 축소되므로 최초 값만 기준으로 보관)
    private void CacheGridBase()
    {
        if (gridBaseCached)
            return;
        if (null != balloonContainer && balloonContainer.TryGetComponent(out gridLayout))
        {
            baseCellSize = gridLayout.cellSize;
            baseSpacing = gridLayout.spacing;
            if (null != balloonTemplate)
                baseItemScale = balloonTemplate.transform.localScale; // 풍선 시각 크기 기준
            gridBaseCached = true;
        }
    }

    private void ClearItems()
    {
        resourceScope.ReleaseAll();
        spawnedItems.Clear();
    }

    private void OnDestroy()
    {
        ClearItems();
    }
}

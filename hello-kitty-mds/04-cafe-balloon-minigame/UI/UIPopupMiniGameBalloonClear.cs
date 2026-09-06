using System;
using System.Collections.Generic;

using Cysharp.Threading.Tasks;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;

using Spine.Unity;

using StatefulUISupport.Scripts.Components;

using UnityEngine;
using UnityEngine.UI;

// 캐릭터 카페 2차 서브콘텐츠 — 우사하나 풍선 게임 클리어 축하 팝업 (기획 892698729 §3-5)
//  마지막 라운드(R5) 열쇠 획득 시 노출. "보물 찾기 성공!" 제목 + 마지막 라운드 보상(최대 8) 미리보기 + "좋아요!" 버튼.
//  보상 실지급은 라운드 진행/클리어(서버 권위, CharacterCafeMiniGameSubContent)에서 이미 처리되므로 본 팝업은 미리보기/연출 전용이다.
//  UI 바인딩은 StatefulUI 역할 기반(UIPopupCharacterCafeBaseRewardResult 와 동일 구조 재사용) — 미배선 역할은 Has* 가드로 안전 no-op.
//   TextRole.TitleText(제목 43173)·GuideText1(내용 43174) / ButtonRole.TouchBlock|BtnConfirm|Close(좋아요!=닫기) /
//   ObjectRole.CommonRewardItem(보상 템플릿)·RewardContainer(생성 부모).
public class UIPopupMiniGameBalloonClear : UIBasePopup
{
    public class Info : IUIInfoData
    {
        public List<RewardInfo> rewards; // 마지막 라운드 보상 미리보기(최대 8)
        public Action onClosed;          // "좋아요!"(닫기) 후 콜백 — 메인 풍선 팝업 종료/복귀(§3-5)
    }

    private const int LIDX_TITLE = 43173; // "보물 찾기 성공!"
    private const int LIDX_DESC = 43174;  // "축하해요!\n숨겨진 보물을 찾았어요!"
    // "좋아요!"(40513) 버튼 라벨은 비가변 → 프리팹 UITextEx(mStringKey=40513)에 직접 바인딩.

    // 클리어 축하 Spine(우사하나 장식물, §4-4 게임 클리어 연출) — ObjectRole.Spine(SkeletonGraphic) 미배선/로드 실패 시 graceful no-op.
    //  ※ [바인딩 완료 06-14] 프리팹 SpineRenderer 노드에 SkeletonGraphic 부착 + StatefulComponent ObjectRole.Spine 등록 완료(임시 우사하나 에셋 바인딩).
    //  ※ TODO[art]: 전용 "우사하나 장식물 클리어" 연출 Spine 아트 미빌드 → 임시로 우사하나 캐릭터(Idle_Front) 로드. 전용 아트 확정 시 경로/애니명만 교체.
    private const string CLEAR_SPINE_PATH = "12101_USaHaNa_SkeletonData";
    private const string CLEAR_SPINE_ANIM = "Idle_Front";

    // 보상 개수별 크기 조정(§3-5 "보상 개수에 따라 크기 조정") — 줄 수(=ceil(개수/열))가 적을수록 셀을 크게.
    //  고정 폭(Content 1040) 내에서 안전하며 높이는 ContentSizeFitter 자동. 값은 디자이너 튜닝 가능.
    private const float CELL_SCALE_1ROW = 1.25f; // 1줄(≤3개) — 가장 크게
    private const float CELL_SCALE_2ROW = 1.1f;  // 2줄(4~6개)
    private const float CELL_SCALE_3ROW = 1f;    // 3줄(7~8개) — 기본

    [SerializeField] private GridLayoutGroup rewardGrid; // 보상 그리드(Content) — 개수별 셀 크기 조정(§3-5)

    private Info curInfo;
    // 클리어 보상 미리보기 — ObjectRole.CommonRewardItem(템플릿)을 보상 수만큼 동적 생성한 슬롯 목록. OnDisable 에서 정리(Destroy).
    private readonly List<CommonRewardItem> spawnedRewardSlots = new();
    private Vector2 baseRewardCellSize; // 그리드 기본 셀/간격(첫 사용 시 캐시) — 개수별 스케일 기준
    private Vector2 baseRewardSpacing;
    private bool rewardGridBaseCached;

    protected override void OnEnable()
    {
        base.OnEnable();

        // "좋아요!"/화면 터치/닫기 → 종료/복귀. 프리팹에 어떤 역할이 바인딩돼 있어도 동작하도록 가드 연결.
        if (Stateful.HasButton(ButtonRole.BtnConfirm))
            Stateful.AddButtonListener(ButtonRole.BtnConfirm, OnClickConfirm);
        if (Stateful.HasButton(ButtonRole.TouchBlock))
            Stateful.AddButtonListener(ButtonRole.TouchBlock, OnClickConfirm);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.AddButtonListener(ButtonRole.Close, OnClickConfirm);
    }

    protected override void OnDisable()
    {
        if (Stateful.HasButton(ButtonRole.BtnConfirm))
            Stateful.RemoveButtonListener(ButtonRole.BtnConfirm, OnClickConfirm);
        if (Stateful.HasButton(ButtonRole.TouchBlock))
            Stateful.RemoveButtonListener(ButtonRole.TouchBlock, OnClickConfirm);
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.RemoveButtonListener(ButtonRole.Close, OnClickConfirm);

        ClearSpawnedRewardSlots();

        base.OnDisable();
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);
        curInfo = data as Info;

        SetTexts();
        SetRewardPreview();
        PlayClearSpineAsync().Forget();
    }

    // 클리어 축하 Spine(우사하나 장식물, §4-4) 재생 — ObjectRole.Spine(SkeletonGraphic) 미배선/경로 미설정/로드 실패 시 graceful no-op.
    //  퍼즐 클리어(RefreshIllustrationAsync)와 동일 패턴: LoadScopedAsync → skeletonDataAsset 교체 → Initialize → 애니 재생.
    //  지급/복귀와 무관한 순수 시각 연출이므로 실패해도 보상 미리보기·복귀 흐름은 정상 동작한다.
    private async UniTaskVoid PlayClearSpineAsync()
    {
        if (string.IsNullOrEmpty(CLEAR_SPINE_PATH)) return;
        if (!Stateful.TryGetObjectComponent(ObjectRole.Spine, out SkeletonGraphic spine) || null == spine) return;

        var ct = gameObject.GetCancellationTokenOnDestroy();

        // 이미 동일 에셋이면 재로드/재초기화 생략(재진입 대비).
        if (null == spine.skeletonDataAsset || spine.skeletonDataAsset.name != CLEAR_SPINE_PATH)
        {
            SkeletonDataAsset asset = await this.LoadScopedAsync<SkeletonDataAsset>(CLEAR_SPINE_PATH, ct);
            if (this == null || null == spine || null == asset) return;

            spine.skeletonDataAsset = asset;
            spine.Initialize(true);
        }

        if (!string.IsNullOrEmpty(CLEAR_SPINE_ANIM) && null != spine.AnimationState)
            spine.AnimationState.SetAnimation(0, CLEAR_SPINE_ANIM, true);
    }

    // 제목(43173)·내용(43174) — 비가변이나 프리팹 텍스트 노드 부재 대비 코드 구동(역할 미배선 시 no-op).
    private void SetTexts()
    {
        if (Stateful.HasText(TextRole.TitleText))
            Stateful.SetText(TextRole.TitleText, TableManager.GetText(LIDX_TITLE));
        if (Stateful.HasText(TextRole.GuideText1))
            Stateful.SetText(TextRole.GuideText1, TableManager.GetText(LIDX_DESC));
    }

    // 클리어 보상 미리보기(마지막 라운드 보상, 최대 8) — ObjectRole.CommonRewardItem(템플릿)을 보상 수만큼 복제 생성한다.
    //  (UIPopupCharacterCafeBaseRewardResult.SetRewardPreview 와 동일 패턴 — 동적 슬롯 + OnDisable 정리.)
    private void SetRewardPreview()
    {
        ClearSpawnedRewardSlots();

        List<RewardInfo> rewards = curInfo?.rewards;
        if (rewards.IsNullOrEmpty())
            return;

        if (!Stateful.HasObject(ObjectRole.CommonRewardItem)) return;
        if (!Stateful.TryGetObjectComponent(ObjectRole.CommonRewardItem, out CommonRewardItem template)) return;

        // 부모 = ObjectRole.RewardContainer(미배선 시 템플릿의 부모로 폴백).
        Transform container = Stateful.HasObject(ObjectRole.RewardContainer)
            ? Stateful.GetObject(ObjectRole.RewardContainer).Object.transform
            : template.transform.parent;

        // 템플릿 자체는 숨기고 복제본만 노출한다.
        template.gameObject.SetActive(false);

        int count = rewards.Count;
        for (int i = 0; i < count; ++i)
        {
            if (null == rewards[i])
                continue;

            GameObject obj = this.InstantiateScoped(template.gameObject, container);
            if (null == obj)
                continue;

            obj.transform.localScale = Vector3.one;
            obj.transform.localPosition = Vector3.zero;
            obj.SetActive(true);

            if (!obj.TryGetComponent(out CommonRewardItem slot))
                continue;

            slot.SetInfo(rewards[i]);
            BindRewardSlotInfo(slot); // [§3-5] 보상 터치 → 타입별 인포(장식물 제외)
            spawnedRewardSlots.Add(slot);
        }

        // 보상 개수별 크기 조정(§3-5) — 생성된 슬롯 수에 따라 그리드 셀 크기 스케일.
        ApplyRewardLayout(spawnedRewardSlots.Count);
    }

    // 클리어 보상 터치 → 타입별 인포(§3-5, 보물상자 §3-1과 동일: Block 7=인포/확률표 · BadgePack 10=뱃지 말풍선).
    //  장식물(Facility 등)·재화는 인포 연결 없음 → 인포 버튼 비활성. 슬롯의 인포 버튼(BaseButtonObj)에 직접 핸들러를 건다.
    private void BindRewardSlotInfo(CommonRewardItem slot)
    {
        if (null == slot)
            return;

        GameObject btnObj = slot.BaseButtonObj;
        if (null == btnObj || !btnObj.TryGetComponent(out Button infoButton))
            return;

        ItemType type = slot.ItemType;
        bool hasInfo = type == ItemType.Block || type == ItemType.BlockLimitedTerm || type == ItemType.BadgePack;

        infoButton.onClick.RemoveAllListeners();
        btnObj.SetActive(hasInfo);
        if (!hasInfo)
            return;

        CommonRewardItem captured = slot;
        infoButton.onClick.AddListener(() => CommonRewardItem.OnClickShowInfo(captured, null));
    }

    // 보상 개수별 크기 조정(§3-5) — 줄 수(ceil(개수/열))가 적을수록 셀을 크게(고정 폭 내 안전, 높이 자동).
    //  rewardGrid 미바인딩 시 graceful no-op(기존 고정 셀 유지). 기본 셀/간격은 첫 호출 시 캐시(스케일 기준).
    private void ApplyRewardLayout(int count)
    {
        if (null == rewardGrid || count <= 0)
            return;

        if (!rewardGridBaseCached)
        {
            baseRewardCellSize = rewardGrid.cellSize;
            baseRewardSpacing = rewardGrid.spacing;
            rewardGridBaseCached = true;
        }

        int columns = Mathf.Max(1, rewardGrid.constraintCount);
        int rows = Mathf.CeilToInt(count / (float)columns);
        float scale = rows <= 1 ? CELL_SCALE_1ROW : (rows == 2 ? CELL_SCALE_2ROW : CELL_SCALE_3ROW);

        rewardGrid.cellSize = baseRewardCellSize * scale;
        rewardGrid.spacing = baseRewardSpacing * scale;
    }

    // 동적 생성한 보상 미리보기 슬롯 제거 — 재생성(SetInfo 재진입) 및 OnDisable 시 호출.
    private void ClearSpawnedRewardSlots()
    {
        int count = spawnedRewardSlots.Count;
        for (int i = 0; i < count; ++i)
        {
            if (null != spawnedRewardSlots[i])
                Destroy(spawnedRewardSlots[i].gameObject);
        }
        spawnedRewardSlots.Clear();
    }

    // "좋아요!"/화면 터치(§3-5) — 팝업 닫고 메인 풍선 팝업 종료/복귀 콜백 호출(보상 실지급은 이미 서버에서 처리됨).
    private void OnClickConfirm()
    {
        Action onClosed = curInfo?.onClosed;
        Close();
        onClosed?.Invoke();
    }
}

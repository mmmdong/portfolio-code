using System.Collections.Generic;
using System.Threading;

using Cysharp.Threading.Tasks;

using GameCore.Utils;       // DLogger

using GameLogic.Define;     // IUIInfoData, RewardInfo
using GameLogic.Extension;  // IsNullOrEmpty
using GameLogic.Management; // LoadScopedAsync

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// FourDropItem(향기 가득 꽃다발 축제) 스페셜(축하) 팝업의 데이터 — 완성한 특별 아이템/보상 표시용(순수 view).
/// 값은 호출측(인게임)에서 주입한다. MergeEvent_Special 의 sBlockType 6(특별한 첫 획득 아이템) 참고.
///
/// Sprite 가 아니라 경로/RewardInfo 를 넘기는 이유: 프리팹이 아이콘을 <see cref="CommonRewardItem"/> 으로 저작해 뒀고
/// 그 컴포넌트가 스프라이트 로드를 스스로 하기 때문이다. 호출측이 미리 await 할 필요가 없어 팝업 오픈이 동기다.
/// </summary>
public struct PopupFourDropItemSpecialResultInform : IUIInfoData
{
    public string itemIconPath;         // 가운데 노출할 특별 아이템 아이콘 경로(Special.index → Block_Main.resourcePath)
    public List<RewardInfo> rewards;    // 보상 묶음(Special.value2 → MergeEvent_RewardGroup → MergeEvent_Reward) 전체. 순서대로 슬롯에 채운다.
}

/// <summary>
/// FourDropItem 스페셜(축하) 팝업. <see cref="UIBasePopup"/> 기반, 기획서 v17 §5-3 축하 팝업.
///
/// 특별한 아이템 완성 시 등장해 "특별한 완성!" 축하와 획득 보상을 보여주고 [좋아요]로 닫는다.
/// 본 클래스는 <b>순수 view</b> 다 — 보상 지급은 호출측(MergeEvent.TryTriggerFourDropItemAcquire)이 팝업보다 먼저 확정한다.
/// 그래서 이 팝업이 취소·파괴되더라도 지급은 유실되지 않는다.
/// </summary>
public class UIPopupFourDropItemSpecialResult : UIBasePopup
{
    //[v17 §5-3] 보상 '수량' 노출 최소치. v16 은 1 이었다 — 값이 바뀌는 지점이라 상수로 못박는다.
    //⚠️ 임계가 거르는 것은 '수량 텍스트'뿐이다. 보상 아이콘은 수량과 무관하게 항상 노출된다(기획서 문면: "보상 아이템 이미지와 수량 노출 / 수량은 2이상일 때만 노출").
    //  v16 해석(수량 미달이면 보상을 통째로 숨김)은 오독이었다.
    public const int REWARD_VISIBLE_MIN_COUNT = 2;

    //가운데 특별 아이템의 아이콘 표면.
    //🔴 [2026-08-13] 종전 선언은 CommonRewardItem 이었는데 **프리팹에 그 컴포넌트가 없어 영영 미바인딩**이었다(NRE 원인).
    //  아트는 이 자리를 CommonRewardItem 이 아니라 **머지 블록 프리팹 인스턴스**로 저작했다 —
    //  Contents/Center/Grade/mergeBlockItem_root/EventBlockFourDropItem_FlowerGarden
    //  (MergeEvent_Resources.blockPrefabPath 와 같은 프리팹이라 보드의 블록과 같은 그림이 나온다).
    //  그 안 EventBlockView 의 Image 가 스프라이트 없이 비어 있어 그곳이 곧 아이콘 표면이다 → 타입을 실물에 맞춘다.
    //  블록 로직(EventBlockMain)은 blockData 없이는 Awake 에서 이벤트 구독만 하고 UpdateBlockData 가 불리지 않아
    //  이 Image 를 직접 채워도 덮어써지지 않는다(실측 확인).
    //경로는 Block_Main.resourcePath 라 아이템 타입 기반 규칙(ResourceUtils.GetSpritePath)과 달라 직접 로드한다.
    [Header("Item")]
    [SerializeField] private Image centerItemImage;

    //보상 슬롯. 프리팹에 4개가 저작돼 있고 런타임 증감이 없어 List 가 아니라 배열이다.
    //기획서 §5-3 이 보상 여러 개를 상정하므로 슬롯 수만큼 순서대로 채우고 남는 슬롯은 숨긴다.
    [Header("Rewards")]
    [SerializeField] private GameObject rewardRoot;
    [SerializeField] private CommonRewardItem[] rewardItems;

    [Header("Buttons")]
    [SerializeField] private UIButtonEx closeButton;    // 닫기("좋아요!") — Btn_Close

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);

        if (data == null)
        {
            Close();
            return;
        }

        var inform = (PopupFourDropItemSpecialResultInform)mData;
        Refresh(inform);
    }

    protected override void Start()
    {
        base.Start();

        closeButton.onClick.AddListener(OnClickClose);
    }

    protected override void OnDestroy()
    {
        closeButton.onClick.RemoveListener(OnClickClose);

        base.OnDestroy();
    }

    // 완성한 특별 아이템 이미지와 보상(이미지+수량)을 표시한다.
    private void Refresh(PopupFourDropItemSpecialResultInform inform)
    {
        //로드는 비동기라 기다리지 않는다 — 보상 표시가 아이콘 로드를 기다릴 이유가 없다(순수 view).
        LoadCenterItemIconAsync(inform.itemIconPath, gameObject.GetCancellationTokenOnDestroy()).Forget();

        int rewardCount = inform.rewards.IsNullOrEmpty() ? 0 : inform.rewards.Count;
        //보상이 아예 없으면(RewardGroup 미발행·빈 묶음) 보상 영역을 통째로 끄고 축하만 띄운다.
        rewardRoot.SetActive(rewardCount > 0);

        if (rewardCount > rewardItems.Length)
            DLogger.Error($"[FourDropItem] 축하 팝업 보상 슬롯이 부족하다 — 보상 {rewardCount}건 / 슬롯 {rewardItems.Length}개. 초과분은 표시되지 않는다(프리팹에 슬롯 추가 필요)");

        //수량 표시 규칙을 한 번만 만들어 전 슬롯이 공유한다.
        //CommonRewardItem.CountDisplayContext.ShowOverOne() 이 같은 규칙(value > 1)이지만 임계가 공용 코드에 박혀 있어,
        //기획이 임계를 다시 바꾸면 그쪽은 고칠 수 없다 → 우리 상수로 컨텍스트를 만들어 넘긴다.
        CommonRewardItem.CountDisplayContext countDisplay = new()
        {
            countDisplayType = CommonRewardItem.ECountDisplayType.ShowOverTargetValue,
            displayOverValue = REWARD_VISIBLE_MIN_COUNT - 1,
        };

        for (int i = 0; i < rewardItems.Length; ++i)
        {
            //남는 슬롯은 null 을 넘겨 숨긴다(CommonRewardItem.SetInfoWithCountDisplayContext 가 info == null 이면 SetActive(false)).
            RewardInfo reward = i < rewardCount ? inform.rewards[i] : null;
            rewardItems[i].SetInfoWithCountDisplayContext(countDisplay, reward);
        }
    }

    /// <summary>
    /// 가운데 특별 아이템 아이콘을 경로로 로드해 그린다(대상은 <see cref="centerItemImage"/> 주석 참조).
    /// 실패·취소해도 팝업의 나머지(축하 문구·보상·닫기)는 그대로 동작한다 — 아이콘만 비는 것이 조용히 죽는 것보다 낫다.
    /// </summary>
    private async UniTaskVoid LoadCenterItemIconAsync(string iconPath, CancellationToken ct)
    {
        //경로가 비어 있으면(Special.index 로 Block_Main 을 못 찾은 경우) 저작 상태 그대로 둔다.
        if (string.IsNullOrEmpty(iconPath)) return;

        Sprite sprite = await this.LoadScopedAsync<Sprite>(iconPath, ct);
        //로드 대기 중 팝업이 닫혔을 수 있다 — Unity 의 fake-null 을 통과시키지 않도록 == 로 본다.
        if (this == null || sprite == null) return;

        centerItemImage.sprite = sprite;
    }

    private void OnClickClose()
    {
        Close();
    }
}

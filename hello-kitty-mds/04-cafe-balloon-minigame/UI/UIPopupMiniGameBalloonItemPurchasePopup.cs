using System;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;
using GameCore.Utility;
using GameCore.Utils;
using GameLogic;
using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;
using GameLogic.Network;
using GameLogic.Network.FakeServer.FsDataHandler.LiveEvent;

using StatefulUISupport.Scripts.Components;

using UnityEngine;

// 캐릭터 카페 2차 서브콘텐츠 — 우사하나 풍선 게임 "재화 부족 팝업"(purchase-modal, 상세 892698729 §3-6)
// 풍선 터뜨리기 재화(꽃) 부족 시 노출. 미니게임 테이블 상점 슬롯(다이아/광고)으로 이벤트 재화를 구매하거나
// 머지 보드/상점으로 이동하도록 유도한다. 구매는 Content.TryBuyMiniGameShop 으로 위임(서버 권위).
public class UIPopupMiniGameBalloonItemPurchasePopup : UIBasePopup
{
    private const LiveEventType EVENT_TYPE = LiveEventType.CHARACTERCAFE;

    // [로컬라이징 LIdx] 기획 892698729 §3-6 재화 부족 팝업
    //   제목(43175 "아이템 부족!")은 비가변 → 프리팹 UITextEx(TitleText)에 직접 바인딩(인스펙터). 본 코드에서 설정하지 않음.
    private const int LIDX_DESC = 43176;  // 가변 "{0}이 부족합니다.\n다이아를 사용해 구매해보세요!" ({0}=이벤트 재화 아이콘 sprite 태그, Setting.eventIcon 자동 매칭)
    private const int LIDX_HINT = 43179;  // 가변 "머지 미션을 완료하고, {0}을 모으세요!" ({0}=이벤트 재화 아이콘 sprite 태그, TextDesc2/GuideText2)
    // 구매 실패 사유별 처리: 다이아 부족 = 다이아 상점 숏컷(WrapLinkToShopByLackDia), 그 외(일일 한도 초과 등) = 한도 소진 토스트.
    //  (구 LIDX_BUY_FAIL=0 단일 폴백 토스트 폐기 — 사유 미구분으로 비현지화 문구가 노출되던 것을 사유별 분기로 교체)
    private const int LIDX_BUY_LIMIT = 23506; // 일일/상품 구매 한도 소진 토스트 (MyDreamPartner 공용 스트링)

    // [ISSUE-74] 이벤트 재화 구매 재화 상승 연출 — 아이콘 버스트 수/비행 시간.
    private const int CURRENCY_FX_MAX_ICON = 10;          // 재화 상승 연출 아이콘 최대 수
    private const float CURRENCY_FX_MOVE_DURATION = 0.6f; // 재화 상승 비행 시간(초)

    // [ISSUE-75] 미니게임(풍선) 광고 시청 시 에어브릿지 adImpression Label 지정값(컨플 928645121, 기획 요청).
    private const string AD_IMPRESSION_LABEL = "cafeminigame";

    public class Info : IUIInfoData
    {
        public ContentEventCharacterCafe content; // 구매 위임 대상 (TryBuyMiniGameShop)
        public int objectIdx;                     // 미니게임을 띄운 오브젝트 (objectEventIdx 연결)
        public Action onPurchased;                // 구매 성공 시 호출 (메인 팝업 재화 갱신)
    }

    [SerializeField] private UIMiniGameBalloonPurchaseItem[] purchaseItems; // 상점 슬롯(테이블 배열 3, 인스펙터 고정 바인딩)
    [SerializeField] private UITextEx diaCurrencyText; // 상단 보유 다이아 표시(CurrencyDia) — 구매 시 최신화
    [SerializeField] private Transform eventResourceAnchor; // [ISSUE-74] 구매 재화 상승 연출 목적지(보유 재화 UI = EventResource 노드)

    private Info curInfo;
    private int objectEventIdx; // curInfo.objectIdx(Event_CharacterCafeObject.index) → 미니게임 인덱스. 미니게임 테이블 조회 키(첫 SetInfo 시 캐싱)

    protected override void OnEnable()
    {
        base.OnEnable();
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.AddButtonListener(ButtonRole.Close, Close);
        if (Stateful.HasButton(ButtonRole.GoMerge))
            Stateful.AddButtonListener(ButtonRole.GoMerge, OnClickGoMerge);
        if (Stateful.HasButton(ButtonRole.GoShop))
            Stateful.AddButtonListener(ButtonRole.GoShop, OnClickGoShop);
    }

    protected override void OnDisable()
    {
        if (Stateful.HasButton(ButtonRole.Close))
            Stateful.RemoveButtonAllListener(ButtonRole.Close);
        if (Stateful.HasButton(ButtonRole.GoMerge))
            Stateful.RemoveButtonAllListener(ButtonRole.GoMerge);
        if (Stateful.HasButton(ButtonRole.GoShop))
            Stateful.RemoveButtonAllListener(ButtonRole.GoShop);
        base.OnDisable();
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);
        curInfo = data as Info;
        if (null == curInfo || null == curInfo.content)
        {
            DLogger.Error($"[{GetType().Name}] invalid info");
            return;
        }

        // 미니게임 상점 테이블(GetCharacterCafeMiniGameDatas) 조회 키 = objectEventIdx.
        // 진입 오브젝트(Event_CharacterCafeObject)에서 미니게임 인덱스를 해석해 캐싱한다.
        objectEventIdx = ResolveObjectEventIdx(curInfo.objectIdx);

        RefreshTexts();
        RefreshCurrency();
        BuildShopItems();
    }

    private void RefreshTexts()
    {
        // 제목(43175)은 비가변 → 프리팹 UITextEx(TitleText)에서 직접 바인딩(인스펙터). 코드에서 설정하지 않는다.

        // 재화 아이콘 = 구매 비행 연출(PlayCurrencyAcquireFxAsync)과 동일 소스(ResourceUtils → Setting.eventIcon, 미입력 시 Item 테이블 폴백).
        //  본문(43176)·안내(43179) 두 가변 문구가 공유하므로 1회만 해석한다.
        string eventIcon = ResourceUtils.GetSpritePath(ItemType.Currency, (int)CurrencyType.CharacterCafeEventCurrency);
        string eventIconSprite = string.Format(EventCharacterCafeHelper.EVENT_CURRENCY_SPRITE_TAG_FORMAT, eventIcon);

        // 내용(43176) — {0}=이벤트 재화 아이콘(sprite 태그). 아이콘은 활성 카페 Setting.eventIcon 으로 캐릭터별 자동 매칭(ISSUE-76).
        if (Stateful.HasText(TextRole.Text))
        {
            string descFormat = LIDX_DESC > 0 ? TableManager.GetText(LIDX_DESC) : "{0}이 부족합니다.\n다이아를 사용해 구매해보세요!";
            Stateful.SetText(TextRole.Text, string.Format(descFormat, eventIconSprite));
        }

        // 안내(43179, TextDesc2/GuideText2) — {0}=이벤트 재화 아이콘(sprite 태그). "머지 미션을 완료하고, {0}을 모으세요!"
        if (Stateful.HasText(TextRole.GuideText2))
        {
            string hintFormat = LIDX_HINT > 0 ? TableManager.GetText(LIDX_HINT) : "머지 미션을 완료하고, {0}을 모으세요!";
            Stateful.SetText(TextRole.GuideText2, string.Format(hintFormat, eventIconSprite));
        }
    }

    private void RefreshCurrency()
    {
        if (Stateful.HasText(TextRole.EventCurrencyText))
            Stateful.SetText(TextRole.EventCurrencyText, EventCurrencyHelper.GetEventCurrency(EVENT_TYPE));

        // 상단 보유 다이아 — 현재 보유량으로 최신화(구매로 다이아 소모 시 DoBuy → RefreshCurrency 재호출로 반영).
        if (null != diaCurrencyText)
            diaCurrencyText.SetText($"{DataManager.Instance.GetDiamond()}");
    }

    // 미니게임 테이블 첫 행(이벤트 단위 상점 구성)으로 상점 슬롯 구성. 일일 구매횟수는 오브젝트별 진행 데이터에서 차감.
    private void BuildShopItems()
    {
        if (purchaseItems.IsNullOrEmpty())
            return;

        int round = GetCurrentRound();
        var shopTable = GetShopTable(round);
        var goodsCounts = shopTable?.shopEventGoodsCount;
        var buyTypes = shopTable?.shopBuyType;
        var buyValues = shopTable?.shopBuyValue;
        var buyLimits = shopTable?.shopBuyLimit;
        var slotCount = purchaseItems.Length;

        // 일일 구매 횟수는 서브컨텐츠(세션 영속)가 소유 — 스냅샷 기반 transient 모델은 호출마다 재생성돼 카운트가 유지되지 않는다(광고/구매 한도 미동작).
        CharacterCafeMiniGameSubContent mini = curInfo?.content?.MiniGameSubContent;

        for (var i = 0; i < slotCount; i++)
        {
            var item = purchaseItems[i];
            if (null == item)
                continue;

            // 테이블 상품 수보다 많은 슬롯은 비활성
            if (goodsCounts == null || !goodsCounts.IsInOfRange(i))
            {
                item.SetData(null);
                continue;
            }

            int buyLimit = buyLimits.IsInOfRange(i) ? buyLimits[i] : 0;
            var itemInfo = new UIMiniGameBalloonPurchaseItem.Info
            {
                shopIndex = i,
                goodsCount = goodsCounts[i],
                buyType = buyTypes.IsInOfRange(i) ? buyTypes[i] : 0,
                buyValue = buyValues.IsInOfRange(i) ? buyValues[i] : 0,
                buyLimit = buyLimit,
                remainCount = (null != mini) ? mini.GetShopRemainCount(curInfo.objectIdx, round, i, buyLimit) : -1,
                onBuy = OnClickBuy,
            };
            item.SetData(itemInfo);
        }
    }

    // 구매 클릭 — 다이아 타입은 사용 확인 팝업 경유, 광고 타입은 광고 시청 완료 후 구매(서버 핸들러가 광고 타입은 다이아 미차감 처리).
    private void OnClickBuy(int shopIndex)
    {
        var shopTable = GetShopTable();
        if (null == shopTable)
            return;

        var buyType = shopTable.shopBuyType.IsInOfRange(shopIndex)
            ? shopTable.shopBuyType[shopIndex]
            : UIMiniGameBalloonPurchaseItem.BUY_TYPE_DIA;

        if (buyType == UIMiniGameBalloonPurchaseItem.BUY_TYPE_AD)
            ShowAdThenBuy(shopIndex);
        else
            OpenDiaConfirm(shopIndex, shopTable.shopBuyValue.IsInOfRange(shopIndex) ? shopTable.shopBuyValue[shopIndex] : 0);
    }

    // 다이아 구매 — 사용 확인 팝업(UIPopupDiaConfirm) 경유 후 구매.
    //  [선검사] 송신 전 다이아 보유량 검사 — 부족하면 확인 팝업 대신 표준 "다이아 부족 → 다이아 상점" 숏컷으로 유도한다
    //  (에너지/머지/상점 등 코드베이스 공통 WrapLinkToShopByLackDia 패턴. 확인 팝업은 잔액 검사를 하지 않으므로 여기서 선검사).
    private void OpenDiaConfirm(int shopIndex, int price)
    {
        if (price > 0 && DataManager.Instance.GetCurrencyCount((int)CurrencyType.Diamond) < price)
        {
            LinkToShopByLackDia();
            return;
        }

        UIManager.OpenUIMsgAsync<UIPopupDiaConfirm>(new UIPopupDiaConfirm.InfoData
        {
            diaConfirmType = UIPopupDiaConfirm.EDiaConfirmType.ShopPurchase,
            price = price,
            callBackPurchase = _ => DoBuy(shopIndex),
        }).Forget();
    }

    // 광고 구매 — 광고 시청 완료 시 구매(핸들러가 광고 타입은 재화만 지급). 광고 불가/실패 시 안내 팝업.
    private void ShowAdThenBuy(int shopIndex)
    {
        AdvertisementManager.Instance.ShowRewardedVideo(new AdvertisementInfoData(AdsDailyTriggerType.Ads_PurchaseDaily, -1, resultType =>
        {
            switch (resultType)
            {
                case AdvertisementManager.EResultType.OnAdRewarded:
                    EventCharacterCafeHelper.SendCafeAdLog(); // [운영툴 로그] 미니게임(풍선) 재화 구매 광고 시청 완료
                    DoBuy(shopIndex);
                    break;
                case AdvertisementManager.EResultType.OnAdUnavailable:
                case AdvertisementManager.EResultType.OnAdShowFaile:
                    UIManager.OpenUIMsgAsync<UINotifyMessage>(new NotifyMessageInfo(TableManager.GetText(AdvertisementManager.STR_KEY_UNAVAILABLE), null)).Forget();
                    break;
            }
        }, adImpressionLabel: AD_IMPRESSION_LABEL));
    }

    private void DoBuy(int shopIndex)
    {
        if (null == curInfo || null == curInfo.content)
            return;

        CharacterCafeMiniGameSubContent mini = curInfo.content.MiniGameSubContent;
        if (null == mini || !mini.TryBuyMiniGameShop(curInfo.objectIdx, shopIndex, out BuyMiniGameShopResult buyResult))
        {
            // 선검사를 통과했어도(확인 팝업 사이 잔액 변동 등 레이스) 핸들러가 false 를 반환할 수 있다 → 사유별 안내.
            //  · 다이아 부족 → 다이아 상점 숏컷(WrapLinkToShopByLackDia)
            //  · 그 외(일일 한도 초과 등, 버튼 비활성으로 1차 차단되지만 방어) → 한도 소진 토스트(23506)
            if (IsLackDiaForShop(shopIndex))
                LinkToShopByLackDia();
            else
                ShowToastMsg(LIDX_BUY_LIMIT);
            return;
        }

        RefreshCurrency();
        BuildShopItems();
        curInfo.onPurchased?.Invoke();

        // [ISSUE-74] 이벤트 재화 구매 시 재화 상승 연출 — 구매 슬롯 → 상단 재화 UI(CurrencyIcon) 비행.
        PlayCurrencyAcquireFx(shopIndex, buyResult);
    }

    // [ISSUE-74] 이벤트 재화 구매 재화 상승 연출 — 구매 슬롯 → 본 팝업의 EventResource(보유 재화 UI)로 이벤트 재화 아이콘 비행.
    private void PlayCurrencyAcquireFx(int shopIndex, BuyMiniGameShopResult buyResult)
    {
        int grantCount = buyResult?.grantedCurrency ?? 0;
        if (grantCount <= 0 || null == eventResourceAnchor || !purchaseItems.IsInOfRange(shopIndex) || null == purchaseItems[shopIndex])
            return;

        PlayCurrencyAcquireFxAsync(purchaseItems[shopIndex].transform.position, eventResourceAnchor.position, grantCount).Forget();
    }

    // 이벤트 재화(218) 아이콘을 시작점 → 목적지(EventResource)로 비행(공용 EffectHelper.CreateImageObjectTargetAsync). 본 팝업 파괴 시 취소.
    private async UniTaskVoid PlayCurrencyAcquireFxAsync(Vector3 startPos, Vector3 destPos, int grantCount)
    {
        UIWindowEffectAcquireFX fx = await UIManager.OpenUIMsgAsync<UIWindowEffectAcquireFX>(null, ct: destroyCancellationToken);
        if (this == null || null == fx || null == fx.FxParentTransform)
            return;

        string iconPath = ResourceUtils.GetSpritePath(ItemType.Currency, (int)CurrencyType.CharacterCafeEventCurrency);
        int count = Mathf.Clamp(grantCount, 1, CURRENCY_FX_MAX_ICON);
        UniTask[] flies = new UniTask[count];
        for (int i = 0; i < count; i++)
            flies[i] = EffectHelper.CreateImageObjectTargetAsync(iconPath, fx.FxParentTransform, startPos, destPos, CURRENCY_FX_MOVE_DURATION, destroyCancellationToken);
        await UniTask.WhenAll(flies);
    }

    // 해당 상점 슬롯이 다이아 구매형이고 보유 다이아가 가격 미만인지 — 구매 실패 사유 분기용(다이아 부족 vs 한도 초과 등).
    private bool IsLackDiaForShop(int shopIndex)
    {
        var shopTable = GetShopTable();
        if (null == shopTable)
            return false;

        int buyType = shopTable.shopBuyType.IsInOfRange(shopIndex)
            ? shopTable.shopBuyType[shopIndex]
            : UIMiniGameBalloonPurchaseItem.BUY_TYPE_DIA;
        if (buyType != UIMiniGameBalloonPurchaseItem.BUY_TYPE_DIA)
            return false; // 광고형은 다이아 미소모

        int price = shopTable.shopBuyValue.IsInOfRange(shopIndex) ? shopTable.shopBuyValue[shopIndex] : 0;
        return price > 0 && DataManager.Instance.GetCurrencyCount((int)CurrencyType.Diamond) < price;
    }

    // 다이아 부족 표준 처리 — 다이아 상점으로 숏컷(에너지/머지/상점 등 코드베이스 공통 패턴).
    private void LinkToShopByLackDia()
    {
        UIManager.Instance.FindUIWindow<UIWindowLobbyMain>()?.WrapLinkToShopByLackDia();
    }

    private void ShowToastMsg(int lidx)
    {
        var msg = lidx > 0 ? TableManager.GetText(lidx) : "구매할 수 없어요.";
        UIManager.OpenUIMsgAsync<UIHUDToast>(new ToastData(null, msg, 1.0f)).Forget();
    }

    // 머지 보드로 이동 (플로우차트 B: 머지 보드에 진입) — Content 위임 후 카페/미니게임 팝업 전체 종료
    //  [ISSUE-77] 본 구매 팝업만 닫으면 카페·미니게임 팝업이 머지판 위에 남으므로, 카페 관련 팝업을 일괄 종료한다(본 팝업 포함).
    private void OnClickGoMerge()
    {
        curInfo?.content?.MoveToMergeBoard();
        EventCharacterCafeHelper.CloseAllPopup();
    }

    // 상점으로 이동 (플로우차트 B: 다이아 상점 진입)
    //  탭 미지정으로 열면 기본 탭(매일 상점)으로 떨어지므로, 토스트 없이 다이아 충전 탭으로 직접 진입시킨다
    //  (LobbyMain 위임 → 본 팝업이 닫혀도 진입 유지).
    private void OnClickGoShop()
    {
        UIManager.Instance.FindUIWindow<UIWindowLobbyMain>()?.WrapLinkToShopDia();
        Close();
    }

    // [ISSUE-65] 상점 구성은 라운드별 — 현재 진행 라운드의 미니게임 테이블 행에서 읽는다(풍선 팝업 popCost/컬럼 조회와 동일 규약).
    //  (구 rounds[0] 고정 = 모든 라운드에서 1라운드 상점이 노출되던 버그)
    private EventCharacterCafeMiniGameTableData GetShopTable() => GetShopTable(GetCurrentRound());

    private EventCharacterCafeMiniGameTableData GetShopTable(int round)
        => TableManager.Instance.GetCharacterCafeMiniGameData(objectEventIdx, round);

    // 현재 진행 라운드(1-base, 폴백 1) — 미니게임 진행 모델(curRound) 기준. 풍선 팝업 HasEnoughCurrencyToPop 과 동일 규약으로 맞춘다.
    private int GetCurrentRound()
    {
        int round = EventCharacterCafeHelper.GetMiniGameData(curInfo.objectIdx)?.curRound ?? 1;
        return round > 0 ? round : 1;
    }

    // 진입 오브젝트(Event_CharacterCafeObject.index = objectIdx)에서 미니게임 인덱스(objectEventIdx)를 해석한다.
    // 미니게임 상점 테이블 조회(GetCharacterCafeMiniGameDatas) 의 키로 사용된다. 미정의 시 0(폴백).
    private int ResolveObjectEventIdx(int objectIdx)
    {
        if (TableManager.GetData<EventCharacterCafeObjectTableData>(objectIdx, out EventCharacterCafeObjectTableData objectTable))
            return objectTable.objectEventIdx;

        DLogger.Error($"[{GetType().Name}] not found object table : objectIdx {objectIdx}");
        return 0;
    }
}

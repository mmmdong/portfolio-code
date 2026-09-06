using System;
using System.Threading;

using Cysharp.Threading.Tasks;
using GameCore.Utility;
using GameLogic;
using GameLogic.Extension;
using GameLogic.Management;

using StatefulUI.Runtime.Core;
using StatefulUISupport.Scripts.Components;

using UnityEngine;

// 캐릭터 카페 2차 서브콘텐츠 — 재화 부족 팝업(§3-6) 상점 슬롯 1칸.
// 슬롯 UI는 StatefulUI(StatefulComponent)로 구성되어 있어, 본 클래스는 슬롯의 StatefulComponent 를
// 역할(BuyButton / ItemCountText / PriceCountText / RewardCountText / BuyDia·BuyAd·BuyDisable)로 구동한다.
// 실제 재화 지급/차감은 팝업이 Content.TryBuyMiniGameShop 으로 위임 — 본 클래스는 표시와 클릭 전달만 담당한다.
public class UIMiniGameBalloonPurchaseItem : MonoBehaviour
{
    // 구매 타입 규약 (서버/테이블 shopBuyType 와 동일) — 0=다이아, 1=광고
    public const int BUY_TYPE_DIA = 0;
    public const int BUY_TYPE_AD = 1;

    // [로컬라이징 LIdx] 일일 구매 제한 표시 (Confluence 892698729 §3-6)
    private const int LIDX_DAILY_LIMIT = 43178; // "하루 {0}회"
    private const int LIDX_FREE = 10112;        // "무료" (shopBuyValue 0 = 무료, 0 초과 = 가격 노출)
    private const int LIDX_REMAIN_TIME = 21603; // "남은 시간 : " (일일 리셋 카운트다운 접두, 기획 §3-6 ④)

    public class Info
    {
        public int shopIndex;     // 상품 배열 인덱스 (0~2)
        public int goodsCount;    // 지급 이벤트 재화 수량
        public int buyType;       // 0=다이아, 1=광고
        public int buyValue;      // 다이아 비용 (광고는 0)
        public int buyLimit;      // 0=무제한, 1~=하루 n회
        public int remainCount;   // 남은 구매 가능 횟수 (무제한이면 -1)
        public Action<int> onBuy; // 구매 클릭 콜백 (shopIndex)
    }

    [SerializeField] private StatefulComponent stateful; // 슬롯 자체 StatefulComponent (Awake 폴백)

    private Info curInfo;
    private CancellationTokenSource resetTimerCts; // 일일 리셋 "남은 시간" 카운트다운

    private void Awake()
    {
        if (null == stateful)
            stateful = GetComponent<StatefulComponent>();
    }

    private void Start()
    {
        if (null != stateful && stateful.HasButton(ButtonRole.BuyButton))
            stateful.AddButtonListener(ButtonRole.BuyButton, OnClickBuy);
    }

    private void OnDestroy()
    {
        if (null != stateful && stateful.HasButton(ButtonRole.BuyButton))
            stateful.RemoveButtonAllListener(ButtonRole.BuyButton);
        CancelResetTimer();
    }

    public void SetData(Info info)
    {
        curInfo = info;

        // 테이블에 없는 슬롯은 비활성 (상품이 3개 미만일 때)
        if (null == info)
        {
            gameObject.SetActive(false);
            return;
        }
        gameObject.SetActive(true);

        if (null == stateful)
            stateful = GetComponent<StatefulComponent>();
        if (null == stateful)
            return;

        var isAd = info.buyType == BUY_TYPE_AD;
        var limited = info.buyLimit > 0;
        var soldOut = limited && info.remainCount <= 0;

        // 구매 타입/소진에 따른 상태 전환: 소진 > 광고 > (다이아 제한구매) > 다이아
        //  다이아 + 일일 한도 보유 = 제한 구매(CountDia, §4-14). CountDia 미정의 프리팹은 BuyDia 로 폴백.
        // [ISSUE-68] 상태 전환을 텍스트보다 먼저 적용한다 — SetState 가 Localize 항목을 재적용(UITextEx 자동 로컬라이즈)하므로,
        //  텍스트를 나중에 써야 코드값("하루 N회")이 포맷 미치환 원본("하루 {0}회"/"{0} 남음")에 덮이지 않는다.
        StateRole stateRole;
        if (soldOut)
            stateRole = StateRole.BuyDisable;
        else if (isAd)
            stateRole = StateRole.BuyAd;
        else if (limited && stateful.HasState((int)StateRole.CountDia))
            stateRole = StateRole.CountDia;
        else
            stateRole = StateRole.BuyDia;
        stateful.SetState((int)stateRole);

        // 지급 이벤트 재화 개수
        if (stateful.HasText(TextRole.ItemCountText))
            stateful.SetText(TextRole.ItemCountText, info.goodsCount);

        // 다이아 비용 (광고는 표시하지 않음). shopBuyValue 0=무료, 0 초과=가격 노출.
        //  [ISSUE-68] 프리팹 baked "무료"(LIdx 10112)를 코드가 가격으로 덮는다 — 유료 슬롯은 가격이 보여야 함(기획 §3-6 ④ "필요 재화 수량").
        if (!isAd && stateful.HasText(TextRole.PriceCountText))
            stateful.SetText(TextRole.PriceCountText, info.buyValue > 0 ? $"{info.buyValue}" : TableManager.GetText(LIDX_FREE));

        // 일일 구매 제한 표시 ("하루 {0}회", {0}=shopBuyLimit), 무제한이면 빈 문자열.
        //  다이아 슬롯=RewardCountText 노드, 광고 슬롯=BtnFree 노드를 사용하므로 둘 다 채운다(ISSUE-68).
        var limitText = limited ? GetDailyLimitText(info.buyLimit) : string.Empty;
        if (stateful.HasText(TextRole.RewardCountText))
            stateful.SetText(TextRole.RewardCountText, limitText);
        if (stateful.HasText(TextRole.BtnFree))
            stateful.SetText(TextRole.BtnFree, limitText);

        // 한도 소진 시 구매 버튼 클릭 차단
        if (stateful.HasButton(ButtonRole.BuyButton))
        {
            var button = stateful.GetButton(ButtonRole.BuyButton).Button;
            if (null != button)
                button.interactable = !soldOut;
        }

        // 한도 소진 시 일일 리셋까지 "남은 시간" 카운트다운 표시
        UpdateResetTimer(soldOut);
    }

    private string GetDailyLimitText(int buyLimit)
    {
        return LIDX_DAILY_LIMIT > 0 ? string.Format(TableManager.GetText(LIDX_DAILY_LIMIT), buyLimit) : $"하루 {buyLimit}회";
    }

    // 일일 리셋 "남은 시간"(다음 UTC 자정까지). 소진 상태일 때만 카운트다운, 아니면 빈 문자열.
    private void UpdateResetTimer(bool soldOut)
    {
        CancelResetTimer();

        if (!stateful.HasText(TextRole.TimerText))
            return;

        if (!soldOut)
        {
            stateful.SetText(TextRole.TimerText, string.Empty);
            return;
        }

        resetTimerCts = CancellationTokenSource.CreateLinkedTokenSource(gameObject.GetCancellationTokenOnDestroy());
        RunResetTimerAsync(resetTimerCts.Token).Forget();
    }

    private async UniTaskVoid RunResetTimerAsync(CancellationToken token)
    {
        var resetTime = DataManager.Instance.GetCurrentTime().Date.AddDays(1); // 다음 UTC 자정
        while (!token.IsCancellationRequested)
        {
            var remain = resetTime - DataManager.Instance.GetCurrentTime();
            if (remain.TotalSeconds <= 0)
            {
                stateful.SetText(TextRole.TimerText, FormatRemainText(TimeSpan.Zero));
                break;
            }

            stateful.SetText(TextRole.TimerText, FormatRemainText(remain));
            await UniTask.Delay(1000, cancellationToken: token).SuppressCancellationThrow();
        }
    }

    // 기획 §3-6 ④: 60분 초과 = hh mm / 60분 이하 = mm ss
    // 기획 §3-6 ④: "남은 시간 : "(21603) + 60분 초과 = hh mm / 60분 이하 = mm ss
    private string FormatRemainText(TimeSpan remain)
    {
        string time = remain.TotalMinutes > 60d
            ? $"{(int)remain.TotalHours:00}:{remain.Minutes:00}"
            : $"{remain.Minutes:00}:{remain.Seconds:00}";
        return $"{TableManager.GetText(LIDX_REMAIN_TIME)}{time}";
    }

    private void CancelResetTimer()
    {
        if (null == resetTimerCts)
            return;
        resetTimerCts.Cancel();
        resetTimerCts.Dispose();
        resetTimerCts = null;
    }

    private void OnClickBuy()
    {
        if (null == curInfo)
            return;
        curInfo.onBuy?.Invoke(curInfo.shopIndex);
    }
}

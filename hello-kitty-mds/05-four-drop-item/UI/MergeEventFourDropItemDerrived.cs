using System;
using System.Collections.Generic;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management.UISupport;
using GameLogic.Network;

using UnityEngine;

namespace GameLogic.MergeEvent.SubObject
{
    /// <summary>
    /// [검수 시트 row 30] FourDropItem 파생 컨트롤러 — <b>상단 이벤트 재화 HUD</b>(테마01 표기: 물뿌리개).
    /// 검수 요구: *"재화 획득 시 상단에 재화 UI 등장 필요"*.
    ///
    /// 타 머지이벤트는 이 자리에 파생 컨트롤러가 있어 상단 재화 표시·획득 연출·투사체 도착점이 전부 붙어 있는데
    /// (BossRaid = <c>MergeEventBossRaidDerrived</c> / GetPoint = <c>MergeEventGetPointDerrived</c>),
    /// FourDropItem 만 <c>MergeEvent.derrivedController</c> 가 <b>미바인딩</b>이라 그 경로가 통째로 죽어 있었다.
    /// 그래서 재화 블록을 먹을 때마다 <c>MergeEventHelper.OnSequenceGainCurrency</c> 가 빈 시퀀스를 돌려주고
    /// <c>DLogger.Error</c> 만 쌓였다(<c>GetProjectileTargetPos</c> 도 <c>Vector3.zero</c> 를 반환했다).
    ///
    /// 🔴 <b>호출 배선은 이미 완비돼 있다 — 이 클래스를 프리팹에 붙이는 것으로 끝난다.</b>
    /// <c>MergeEvent.UpdateDerrivedController()</c> 가 재화가 바뀌는 세 지점(<c>GainCurrency</c> · <c>PeakingClick</c> ·
    /// <c>UseGrowCurrency</c>)과 팝업 초기화에서 이미 <see cref="SetInfo"/> 를 호출한다.
    ///
    /// ⚠️ <b>무한모드 HUD(계획서 `K1`)와의 관계</b> — <c>MergeEventGetPointHelper.GetDerrivedController()</c> 는
    /// <c>as MergeEventGetPointDerrived</c> 로 캐스팅해 무한 페이즈 진행도를 그린다. 이 클래스는 그 타입이 아니므로
    /// 그 캐스팅은 계속 null 이다 — <b>다만 종전에도 필드 자체가 null 이라 결과가 같아 회귀가 아니다.</b>
    /// 무한모드 HUD 를 붙일 때는 이 컨트롤러가 그 역할까지 겸하도록 확장하거나, 페이즈별로 갈아끼우는 설계가 필요하다.
    ///
    /// 표시 대상은 <c>FsMergeEventState.currencyPoint</c> 하나이고, 하단 팻말(<c>MergeEventFourDropItemCurrencyText</c>)과
    /// <b>같은 값·같은 표기</b>를 쓴다 — 두 곳이 다르면 유저는 어느 쪽이 진짜인지 알 수 없다.
    /// 아이콘은 프리팹 저작물을 그대로 둔다(코드에서 로드하지 않는다) — 재화 아이콘 주소가 `.prefab` 이라
    /// <c>Sprite</c> 로 못 읽는 문제가 아직 열려 있다(계획서 `CURR-01` ①).
    /// </summary>
    public class MergeEventFourDropItemDerrived : MergeEventDerrivedControllerBase, ISliderIncreaseInterface, IPumpingItemInterface
    {
        //표기 규약: 개수 앞에 x 를 붙인다(예: x12). 하단 팻말(MergeEventFourDropItemCurrencyText)과 동일해야 한다.
        private const string CURRENCY_TEXT_FORMAT = "x{0}";

        //보유량 텍스트. 이 컨트롤러가 전담한다 — 하단 팻말과 달리 여기서는 획득 연출(숫자 증가)까지 돌린다.
        [SerializeField] private UITextEx currencyText;
        //투사체 도착점 겸 팝(pumping) 연출 대상. 보통 재화 아이콘의 RectTransform 을 물린다.
        [SerializeField] private RectTransform currencyRect;

        //[무한모드/K1] 상시 안내 문구 LIdx. 기획서 §5-1 '진행도 무한 모드' 4번 — "병합하여 포인트를 획득하세요."
        //🔴 한국어·영어 **미발행**이다(2026-08-14 실측: text_cn 에만 존재). 그래서 아래에서 빈 문자열 가드를 둔다 —
        //  발행되면 코드 수정 없이 뜬다. 미발행 문구를 그리지 않는 것은 MergeEventBoard 의 TXT_* 가드와 같은 규약이다.
        private const int INFINITY_NOTICE_LIDX = 120612;

        //[무한모드/K1] 보상 말풍선 유지 시간. 기획서 §5-1 '진행도 무한 모드' 5번 — "3초간 유지 후 제거".
        private const float TOOLTIP_AUTO_CLOSE_SECONDS = 3f;

        //[무한모드/K1] 무한 페이즈 진행도 HUD(기획서 970293249 §5-1 '진행도 무한 모드').
        //🔴 프리팹 계층 실측(2026-08-14, 기획 목업 대조) — 넷은 **서로 다른 자리**다. 한 덩어리로 보고 묶으면 안 된다:
        //    EventMergeInfinity /RewardSlider                         ← ② 점수 게이지(UISliderController: GuageText "120/500" + GuageFill)
        //    EventMergeInfinity /RewardSlider/Background/Rank/Text (TMP)_2  ← ① 순위 뱃지 텍스트("상위 80%")
        //    EventMergeInfinity /RewardSlider/Background/Reward       ← ③ 보상 상자(터치 말풍선은 ⑤ 미저작이라 이번 스코프 밖)
        //    EventMergeInfinity /Text (TMP)_1                         ← ④ 상시 안내 문구
        //  ⚠️ 종전에는 ①을 `Text (TMP)_1`(=④ 자리)에 물려 두었다 — 순위가 안내 문구 자리에 찍히던 오배선이다.
        //이 컴포넌트는 Bottom/Sign 에 붙어 있지만 SerializeField 참조라 계층을 건너도 무방하다(갱신 훅을 한 클래스로 모은다).
        //🔴 오브젝트 on/off 는 여기서 하지 않는다 — MergeEvent.ApplyProgressViewByPhase 가 전담한다.
        //  두 주체가 켜고 끄면 페이즈 전환에서 서로를 덮어 겹쳐 보이거나 둘 다 사라진다.
        [SerializeField] private UISliderController infinityPointSlider;
        [SerializeField] private UITextEx infinityRankText;
        [SerializeField] private UITextEx infinityNoticeText;

        //[무한모드/K1] 보상 말풍선(기획서 970293249 §5-1 '진행도 무한 모드' 5번 — "보상 상자 터치 시 등장 / 3초간 유지 후 제거").
        //  infinityRewardButton  = `EventMergeInfinity `/RewardSlider/Background/**Reward**
        //  infinityRewardTooltip = `PopupBase/BossRaidMain/**UISimpleRewardTooltip**` (프리팹에 이미 있고 비활성으로 출고돼 있다)
        //🔴 툴팁이 무한모드 UI **바깥**(BossRaidMain 직속)에 있는 것은 의도다 — 슬라이더 안에 두면
        //  Background 의 마스크·정렬에 잘려 말풍선 꼬리가 사라진다. GetPoint 파생도 같은 배치를 쓴다.
        [SerializeField] private UIButtonEx infinityRewardButton;
        [SerializeField] private UISimpleRewardTooltip infinityRewardTooltip;

        private Info curInfo;

        //진행 중인 숫자 증가 연출. 연속 획득에서 이전 트윈이 살아 있으면 값이 뒤로 튄다 → 매번 갈아끼운다.
        private Sequence currencyGainSequence;

        //목표 점수 미발행 경고를 1회만 남기기 위한 표식. 이 컨트롤러는 병합마다 갱신돼 무조건 찍으면 콘솔이 덮인다.
        private bool loggedInvalidNeedPoint;

        //보상 말풍선 자동 닫힘 타이머. 재터치하면 이전 것을 버리고 다시 만든다(겹치면 먼저 만든 쪽이 방금 연 말풍선을 닫는다).
        private CancellationTokenSource tooltipAutoCloseCts;

        private void Start()
        {
            //델리게이트 구독은 Start 에서 한다(프로젝트 규약 — Awake 는 바인딩만). 해제는 OnDestroy 1회.
            if (infinityRewardButton != null) infinityRewardButton.onClick.AddListener(OnClickInfinityReward);
        }

        private void OnDestroy()
        {
            if (infinityRewardButton != null) infinityRewardButton.onClick.RemoveListener(OnClickInfinityReward);
            CancelTooltipAutoClose();
        }

        private void OnDisable()
        {
            //팝업이 닫히면 트윈을 끊는다. 남겨 두면 다음 입장에서 옛 시작값부터 숫자가 올라간다.
            currencyGainSequence?.Kill();
            currencyGainSequence = null;

            //말풍선도 함께 내린다 — 켜진 채 남으면 다음 입장에서 이전 보상이 떠 있는 상태로 시작한다.
            CancelTooltipAutoClose();
            if (infinityRewardTooltip != null) infinityRewardTooltip.gameObject.SetActive(false);
        }

        /// <summary>
        /// [무한모드/K1] 보상 상자 터치 → 보상 말풍선(기획서 §5-1 '진행도 무한 모드' 5번).
        /// 표시 대상은 <b>이번 라운드 도달 시 받을 보상</b>이다(<c>MergeEventHelper.GetRoundRewards</c>) —
        /// 게이지가 채워지는 그 목표의 보상이라 게이지·상자·말풍선이 같은 값을 가리킨다.
        ///
        /// 🔴 무한 페이즈에서만 연다. 수집 페이즈에는 이 UI 자체가 꺼져 있어 눌릴 일이 없지만,
        ///   버튼이 살아 있는 경로가 생겨도 엉뚱한 라운드 보상을 그리지 않도록 판정을 여기서도 건다.
        /// </summary>
        private void OnClickInfinityReward()
        {
            if (infinityRewardTooltip == null) return;
            if (curInfo == null || curInfo.MergeEventData == null) return;
            if (MergeEventHelper.IsFourDropItemInfinitePhase(curInfo.MergeEventData) == false) return;

            List<RewardPacketData> rewardPackets = MergeEventHelper.GetRoundRewards(curInfo.MergeEventData);
            if (rewardPackets.IsNullOrEmpty()) return;

            List<RewardInfo> rewardInfos = RewardHelper.GetRewardInfoByRewardPacketDatas(rewardPackets);
            if (rewardInfos.IsNullOrEmpty()) return;

            //수량 표기 규약은 GetPoint 말풍선과 같게 둔다 — 같은 프리팹(UISimpleRewardTooltip)을 쓰므로 표기가 갈리면 안 된다.
            CommonRewardItem.CountDisplayContext displayContext = new()
            {
                countDisplayType = CommonRewardItem.ECountDisplayType.ShowOverTargetValue,
                displayOverValue = 1,
            };

            //위치는 눌린 상자 자리다 — 말풍선 꼬리가 그 상자를 가리켜야 한다(목업 5번).
            infinityRewardTooltip.ShowTooltipWithCustomAction(displayContext,
                                                              infinityRewardButton.transform.position,
                                                              rewardInfos,
                                                              targetIndex: 0,
                                                              infoCustomAction: null,
                                                              onComplete: null);

            StartTooltipAutoClose();
        }

        /// <summary>기획서 §5-1 *"3초간 유지 후 제거"*. 재터치하면 이전 타이머를 버리고 다시 센다.</summary>
        private void StartTooltipAutoClose()
        {
            CancelTooltipAutoClose();

            tooltipAutoCloseCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            WaitAndCloseTooltipAsync(tooltipAutoCloseCts.Token).Forget();
        }

        private void CancelTooltipAutoClose()
        {
            if (tooltipAutoCloseCts == null) return;

            tooltipAutoCloseCts.Cancel();
            tooltipAutoCloseCts.Dispose();
            tooltipAutoCloseCts = null;
        }

        private async UniTaskVoid WaitAndCloseTooltipAsync(CancellationToken ct)
        {
            //취소되면 조용히 빠진다 — 재터치·팝업 닫힘으로 이미 정리된 뒤 유령 타이머가 다시 닫는 것을 막는다.
            bool canceled = await UniTask.Delay(TimeSpan.FromSeconds(TOOLTIP_AUTO_CLOSE_SECONDS), cancellationToken: ct)
                                         .SuppressCancellationThrow();
            if (canceled) return;
            if (infinityRewardTooltip == null) return;

            infinityRewardTooltip.HideTooltip();
        }

        /// <summary>
        /// [무한모드/K1-4] 반복 보상이 <b>뿜어져 나오는 자리</b> — 진행도 영역의 보상 상자
        /// (<c>EventMergeInfinity /RewardSlider/Background/Reward</c>).
        ///
        /// 말풍선을 띄우는 <see cref="infinityRewardButton"/> 과 <b>같은 오브젝트</b>라 필드를 따로 두지 않는다 —
        /// 하나만 바인딩하면 '터치해서 보상을 확인하는 자리'와 '보상이 나오는 자리'가 항상 일치한다.
        /// 기획서 970293249 §5-1 '진행도 무한 모드 > 보상' 이 그 상자를 가리킨다
        /// (*"점수 획득 시 바닥에 드랍 형식 / 공간 부족 시 버블 위치로 날아가 제공"*).
        ///
        /// 미바인딩이면 <c>false</c> — 호출부가 자기 폴백으로 물러선다(여기서 <c>Vector3.zero</c> 를 돌려주면
        /// 월드 원점에서 보상이 날아오므로, '값이 없다'와 '원점'을 반드시 구분해야 한다).
        /// </summary>
        public bool TryGetInfinityRewardBoxPosition(out Vector3 position)
        {
            position = Vector3.zero;
            if (infinityRewardButton == null) return false;

            position = infinityRewardButton.transform.position;
            return true;
        }

        /// <summary>
        /// <c>MergeEvent.UpdateDerrivedController()</c> 가 재화 변동·팝업 초기화 때마다 호출한다.
        /// 연출 없이 <b>현재 보유량으로 즉시 맞추는</b> 자리다 — 획득 연출은 <see cref="OnSequenceGainCurrency"/> 가 따로 돈다.
        /// </summary>
        public override void SetInfo(IUIInfoData uiInfoData)
        {
            curInfo = uiInfoData as Info;
            if (curInfo == null || curInfo.MergeEventData == null) return;

            ApplyCurrencyText(curInfo.MergeEventData.currencyPoint);
            ApplyInfinityProgress(curInfo.MergeEventData);
        }

        /// <summary>
        /// [무한모드/K1] 무한 페이즈 진행도 — 점수 게이지와 순위 문구(기획서 970293249 §5-1 '진행도 무한 모드').
        ///
        /// 수집 페이즈에는 <b>아무 것도 하지 않는다</b>. 그 구간에는 순위 자체가 없고,
        /// 오브젝트도 꺼져 있어 값을 써도 보이지 않는다(꺼진 채로 써 두면 다음 페이즈 전환에서 낡은 값이 한 프레임 드러난다).
        ///
        /// 이 함수가 <see cref="SetInfo"/> 안에 있는 이유 — <c>MergeEvent.OnUpdateEventGetPoint</c> 가 점수를 갱신할 때마다
        /// <c>UpdateDerrivedController()</c> 를 부르고 그것이 곧 <see cref="SetInfo"/> 다. 즉 <b>갱신 훅이 이미 완비돼 있어</b>
        /// 여기에 얹는 것만으로 병합·라운드 보상 수령 양쪽에서 HUD 가 따라온다.
        /// </summary>
        private void ApplyInfinityProgress(FsMergeEventState mergeEventState)
        {
            if (MergeEventHelper.IsFourDropItemInfinitePhase(mergeEventState) == false) return;

            int curPoint  = MergeEventGetPointHelper.GetCurPoint(mergeEventState);
            //[기획 확정 2026-08-14] 목표 점수의 정본은 MergeEvent_FourDropItemSet.repeatRewardPoint 다.
            //🔴 종전에는 MergeEventGetPointHelper.GetNeedPoint(= MergeEvent_Round.value1)를 썼는데, 501 의 Round 행은
            //  value1 이 0 이라 게이지가 `n/0` 으로 고정됐다. 반복 보상은 라운드 축이 아니므로 소스 자체를 바꾼다
            //  (보상 내용물만 그 Round 행의 rewardIdx 에서 계속 가져온다 — 조건과 보상의 축이 갈려 있다).
            //  판정(IsInfiniteRepeatRewardReady)·차감(FsProcessMergeEvent)과 **같은 함수**를 경유해야
            //  게이지가 100% 인데 보상이 안 나오는 식의 어긋남이 생기지 않는다.
            int needPoint = MergeEventFourDropItemHelper.GetInfiniteRepeatRewardPoint(mergeEventState);

            //🔴 목표 점수 미발행 진단. 0 이면 게이지가 `n/0` 으로 고정되고 반복 보상 경로도 통째로 잠긴다 —
            //  원인이 데이터인데 화면만 보면 코드 버그로 보이므로 여기서 한 번은 남긴다.
            //값이 바뀔 때만 남긴다 — 이 함수는 병합마다 도는 자리라 무조건 찍으면 콘솔이 로그로 덮인다.
            if (needPoint <= 0 && loggedInvalidNeedPoint == false)
            {
                loggedInvalidNeedPoint = true;
                DLogger.Error($"[FourDropItem] 무한모드 목표 점수가 0 이다 — MergeEvent_FourDropItemSet {mergeEventState.id} 의"
                              + " repeatRewardPoint 미발행. 게이지가 n/0 으로 고정되고 반복 보상이 지급되지 않는다(기획 데이터 확인 필요).");
            }

            if (infinityPointSlider != null)
            {
                //🔴 0 나눗셈 가드. 목표가 0 이면 진행률을 만들 수 없다 — 그대로 나누면 NaN 이 슬라이더에 들어가 게이지가 통째로 사라진다.
                infinityPointSlider.SetSliderAmount(needPoint > 0 ? (float)curPoint / needPoint : 0f);
                infinityPointSlider.SetGuageText($"{curPoint}/{needPoint}");
            }

            //🔴 순위에 먹이는 점수는 게이지와 **다르다**. 게이지는 '이번 라운드 진행도'(차감되는 eventPoint)지만
            //  MergeEvent_Rank 는 누적 축이라(0~9999999) 이벤트 시작부터의 총 획득량을 넘겨야 한다 —
            //  라운드 보상마다 차감된 분을 합산해 주는 GetInfinitePhaseRankScore 를 쓴다.
            //  둘을 같은 값으로 두면 라운드를 넘길 때마다 순위가 최하위 구간으로 되돌아간다.
            if (infinityRankText != null)
            {
                int rankScore = MergeEventFourDropItemHelper.GetInfinitePhaseRankScore(mergeEventState);
                if (MergeEventFourDropItemHelper.TryGetInfinitePhaseRankText(mergeEventState, rankScore, out string rankText))
                    infinityRankText.SetText(rankText);
            }

            ApplyInfinityNotice();
        }

        /// <summary>
        /// [무한모드/K1] 상시 안내 문구(기획서 §5-1 '진행도 무한 모드' 4번 — 게이지 아래에 항상 떠 있는 줄).
        /// 값이 변하지 않는 고정 문구라 매 갱신마다 다시 쓸 필요는 없지만, 여기서 함께 처리해 두면
        /// 무한 페이즈에 들어온 어느 경로로든(팝업 오픈 / 도감에서 복귀) 한 번은 채워진다.
        /// 🔴 <b>미발행이면 그리지 않는다</b> — 한국어·영어 LIdx 가 아직 비어 있어(2026-08-14 실측)
        ///   그대로 쓰면 빈 줄이나 포맷 잔해가 화면에 남는다. 발행되면 코드 수정 없이 뜬다.
        /// </summary>
        private void ApplyInfinityNotice()
        {
            if (infinityNoticeText == null) return;

            string notice = INFINITY_NOTICE_LIDX.L();
            if (string.IsNullOrEmpty(notice)) return;

            infinityNoticeText.SetText(notice);
        }

        /// <summary>
        /// 재화 블록 획득 시 투사체가 날아올 지점. 미바인딩이면 자기 위치로 물러선다 —
        /// <c>Vector3.zero</c>(월드 원점)를 돌려주면 투사체가 화면 밖으로 날아간다.
        /// </summary>
        public override Vector3 GetProjectileTargetPos()
        {
            if (currencyRect == null) return transform.position;

            return currencyRect.position;
        }

        /// <summary>
        /// [<c>ISliderIncreaseInterface</c>] 재화 획득 연출 — 상단 숫자가 <paramref name="startvalue"/> 에서
        /// <paramref name="goalvalue"/> 까지 오른다. 호출부는 <c>EventBlockEffectController</c> 의 투사체 시퀀스이고,
        /// 투사체 도착과 같은 박자로 묶여 돈다.
        /// </summary>
        public Sequence OnSequenceGainCurrency(int startvalue, int goalvalue, float duration)
        {
            currencyGainSequence?.Kill();
            currencyGainSequence = DOTween.Sequence();

            //프리팹 바인딩 전이면 빈 시퀀스를 돌려준다 — 호출부가 Join/Append 로 이어 붙이므로 null 은 안 된다.
            if (currencyText == null) return currencyGainSequence;

            currencyGainSequence.Append(DOVirtual.Int(startvalue, goalvalue, duration, ApplyCurrencyText));

            return currencyGainSequence;
        }

        /// <summary>
        /// [<c>IPumpingItemInterface</c>] 투사체가 닿는 순간 재화 아이콘이 튀는 연출.
        /// <paramref name="startScale"/> 에서 1배로 돌아오며, 타 이벤트(GetPoint)와 같은 이징을 쓴다.
        /// </summary>
        public Sequence OnSequencePumpingItem(float startScale, float duration)
        {
            Sequence sequence = DOTween.Sequence();
            if (currencyRect == null) return sequence;

            return sequence.Append(currencyRect.DOScale(1.0f, duration).SetEase(Ease.OutCubic).From(startScale));
        }

        private void ApplyCurrencyText(int value)
        {
            if (currencyText == null) return;

            currencyText.SetText(string.Format(CURRENCY_TEXT_FORMAT, value));
        }
    }
}

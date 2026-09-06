using GameCore.Utils;

using GameLogic.Network;

using UnityEngine;

namespace GameLogic.MergeEvent.SubObject
{
    /// <summary>
    /// 이벤트 재화 보유량 텍스트(FourDropItem). 테마01 표기: 물뿌리개. 메인 팝업 Bottom/Sign/Txt_Count 에 붙는다.
    /// 기획서 970293249 §5-1 *"물뿌리개 영역 — 물뿌리개 아이콘 표시 / **보유 물뿌리개 수 표시**"*.
    ///
    /// BossRaid·GetPoint 는 재화 HUD 를 <see cref="MergeEventDerrivedControllerBase"/> 파생 컨트롤러가 그리는데
    /// FourDropItem 은 파생 컨트롤러가 없어 그 경로가 통째로 비어 있다 → 이 전용 컴포넌트로 분리했다.
    /// 갱신은 <see cref="MergeEventUpdateCurrencyMsg"/> 구독으로 받는다(폴링 없음).
    /// 발신 지점은 재화가 바뀌는 곳 전부다 — 낙관적 차감(<see cref="MergeEventBoard.AddCurrencyForSequence"/>) +
    /// 서버 권위값 확정(MergeEvent 의 GainCurrency·PeakingClick·UseGrowCurrency 액션).
    /// </summary>
    public class MergeEventFourDropItemCurrencyText : MonoBehaviour
    {
        //보드가 재화 보유량(FsMergeEventState.currencyPoint)의 단일 진실이다. 인스펙터 바인딩.
        [SerializeField] private MergeEventBoard eventBoard;

        //같은 게임오브젝트에 붙어 있는 고정 컴포넌트라 Awake 에서 1회만 캐싱한다(런타임 반복 GetComponent 금지).
        private UITextEx currencyText;

        private void Awake()
        {
            currencyText = GetComponent<UITextEx>();
        }

        private void OnEnable()
        {
            //팝업을 다시 열었을 때도 현재 보유량으로 복원한다(구독 전에 1회 그린다).
            Refresh();
        }

        private void Start()
        {
            //델리게이트/이벤트 구독은 Start 에서 처리한다(프로젝트 규약). 해제는 OnDestroy 1회.
            Message.AddListener<MergeEventUpdateCurrencyMsg>(OnUpdateCurrency);

            //구독 직후 한 번 더 그린다 — OnEnable 시점에는 보드가 아직 이벤트 데이터를 받기 전이라 조기 반환한다.
            //보드 초기화가 이 Start 보다 먼저 끝나 초기화 알림(MergeEvent.SetInfoAsync)을 놓치는 순서에서도
            //여기서 현재 보유량을 집어 온다. 데이터가 아직이면 Refresh 가 그대로 조기 반환하므로 무해하다.
            Refresh();
        }

        private void OnDestroy()
        {
            Message.RemoveListener<MergeEventUpdateCurrencyMsg>(OnUpdateCurrency);
        }

        private void OnUpdateCurrency(MergeEventUpdateCurrencyMsg msg)
        {
            Refresh();
        }

        private void Refresh()
        {
            //프리팹 바인딩 전이거나 보드가 아직 이벤트 데이터를 받기 전이면 건드리지 않는다 —
            //0 을 먼저 그려 두면 실제 보유량이 들어오기 전까지 유저에게 '재화 없음'으로 보인다.
            if (eventBoard == null) return;

            FsMergeEventState mergeEventInfo = eventBoard.MergeEventInfo;
            if (mergeEventInfo == null) return;

            //표기 규약: 개수 앞에 x 를 붙인다(예: x12). 접두사가 붙는 곳은 여기 한 곳뿐이라 상수로 빼지 않는다.
            currencyText.SetText($"x{mergeEventInfo.currencyPoint}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;
using GameLogic.Network;

using Spine.Unity;

using UnityEngine;
using UnityEngine.Events;

namespace GameLogic.MergeEvent.SubObject
{
    /// <summary>
    /// 화단 패널 컨트롤러(FourDropItem). 테마01 표기: 캐릭터 앞 밭.
    /// 메인 팝업 하단 FieldPanel 에 붙어, 활성화된 화단 수만큼 블록을 한 번에 소환한다.
    ///
    /// 🔴 [UI 변경 2026-08-14] <b>드랍 버튼이 FieldPanel 하나에서 화단 칸마다(Field01~04)로 쪼개졌다.</b>
    ///   프리팹에서 FieldPanel 자신의 <c>UIButtonEx</c>·<c>Image</c> 는 꺼졌고(<c>m_Enabled: 0</c>),
    ///   대신 각 칸이 자기 버튼을 갖는다. 칸을 나눈 이유는 재화가 모자랄 때 안내 말풍선을
    ///   <b>눌린 칸 바로 위</b>에 띄워야 하기 때문이다(기획서 §5-1 "아이템 생성 영역"의 아이콘 안내).
    ///   네 칸의 소환 동작 자체는 완전히 같다 — 어느 칸을 눌러도 활성 화단 수만큼 나온다.
    /// 레퍼런스(Gossip Harbor 0:00~0:03)의 '나무를 탭하면 재화를 쓰고 블록이 머지판으로 날아오는' 조작에 대응한다.
    ///
    /// 활성 화단 수가 곧 1회 소환 수다 — MergeEvent_FourDropItemCycle 의 itemDropCount(1~4)와 같은 축이고
    /// MergeEventHelper.GROW_MAX_COUNT(4)가 상한이라 이벤트 이름의 '4 Drop' 과도 일치한다.
    /// 🔴 최소 1개는 항상 활성이다(<see cref="MIN_ACTIVE_FIELD_COUNT"/>) — 0이면 탭해도 아무 일이 없는
    ///   무반응 상태가 되고 유저는 고장으로 읽는다. 값이 들어오는 두 경로(저장값 복원 · <see cref="TryAddActiveField"/>)가
    ///   각각 클램프하고, 화단 수를 임의로 세팅하는 통로는 두지 않는다.
    ///
    /// 소환 대상은 MergeEvent_FourDropItemSet.itemIdx1(일반 등장 아이템)이라 테마/이벤트가 바뀌어도 하드코딩이 없다.
    /// 레어(itemIdx2)는 MergeEvent_FourDropItemCycle 의 주기(pity)가 정하는 축이라 여기서 흉내내지 않는다.
    /// [잠정] 재화 소모량과 소환 규칙은 기획 정식 데이터 확정 시 변경 가능(Plan.md 잠정 작업 원칙).
    /// </summary>
    public class MergeEventFourDropItemFieldPanel : MonoBehaviour
    {
        /// <summary>
        /// 항상 활성인 최소 화단 수. 기획 확정: 기본 1개.
        /// 정의는 <see cref="MergeEventHelper.GROW_MIN_FIELD_COUNT"/> 한 곳이다 — 저장값 0 보정이 데이터 계층에도 필요해
        /// 그쪽에 두고 여기서는 별칭만 둔다(둘이 어긋나면 그림과 저장값이 따로 논다).
        /// </summary>
        public const int MIN_ACTIVE_FIELD_COUNT = MergeEventHelper.GROW_MIN_FIELD_COUNT;

        //1회 소환당 재화 소모량은 **배수 토글**이 정한다(기획서 §5-1 "한 번에 사용되는 재화량 표시 / 최대 2배").
        //활성 화단 수에는 비례하지 않는다 — 탭 1회가 과금 단위이고, 화단 수는 배수가 아니라 진행도이기 때문이다.
        //소모량·생성 블록은 MergeEventFourDropItemHelper.TryGetSummonPlan 한 곳이 확정한다(손가락 가이드①과 공유).

        //🔴 FX_4DropItem_Touch 는 여기서 쓰지 않는다(리드 확정 2026-08-12) — **바구니(상호작용 오브젝트) 전용**이다.
        //  종전에는 화단도 같은 에셋을 띄웠는데, 화단에는 아래 물뿌리개 모션 + 물 파티클이 이미 있어 연출이 겹쳤다.
        //  바구니측 재생은 MergeEventInteractionObject.USE_EFFECT_PATH 가 담당한다.

        //[기획서 970293249 §6] 화단 터치 시 "**물뿌리개로 화단에 물을 주는 모션** 재생".
        //Ani_Controller_4DropItemHatchet01 실측: 상태가 'Active' **하나뿐이고 파라미터가 없다**(default = Active).
        //→ 트리거로 부를 수단이 없다. 그래서 **평소엔 오브젝트를 꺼 두고**, 터치 순간 켜면서 Play 로 처음부터 되감아 재생한다.
        //물 파티클(fx_FourDropHatcht01Water 계열)은 이 오브젝트의 자식으로 이미 들어와 있어 클립이 함께 돌린다 — 별도 로드가 없다.
        private const string WATERING_CAN_ANIM_STATE = "Active";
        //[검수 시트 row 3] 꽃 스파인 클립명. Spine_FourDropItem_Main_Flower 실측 — Hit(1.10s) / Grow(1.33s) / Idle(2.67s).
        //프리팹 저작은 startingAnimation = Idle(loop) 이라 평상시 그림은 이미 맞고, 빠져 있던 것은 아래 두 개의 '사건' 재생이다.
        private const string FLOWER_ANIM_HIT  = "Hit";   //물뿌리개로 물을 맞은 순간
        private const string FLOWER_ANIM_GROW = "Grow";  //씨앗이 심어져 새 꽃이 자라는 순간
        private const string FLOWER_ANIM_IDLE = "Idle";  //두 연출이 끝나면 되돌아갈 기본 루프

        //[검수 시트 row 3-①] 물 뿌리는 파티클. Addressable bare address 등록명 그대로다(Effects.asset).
        private const string WATER_EFFECT_PATH = "fx_FourDropHatcht01Water";
        //프리팹 실측(파티클 6개): 가장 늦게 끝나는 Fx_Booster_tail_2_alpha 가 5.40초다. AutoKillEffect 가 없어 명시 회수가 필요하다.
        //🔴 모션(0.8초)보다 훨씬 길다 — 꼬리 알파가 남는 구성이라 값 자체는 실측이 맞지만, 물이 공중에 오래 남아 보이면
        //  아트가 클립을 줄이거나 이 상수를 낮춰야 한다(연출 판단이라 임의로 자르지 않았다).
        private const float WATER_EFFECT_DURATION = 5.4f;
        //Ani_Clip_FellingItemHatchet01 실측 0.8초(60fps · m_StopTime 0.8 · 루프 아님).
        //재생 길이는 런타임 상태에서 읽고, 그것을 못 읽었을 때만 이 값으로 물러선다(아트가 클립을 갈아도 코드가 따라간다).
        private const float WATERING_CAN_ANIM_FALLBACK_DURATION = 0.8f;

        //[UI 변경 2026-08-14] 재화가 모자란 채로 화단을 눌렀을 때 뜨는 안내 말풍선(프리팹 <c>Tooltip_FieldItem</c>)의 두트윈 id.
        //프리팹에 DOTweenAnimation 이 **두 개** 저작돼 있고 둘 다 autoPlay 가 꺼져 있어, 재생은 전적으로 코드가 id 로 지목한다.
        //🔴 Open 은 isFrom 트윈(0 → 저작 크기)이고 Close 는 (저작 크기 → 0) 이다. 둘이 **같은 RectTransform 의 스케일**을
        //  공유하므로, 한쪽을 태우기 전에 반대쪽을 '다 보이는 상태'로 정리해야 크기가 어긋난 채 굳지 않는다
        //  (Open 은 Complete, Close 는 Rewind 가 그 지점이다). 선례: UIWindowMyDreamPartnerLobby 의 말풍선 On/Off.
        private const string FIELD_TOOLTIP_TWEEN_OPEN_ID  = "Open";
        private const string FIELD_TOOLTIP_TWEEN_CLOSE_ID = "Close";
        //유지 시간은 기획서 §5-1 말풍선 규약("3초간 유지 후 제거")을 따른다 — 형제 안내(MergeEventInteractionObject)와 같은 값이다.
        private const float FIELD_TOOLTIP_DURATION = 3f;

        [SerializeField] private MergeEventBoard eventBoard;
        //🔴 토글 대상은 밭 바닥(Field01~04)이 아니라 그 위에 얹힌 꽃(Flower01~04)이다.
        //  바닥은 밭의 크기를 보여주는 배경이라 항상 켜져 있어야 하고, 켜고 끄면 밭이 통째로 사라져 보인다.
        //  인스펙터 배열 순서가 곧 활성화 순서다(앞에서부터 채운다).
        //런타임 Add/Remove 가 없으므로 List 가 아니라 배열이다.
        [SerializeField] private GameObject[] flowerObjects;
        //[기획서 §6] 물뿌리개(테마01 표기). 같은 부모(Bottom) 아래 형제라 인스펙터로 묶는다.
        //비어 있으면 모션만 건너뛴다 — 소환 자체는 물뿌리개와 무관하다.
        [SerializeField] private Animator wateringCanAnimator;
        //[기획 2026-08-13 · 이수진] *"물뿌려질때 위치는 꽃 우상단에 위치하게 해주세요"* — 물뿌리개를 꽃 기준 어디에 놓을지.
        //상수로 박지 않고 인스펙터에 두는 이유: '우상단'의 정도는 눈으로 맞추는 값이라 코드가 정할 수 없다.
        //단위는 **물뿌리개 부모(Bottom)의 로컬 좌표계**다 — 꽃 위치를 그 공간으로 환산한 뒤 이 값을 더한다.
        [SerializeField] private Vector2 wateringCanFlowerOffset = new Vector2(70f, 60f);
        //[UI 변경 2026-08-14] 블록 드랍 버튼 = 화단 칸(Field01~04)의 UIButtonEx.
        //인덱스는 flowerObjects 와 1:1 이다 — 말풍선을 '눌린 칸' 위에 띄우려면 어느 칸이 눌렸는지가 필요하고,
        //그 판정을 이름 탐색이 아니라 배열 순서로 하기 위해서다.
        //🔴 꽃이 아직 자라지 않은 칸도 버튼은 살아 있다 — 칸(Field0N) 자체는 항상 켜져 있고 꽃(FellingFlower0N)만 토글되기 때문이다.
        //  네 칸 전부 같은 소환을 하므로 이것이 동작 차이를 만들지는 않는다.
        //런타임 Add/Remove 가 없으므로 List 가 아니라 배열이다.
        [SerializeField] private UIButtonEx[] fieldButtons;
        //[기획서 §5-1 아이템 생성 영역] 재화가 모자랄 때 띄우는 안내 말풍선. 평소에는 꺼져 있어야 한다.
        //🔴 이 오브젝트는 FieldPanel 의 자식이 아니라 팝업 루트(BossRaidMain) 아래 **형제**다 —
        //  패널이 꺼져도 따라 꺼지지 않으므로 OnDisable 에서 직접 숨긴다.
        [SerializeField] private RectTransform fieldItemTooltip;
        //말풍선 미세 조정(말풍선 부모의 앵커 좌표 단위). 0 이면 말풍선 밑변이 눌린 칸의 윗변에 정확히 붙는다
        //(말풍선 피벗이 (0.5, 0) = 아래 중앙이라 그렇다). 리드 확정으로 기본값을 **조금 아래**로 내려 칸과 살짝 겹치게 한다.
        //'얼마나 내릴지'는 눈으로 맞추는 값이라 상수로 박지 않고 인스펙터에 둔다.
        [SerializeField] private Vector2 fieldItemTooltipOffset = new Vector2(0f, -30f);

        //[검수 시트 row 3] flowerObjects 자식의 SkeletonGraphic 캐시(Awake 1회). 인덱스는 flowerObjects 와 1:1 이다.
        private SkeletonGraphic[] flowerSpines;

        //물뿌리개가 지나치는 꽃을 순서대로 담는 버퍼(꽃 인덱스 / 그 지점의 경로 진행도 0~1).
        //원소가 최대 4개고 터치마다 한 번 채우므로 재사용 필드로 충분하다(매 터치 할당을 피한다).
        private readonly List<int>   flowerHitOrder    = new();
        private readonly List<float> flowerHitProgress = new();

        //[검수 시트 row 21] 소환 출발점·말풍선 위치 계산용 버퍼. RectTransform.GetWorldCorners 가 길이 4 배열을 요구하는데,
        //터치마다 새로 만들면 그만큼 GC 가 쌓인다 → 재사용한다(길이는 API 계약상 항상 4다).
        //두 용도가 모두 동기 계산이라 서로 값을 밟지 않는다.
        private readonly Vector3[] worldCornersBuffer = new Vector3[4];

        //물뿌리개 표시 타이머. 연타하면 이전 대기가 **방금 켠** 물뿌리개를 꺼 버리므로 매번 갈아끼운다.
        private CancellationTokenSource wateringCanCts;

        //[UI 변경 2026-08-14] 말풍선 두트윈. GetComponents 는 대상이 꺼져 있어도 동작하므로 Awake 에서 1회 캐싱한다
        //(런타임 반복 GetComponent 금지). 🔴 단 DOTweenAnimation.tween 자체는 그 오브젝트가 **처음 켜질 때** 생기므로
        //재생 지시는 반드시 SetActive(true) 뒤에 해야 한다.
        private DOTweenAnimation fieldTooltipOpenTween;
        private DOTweenAnimation fieldTooltipCloseTween;

        //말풍선 자동 숨김 타이머. 다른 칸을 연달아 누르면 이전 타이머가 방금 띄운 말풍선을 지우므로 매번 갈아끼운다.
        private CancellationTokenSource fieldTooltipCts;

        //onClick 해제용 델리게이트 보관. 람다는 만들 때마다 참조가 달라, 저장해 두지 않으면 뗄 수 없다.
        private UnityAction[] fieldButtonClickActions;

        private int activeFieldCount = MIN_ACTIVE_FIELD_COUNT;

        //[검수 시트 row 24] 서버가 확정했지만 **아직 그림에 반영하지 않은** 화단 수.
        //씨앗이 화단에 도착한 뒤에 꽃이 자라야 해서(기획 지적) 데이터와 그림의 시점이 갈린다.
        //반영은 CommitAddedField 하나뿐이고, 그 전까지 activeFieldCount(=소환 수)는 옛 값을 유지한다 —
        //도착 전에 소환하면 아직 자라지도 않은 꽃 몫까지 나오기 때문이다.
        private int pendingActiveFieldCount = MIN_ACTIVE_FIELD_COUNT;

        /// <summary>현재 활성 화단 수 = 1회 소환 수.</summary>
        public int ActiveFieldCount => activeFieldCount;

        private void Awake()
        {
            CacheFlowerSpines();
            CacheFieldTooltipTweens();
        }

        /// <summary>
        /// [UI 변경 2026-08-14] 말풍선의 열림/닫힘 두트윈을 <b>id 로 갈라</b> 캐싱한다.
        /// 같은 오브젝트에 <see cref="DOTweenAnimation"/> 이 둘 붙어 있어 컴포넌트 타입만으로는 구분할 수 없다.
        /// 대상이 꺼져 있어도 <c>GetComponents</c> 는 동작하므로 여기서 1회만 찾는다.
        /// </summary>
        private void CacheFieldTooltipTweens()
        {
            if (fieldItemTooltip == null) return;

            DOTweenAnimation[] tooltipTweens = fieldItemTooltip.GetComponents<DOTweenAnimation>();
            for (int i = 0; i < tooltipTweens.Length; ++i)
            {
                if (tooltipTweens[i].id == FIELD_TOOLTIP_TWEEN_OPEN_ID)       fieldTooltipOpenTween  = tooltipTweens[i];
                else if (tooltipTweens[i].id == FIELD_TOOLTIP_TWEEN_CLOSE_ID) fieldTooltipCloseTween = tooltipTweens[i];
            }
        }

        /// <summary>
        /// [검수 시트 row 3] 꽃 스파인을 1회만 캐싱한다. 런타임(터치마다)에 GetComponentInChildren 을 돌리지 않기 위해서다.
        /// 프리팹 실측: <c>flowerObjects[i]</c> = <c>FellingFlower0N</c>, 그 자식 <c>Flower</c> 에 SkeletonGraphic 이 있다.
        /// 꽃이 꺼져 있는 동안에도 참조는 살아 있어야 하므로 <c>includeInactive: true</c> 로 찾는다.
        /// </summary>
        private void CacheFlowerSpines()
        {
            if (flowerObjects.IsNullOrEmpty()) return;

            flowerSpines = new SkeletonGraphic[flowerObjects.Length];
            for (int i = 0; i < flowerObjects.Length; ++i)
            {
                if (flowerObjects[i] == null) continue;

                flowerSpines[i] = flowerObjects[i].GetComponentInChildren<SkeletonGraphic>(true);
            }
        }

        private void OnEnable()
        {
            //팝업을 다시 열어도 활성 수가 그림에 반영되도록 매번 저장값에서 복원한다.
            //저장값이 단일 진실이다 — 이 필드는 캐시일 뿐이라 여기서 덮어써야 재입장 시 어긋나지 않는다.
            activeFieldCount = FsWebManager.GetProcess<FsProcessMergeEvent>() == null
                ? MIN_ACTIVE_FIELD_COUNT
                : GetStoredFieldCount();
            //미반영분도 함께 맞춘다 — 도착 연출 도중 팝업이 닫혔다면 그 예약이 남아 다음 입장에 꽃이 한 번 더 자란다.
            pendingActiveFieldCount = activeFieldCount;
            ApplyActiveFieldCount();
            //[기획서 §6] 물뿌리개는 **터치하는 순간에만** 나온다 — 평소엔 꺼져 있어야 화단만 보이는 그림이 된다.
            //OnEnable 이라 팝업을 다시 열 때도 항상 평소 상태에서 시작한다(대기 도중 닫혔더라도).
            SetWateringCanActive(false);
            //말풍선도 같은 이유로 평소 상태(꺼짐)에서 시작한다 — 안내 도중 팝업이 닫혔다면 그대로 떠 있는 채로 재입장한다.
            HideFieldItemTooltip();
        }

        private void OnDisable()
        {
            //팝업이 닫히면 대기를 끊고 그림도 평소 상태로 되돌린다 — 켜진 채 남으면 다음 입장에 물뿌리개가 떠 있다.
            wateringCanCts = wateringCanCts.CancelAndDispose(false);
            SetWateringCanActive(false);
            //🔴 말풍선은 이 패널의 자식이 아니라 팝업 루트 아래 형제라, 패널이 꺼져도 따라 꺼지지 않는다 → 직접 숨긴다.
            HideFieldItemTooltip();
        }

        private void Start()
        {
            //델리게이트/이벤트 구독은 Start 에서 처리한다(프로젝트 규약). 해제는 OnDestroy 1회.
            SubscribeFieldButtons();
        }

        private void OnDestroy()
        {
            UnsubscribeFieldButtons();
            fieldTooltipCts = fieldTooltipCts.CancelAndDispose(false);
        }

        /// <summary>
        /// [UI 변경 2026-08-14] 화단 칸(Field01~04) 버튼에 소환을 건다.
        /// 종전에는 FieldPanel 자신의 <c>UIButtonEx</c> 하나가 그 역할을 했는데, UI 개편으로 그 버튼이 꺼지고
        /// 칸마다 버튼이 생겼다 — 재화가 모자랄 때 안내 말풍선을 <b>눌린 칸</b> 위에 띄워야 해서다.
        /// </summary>
        private void SubscribeFieldButtons()
        {
            if (fieldButtons.IsNullOrEmpty())
            {
                //조용히 넘어가면 화단을 눌러도 아무 일이 없어 '고장'으로 보인다 → 바인딩 누락을 로그로 드러낸다.
                DLogger.Error("MergeEventFourDropItemFieldPanel::SubscribeFieldButtons::fieldButtons 미바인딩 — 화단을 눌러도 소환되지 않는다(프리팹 Field01~04 의 UIButtonEx 를 바인딩할 것).");
                return;
            }

            fieldButtonClickActions = new UnityAction[fieldButtons.Length];
            for (int i = 0; i < fieldButtons.Length; ++i)
            {
                if (fieldButtons[i] == null) continue;

                //🔴 루프 변수를 그대로 캡처하면 네 버튼이 전부 마지막 인덱스를 가리킨다 — 지역 복사가 필수다.
                int fieldIndex = i;
                fieldButtonClickActions[i] = () => OnClickField(fieldIndex);
                fieldButtons[i].onClick.AddListener(fieldButtonClickActions[i]);
            }
        }

        private void UnsubscribeFieldButtons()
        {
            if (fieldButtons.IsNullOrEmpty()) return;
            if (fieldButtonClickActions == null) return;

            int count = Mathf.Min(fieldButtons.Length, fieldButtonClickActions.Length);
            for (int i = 0; i < count; ++i)
            {
                if (fieldButtons[i] == null) continue;
                if (fieldButtonClickActions[i] == null) continue;

                fieldButtons[i].onClick.RemoveListener(fieldButtonClickActions[i]);
            }
        }

        /// <summary>
        /// [UI 변경 2026-08-14] 손가락 가이드·튜토리얼이 가리킬 <b>대표 드랍 버튼</b>(첫 화단 칸).
        ///
        /// 🔴 튜토리얼 순서2 는 이 대상에서 <c>Button</c> 을 꺼내 <c>onClick.Invoke()</c> 로 <b>대리 터치</b>한다
        ///   (<c>CommonTutorialManager.HandleTutorialEndProcess</c>) — 즉 <b>리스너가 붙어 있는</b> 오브젝트를 돌려줘야 한다.
        ///   패널 자신의 버튼은 UI 개편으로 꺼졌으므로(<c>m_Enabled: 0</c>) 여기서 칸 버튼을 준다.
        ///   패널을 그대로 돌려주면 대리 터치가 빈 이벤트를 쏴 <b>순서2 가 종료되지 않고 딤만 남는다</b>.
        ///
        /// 바인딩 전이면 패널 자신으로 물러선다 — 마스크라도 뜨는 편이 <c>null</c> 로 튜토리얼이 멈추는 것보다 낫다.
        /// </summary>
        public RectTransform GetDropButtonRect()
        {
            if (fieldButtons.IsNullOrEmpty() == false)
            {
                for (int i = 0; i < fieldButtons.Length; ++i)
                {
                    if (fieldButtons[i] == null) continue;

                    return fieldButtons[i].transform as RectTransform;
                }
            }

            return transform as RectTransform;
        }

        /// <summary>저장된 활성 화단 수. 0(미설정·구 세이브)은 서버 쪽에서 최소값으로 보정해 돌려준다.</summary>
        private int GetStoredFieldCount()
        {
            FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_MergeEvent_GetGrowFieldCount(out int fieldCount);
            return fieldCount;
        }

        /// <summary>
        /// [FourDropItem] 성장 소모품(씨앗)이 호출한다 — 활성 화단을 1개 늘린다.
        /// 기획서 970293249 §4-1 *"사용 시 화단에 꽃 1개 추가 / 최대 4개까지"*.
        /// 반환 false = 이미 최대치. 🔴 그때도 <b>소모품은 호출측에서 제거한다</b>(기획서 *"시스템 메시지 호출 및 아이템 제거"*).
        /// 저장은 서버 쪽(<c>AddGrowFieldCount</c>)이 책임진다 — 여기서 별도로 하지 않는다.
        ///
        /// 🔴 [검수 시트 row 24 · 2026-08-14] <b>여기서는 데이터만 확정하고 그림은 건드리지 않는다.</b>
        ///   종전에는 이 자리에서 칸을 켜고 자라는 클립까지 돌려, 씨앗이 화단으로 <b>날아가기도 전에</b> 꽃이 생겼다
        ///   (기획 지적: *"꽃 심는 타이밍 — 블럭이 들어가고 나서 재생 필요"*).
        ///   그림 반영은 씨앗이 도착한 뒤 <see cref="CommitAddedField"/> 가 한다.
        ///   데이터를 미루지 않는 이유는 이 프로젝트의 일관된 원칙이다 — 연출 콜백에 매달면 취소 시 증가분이 사라진다.
        /// </summary>
        public bool TryAddActiveField()
        {
            FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_MergeEvent_AddGrowField(out bool added, out int fieldCount);

            //최대치라 늘지 않았어도 저장값을 그대로 받아 둔다(캐시가 어긋난 채 남지 않게).
            pendingActiveFieldCount = Mathf.Clamp(fieldCount, MIN_ACTIVE_FIELD_COUNT, flowerObjects.IsNullOrEmpty() ? MIN_ACTIVE_FIELD_COUNT : flowerObjects.Length);

            //최대치라 자랄 꽃이 없으면 그림도 지금 맞춰 둔다 — 기다릴 도착 연출이 없다.
            if (added == false) CommitAddedField();

            return added;
        }

        /// <summary>
        /// [검수 시트 row 24] 씨앗이 화단에 <b>도착한 뒤</b> 호출한다 — 늘어난 칸을 켜고 자라는 클립을 재생한다.
        /// <see cref="TryAddActiveField"/> 가 확정해 둔 값과 화면이 같아질 때까지가 이 함수의 몫이다.
        ///
        /// 멱등이다 — 이미 반영됐으면 아무 일도 하지 않는다. 도착 콜백과 폴백 경로가 둘 다 부를 수 있기 때문이다.
        /// 비행이 취소돼 끝내 불리지 않아도 <c>OnEnable</c> 이 저장값에서 복원하므로 화단 수가 영구히 어긋나지는 않는다.
        /// </summary>
        public void CommitAddedField()
        {
            if (pendingActiveFieldCount == activeFieldCount) return;

            bool grew = pendingActiveFieldCount > activeFieldCount;

            activeFieldCount = pendingActiveFieldCount;
            ApplyActiveFieldCount();

            //[검수 시트 row 3-④] 실제로 늘어났을 때만 새 꽃이 자란다.
            //ApplyActiveFieldCount 가 그 칸을 이미 켠 뒤라 여기서 재생해야 그림이 있는 상태에서 클립이 돈다.
            if (grew) PlayFlowerGrow(activeFieldCount - 1);
        }

        /// <summary>
        /// [검수 시트 row 24] <b>이번에 자랄 꽃 칸</b>의 월드 좌표. 씨앗 비행 도착점과 심기 FX 가 같은 자리를 보도록 여기서만 준다.
        ///
        /// 🔴 종전에는 두 연출 모두 <c>transform.position</c>(패널 원점)을 썼다. 프리팹 실측(2026-08-14) 결과
        ///   네 칸은 패널 원점에서 <b>x −176 ~ +163 / y −74 ~ +75</b> 만큼 떨어져 있어,
        ///   씨앗이 꽃과 무관한 자리에 내려앉고 FX 도 거기서 터졌다 — 기획 지적 *"심어질 때 이펙트 안나옴"* 의 실제 모습이다.
        ///   (칸마다 부모 컨테이너 Field01~04 가 따로 있어 네 좌표가 전부 다르다.)
        ///
        /// 기준은 <c>pendingActiveFieldCount</c> 다 — 그림 반영(<see cref="CommitAddedField"/>)은 씨앗이 도착한 뒤라
        /// 비행을 띄우는 시점에는 <c>activeFieldCount</c> 가 아직 옛 값이다. 그것을 쓰면 <b>직전</b> 꽃으로 날아간다.
        /// 대상 칸은 이 시점에 아직 꺼져 있지만, 비활성 오브젝트도 좌표는 유효하다.
        /// </summary>
        public Vector3 GetPendingFlowerPosition()
        {
            //대상 칸을 못 구하면 패널 원점으로 물러선다 — 연출이 통째로 사라지는 것보다 낫다.
            if (flowerObjects.IsNullOrEmpty()) return transform.position;

            int flowerIndex = pendingActiveFieldCount - 1;
            if (flowerIndex < 0 || flowerIndex >= flowerObjects.Length) return transform.position;
            if (flowerObjects[flowerIndex] == null) return transform.position;

            return flowerObjects[flowerIndex].transform.position;
        }

        /// <summary>
        /// [기획서 §6] 물뿌리개를 <b>켜면서</b> 물 주는 모션을 처음부터 재생하고, 클립이 끝나면 다시 끈다.
        /// 🔴 <c>Ani_Controller_4DropItemHatchet01</c> 은 상태가 <c>Active</c> 하나뿐이고 <b>파라미터가 없다</b> —
        /// 트리거로 부를 수단이 없어, 오브젝트 활성/비활성이 곧 재생 제어다.
        /// (파라미터를 추가하려면 Animator 컨트롤러 저작이 필요한데, 그건 아트 자산이라 코드에서 건드리지 않는다.)
        ///
        /// 🔴 [검수 시트 row 3-① 2026-08-13] *"물뿌리개 뿌릴때 hit 애니 재생안됨"* —
        /// 종전 주석은 *"물 파티클은 이 오브젝트의 자식으로 이미 들어와 있다"* 였는데 <b>사실이 아니다</b>.
        /// <c>FourDropItemHatchet01.prefab</c> 실측 계층은 <c>root(Animator) → Bone_Body → FellingItemHatchet</c> 뿐이고
        /// ParticleSystem 이 하나도 없다. <c>fx_FourDropHatcht01Water</c> 는 <b>독립 FX 프리팹</b>으로만 존재하고
        /// (Addressable bare address 등록 완료) <b>코드 참조가 0건</b>이라 아무도 띄우지 않았다.
        /// → 모션과 함께 여기서 직접 띄운다.
        /// </summary>
        private void PlayWateringCanMotion()
        {
            //프리팹 바인딩 전이면 조용히 건너뛴다 — 소환은 이미 끝났고 연출만 없는 것이라 진행을 막지 않는다.
            if (wateringCanAnimator == null) return;

            //연타 대비 — 이전 타이머가 살아 있으면 그것이 방금 켠 물뿌리개를 먼저 꺼 버린다.
            wateringCanCts = wateringCanCts.CancelAndDispose(true);
            PlayWateringCanMotionAsync(wateringCanCts.Token).Forget();
        }

        /// <summary>
        /// 물뿌리개를 켜고 <see cref="WATERING_CAN_ANIM_STATE"/> 를 되감아 재생한 뒤, 클립 길이만큼 기다렸다가 끈다.
        /// </summary>
        private async UniTaskVoid PlayWateringCanMotionAsync(CancellationToken ct)
        {
            //🔴 활성화가 먼저다 — 꺼져 있는 Animator 에는 Play 가 먹지 않는다.
            SetWateringCanActive(true);
            wateringCanAnimator.Play(WATERING_CAN_ANIM_STATE, 0, 0f);

            //물 파티클보다 먼저 출발점으로 옮긴다 — 파티클은 그 순간의 물뿌리개 좌표에 뿌려지기 때문이다.
            bool hasPath = TryGetWateringPath(out int startIndex, out int endIndex);
            if (hasPath) MoveWateringCanToFlower(startIndex);

            PlayWaterFx();

            //Play 는 다음 평가 때 반영된다 — 한 프레임 뒤라야 상태 길이를 읽을 수 있다.
            bool canceled = await UniTask.NextFrame(ct).SuppressCancellationThrow();
            if (canceled) return;

            float duration = wateringCanAnimator.GetCurrentAnimatorStateInfo(0).length;
            if (duration <= 0f) duration = WATERING_CAN_ANIM_FALLBACK_DURATION;

            if (hasPath)
            {
                canceled = await PlayWateringTravelAsync(startIndex, endIndex, duration, ct).SuppressCancellationThrow();
            }
            else
            {
                //경로를 못 구했다(화단 미바인딩 등) → 이동 없이 반응만 살린다. 연출이 통째로 사라지는 것보다 낫다.
                PlayAllActiveFlowerHit();
                canceled = await UniTask.Delay(TimeSpan.FromSeconds(duration), cancellationToken: ct).SuppressCancellationThrow();
            }

            //취소는 '다음 터치가 다시 켰다' 또는 '팝업이 닫혔다'는 뜻이라, 여기서 끄면 방금 켠 것을 지운다.
            if (canceled) return;

            SetWateringCanActive(false);
        }

        /// <summary>
        /// [기획 2026-08-13 · 이수진] 물뿌리개가 <b>시작 꽃 → 끝 꽃</b> 으로 이동하고,
        /// *"물뿌리개가 지나가는 위치에 맞춰 hit 애니를 순차적으로 재생"* 한다.
        /// 지나치는 시점은 각 꽃을 경로 위로 정사영한 진행도(0~1)라, 경로 중간의 꽃도 제 차례에 맞는다
        /// (꽃 4개일 때 앞중앙 1번·뒤중앙 4번은 x 가 거의 같아 사실상 동시에 맞는다 — 기획 확인분).
        /// </summary>
        private async UniTask PlayWateringTravelAsync(int startIndex, int endIndex, float duration, CancellationToken ct)
        {
            Transform canTransform = wateringCanAnimator.transform;
            Vector3   startLocal   = GetWateringCanLocalPosition(startIndex);
            Vector3   endLocal     = GetWateringCanLocalPosition(endIndex);

            BuildFlowerHitOrder(startIndex, endIndex);

            int   nextHit = 0;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float progress = duration <= 0f ? 1f : Mathf.Clamp01(elapsed / duration);
                canTransform.localPosition = Vector3.Lerp(startLocal, endLocal, progress);

                //첫 프레임은 progress 0 이라 출발 지점의 꽃이 곧바로 맞는다.
                while (nextHit < flowerHitProgress.Count && progress >= flowerHitProgress[nextHit])
                {
                    PlayFlowerOneShot(flowerSpines[flowerHitOrder[nextHit]], FLOWER_ANIM_HIT);
                    ++nextHit;
                }

                elapsed += Time.deltaTime;
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            canTransform.localPosition = endLocal;

            //마지막 프레임 이후에 남은 꽃(진행도 1.0 부근)은 여기서 마저 태운다 — 도착점의 꽃을 빠뜨리지 않는다.
            while (nextHit < flowerHitProgress.Count)
            {
                PlayFlowerOneShot(flowerSpines[flowerHitOrder[nextHit]], FLOWER_ANIM_HIT);
                ++nextHit;
            }
        }

        /// <summary>
        /// 꽃 수별 물뿌리개 <b>이동 시작/끝 화단</b>. 배열 인덱스는 화단 번호보다 1 작다(프리팹 실측으로 1:1).
        /// <code>
        /// 꽃 1개 → 1        (이동 없음)
        /// 꽃 2개 → 1 &gt; 2
        /// 꽃 3개 → 3 &gt; 2
        /// 꽃 4개 → 3 &gt; 2
        /// </code>
        /// 🔴 표의 두 숫자는 '맞을 꽃 목록'이 아니라 <b>이동의 두 끝점</b>이다 —
        ///   3개일 때 경유지인 1번이 빠져 있는 것이 근거이고, 본문도 *"지나가는 위치에 맞춰"* 라고 적었다.
        ///   그래서 중간에 놓인 꽃은 <see cref="BuildFlowerHitOrder"/> 가 경로에서 스스로 찾아낸다.
        /// </summary>
        private bool TryGetWateringPath(out int startIndex, out int endIndex)
        {
            startIndex = 0;
            endIndex   = 0;
            if (flowerObjects.IsNullOrEmpty()) return false;

            switch (activeFieldCount)
            {
                case 1:  startIndex = 0; endIndex = 0; break;
                case 2:  startIndex = 0; endIndex = 1; break;
                default: startIndex = 2; endIndex = 1; break;
            }

            //발행 데이터가 바뀌어 활성 화단이 표보다 적을 때 없는 칸을 가리키지 않도록 잘라 준다.
            int lastIndex = Mathf.Min(activeFieldCount, flowerObjects.Length) - 1;
            if (lastIndex < 0) return false;

            startIndex = Mathf.Clamp(startIndex, 0, lastIndex);
            endIndex   = Mathf.Clamp(endIndex,   0, lastIndex);

            return flowerObjects[startIndex] != null && flowerObjects[endIndex] != null;
        }

        /// <summary>활성 꽃들을 물뿌리개 경로 진행도 오름차순으로 담는다. 원소가 최대 4개라 삽입 정렬로 충분하다.</summary>
        private void BuildFlowerHitOrder(int startIndex, int endIndex)
        {
            flowerHitOrder.Clear();
            flowerHitProgress.Clear();
            if (flowerSpines == null) return;

            Vector3 startPosition = flowerObjects[startIndex].transform.position;
            Vector3 pathVector    = flowerObjects[endIndex].transform.position - startPosition;
            float   pathSqr       = pathVector.sqrMagnitude;

            int count = Mathf.Min(activeFieldCount, flowerObjects.Length);
            for (int i = 0; i < count; ++i)
            {
                if (flowerObjects[i] == null || flowerSpines[i] == null) continue;

                //경로 위로 정사영해 '물뿌리개가 이 꽃을 언제 지나는가'를 0~1 로 얻는다.
                //이동이 없는 경우(꽃 1개)는 분모가 0이라 전부 0 — 출발과 동시에 맞는다.
                float progress = pathSqr <= 0f
                    ? 0f
                    : Mathf.Clamp01(Vector3.Dot(flowerObjects[i].transform.position - startPosition, pathVector) / pathSqr);

                int insertIndex = flowerHitProgress.Count;
                for (int j = 0; j < flowerHitProgress.Count; ++j)
                {
                    if (progress >= flowerHitProgress[j]) continue;

                    insertIndex = j;
                    break;
                }

                flowerHitProgress.Insert(insertIndex, progress);
                flowerHitOrder.Insert(insertIndex, i);
            }
        }

        /// <summary>물뿌리개를 지정 꽃의 우상단으로 즉시 옮긴다(<see cref="wateringCanFlowerOffset"/>).</summary>
        private void MoveWateringCanToFlower(int flowerIndex)
        {
            wateringCanAnimator.transform.localPosition = GetWateringCanLocalPosition(flowerIndex);
        }

        /// <summary>
        /// 지정 꽃의 우상단에 해당하는 물뿌리개 로컬 좌표(물뿌리개 부모 기준).
        /// 꽃과 물뿌리개는 부모가 달라(Field0N / Bottom) 꽃 좌표를 물뿌리개 부모 공간으로 환산한 뒤 오프셋을 더한다.
        /// z 는 저작값을 유지한다 — UI 라 깊이를 건드릴 이유가 없다.
        /// </summary>
        private Vector3 GetWateringCanLocalPosition(int flowerIndex)
        {
            Transform canTransform = wateringCanAnimator.transform;
            Transform canParent    = canTransform.parent;
            if (canParent == null || flowerObjects[flowerIndex] == null) return canTransform.localPosition;

            Vector3 flowerLocal = canParent.InverseTransformPoint(flowerObjects[flowerIndex].transform.position);

            return new Vector3(flowerLocal.x + wateringCanFlowerOffset.x,
                               flowerLocal.y + wateringCanFlowerOffset.y,
                               canTransform.localPosition.z);
        }

        /// <summary>
        /// [검수 시트 row 3-①] 물 뿌리는 파티클을 물뿌리개 위치에 띄운다.
        /// 물뿌리개 오브젝트의 자식이 아니라 <b>독립 FX</b> 라 코드가 직접 로드해야 한다(사유는 <see cref="PlayWateringCanMotion"/> 주석).
        /// 연출이라 실패해도 소환 진행을 막지 않는다.
        /// </summary>
        private void PlayWaterFx()
        {
            if (eventBoard == null) return;
            if (wateringCanAnimator == null) return;

            //회수는 CreateFx 의 finally 가 책임진다 — 이 프리팹에는 AutoKillEffect 가 없어 직접 띄우면 반납되지 않고 쌓인다.
            EffectHelper.CreateFx(WATER_EFFECT_PATH,
                                  eventBoard.EffectParent,
                                  wateringCanAnimator.transform.position,
                                  WATER_EFFECT_DURATION,
                                  gameObject.GetCancellationTokenOnDestroy()).Forget();
        }

        /// <summary>
        /// 활성 화단의 꽃 <b>전부</b>가 동시에 <c>Hit</c> 를 재생하는 <b>폴백</b>.
        /// 🔴 정상 경로가 아니다 — 기획(2026-08-13)은 *"물뿌리개가 지나가는 위치에 맞춰 **순차적으로**"* 를 요구하고,
        ///   그것은 <see cref="PlayWateringTravelAsync"/> 가 담당한다.
        ///   여기는 이동 경로를 못 구했을 때(화단 미바인딩 등) 물맞는 반응이 통째로 사라지지 않게 남겨 둔 자리다.
        /// </summary>
        private void PlayAllActiveFlowerHit()
        {
            if (flowerSpines == null) return;

            for (int i = 0; i < flowerSpines.Length; ++i)
            {
                //비활성 화단은 그림 자체가 꺼져 있어 재생해도 보이지 않는다 → 건너뛴다.
                if (i >= activeFieldCount) break;

                PlayFlowerOneShot(flowerSpines[i], FLOWER_ANIM_HIT);
            }
        }

        /// <summary>
        /// [검수 시트 row 3-④] 씨앗으로 <b>새로 늘어난 꽃</b>이 <c>Grow</c> 를 1회 재생하고 <c>Idle</c> 루프로 돌아온다.
        /// 방금 켜진 마지막 칸만 자라야 한다 — 전부 태우면 이미 자라 있던 꽃까지 다시 솟는다.
        /// </summary>
        private void PlayFlowerGrow(int flowerIndex)
        {
            if (flowerSpines == null) return;
            if (flowerIndex < 0 || flowerIndex >= flowerSpines.Length) return;

            //[HL-915] 1227 "씨앗 사용 후 꽃이 자랄때" — 기획이 지정한 시점은 **Flower 의 SkeletonGraphic 이 Grow 클립을 무는 순간**이라 이 자리다.
            //🔴 호출부(EventBlockControllerGrowItem)에 두었던 것을 여기로 내렸다. 그쪽은 두 갈래로 어긋난다:
            //  ① CommitAddedField 는 멱등이라 도착 콜백과 폴백이 둘 다 부르면 두 번째는 조용히 물러서는데,
            //     소리는 그 바깥에 있어 씨앗 하나에 두 번 울렸다.
            //  ② CommitAddedField 는 TryAddActiveField 의 added == false 갈래에서도 불린다 — 그 경로는 호출부를 거치지 않아 소리가 새 나갔다.
            //여기는 grew 판정을 통과한 칸만 닿으므로 화단 최대치라 꽃이 늘지 않는 경우에는 울리지 않는다.
            SoundManager.Instance.PlaySound(SoundName.FourDropItem_FlowerGrow);

            PlayFlowerOneShot(flowerSpines[flowerIndex], FLOWER_ANIM_GROW);
        }

        /// <summary>
        /// 지정 클립을 1회 재생하고 곧바로 <c>Idle</c> 루프를 이어 붙인다.
        /// 클립이 저작돼 있지 않으면 아무 것도 하지 않는다(그림이 기본 Idle 그대로 남는다) —
        /// 테마가 늘어 스파인이 갈릴 때 조용히 예외가 나지 않게 하는 방어다.
        /// </summary>
        private void PlayFlowerOneShot(SkeletonGraphic spine, string animationName)
        {
            if (spine == null) return;

            spine.Initialize(false);

            Spine.AnimationState state = spine.AnimationState;
            Spine.SkeletonData   data  = spine.SkeletonData;
            if (state == null || data == null) return;
            if (data.FindAnimation(animationName) == null) return;

            state.SetAnimation(0, animationName, false);
            if (data.FindAnimation(FLOWER_ANIM_IDLE) != null)
                state.AddAnimation(0, FLOWER_ANIM_IDLE, true, 0f);
        }

        /// <summary>물뿌리개(<c>FellingItemHatchet</c>) 오브젝트를 켜고 끈다. Animator 가 그 오브젝트에 직접 붙어 있다.</summary>
        private void SetWateringCanActive(bool isActive)
        {
            if (wateringCanAnimator == null) return;

            wateringCanAnimator.gameObject.SetActive(isActive);
        }

        private void ApplyActiveFieldCount()
        {
            //[기획서 §2] "화단에 배치된 꽃 수가 3이 되었을 때 재화 2배 소진 기능 활성화" —
            //배수 토글의 활성 판정이 **활성 화단 수**라(MergeEventBoard.CanToggleGrowMultiplier) 이 값이 바뀌는 지점마다 밀어 줘야 한다.
            //버튼은 스스로 변화를 알 수 없고, 화단 수를 바꾸는 경로(씨앗 사용·저장 복원·에디터 치트)가 전부 여기를 지난다.
            if (eventBoard != null) eventBoard.RefreshGrowMultiplierToggle();

            if (flowerObjects.IsNullOrEmpty()) return;

            for (int i = 0; i < flowerObjects.Length; ++i)
            {
                if (flowerObjects[i] == null) continue;

                flowerObjects[i].SetActive(i < activeFieldCount);
            }
        }

        /// <summary>
        /// 화단 칸을 눌렀을 때. <paramref name="fieldIndex"/> 는 <see cref="fieldButtons"/> 의 인덱스이고
        /// <b>안내 말풍선을 어느 칸 위에 띄울지</b>에만 쓰인다 — 소환 동작 자체는 칸과 무관하다.
        /// </summary>
        private void OnClickField(int fieldIndex)
        {
            //프리팹 바인딩 전이면 아무 것도 하지 않는다.
            if (eventBoard == null) return;
            //보드 전환(row push-up) 중에는 보드 변형을 막는다(EventBlockMain 클릭 경로와 동일한 게이팅).
            //[HL-1224] 획득 팝업~구름 해금 연출 구간도 함께 막는다 — 화단 터치는 **블록을 배출**하므로
            //그 블록이 최초 획득이 되면 다음 구름 그룹을 해금해 앞 그룹의 연출 예약을 덮어쓴다.
            if (eventBoard.IsMergeInputBlocked) return;

            int mergeEventId = eventBoard.MergeEventId;
            //[기획서 §5-1] 배수는 **개수가 아니라 레벨**을 올린다 — "2배 진행 시 레벨 1 상승하여 배출".
            //소모량과 생성 블록(승격 결과)을 한 함수가 함께 확정하므로 '재화만 2배 나가고 레벨은 그대로'가 될 수 없다.
            //손가락 가이드①(MergeEvent.TryGetGuideFieldPanel)도 같은 함수를 보므로 안내와 실제가 어긋나지 않는다.
            if (MergeEventFourDropItemHelper.TryGetSummonPlan(mergeEventId, eventBoard.GrowMultiplier, eventBoard.AllowedGrowMultipliers,
                                                             out int summonBlockId, out int currencyConsume, out int appliedMultiplier) == false)
            {
                DLogger.Error($"MergeEventFourDropItemFieldPanel::OnClickField::소환 계획 실패(FourDropItemSet 행 또는 itemIdx1 이 유효하지 않다). event[{mergeEventId}]");
                return;
            }

            if (eventBoard.IsOverCurrency(currencyConsume) == false)
            {
                //[UI 변경 2026-08-14] 토스트에 더해 **눌린 칸 바로 위**에 안내 말풍선을 띄운다
                //(기획서 §5-1 "아이템 생성 영역"의 아이콘 안내).
                //🔴 단 **꽃이 이미 자란 칸**에서는 띄우지 않는다(리드 확정) — 말풍선은 아직 비어 있는 칸에서만 뜬다.
                //  꽃 활성 여부는 ApplyActiveFieldCount 와 같은 기준(i < activeFieldCount)을 쓴다. 두 곳이 갈리면
                //  그림은 꽃이 있는데 안내는 빈 칸으로 도는 어긋남이 생긴다.
                //
                //🔴 [HL-1043] 토스트 문구도 **같은 기준**으로 가른다 — 종전에는 빈 칸에서도 재화 부족 문구가 나가
                //  QA 지적(*"꽃다발 없는 영역 터치시 다른 토스트 메시지가 노출됨"*)을 받았다.
                //  말풍선(아이콘)은 맞는데 텍스트만 어긋나 있던 것이라, 둘을 한 분기에 묶어 다시 갈라지지 않게 한다.
                bool isEmptyField = fieldIndex >= activeFieldCount;
                if (isEmptyField)
                {
                    eventBoard.PlayTextEmptyFieldItem();
                    ShowFieldItemTooltip(fieldIndex);
                }
                else
                {
                    eventBoard.PlayTextEmptyCurrency();
                }

                return;
            }

            //[기획서 §5-1] "빈 공간이 1개라도 있을 때 사용 가능 / 공간 부족 시 버블(=보관함)로 이동".
            //자리가 0이면 거절한다 — 전량이 보관함으로 들어가면 보드에 아무 변화가 없어
            //유저가 재화만 쓰고 아무 것도 못 본 것과 같다.
            //🔴 여기서는 **자리가 있는가**만 본다. 실제 배치 목록은 레어 확정 뒤에 다시 받는다 —
            //  레어가 추가분이라 총 소환 수가 이 시점에는 아직 정해지지 않았기 때문이다.
            Vector3 summonPosition = GetSummonStartPosition();
            if (eventBoard.Searcher.GetEmptySlots_ASC(1).IsNullOrEmpty())
            {
                eventBoard.PlayTextFullBoard(summonPosition);
                return;
            }

            //클라 낙관적 차감 → 이어지는 UseGrowCurrency 액션에서 서버 권위값으로 덮어써진다(화단과 동일 관례).
            eventBoard.AddCurrencyForSequence(-currencyConsume);

            //[기획서 §4-1] "물뿌리개 소모 시 **낮은 확률로** 머지 판에 생성(레어1)" — 뽑기와 주기 진행은 서버가 함께 한다.
            //🔴 재화를 **차감한 뒤** 뽑는다. 앞에서 뽑으면 자리 부족·재화 부족으로 되돌아간 탭에도 주기가 소모돼
            //  유저가 아무것도 못 받고 보장 회차만 까먹는다.
            //레어는 한 탭에 **1개**만 나온다(확률표가 탭 단위라 개수만큼 굴리면 표가 뜻하는 확률이 아니게 된다).
            int rareBlockId = 0;
            if (MergeEventFourDropItemHelper.TryGetRareItemId(mergeEventId, out int rareItemId))
            {
                FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_MergeEvent_TryDrawRareItem(activeFieldCount, out bool isRare);
                if (isRare)
                {
                    //[검수 시트 row 27 · 2026-08-14] *"배수 적용 시 레어 아이템의 레벨도 오를 수 있도록 처리"* —
                    //일반 아이템과 **같은 단계 수**를 태운다. 재화를 그 배수로 냈으니 산출도 같은 기준이어야 한다.
                    //🔴 레어 라인이 그 단계를 못 채우면(최종 단계에 가까울 때) 원본 그대로 둔다 —
                    //  레어의 승격 실패를 이유로 일반 아이템 배수까지 낮추지는 않는다(그쪽은 자기 라인으로 이미 판정이 끝났다).
                    int rareStepCount = MergeEventFourDropItemHelper.GetUpgradeStepCount(appliedMultiplier);
                    rareBlockId = MergeEventFourDropItemHelper.TryGetUpgradedBlockId(rareItemId, rareStepCount, out int upgradedRareId)
                                      ? upgradedRareId
                                      : rareItemId;
                }
            }

            //[검수 시트 row 20 · 2026-08-13] 기획 확정(중요) — *"레어 아이템은 일반 아이템 **대신** 나오는 게 아니라
            //**추가로** 나오는 형태여야 합니다."* 종전에는 첫 칸을 레어로 덮어써 일반 아이템이 한 개 줄었다.
            //→ 일반은 항상 활성 화단 수(activeFieldCount)만큼 나오고, 레어는 그 위에 1개가 더 얹힌다.
            int rareCount        = rareBlockId != 0 ? 1 : 0;
            int totalSummonCount = activeFieldCount + rareCount;

            //GetEmptySlots_ASC 는 개수 보장이 없다 → 놓을 수 있는 만큼만 보드에 배치한다.
            List<EventSlot> emptySlots = eventBoard.Searcher.GetEmptySlots_ASC(totalSummonCount);
            int boardAssignCount = emptySlots.IsNullOrEmpty() ? 0 : Mathf.Min(totalSummonCount, emptySlots.Count);
            for (int i = 0; i < boardAssignCount; ++i)
            {
                //레어는 첫 칸에 놓는다 — 보드에 못 놓고 보관함으로 밀리면 유저가 등장을 못 본다.
                //배수 승격은 위 뽑기 지점에서 이미 rareBlockId 에 반영해 두었다(검수 시트 row 27).
                int assignBlockId = (i == 0 && rareCount != 0) ? rareBlockId : summonBlockId;

                EventBlockMain instance = eventBoard.AssignCreateBlockToSlot(emptySlots[i], assignBlockId, MergeEvent.MergeEventAddType.MakeByBlock);
                if (instance == null) continue;

                instance.transform.position = summonPosition;
                instance.OnMoveBlockSpawn(emptySlots[i]);
            }

            //자리가 모자란 분은 보관함으로 보낸다(유실 0). DirectApi_AddDepotBlock 이 SortDepot + SaveChanges 까지 한다.
            //보드에 한 칸도 못 놓은 경우는 위에서 이미 거절했으므로, 레어는 항상 보드로 간다(여기는 일반분만 남는다).
            for (int i = boardAssignCount; i < totalSummonCount; ++i)
            {
                FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_AddDepotBlock(summonBlockId, 1);
            }

            //보관함으로 간 분이 있을 때만 HUD 를 깨운다 — 매 탭마다 보내면 보관함 UI 가 불필요하게 요동친다.
            if (boardAssignCount < totalSummonCount) Message.Send(new MergeEventUpdateDepotMsg());

            SoundManager.Instance.PlaySound(SoundName.MergeEventBlockReward);
            PlayWateringCanMotion();

            FsProcessMergeEvent.MergeEventActionUseGrowCurrency currencyAction = new() { consume = currencyConsume };
            eventBoard.ActionMergeEvent?.Invoke(MergeEvent.MergeEventActionType.UseGrowCurrency, currencyAction);
            eventBoard.ActionMergeEvent?.Invoke(MergeEvent.MergeEventActionType.MakeBlock, null);

            DLogger.Log($"MergeEventFourDropItemFieldPanel::OnClickField::칸[{fieldIndex}] item[{summonBlockId}] 레어[{rareBlockId}] 배수[x{appliedMultiplier}] 소모[{currencyConsume}] 활성화단[{activeFieldCount}] 총소환[{totalSummonCount}] 보드[{boardAssignCount}] 보관함[{totalSummonCount - boardAssignCount}]");
        }

        /// <summary>
        /// [검수 시트 row 21 · 2026-08-13] 소환된 아이템이 출발하는 지점 = <b>밭의 최상단 중앙</b>.
        ///
        /// 기획 지적: *"아이템이 생성될 때, 밭 하단으로 생성됩니다 → **최상단에서 등장해야 합니다**."*
        /// 종전에는 <c>transform.position</c>(= 화단 패널의 피벗)을 그대로 썼는데, 그 피벗이 밭 아래쪽이라
        /// 아이템이 밭에 파묻힌 자리에서 솟아 물을 준 꽃과 무관해 보였다.
        /// 패널의 월드 모서리에서 위 두 점의 중점을 잡아 밭 상단 한가운데에서 출발시킨다 —
        /// 상수 오프셋을 두지 않으므로 밭 크기가 바뀌어도 따라간다.
        /// </summary>
        private Vector3 GetSummonStartPosition()
        {
            RectTransform panelRect = transform as RectTransform;
            //UI 가 아닌 구성(테마 교체 등)이면 종전 기준으로 물러선다 — 연출 위치라 진행을 막을 이유가 없다.
            if (panelRect == null) return transform.position;

            panelRect.GetWorldCorners(worldCornersBuffer);

            //GetWorldCorners 규약: 0 = 좌하 / 1 = 좌상 / 2 = 우상 / 3 = 우하.
            return (worldCornersBuffer[1] + worldCornersBuffer[2]) * 0.5f;
        }

        /// <summary>
        /// [UI 변경 2026-08-14] 재화가 모자랄 때 <b>눌린 칸 바로 위</b>에 안내 말풍선을 띄운다.
        /// 기획서 970293249 §5-1 "메인 팝업 전반 &gt; 아이템 생성 영역"의 아이콘 안내에 대응한다.
        /// </summary>
        private void ShowFieldItemTooltip(int fieldIndex)
        {
            if (fieldItemTooltip == null) return;

            MoveFieldItemTooltipAboveField(fieldIndex);

            //진행 중이던 닫힘 연출을 '다 보이는 상태'로 되돌려 둔다 — 두 트윈이 같은 스케일을 동시에 쓰면 크기가 어긋난 채 굳는다.
            //Close 는 (저작 크기 → 0) 이라 Rewind 가 곧 저작 크기다.
            RewindTween(fieldTooltipCloseTween);

            //🔴 활성화가 먼저다 — DOTweenAnimation 은 자기 Awake 에서 트윈을 만드는데 이 오브젝트는 평소 꺼져 있어,
            //  첫 터치의 Awake 가 바로 이 SetActive 안에서 **동기로** 돈다. 순서를 뒤집으면 첫 터치만 연출이 빠진다.
            fieldItemTooltip.gameObject.SetActive(true);
            if (fieldTooltipOpenTween != null) fieldTooltipOpenTween.DORestartById(FIELD_TOOLTIP_TWEEN_OPEN_ID);

            //연타 대비 — 이전 타이머가 살아 있으면 그것이 방금 띄운 말풍선을 먼저 지운다.
            fieldTooltipCts = fieldTooltipCts.CancelAndDispose(true);
            HideFieldItemTooltipAsync(fieldTooltipCts.Token).Forget();
        }

        /// <summary>
        /// 말풍선을 <b>눌린 칸의 윗변 중앙</b>으로 옮긴다.
        /// 말풍선 피벗이 (0.5, 0)(아래 중앙)이라 그 좌표에 그대로 놓으면 밑변이 칸의 윗변에 붙는다 —
        /// 상수 오프셋 없이 '버튼보다 위'가 성립하고 칸 크기가 바뀌어도 따라간다.
        /// 🔴 말풍선과 화단은 부모가 달라(BossRaidMain / Bottom) 로컬 좌표로는 같은 자리를 못 가리킨다 → 월드 좌표로 옮긴다.
        /// </summary>
        private void MoveFieldItemTooltipAboveField(int fieldIndex)
        {
            if (fieldButtons.IsNullOrEmpty()) return;
            if (fieldIndex < 0 || fieldIndex >= fieldButtons.Length) return;
            if (fieldButtons[fieldIndex] == null) return;

            RectTransform fieldRect = fieldButtons[fieldIndex].transform as RectTransform;
            //UI 가 아닌 구성이면 저작 위치를 그대로 둔다 — 위치가 틀리는 것보다 안 움직이는 편이 낫다.
            if (fieldRect == null) return;

            fieldRect.GetWorldCorners(worldCornersBuffer);

            //GetWorldCorners 규약: 0 = 좌하 / 1 = 좌상 / 2 = 우상 / 3 = 우하.
            fieldItemTooltip.position = (worldCornersBuffer[1] + worldCornersBuffer[2]) * 0.5f;
            //미세 조정은 앵커 좌표로 더한다 — 해상도 스케일을 함께 타야 눈으로 맞춘 값이 유지된다.
            fieldItemTooltip.anchoredPosition += fieldItemTooltipOffset;
        }

        /// <summary>유지 시간이 지나면 닫힘 연출을 태우고 숨긴다.</summary>
        private async UniTaskVoid HideFieldItemTooltipAsync(CancellationToken ct)
        {
            bool canceled = await UniTask.Delay(TimeSpan.FromSeconds(FIELD_TOOLTIP_DURATION), cancellationToken: ct)
                                         .SuppressCancellationThrow();
            //취소는 '다른 칸을 눌러 새로 띄웠다' 또는 '팝업이 닫혔다'는 뜻이라, 여기서 끄면 방금 띄운 것을 지운다.
            if (canceled) return;
            if (fieldItemTooltip == null) return;

            //닫힘 트윈이 저작돼 있지 않으면 그냥 끈다 — 연출이 없다고 말풍선이 화면에 영영 남으면 안 된다.
            if (fieldTooltipCloseTween == null)
            {
                ApplyFieldItemTooltipHidden();
                return;
            }

            //Open 은 isFrom(0 → 저작 크기)이라 Complete 가 '다 보이는 상태'다. 거기서 닫힘을 이어야 크기가 튀지 않는다.
            CompleteTween(fieldTooltipOpenTween);
            fieldTooltipCloseTween.DORestartById(FIELD_TOOLTIP_TWEEN_CLOSE_ID);

            //길이를 상수로 박지 않고 트윈이 멈출 때까지 기다린다 — 아트가 duration 을 바꿔도 코드가 따라간다.
            canceled = await UniTask.WaitWhile(() => IsTweenPlaying(fieldTooltipCloseTween), cancellationToken: ct)
                                    .SuppressCancellationThrow();
            if (canceled) return;

            ApplyFieldItemTooltipHidden();
        }

        /// <summary>말풍선을 연출 없이 즉시 숨긴다(팝업 닫힘·재입장 등 <b>연출을 보여 줄 수 없는</b> 경로).</summary>
        private void HideFieldItemTooltip()
        {
            fieldTooltipCts = fieldTooltipCts.CancelAndDispose(false);
            ApplyFieldItemTooltipHidden();
        }

        /// <summary>
        /// 말풍선 오브젝트를 끄고 스케일을 저작 상태로 되돌린다.
        /// 트윈 도중 꺼지면 중간 스케일로 굳은 채 남아 다른 경로(에디터 미리보기 등)에서 작게 보인다 →
        /// 1 로 되돌려 둔다(형제 안내 <see cref="MergeEventInteractionObject"/> 와 같은 처리).
        /// </summary>
        private void ApplyFieldItemTooltipHidden()
        {
            if (fieldItemTooltip == null) return;

            fieldItemTooltip.localScale = Vector3.one;
            fieldItemTooltip.gameObject.SetActive(false);
        }

        /// <summary>두트윈이 실제 재생 중인지(생성 전·완료·kill 상황 안전 가드 포함).</summary>
        private static bool IsTweenPlaying(DOTweenAnimation tweenAnimation)
            => tweenAnimation != null
               && tweenAnimation.tween != null
               && tweenAnimation.tween.IsActive()
               && tweenAnimation.tween.IsPlaying();

        /// <summary>진행 중이던 트윈을 시작 지점으로 되돌리고 멈춘다.</summary>
        private static void RewindTween(DOTweenAnimation tweenAnimation)
        {
            if (tweenAnimation == null) return;
            if (tweenAnimation.tween == null || tweenAnimation.tween.IsActive() == false) return;

            tweenAnimation.tween.Rewind();
        }

        /// <summary>진행 중이던 트윈을 끝 지점으로 즉시 보낸다.</summary>
        private static void CompleteTween(DOTweenAnimation tweenAnimation)
        {
            if (tweenAnimation == null) return;
            if (tweenAnimation.tween == null || tweenAnimation.tween.IsActive() == false) return;

            tweenAnimation.tween.Complete();
        }
    }
}

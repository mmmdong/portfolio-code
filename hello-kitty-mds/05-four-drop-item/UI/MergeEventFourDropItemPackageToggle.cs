using UnityEngine;

namespace GameLogic.MergeEvent.SubObject
{
    /// <summary>
    /// 이벤트 패키지 버튼 노출 토글(FourDropItem). 메인 팝업 `PassPanel/Btn_Arrow` 에 붙는다.
    ///
    /// 토글 값 하나로 <b>둘</b>을 그린다 — 패키지 버튼의 노출과, 화살표 아이콘의 방향이다.
    /// 화살표는 회전이 아니라 <b>반전 저작본 2개를 번갈아 켜는</b> 방식이다(<c>ToggleOn</c> 0° / <c>ToggleOff</c> Y축 180°).
    ///
    /// 패키지 대상 게임오브젝트는 형제인 `Btn_EventPackage _1` 이고, 토글이 On 이면 켜고 Off 면 끈다.
    /// 부모 `PassPanel` 이 LayoutGroup 이라 SetActive 만으로 남은 버튼들의 배치가 따라온다 —
    /// 좌표를 직접 옮기지 않는 이유다.
    ///
    /// <b>토글 상태는 <see cref="UIToggleEx"/> 가 들고 있고 이 클래스는 그 값을 대상 GO 에 옮기기만 한다.</b>
    /// 상태를 여기 따로 두면 진실이 둘이 되어, 인스펙터에서 `m_IsOn` 을 바꿨을 때 표시와 어긋난다.
    /// </summary>
    public class MergeEventFourDropItemPackageToggle : MonoBehaviour
    {
        //토글이 On 일 때만 보이는 대상. 프리팹 실측: `PassPanel/Btn_EventPackage _1`(m_IsActive 0 으로 출고).
        [SerializeField] private GameObject packageObject;

        //토글이 Off 일 때만 보이는 **반전 화살표**. 프리팹 실측: `PassPanel/Btn_Arrow/ToggleOff` —
        //`ToggleOn` 과 같은 스프라이트(FourDrop_Arrow.png)를 Y축 180° 돌려 저작한 짝이다.
        //🔴 배선이 빠져 있었다: `ToggleOn` 은 UIToggleEx.mCheckImg 가 껐다 켜지만 `ToggleOff` 는 어디에도 물려 있지 않았고
        //  프리팹 출고값이 m_IsActive 0 이라, Off 에서 **두 화살표가 모두 꺼져 아이콘이 통째로 사라졌다**
        //  (기본값이 m_IsOn 0 이라 팝업을 여는 순간부터 그 상태였다).
        //🔴 On 쪽은 여기서 건드리지 않는다 — mCheckImg 가 이미 주인이라 양쪽에서 만지면 진실이 둘이 된다.
        [SerializeField] private GameObject arrowOffObject;

        //같은 게임오브젝트에 붙어 있는 고정 컴포넌트라 Awake 에서 1회만 캐싱한다(런타임 반복 GetComponent 금지).
        private UIToggleEx arrowToggle;

        //🔴 [지시 2026-08-14] 이 이벤트에 **연결된 패키지가 실제로 있는가**. 판정은 MergeEvent.UpdatePackageButton 이
        //  MergeEvent_Master.packageIdx 로 하고(보스레이드와 같은 공용 경로), 결과만 여기로 넘어온다.
        //  기본값 false 가 안전한 쪽이다 — 통보를 받기 전에는 켜지 않는다(없는 패키지 버튼이 한 프레임 번쩍이지 않는다).
        private bool packageAvailable;

        private void Awake()
        {
            arrowToggle = GetComponent<UIToggleEx>();
        }

        private void OnEnable()
        {
            //팝업은 파괴가 아니라 SetActive 로 재사용된다 → 꺼져 있는 동안 어긋났을 수 있는 표시를 현재 토글 값으로 맞춘다.
            //onValueChanged 는 값이 '바뀔 때' 만 오므로 초기 동기화는 여기서 해야 한다.
            ApplyView(arrowToggle.isOn);
        }

        private void Start()
        {
            //델리게이트/이벤트 구독은 Start 에서 처리한다(프로젝트 규약). 해제는 OnDestroy 1회.
            arrowToggle.onValueChanged.AddListener(ApplyView);
        }

        private void OnDestroy()
        {
            arrowToggle.onValueChanged.RemoveListener(ApplyView);
        }

        /// <summary>
        /// [지시 2026-08-14] 연결 패키지 유무를 통보받는다 — 호출자는 <c>MergeEvent.UpdatePackageButton</c> 하나뿐이다.
        ///
        /// 🔴 <b>같은 게임오브젝트를 두 주체가 쥐고 있었다.</b> <c>UpdatePackageButton</c> 은 <c>MergeEvent_Master.packageIdx</c>
        ///   판정 결과로, 이 토글은 화살표 상태로 각각 <c>Btn_EventPackage _1</c> 을 켜고 껐다 → 나중에 도는 쪽이 이겨서
        ///   ①패키지가 없는데 화살표를 펴면 빈 버튼이 뜨고 ②접어 둔 상태로 팝업을 열면 데이터 갱신이 그것을 다시 폈다.
        ///   이제 활성 조건은 <b>둘의 AND</b> 이고, 그리는 주체는 이 클래스 하나로 모았다.
        ///
        /// 보스레이드는 이 토글이 없어 <c>UpdatePackageButton</c> 이 단독으로 그린다 — 그쪽 동작은 그대로다.
        /// </summary>
        public void SetPackageAvailable(bool available)
        {
            packageAvailable = available;

            //🔴 화살표 뿌리는 arrowToggle 유무와 **무관하게 먼저** 확정한다. 이 한 줄이 이 클래스의 유일한 안전장치다.
            //  순서를 바꾸면(아래 null 체크가 먼저면) 자기 자신을 잠근다 —
            //  이 GO 가 꺼져 있으면 Awake 가 돌지 않아 arrowToggle 이 null 인데, 그 상태로 물러서면
            //  ①OnEnable 은 GO 가 꺼져 있어 안 돌고 ②여기는 조기 반환이라 다시 켜 줄 주체가 하나도 없다.
            //  그동안 UpdatePackageButton 은 같은 프레임에 패키지 버튼(형제 GO)을 켜므로
            //  **'패키지 버튼만 뜨고 화살표는 영영 사라진'** 상태로 굳는다.
            //  SetActive(true) 는 Awake → OnEnable 을 **동기로** 부르므로, 이 줄이 끝나면 arrowToggle 이 채워져 있고
            //  OnEnable 안의 ApplyView 도 이미 한 번 돈 상태다(아래 호출은 같은 값으로 한 번 더 그리는 것뿐이라 무해하다).
            gameObject.SetActive(available);

            //available == false 로 켜지 못했고 Awake 도 아직인 경우만 여기 걸린다 — 값은 받아 뒀고 그릴 대상도 꺼져 있다.
            if (arrowToggle == null) return;

            ApplyView(arrowToggle.isOn);
        }

        private void ApplyView(bool isOn)
        {
            //토글은 '펼침/접힘'만 말한다. '패키지가 존재하는가'는 데이터가 정하므로 둘을 함께 본다.
            packageObject.SetActive(isOn && packageAvailable);
            //화살표 **방향** 표시는 토글 값만 따른다(패키지 유무는 아래에서 뿌리째 감추는 것으로 처리한다).
            arrowOffObject.SetActive(isOn == false);

            //🔴 [지시 2026-08-14] 패키지 기간이 아니면 화살표까지 감춘다 —
            //  펼칠 것이 없는 화살표는 눌러도 아무 일이 없어 유저에게 고장으로 읽힌다. GO 를 끄면 레이캐스트도 함께 죽는다.
            //  자식 표시를 먼저 확정한 뒤 마지막에 끈다(순서가 반대여도 값은 보존되지만 읽는 사람이 헷갈리지 않게 고정한다).
            //
            //🔴 <b>이 줄이 자기 GO 를 끌 수 있어도 잠기지 않는 근거는 SetPackageAvailable 의 SetActive 한 줄뿐이다.</b>
            //  프리팹 출고값(active=1)이나 '누군가 다시 켜 주겠지' 에 기대면 안 된다 —
            //  꺼진 상태로 프리팹이 저장되는 순간 Awake 가 영영 돌지 않아 그 전제가 통째로 깨진다.
            //  그래서 되살리는 책임을 arrowToggle 참조가 필요 없는 자리(SetPackageAvailable 최상단)로 올려 두었다.
            gameObject.SetActive(packageAvailable);
        }
    }
}

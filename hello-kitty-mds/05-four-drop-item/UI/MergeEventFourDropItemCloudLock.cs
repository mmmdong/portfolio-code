using UnityEngine;

namespace GameLogic.MergeEvent.SubObject
{
    /// <summary>
    /// 잠금 영역 자물쇠(FourDropItem). 프리팹: <c>UIFourDropLock</c>.
    /// 보드에 <b>하나만</b> 뜬다 — 다음에 해금될 구름 그룹 위, 그 그룹의 기하 중앙이다(연출 계획서 §21-0-3).
    ///
    /// 두 이미지는 <b>밝기</b>가 아니라 <b>구간 종류</b>로 갈린다 —
    /// 기획서 §3-2 10번 *"**특별한 아이템 구간**에는 구름과 열쇠의 **색상이 다르도록** 구성"*.
    /// <c>4DropEvent_Lock01</c>(노랑) = 일반 구간 / <c>4DropEvent_Lock02</c>(보라) = 특별 구간이고,
    /// 판정은 <c>MergeEventFourDropItemHelper.IsSpecialCloudGroup</c>(그룹을 여는 Special 행의 <c>sBlockType</c> 5/6)이 한다.
    /// ※ 밝기 축('잠금 블록 1/2')은 <b>구름</b>이 담당한다(<c>EventBlockStateView.ApplyCloudBrightness</c>) — 자물쇠와 축이 다르다.
    ///
    /// 🔴 프리팹의 자식 이름이 <c>Lcok01</c>·<c>Lcok02</c> 로 <b>오타</b>다(Lock→Lcok).
    /// 이름으로 찾으면 아트가 오타를 고치는 순간 조용히 깨지므로 <b>인스펙터 바인딩</b>으로만 접근한다.
    /// </summary>
    public class MergeEventFourDropItemCloudLock : MonoBehaviour
    {
        //바인딩 대상: UIFourDropLock/Lcok01(노랑·일반) · Lcok02(보라·특별).
        [SerializeField] private GameObject normalSectionLock;
        [SerializeField] private GameObject specialSectionLock;

        /// <summary>
        /// 일반 구간 / 특별 구간 자물쇠 중 하나만 켠다.
        /// 둘 다 켜면 겹쳐 보이므로 한 곳에서 배타적으로 다룬다 — 프리팹 기본값은 둘 다 켜져 있다.
        /// </summary>
        public void SetSpecialSection(bool isSpecialSection)
        {
            if (normalSectionLock != null) normalSectionLock.SetActive(isSpecialSection == false);
            if (specialSectionLock != null) specialSectionLock.SetActive(isSpecialSection);
        }
    }
}

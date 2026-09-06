using UnityEngine;

namespace GameLogic.MergeEvent.SubObject
{
    /// <summary>
    /// [검수 시트 row 29 · 항목 28] FourDropItem 메인 팝업이 <b>자기 안에</b> 들고 있는 유휴 가이드 손가락.
    /// 메인 팝업의 <c>Fingerbase</c>(공용 <c>Prefabs/Tutorial/Fingerbase.prefab</c> 의 사본)에 붙는다.
    ///
    /// 🔴 <b>왜 공용 손가락을 쓰지 않는가</b> — 유휴 가이드는 원래 <c>UIWindowCommonTutorial.fingerBase</c> 를 띄우는데,
    ///   그것은 이 팝업 <b>바깥</b>(별도 튜토리얼 창)에 있어 4드롭 메인 팝업 위로 올라오지 않는다.
    ///   아트가 팝업 안에 손가락 사본을 미리 저작해 둔 것도 같은 이유로 보이며(<c>m_IsActive 0</c> 출고),
    ///   다만 그것을 참조하는 코드가 0곳이라 안내가 통째로 보이지 않았다.
    ///
    /// 표시 규칙은 공용 구현(<c>UIWindowCommonTutorial.PlayFingerTweenPos</c>)을 그대로 따른다 —
    /// 대상의 <b>월드 코너 중심</b>에 놓고 <c>Push</c> 클립을 재생한다. 규칙이 갈리면 같은 안내가 콘텐츠마다 달라 보인다.
    /// </summary>
    public class MergeEventFourDropItemGuideFinger : MonoBehaviour
    {
        //공용 구현과 같은 클립명. 프리팹이 같은 소스(Prefabs/Tutorial/Fingerbase.prefab)라 컨트롤러도 동일하다.
        private const string FINGER_ANIM_PUSH = "Push";

        //프리팹 실측: 손가락 애니메이터는 자식 `Bone_Finger` 에 있다. 인스펙터 바인딩이 확정이라 가드를 두지 않는다.
        [SerializeField] private Animator fingerAnimator;

        /// <summary>
        /// 대상 위로 손가락을 띄운다. 좌표 규칙은 공용 구현과 동일하다(월드 코너 중심).
        ///
        /// 🔴 <b>실제로 띄웠는지를 돌려준다</b> — 호출자(<c>MergeEvent.TryPlayGuideFinger</c>)가 이 값을 그대로 반환하고,
        ///   그 결과가 <c>CommonTutorialManager.ActiveIdleGuideFinger</c> 의 *공용 손가락 폴백* 분기를 정한다.
        ///   대상이 없는데 참을 돌려주면 폴백까지 건너뛴 채 <c>isIdleGuideRunning</c> 만 참이 되어,
        ///   <b>손가락은 안 뜨는데 유휴 가이드가 잠기는</b> 안내 데드락이 된다(유저가 화면을 만질 때까지 다음 케이스로 못 넘어간다).
        /// </summary>
        public bool Show(RectTransform target)
        {
            if (target == null) return false;

            //anchoredPosition 이 아니라 월드 좌표를 쓴다 — 대상이 다른 캔버스·다른 부모 아래일 수 있어
            //로컬 좌표계로는 같은 자리를 가리킬 수 없다(보드 블록과 하단 UI 가 서로 다른 계층에 있다).
            Vector3[] worldCorners = new Vector3[4];
            target.GetWorldCorners(worldCorners);

            gameObject.SetActive(true);
            transform.position = (worldCorners[0] + worldCorners[2]) * 0.5f;

            fingerAnimator.enabled = true;
            fingerAnimator.Play(FINGER_ANIM_PUSH);
            return true;
        }

        /// <summary>손가락을 내린다. 공용 구현의 <c>DeactiveFingerAnimator</c> 와 같이 애니로 밀린 트랜스폼도 되돌린다.</summary>
        public void Hide()
        {
            fingerAnimator.enabled = false;
            fingerAnimator.transform.localPosition = Vector3.zero;
            fingerAnimator.transform.localScale = Vector3.one;

            gameObject.SetActive(false);
        }
    }
}

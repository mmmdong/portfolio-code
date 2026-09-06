using System;

using Cysharp.Threading.Tasks;

using GameLogic.Define;
using GameLogic.Management;

using UnityEngine;

namespace GameLogic.TrophyChallenge
{
    // HUD / 다른 컨텐츠에서 트로피 챌린지 팝업/HUD 버튼을 다룰 때 사용하는 진입점 (Facade).
    // UIManager / Addressable 의 구체 호출을 본 클래스 한 곳에 모아두어,
    // HUD/메뉴/튜토리얼/리뷰툴 등 다양한 호출 지점에서 동일한 경로로 진입하도록 한다(SRP).
    public static class TrophyChallengeUIBridge
    {
        // 명세서.md §7.1 메인 팝업.
        // 프로젝트 공통 패턴(BadgePopupController / ContentEventPudding / UILuckyMatching 동일):
        //   UIManager.OpenUIMsgAsync<T>(infoData).Forget()
        // 어드레서블 어드레스가 타입명과 일치(`m_Address: UIPopupTrophyChallenge`) 하므로 타입 기반 진입.
        public static void OpenMainPopup(long trophyId = 0)
        {
            if (!TryResolveResource(trophyId, out var resource) || resource == null)
            {
                Debug.LogWarning("[TrophyChallenge] 활성 트로피 또는 리소스 미존재 — 팝업 진입 차단");
                return;
            }

            UIManager.OpenUIMsgAsync<UIPopupTrophyChallenge>().Forget();
        }

        // 명세서.md §6.2 (1) — 시작 팝업 진입(레벨 도달 + 미참여 시 자동 호출 포함).
        // 현재 아키텍처상 _Start 는 UIPopupTrophyChallenge 의 내부 패널이며, 팝업 진입 시
        // SetInfo → ShouldShowStartPanel 분기가 _Start 패널을 노출한다. 따라서 OpenMainPopup 과 동일하게
        // UIPopupTrophyChallenge 를 직접 연다(startPopupPath 별도 로드 설계 잔재인 OnRequestOpenPath 미경유).
        public static void OpenStartPopup(long trophyId = 0) => OpenMainPopup(trophyId);

        // 명세서.md §7.1 — 인포 팝업 진입은 메인 팝업(UIPopupTrophyChallenge.OnInfoClicked)이 직접 처리한다.
        // 인포 팝업 루트 컴포넌트가 UIInfoPopupController(MonoBehaviour, UIBase 비파생)라 UIManager 표준 진입을
        // 사용할 수 없어, 메인 팝업이 어드레서블 인스턴스화 + OnSequenceOpenPopup 흐름으로 직접 띄운다.

        // 어드레서블 키 기반으로 팝업을 띄우는 책임은 프로젝트의 공통 UIManager 에 위임.
        // 본 이벤트를 통해 의존 방향을 한 방향으로 유지(도메인 → UI 한정 진입점).
        public static event Action<string> OnRequestOpenPath;

        private static bool TryResolveResource(long trophyId, out TrophyChallengeResourceTableData resource)
        {
            resource = null;
            var manager = TrophyChallengeManager.Instance;
            if (trophyId == 0)
            {
                // 미작업 명세서 G-2/G-3 — 활성 시즌뿐 아니라 만료됐지만 _Clear 강제 진입 대상 시즌도 탐색.
                foreach (var pair in manager.GetMasters())
                {
                    if (!manager.IsActiveSeasonOrPendingClear(pair.Key)) continue;
                    trophyId = pair.Key;
                    break;
                }
            }

            var master = manager.GetMaster(trophyId);
            if (master?.group == null) return false;

            return TableManager.GetData<TrophyChallengeResourceTableData>(master.group.resourceIdx, out resource);
        }
    }
}

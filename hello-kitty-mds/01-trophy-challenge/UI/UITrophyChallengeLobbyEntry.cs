using GameCore.Utils;

using GameLogic.TrophyChallenge;

using UnityEngine;
using UnityEngine.UI;

// 명세서.md §7.1 HUD 입장 버튼 — 트로피 챌린지 HUD 아이콘.
// 프리팹 (Assets/Resources_moved/UI/Prefabs/TrophyChallenge/LobbyTrophyChallenge.prefab) 의 root 에 부착되며,
// (1) 클릭 시 메인 팝업 진입, (2) 활성 시즌이 없거나 오픈 레벨 미달 시 자동 비활성, (3) 레드닷 갱신을 담당한다.
[RequireComponent(typeof(UIButtonEx))]
public class UITrophyChallengeLobbyEntry : MonoBehaviour
{
    [Header("Optional Bindings (자동 탐색 후 비어있을 때만 사용)")]
    [SerializeField] private UIButtonEx clickButton;
    [SerializeField] private GameObject redDotRoot;
    [SerializeField] private UITimerSimple timer;
    [SerializeField] private Image iconImage;
    [SerializeField] private UISpriteAsyncLoader iconLoader;

    private long currentTrophyId;
    private string lastIconKey = string.Empty;

    private void Awake()
    {
        // GetComponent 류는 동일 GameObject 의 컴포넌트 캐싱이므로 그대로 유지.
        if (clickButton == null) clickButton = GetComponent<UIButtonEx>();
        if (timer == null) timer = GetComponent<UITimerSimple>();
        if (iconLoader == null) iconLoader = GetComponent<UISpriteAsyncLoader>();

        // TODO[binding]: redDotRoot 는 LobbyTrophyChallenge.prefab 인스펙터에서 'UIRedDot' 자식 직접 바인딩 필요.
        // TODO[binding]: iconImage 는 LobbyTrophyChallenge.prefab 인스펙터에서 'Image' 자식의 Image 컴포넌트 직접 바인딩 필요.
        // 자동 탐색(transform.Find) 은 Find 류 사용 금지 규칙에 따라 제거됨. 바인딩이 비어 있으면
        // RefreshIcon / RefreshRedDot 이 무동작이 되므로 인스펙터에서 반드시 바인딩 필요.
    }

    private void Start()
    {
        if (clickButton != null) clickButton.onClick.AddListener(OnClick);

        // OnTrophyChallengeStateChangedMsg 가 NotifyChallengeComplete / ClaimFinalReward 콜백에서
        // OnTrophyChallengeCompletedMsg / OnTrophyChallengeFinalRewardClaimedMsg 와 함께 발행되므로
        // (TrophyChallengeManager.cs:207, 230) 본 컴포넌트는 OnStateChanged 만 구독해도 충분하다.
        Message.AddListener<OnTrophyChallengeMasterRefreshedMsg>(OnMasterRefreshed);
        Message.AddListener<OnTrophyChallengeInfoRefreshedMsg>(OnInfoRefreshed);
        Message.AddListener<OnTrophyChallengeStateChangedMsg>(OnStateChanged);
        Message.AddListener<OnTrophyChallengeProgressUpdatedMsg>(OnProgressUpdated);

        Refresh();
    }

    private void OnDestroy()
    {
        if (clickButton != null) clickButton.onClick.RemoveListener(OnClick);

        Message.RemoveListener<OnTrophyChallengeMasterRefreshedMsg>(OnMasterRefreshed);
        Message.RemoveListener<OnTrophyChallengeInfoRefreshedMsg>(OnInfoRefreshed);
        Message.RemoveListener<OnTrophyChallengeStateChangedMsg>(OnStateChanged);
        Message.RemoveListener<OnTrophyChallengeProgressUpdatedMsg>(OnProgressUpdated);
    }

    private void OnMasterRefreshed(OnTrophyChallengeMasterRefreshedMsg _) => Refresh();
    private void OnInfoRefreshed(OnTrophyChallengeInfoRefreshedMsg _) => Refresh();
    private void OnStateChanged(OnTrophyChallengeStateChangedMsg _) => Refresh();
    private void OnProgressUpdated(OnTrophyChallengeProgressUpdatedMsg _) => Refresh();

    public void Refresh()
    {
        var manager = TrophyChallengeManager.Instance;
        if (!TryResolveActiveTrophy(out currentTrophyId))
        {
            // 활성 시즌 없음 — HUD 비활성. (명세서.md §7.6 노출 조건 반전)
            gameObject.SetActive(false);
            return;
        }

        if (!gameObject.activeSelf) gameObject.SetActive(true);

        RefreshIcon();
        RefreshTimer();
        RefreshRedDot(manager);
    }

    private bool TryResolveActiveTrophy(out long trophyId)
    {
        trophyId = 0;
        var manager = TrophyChallengeManager.Instance;
        foreach (var pair in manager.GetMasters())
        {
            if (!manager.IsActiveSeason(pair.Key)) continue;
            trophyId = pair.Key;
            return true;
        }
        return false;
    }

    private void RefreshIcon()
    {
        if (iconLoader == null || iconImage == null) return;

        var master = TrophyChallengeManager.Instance.GetMaster(currentTrophyId);
        if (master?.group == null) return;

        // 리소스 테이블의 buttonIconPath 가 어드레서블 키
        if (!GameLogic.Management.TableManager.GetData<GameLogic.Define.TrophyChallengeResourceTableData>(master.group.resourceIdx, out var resource))
            return;
        if (resource == null) return;

        var key = resource.buttonIconPath;
        if (string.IsNullOrEmpty(key) || key.Equals(lastIconKey)) return;
        lastIconKey = key;
        iconLoader.Load(key, (sprite, color) =>
        {
            if (iconImage == null) return;
            iconImage.sprite = sprite;
            iconImage.color = color;
        });
    }

    private void RefreshTimer()
    {
        if (timer == null) return;

        // 명세서.md §7.2 — 잔여 시간 영역은 `TrophyInfo.endAt` (유저 기준 종료 시간) 기준 카운트다운.
        var info = TrophyChallengeManager.Instance.GetUserInfo(currentTrophyId);
        if (info == null || info.endAt <= 0) return;

        // 미작업 명세서 G-2 — HUD 노출 상태에서 시즌 만료 도래 시 _Clear 종료 연출로 자동 진입.
        // 만료 콜백 → OpenMainPopup → UIPopupTrophyChallenge.SetInfo → ShouldForceShowClearPopup 통과 → _Clear 패널.
        // ECompleteType.Inactive 가 콜백 발화 이후 HUD 게임오브젝트를 비활성화 (UITimerSimple.OnComplete 순서 기준).
        var expiredTrophyId = currentTrophyId;
        timer.StartTimer(
            DateTimeUtils.GetUnixTime(info.endAt),
            UITimerSimple.ECompleteType.Inactive,
            callbackAction: () =>
            {
                // G-1 충돌 회피 — 이미 팝업이 열려 있으면 창 측 OnSeasonTimerExpired 가 _Clear 분기를 처리한다.
                // 여기서 또 OpenMainPopup 을 호출하면 UIManager 가 InActiveUI→재오픈하며 SetInfo 를 재실행해
                // 화면이 깜빡인다. 중복 진입 스킵.
                if (GameLogic.Management.UIManager.Instance.HasActiveUIBasePopup()) return;

                // 깜빡임 방지 — _Clear 강제 진입 대상(최종 보상 미열람 / 만료)이 아니면 진입 자체를 스킵.
                if (!TrophyChallengeManager.Instance.ShouldForceShowClearPopup(expiredTrophyId)) return;

                TrophyChallengeUIBridge.OpenMainPopup(expiredTrophyId);
            });
    }

    private void RefreshRedDot(TrophyChallengeManager manager)
    {
        if (redDotRoot == null) return;
        var show = manager.HasAnyRedDot();
        if (redDotRoot.activeSelf != show) redDotRoot.SetActive(show);
    }

    private void OnClick()
    {
        // 명세서.md §6.2 (2) — 진입 시 메인 팝업으로.
        TrophyChallengeUIBridge.OpenMainPopup(currentTrophyId);
    }
}

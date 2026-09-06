using System.Threading;

using Cysharp.Threading.Tasks;

using GameCore.Utils;

using GameLogic.Define;
using GameLogic.GameManagement;
using GameLogic.Management;
using GameLogic.TrophyChallenge;

using UnityEngine;

// 명세서.md §6.2 ~ §7 — 트로피 챌린지 메인 팝업 (오케스트레이터).
// 프리팹 구조 (Assets/Resources_moved/UI/Prefabs/TrophyChallenge/UIPopupTrophyChallenge.prefab):
//   UIPopupTrophyChallenge (root, UIBasePopup)
//   ├─ ImgBackground
//   ├─ UIPopupTrophyChallenge_Start  — 시작 패널 (PanelTrophyChallenge_Start)
//   ├─ UIPopupTrophyChallenge_Main   — 메인 패널 (PanelTrophyChallenge_Main)
//   └─ UIPopupTrophyChallenge_Clear  — 클리어 패널 (PanelTrophyChallenge_Clear)
//
// 본 클래스의 책임:
//   - 활성 시즌 해석 (TryResolveActiveTrophy) → 각 패널에 컨텍스트 주입 (Setting)
//   - 패널 전환 (Show*Panel)
//   - 메시지 구독 후 패널로 위임 (Info/State/Progress/FinalRewardClaimed)
//   - 패널 이벤트 구독 (Start/Main/Clear 의 버튼 인터랙션)
//
// 컨텐츠 흐름 (명세서 §6.2):
//   _Start 진입(자동) → 도전 버튼 → _Main 진행 → clearCount 도달 + 자동 ClaimFinalReward
//   → _Clear 종료 연출(3단계) → 닫기 + 최종 보상 팝업
public class UIPopupTrophyChallenge : UIBasePopup
{
    // 명세서.md §7.1 — 인포 팝업 어드레서블 키. 프리팹의 루트 컴포넌트는 `UIInfoPopupController`
    // (UIBase 파생 아님) 이므로 UIManager 표준 진입 대신 직접 어드레서블 인스턴스화 후
    // OnSequenceOpenPopup() 으로 표시한다. PopupstoreMain 의 동일 패턴 참조.
    private const string INFO_POPUP_ADDRESS = "UIPopupTrophyChallenge_Info";

    [Header("[Panel Components — 명세서 §6.2]")]
    [SerializeField] private PanelTrophyChallenge_Start startPanel;
    [SerializeField] private PanelTrophyChallenge_Main mainPanel;
    [SerializeField] private PanelTrophyChallenge_Clear clearPanel;

    // 명세서 §7.8 / Confluence 880017421 §2-6 — 최초 튜토리얼이 메인 패널 내부 요소를 포커싱하기 위해
    // TrophyChallengeHelper 가 본 메인 패널에 접근한다.
    public PanelTrophyChallenge_Main MainPanel => mainPanel;

    private long currentTrophyId;
    private TrophyChallengeResourceTableData currentResource;
    // 명세서.md §7.1 — 인포 팝업 인스턴스(첫 호출 시 어드레서블에서 로드 후 메인 팝업 자식으로 부착).
    // 본 팝업이 destroyed 되면 자식이라 함께 정리된다.
    private GameObject infoPopupGameObject;
    private UIInfoPopupController infoPopupController;
    // 최종 보상 수령 직후 진입한 종료 연출인지 — true 면 연출이 끝난 시점(OnClearClosed) 에
    // 최종 보상 팝업을 노출하고, 그 팝업이 닫힐 때 본 팝업을 닫는다.
    // 재접속 강제 연출(§10) 로 진입한 경우엔 false — 이미 지난 시즌의 보상팝업을 다시 띄우지 않는다.
    private bool pendingFinalRewardPopup;

    protected override void Start()
    {
        base.Start();

        // 패널 공통 닫기 버튼 (PanelTrophyChallenge.OnCloseClicked) 은 패널별로 다르게 처리:
        //   _Start: 팝업 종료가 아닌 _Main 전환 (도전 버튼도 동일 이벤트 발화 — _Start 내부에서 통합)
        //   _Main / _Clear: 팝업 종료 (UIBasePopup.Close)
        if (startPanel != null)
        {
            startPanel.OnCloseClicked += OnStartInteracted;
        }
        if (mainPanel != null)
        {
            mainPanel.OnCloseClicked += Close;
            mainPanel.OnInfoClicked += OnInfoClicked;
        }
        if (clearPanel != null)
        {
            clearPanel.OnCloseClicked += OnClearClosed;
        }

        Message.AddListener<OnTrophyChallengeInfoRefreshedMsg>(OnInfoRefreshed);
        Message.AddListener<OnTrophyChallengeStateChangedMsg>(OnStateChanged);
        Message.AddListener<OnTrophyChallengeProgressUpdatedMsg>(OnProgressUpdated);
        Message.AddListener<OnTrophyChallengeFinalRewardClaimedMsg>(OnFinalRewardClaimed);
    }

    protected override void OnDestroy()
    {
        if (startPanel != null)
        {
            startPanel.OnCloseClicked -= OnStartInteracted;
        }
        if (mainPanel != null)
        {
            mainPanel.OnCloseClicked -= Close;
            mainPanel.OnInfoClicked -= OnInfoClicked;
        }
        if (clearPanel != null)
        {
            clearPanel.OnCloseClicked -= OnClearClosed;
        }

        Message.RemoveListener<OnTrophyChallengeInfoRefreshedMsg>(OnInfoRefreshed);
        Message.RemoveListener<OnTrophyChallengeStateChangedMsg>(OnStateChanged);
        Message.RemoveListener<OnTrophyChallengeProgressUpdatedMsg>(OnProgressUpdated);
        Message.RemoveListener<OnTrophyChallengeFinalRewardClaimedMsg>(OnFinalRewardClaimed);

        base.OnDestroy();
    }

    public override void SetInfo(IUIInfoData data)
    {
        base.SetInfo(data);

        if (!TryResolveActiveTrophy(out currentTrophyId, out currentResource))
        {
            // 활성 시즌 없음 — 진입 자체를 차단해야 하지만, 진입했으면 닫는다.
            Close();
            return;
        }

        // 명세서.md §6.2 / §10 — 진입 분기. 각 Show*Panel 이 자기 패널의 Setting() 을 호출하므로 여기선 분기만.
        //   ① 최종 보상 연출 미시청분 존재 → _Clear 강제 진입 (재접속 강제 연출, §10). 패널 미바인딩 시 ②③ 폴백.
        //   ② 시즌 첫 만남(미참여 + 시작 팝업 미열람) → _Start
        //   ③ 그 외 → _Main
        var manager = TrophyChallengeManager.Instance;
        if (manager.ShouldForceShowClearPopup(currentTrophyId) && ShowClearPanel())
            return;

        if (manager.ShouldShowStartPanel(currentTrophyId))
            ShowStartPanel();
        else
            ShowMainPanel();
    }

    private bool TryResolveActiveTrophy(out long trophyId, out TrophyChallengeResourceTableData resource)
    {
        trophyId = 0;
        resource = null;

        var manager = TrophyChallengeManager.Instance;
        foreach (var pair in manager.GetMasters())
        {
            // 미작업 명세서 G-2/G-3 — 활성 시즌뿐 아니라 만료됐지만 _Clear 강제 진입 대상 시즌도 진입 허용.
            // SetInfo 단계에서 ShouldForceShowClearPopup 판정 → _Clear 패널로 분기된다.
            if (!manager.IsActiveSeasonOrPendingClear(pair.Key)) continue;

            trophyId = pair.Key;
            var master = pair.Value;
            if (master?.group != null)
                TableManager.GetData<TrophyChallengeResourceTableData>(master.group.resourceIdx, out resource);

            return true;
        }
        return false;
    }

    private void OnInfoRefreshed(OnTrophyChallengeInfoRefreshedMsg _)
    {
        if (mainPanel != null) mainPanel.OnDataChanged();
    }

    private void OnStateChanged(OnTrophyChallengeStateChangedMsg msg)
    {
        if (msg == null || msg.TrophyId != currentTrophyId) return;
        if (mainPanel != null) mainPanel.OnDataChanged();
    }

    // 명세서.md §7.4.1 — 인게임 조건 충족으로 진행도 누적 시 슬라이더 실시간 갱신.
    private void OnProgressUpdated(OnTrophyChallengeProgressUpdatedMsg msg)
    {
        if (msg == null || msg.TrophyId != currentTrophyId) return;
        if (mainPanel != null) mainPanel.OnDataChanged();
    }

    // 명세서.md §7.4.2 — 최종 보상 수령 직후 클리어 패널 + LIdx 31410 ("트로피 챌린지 성공!") 연출.
    // ISSUE-02 (2026-05-26) — 최종 보상 수령 메시지 도착.
    // 단일 보상 키티 연출(UIPopupRewardResult) → _Clear 종료 연출 → 최종 보상 팝업 순으로 직렬화한다.
    // 메인 패널이 단일 보상 시퀀스를 마친 뒤 ShowClearPanel(withFinalRewardPopup: true) 을 호출하고,
    // 최종 보상 팝업은 연출이 끝난 시점(OnClearClosed) 에 노출된다.
    // 메인 패널이 미바인딩된 케이스(이론상 발생 X)만 종료 연출 즉시 노출로 폴백.
    private void OnFinalRewardClaimed(OnTrophyChallengeFinalRewardClaimedMsg msg)
    {
        if (msg == null || msg.TrophyId != currentTrophyId) return;

        if (mainPanel != null)
        {
            mainPanel.HandleFinalRewardClaim();
            return;
        }

        ShowClearPanel();
    }

    // 명세서.md §7.1 — 인포 버튼 클릭 시 인포 팝업 진입.
    // 첫 호출 시 어드레서블에서 로드 후 본 팝업 자식으로 인스턴스화 → SetActive(true) + OnSequenceOpenPopup.
    // 두 번째 이후는 캐싱된 인스턴스를 재사용한다.
    private void OnInfoClicked()
    {
        if (currentResource == null) return;

        if (infoPopupController != null)
        {
            ShowInfoPopup();
            return;
        }
        LoadAndShowInfoPopupAsync(gameObject.GetCancellationTokenOnDestroy()).Forget();
    }

    private async UniTaskVoid LoadAndShowInfoPopupAsync(CancellationToken ct)
    {
        var loaded = await this.InstantiateScopedAsync(INFO_POPUP_ADDRESS, transform, pooling: false, ct: ct);
        if (this == null || loaded == null) return;

        infoPopupGameObject = loaded;
        infoPopupController = loaded.GetComponent<UIInfoPopupController>();
        if (infoPopupController == null)
        {
            DLogger.Error($"[UIPopupTrophyChallenge] UIInfoPopupController 컴포넌트 미부착 — {INFO_POPUP_ADDRESS}");
            return;
        }

        infoPopupGameObject.SetActive(false);
        ShowInfoPopup();
    }

    private void ShowInfoPopup()
    {
        if (infoPopupGameObject == null) return;

        infoPopupGameObject.SetActive(true);
        infoPopupController.SetInfo();
        infoPopupController.OnSequenceOpenPopup();
    }

    // _Start 의 도전 / 닫기 버튼 핸들러 — 어느 쪽이든 본 시즌으로 마킹 후 _Main 으로 전환.
    // 명세서.md §6.2 (1): 닫기 버튼도 팝업을 종료하지 않고 _Main 으로 전환 (시즌별 1회 안내가 끝났으므로).
    private void OnStartInteracted()
    {
        TrophyChallengeManager.Instance.MarkAutoStartPopupShown(currentTrophyId);
        ShowMainPanel();
    }

    // 명세서.md §10 — _Clear 패널 닫기(= 종료 연출 3단계 완주). 유저가 명시적으로 닫은 시점에
    // 클리어 연출 시청 완료로 마킹한다. (연출 도중 이탈 시에는 마킹되지 않아 재접속/재진입 시
    // _Clear 로 강제 재노출된다.)
    // 최종 보상 수령 직후 진입한 종료 연출이면, ③ 캐릭터 연출이 닫히는 이 시점에 최종 보상 팝업을
    // 노출한다 (연출 → 닫히면서 보상팝업). 보상팝업 오픈을 먼저 걸어두고 본 팝업을 닫으므로,
    // 본 팝업의 닫힘 연출과 보상팝업 등장이 이어진다.
    private void OnClearClosed()
    {
        TrophyChallengeManager.Instance.MarkClearPopupShown(currentTrophyId);

        if (pendingFinalRewardPopup && mainPanel != null)
            mainPanel.OpenFinalRewardPopup();

        pendingFinalRewardPopup = false;
        Close();
    }

    // 명세서.md §6.2 (1) — 컨텐츠 시작 시 _Start 패널부터.
    // 활성화 직후 startPanel.Setting() 을 호출해 컨텍스트 주입(타이머 등) 을 보장.
    // 시작 팝업 노출 메타베이스 로그(`trophy_challenge_start`)는 PanelTrophyChallenge_Start.Start() 에서 송신.
    private void ShowStartPanel()
    {
        SetActiveSafe(startPanel, true);
        SetActiveSafe(mainPanel, false);
        SetActiveSafe(clearPanel, false);

        if (startPanel != null) startPanel.Setting(currentTrophyId, currentResource);
    }

    // 명세서.md §6.2 (2) — 도전 버튼 / 닫기 버튼 클릭 → _Main 진입.
    // 활성화 직후 mainPanel.Setting() 을 호출해 LoopScrollRect.RefillCells 가 정상 동작하도록 함
    // (비활성 상태에서 RefillCells 는 셀 인스턴스화에 실패).
    public void ShowMainPanel()
    {
        SetActiveSafe(startPanel, false);
        SetActiveSafe(mainPanel, true);
        SetActiveSafe(clearPanel, false);

        if (mainPanel != null) mainPanel.Setting(currentTrophyId, currentResource);
    }

    // 명세서.md §7.4.2 / §13.6 — 최종 보상 수령 직후 _Clear 패널 연출 ("트로피 챌린지 성공!").
    // clearPanel.Setting() 이 클리어 텍스트 세팅을 담당한다.
    // withFinalRewardPopup: 최종 보상 수령 흐름으로 진입한 경우 true — 연출이 끝나면 최종 보상 팝업을 노출한다.
    // 반환값: 패널을 실제로 노출했으면 true, 미바인딩 등으로 스킵했으면 false.
    public bool ShowClearPanel(bool withFinalRewardPopup = false)
    {
        if (clearPanel == null)
        {
            DLogger.Error("[TrophyChallenge] _Clear 패널 미바인딩 — 클리어 연출 스킵");
            return false;
        }

        pendingFinalRewardPopup = withFinalRewardPopup;

        SetActiveSafe(startPanel, false);
        SetActiveSafe(mainPanel, false);
        SetActiveSafe(clearPanel, true);

        clearPanel.Setting(currentTrophyId, currentResource);
        return true;
    }

    private static void SetActiveSafe(Component target, bool active)
    {
        if (target == null) return;
        var go = target.gameObject;
        if (go.activeSelf != active) go.SetActive(active);
    }
}

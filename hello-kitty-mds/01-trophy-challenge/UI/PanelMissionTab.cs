using System;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using GameContents._Helper;
using GameContents.BadgeCollection;

using GameCore.Utils;

using GameLogic;
using GameLogic.Define;
using GameLogic.GameManagement;
using GameLogic.Management;
using GameLogic.Network;
using GameLogic.TrophyChallenge;

using StatefulUI.Runtime.Core;
using StatefulUISupport.Scripts.Components;

using UnityEngine;
using UnityEngine.UI;

// 명세서.md §7.3 — 챌린지 미션 상세 정보 패널 (단일 탭).
// 상태/문구 전환은 MissionTabBase/PopupBG 의 StatefulComponent(StateRole·TextRole)로 처리한다.
//   ① 미선택      : NotifyProgressInfoOff + Default   / DefaultText   = 31402
//   ② 진행 가능    : InfoOn + Default                  / BtnConfirm    = 바로가기
//   ③ 완료·미수령  : InfoOn + Clear                    / BtnConfirm    = 31407(보상 받기)
//   ④ 보상 수령    : NotifyProgressInfoOff + Clear      / DefaultText   = 31453
//   ⑤ 잠금        : InfoLock                          / EventDescText = 31408
//   ⑥ 종료        : InfoLock                          / EventDescText = 31409
public class PanelMissionTab : MonoBehaviour
{
    // 명세서 §7.3 — TextRole 문구 LIdx.
    private const int LIDX_DEFAULT_DESC  = 31402; // ① 미선택 디폴트 설명
    private const int LIDX_REWARDED_DESC = 31453; // ④ 보상 수령 후 설명
    private const int LIDX_LOCKED        = 31408; // ⑤ 잠금 안내
    private const int LIDX_EXPIRED       = 31409; // ⑥ 종료 안내
    private const int LIDX_ACTION_CLAIM  = 31407; // ③ '보상 받기' 버튼
    private const int LIDX_ACTION_SHORTCUT = 10116; // ② '바로가기' 버튼
    // 명세서 §13.4 — 탭 활성 시 등장 연출 DOTween 시간(초).
    private const float APPEAR_DURATION  = 0.18f;
    // 기획서 §5-3 / 명세서 §7.10 — 바로가기 대상 컨텐츠 비활성(미오픈/종료) 시 안내 문구.
    // ⑥ 종료 안내(LIDX_EXPIRED)와 동일 LIdx(31409 "이벤트가 종료됐어요.")를 의도적으로 재사용(기획 확정 2026-05-22).
    private const int LIDX_SHORTCUT_UNAVAILABLE = 31409;

    [Header("[상태/문구 — MissionTabBase/PopupBG StatefulComponent]")]
    // 명세서 §7.3 — StateRole/TextRole 로 미션 탭 상태·문구를 전환. 텍스트(이름/설명/버튼) 는 본 stateful 이 담당.
    [SerializeField] private StatefulComponent stateful;
    public RectTransform MissionTabTutorialRect => stateful.transform as RectTransform;

    [Header("[게이지 / 보상 — 명세서 §7.3]")]
    [SerializeField] private Slider progressGauge;
    [SerializeField] private UITextEx progressText;
    // 880017421 §2-3 missionIcon(2026-05-22 추가) — 미션 진행도 게이지에 붙는 아이콘.
    // TrophyChallenge_Mission.missionIcon 어드레서블 키를 비동기 로드. PanelTrophyChallenge_Main.prefab
    // 의 progressGauge(Slider) 자식 MissionIcon 게임오브젝트의 Image 를 바인딩.
    [SerializeField] private Image missionIcon;
    // TODO[binding]: 보상 슬롯 (CommonRewardItem) — 최대 3 개. iconPath 기반 SetInfo 호출.
    [SerializeField] private CommonRewardItem[] rewardItems;

    [Header("[등장 연출 — 명세서 §13.4]")]
    // 활성 시 0 → 1 스케일 등장 연출. 미지정 시 transform 폴백.
    [SerializeField] private RectTransform appearTarget;

    [Header("[Clear 상태 파티클 — Bottom/effect]")]
    // 명세서 §13.2 — Default 상태(①②)에서는 비활성, Clear 상태(③④)에서는 활성 + Play.
    // Fx_TrophyChallengeReward(보상 수령 연출)는 트로피 셀 클릭 연출로 일원화되어 본 패널에서 제거됨
    // — PanelTrophyChallenge_Main.cellClickFx 참조.
    [SerializeField] private ParticleSystem auraFx;     // Fx_ScrollPanelTrophyChallenge_Frame_Outline_Aura

    private TrophyChallengeViewModel viewModel;
    private Tween appearTween;
    // RefreshMissionIcon 비동기 로드 중 다른 챌린지가 선택되면 콜백 결과를 폐기하기 위한 최신 요청 키.
    private string requestedMissionIconKey;
    // 명세서 §7.3 ②③ — 상태에 따라 역할(보상 받기 / 바로가기)이 갈리는 단일 확정 버튼. Awake 에서 캐싱.
    private UIButtonEx confirmBtn;
    // Setting() 단계(미션 탭 비활성)에서 들어온 요청을 활성화 이후 시점에 적용하기 위한 지연 플래그.
    private bool started;
    private bool hasRequest;
    // 명세서 §7.3 ①⑦⑧ — ① 미선택 디폴트 상태의 상세 텍스트 LIdx(mainPopupSub1/2/3). _Main 이 주입.
    private int defaultDescLIdx;

    // 명세서 §7.3 — 단일 확정 버튼(BtnConfirm) 참조를 stateful 에서 캐싱(바인딩 전용).
    private void Awake()
    {
        if (stateful != null && stateful.HasButton(ButtonRole.BtnConfirm))
            confirmBtn = stateful.GetButton(ButtonRole.BtnConfirm).Button as UIButtonEx;
    }

    // Start 시점이면 PopupBG StatefulComponent 의 Awake/OnEnable 이 끝나 내부 StateProcessor 가 준비된다.
    // Setting() 단계의 이른 호출(컴포넌트 비활성, _stateProcessor 미생성)은 본 Start 에서 일괄 반영한다.
    private void Start()
    {
        started = true;
        // 보상 수령 시점에 현재 탭을 ③ → ④ 로 갱신하기 위해 챌린지 완료 메시지를 구독.
        Message.AddListener<OnTrophyChallengeCompletedMsg>(OnRewardClaimed);
        // 명세서 §7.3 ②③ — BtnConfirm 클릭 분기(보상 받기 / 바로가기) 리스너 등록.
        if (confirmBtn != null) confirmBtn.onClick.AddListener(OnConfirmClicked);
        if (hasRequest) Refresh();
    }

    private void OnDisable()
    {
        appearTween?.Kill();
        appearTween = null;
    }

    private void OnDestroy()
    {
        Message.RemoveListener<OnTrophyChallengeCompletedMsg>(OnRewardClaimed);
        if (confirmBtn != null) confirmBtn.onClick.RemoveListener(OnConfirmClicked);
    }

    // 명세서 §7.3 ① — 트로피 미선택(디폴트) 상태. _Main 진입 직후 호출.
    public void ShowDefault()
    {
        viewModel = null;
        hasRequest = true;
        if (started) Refresh();
    }

    // 기획서 §4-2 / 명세서 §7.3 ①⑦⑧ — 디폴트 상세 텍스트 LIdx(mainPopupSub1/2/3)를 _Main 이 주입.
    // ① 미선택 상태면 즉시 반영, 챌린지 선택 중이면 캐싱만 — 다음 ShowDefault() 시 반영.
    public void SetDefaultDescLIdx(int lidx)
    {
        defaultDescLIdx = lidx;
        if (started && viewModel == null)
            SetStatefulText(TextRole.DefaultText, CurrentDefaultDescLIdx());
    }

    // 주입된 디폴트 LIdx — 미주입(0) 시 sub1(31402) 폴백.
    private int CurrentDefaultDescLIdx() => defaultDescLIdx > 0 ? defaultDescLIdx : LIDX_DEFAULT_DESC;

    // 명세서 §7.3 ②~⑥ — 클릭된 챌린지의 상세 정보를 본 탭에 적용.
    public void Apply(TrophyChallengeViewModel vm)
    {
        if (vm == null) return;
        viewModel = vm;
        hasRequest = true;
        if (started) Refresh();
    }

    // viewModel == null 이면 ① 미선택 디폴트, 그 외엔 ②~⑥ 클릭 챌린지 적용.
    private void Refresh()
    {
        if (viewModel == null)
        {
            ApplyState(StateRole.NotifyProgressInfoOff);
            ApplyState(StateRole.Default);
            SetStatefulText(TextRole.DefaultText, CurrentDefaultDescLIdx());
            ApplyFx(auraFx, false);
            return;
        }

        RefreshStateful();
        RefreshTitle();
        RefreshDescription();
        RefreshProgress();
        RefreshMissionIcon();
        RefreshRewardIcons();
        ApplyFx(auraFx, IsClearState(viewModel.State));
        PlayAppearAnim();
    }

    // 명세서 §7.3 — vm.State 별 StateRole 조합 + TextRole 문구를 PopupBG StatefulComponent 에 반영.
    private void RefreshStateful()
    {
        switch (viewModel.State)
        {
            case TrophyChallengeState.InProgress:           // ②
                ApplyState(StateRole.InfoOn);
                ApplyState(StateRole.Default);
                // 기획서 §4-3 ② — shortCutType==0 이면 '바로가기' 버튼 미노출.
                SetConfirmButtonActive(HasShortCut());
                // 기획서 §4-3 ② — '바로가기' 버튼 문구 (LIdx 10116).
                SetStatefulText(TextRole.BtnConfirm, LIDX_ACTION_SHORTCUT);
                break;

            case TrophyChallengeState.Completed:            // ③
                ApplyState(StateRole.InfoOn);
                ApplyState(StateRole.Clear);
                SetConfirmButtonActive(true);
                SetStatefulText(TextRole.BtnConfirm, LIDX_ACTION_CLAIM);
                break;

            case TrophyChallengeState.Rewarded:             // ④
                ApplyState(StateRole.NotifyProgressInfoOff);
                ApplyState(StateRole.Clear);
                SetStatefulText(TextRole.DefaultText, LIDX_REWARDED_DESC);
                break;

            case TrophyChallengeState.Locked:               // ⑤
                ApplyState(StateRole.InfoLock);
                SetLockText();
                break;

            case TrophyChallengeState.Expired:              // ⑥
                ApplyState(StateRole.InfoLock);
                SetStatefulText(TextRole.EventDescText, LIDX_EXPIRED);
                break;
        }
    }

    // 인스펙터에 미등록된 State 면 로그만 남기고 스킵.
    private void ApplyState(StateRole role)
    {
        if (stateful == null) return;

        var roleInt = (int)role;
        if (!stateful.HasState(roleInt))
        {
            DLogger.Error($"[TrophyChallenge] MissionTab PopupBG stateful 에 미등록 State: {role}");
            return;
        }
        stateful.SetState(roleInt);
    }

    // LIdx 텍스트를 stateful 에 반영.
    private void SetStatefulText(TextRole role, int lidx)
        => SetStatefulText(role, TableManager.GetText(lidx));

    // raw 문자열 버전 — 남은 시간이 합성된 문구 등 단일 LIdx 가 아닌 텍스트에 사용.
    // 인스펙터에 미등록된 TextRole 이면 로그만 남기고 스킵.
    private void SetStatefulText(TextRole role, string value)
    {
        if (stateful == null) return;

        if (!stateful.HasText(role))
        {
            DLogger.Error($"[TrophyChallenge] MissionTab PopupBG stateful 에 미등록 Text: {role}");
            return;
        }
        stateful.SetText(role, value);
    }

    // 명세서 §7.3 ⑤ — 잠금 안내(31408)에 미션 startDate 까지 남은 시간을 합쳐 노출.
    // 31408 에 "{0}" 플레이스홀더가 있으면 그 자리에, 없으면 문구 뒤에 남은 시간을 붙인다.
    // Refresh 시점 1회 계산 — 초 단위 라이브 갱신이 필요하면 UITimerSimple 바인딩 별도 검토.
    // 데이터 소스는 패킷 `TrophyMissionGroupPacketData.startDate` (운영툴 응답의 시즌 권위값).
    private void SetLockText()
    {
        var lockText = TableManager.GetText(LIDX_LOCKED);
        var row = viewModel.MissionGroupRow;
        if (row != null && row.startDate > 0)
        {
            var remain = DateTimeUtils.GetUnixTime(row.startDate) - DataManager.Instance.GetCurrentTime();
            if (remain > TimeSpan.Zero)
            {
                var remainText = remain.ToFormattedString();
                lockText = lockText.Contains("{0}")
                    ? string.Format(lockText, remainText)
                    : $"{lockText} {remainText}";
            }
        }
        SetStatefulText(TextRole.EventDescText, lockText);
    }

    // 명세서 §7.3 — 클릭된 챌린지 미션 이름(nameLIdx)을 ChallengeMissionNameText TextRole 로 반영.
    private void RefreshTitle()
    {
        var row = viewModel.MissionTableRow;
        if (row == null) return;

        SetStatefulText(TextRole.ChallengeMissionNameText, row.nameLIdx);
    }

    // 명세서 §7.3 — 클릭된 챌린지 클리어 조건 설명(subLIdx)을 DescText TextRole 로 반영.
    // subLIdx 문구는 {0}=conditionCount, {1}=conditionValue 로 string.Format 적용.
    private void RefreshDescription()
    {
        if (stateful == null) return;

        var row = viewModel.MissionTableRow;
        if (row == null || row.subLIdx <= 0) return;

        if (!stateful.HasText(TextRole.DescText))
        {
            DLogger.Error($"[TrophyChallenge] MissionTab PopupBG stateful 에 미등록 Text: {TextRole.DescText}");
            return;
        }

        var text = TableManager.GetText(row.subLIdx);
        var groupRow = viewModel.MissionGroupRow;
        if (groupRow != null && !string.IsNullOrEmpty(text))
            text = string.Format(text, groupRow.conditionCount, groupRow.conditionValue);

        stateful.SetText(TextRole.DescText, text);
    }

    // 명세서 §7.3 — 진행도 게이지. 완료/수령은 100% 유지, 그 외는 실제 진행도(클램프).
    private void RefreshProgress()
    {
        var required = Mathf.Max(1, viewModel.RequiredCount);
        var isCleared = viewModel.State == TrophyChallengeState.Completed
                     || viewModel.State == TrophyChallengeState.Rewarded;
        var current = isCleared ? required : Mathf.Clamp(viewModel.Progress, 0, required);

        if (progressGauge != null)
        {
            progressGauge.minValue = 0;
            progressGauge.maxValue = required;
            progressGauge.value = current;
        }
        if (progressText != null)
            progressText.SetText($"{current}/{required}");
    }

    // 880017421 §2-3 — 선택된 챌린지의 missionIcon(어드레서블 키)을 비동기 로드해 게이지 아이콘에 반영.
    // 키가 없으면 아이콘을 숨긴다. 로드 도중 다른 챌린지가 선택되면 requestedMissionIconKey 가드로 폐기.
    private void RefreshMissionIcon()
    {
        if (missionIcon == null) return;

        var key = viewModel.MissionTableRow?.missionIcon;
        requestedMissionIconKey = key;

        if (string.IsNullOrEmpty(key))
        {
            missionIcon.enabled = false;
            return;
        }

        // 로드 완료 전까지 숨겨 이전 챌린지 아이콘의 잔상을 방지.
        missionIcon.enabled = false;
        LoadMissionIconAsync(key, gameObject.GetCancellationTokenOnDestroy()).Forget();
    }

    private async UniTaskVoid LoadMissionIconAsync(string key, CancellationToken ct)
    {
        var sprite = await this.LoadScopedAsync<Sprite>(key, ct);
        if (this == null || sprite == null || ct.IsCancellationRequested) return;
        // 로드 도중 다른 챌린지가 선택됐으면 결과 폐기.
        if (requestedMissionIconKey != key) return;

        missionIcon.sprite = sprite;
        missionIcon.enabled = true;
    }

    // 명세서 §7.3 — 최대 3 개 클리어 보상.
    // 서버 패킷 TrophyMissionGroup.rewards[i] 는 Event_Reward.index — EventRewardTableData
    // (itemType / itemIdx / itemValue) 로 풀어 CommonRewardItem 에 표시한다.
    private void RefreshRewardIcons()
    {
        if (rewardItems == null) return;

        var rewardList = viewModel.MissionGroupRow?.rewards;
        var rewardLen = rewardList != null ? rewardList.Length : 0;
        var slotCount = rewardItems.Length;

        for (var i = 0; i < slotCount; ++i)
        {
            var item = rewardItems[i];
            if (item == null) continue;

            var rewardIndex = i < rewardLen ? rewardList[i] : 0;
            if (rewardIndex <= 0
                || !TableManager.GetData(rewardIndex, out EventRewardTableData rewardRow))
            {
                item.gameObject.SetActive(false);
                continue;
            }

            // 명세서 §7.2 / 기획서 870875235 §4-2 / §4-3 ② '보상 정보 영역'
            //   itemType 별 클릭 분기 — Block(7) 인포 팝업 / BadgePack(10) 뱃지 말풍선 /
            //   Facility(12) 장식 위치 이동(호스트 트로피 팝업 닫고 편집 화면 진입 — MoveToShortCut 동일 패턴).
            //   CommonRewardItem.OnClickShowInfo 가 4 타입을 모두 처리한다.
            item.SetInfo((ItemType)rewardRow.itemType, rewardRow.itemIdx, rewardRow.itemValue,
                clicked => CommonRewardItem.OnClickShowInfo(clicked, () => GetComponentInParent<UIBasePopup>()?.Close()));
        }
    }

    // 명세서.md §13.4 — _Main 메인 패널 인터랙션 연출 (탭 활성 시 등장).
    private void PlayAppearAnim()
    {
        var target = appearTarget != null ? appearTarget : transform as RectTransform;
        if (target == null) return;

        appearTween?.Kill();
        target.localScale = Vector3.zero;
        appearTween = target.DOScale(Vector3.one, APPEAR_DURATION).SetEase(Ease.OutBack);
    }

    // 명세서 §13.2 — AuraFx 는 Clear 상태(③④)에서 활성·재생.
    private static bool IsClearState(TrophyChallengeState state)
        => state == TrophyChallengeState.Completed || state == TrophyChallengeState.Rewarded;

    // 보상 수령(OnTrophyChallengeCompletedMsg = RQTrophyChallengeComplete 성공) 시 —
    // 보상 수령한 챌린지가 현재 탭에 표시 중이면 ③ → ④(보상 수령) 상태로 즉시 갱신한다.
    private void OnRewardClaimed(OnTrophyChallengeCompletedMsg msg)
    {
        if (msg == null) return;
        if (viewModel == null) return;
        var trophyId = msg.TrophyId;
        var challengeId = msg.ChallengeId;
        if (trophyId == viewModel.TrophyId && challengeId == viewModel.ChallengeId)
            ReapplyFromManager(trophyId, challengeId);
    }

    // 명세서 §7.3 ②③ / 기획서 §4-3 — BtnConfirm 은 상태에 따라 역할이 갈리는 단일 버튼.
    // 보상받기 조건(③ 완료·미수령) 충족 시 보상 수령, 그 외(② 진행 가능)는 바로가기로 분기한다.
    private void OnConfirmClicked()
    {
        if (viewModel == null) return;

        var canClaimReward = viewModel.State == TrophyChallengeState.Completed;
        if (canClaimReward)
            ClaimReward();
        else
            MoveToShortCut();
    }

    // 명세서 §3.4 / §7.4.1 — 완료·미수령(③) 챌린지의 보상 수령. RQTrophyChallengeComplete 송신.
    // 응답 대기 동안 버튼을 비활성해 중복 송신을 막는다. 성공 시 OnTrophyChallengeCompletedMsg 가
    // OnRewardClaimed 로 들어와 RewardFx 재생 + 탭을 ④(보상 수령) 상태로 갱신한다.
    private void ClaimReward()
    {
        var vm = viewModel;
        if (confirmBtn != null) confirmBtn.interactable = false;

        TrophyChallengeManager.Instance.NotifyChallengeComplete(vm.TrophyId, vm.ChallengeId, success =>
        {
            if (confirmBtn != null) confirmBtn.interactable = true;
            if (!success)
            {
                DLogger.Error($"[TrophyChallenge] 보상 수령 실패 — trophyId={vm.TrophyId} challengeId={vm.ChallengeId}");
                return;
            }

            // 명세서 §11.2 / Confluence 893518164 — 챌린지 개별 달성 보상 획득 메타베이스 로그.
            // 보상받기 버튼 클릭으로 RQTrophyChallengeComplete 성공 응답을 받은 직후 송신.
            // Label = 그룹 번호(gIdx) / Action = 챌린지 번호(conditionIdx) /
            // Value = 현재까지 보상을 받은(isComplete == true) 미션의 누적 개수 — 본 챌린지의
            // MarkCompleted 가 콜백 진입 전 적용되므로 CountCompletedChallenges 결과에 자동 포함된다.
            var missionRow = vm.MissionTableRow;
            if (missionRow != null)
            {
                AnalyticsManager.AnalyticsEventData logData = new();
                AnalyticsManager.CustomSendEvent(AnalyticsEventName.TROPHY_CHALLENGE_REWARD,
                    logData.AddLabel(missionRow.gIdx.ToString())
                           .AddAction(missionRow.conditionIdx.ToString())
                           .AddValue(TrophyChallengeManager.Instance.CountCompletedChallenges(vm.TrophyId)));
            }
        });
    }

    // 기획서 870875235 §4-3 ② / §7-1 '바로가기 연결' — 진행 가능(②) 챌린지의 '바로가기'.
    // shortCutType 별로 연계 컨텐츠/이벤트 화면을 연다.
    //
    // ISSUE-07 — 진입 성공 분기 모두 호스트 트로피 챌린지 팝업을 닫는다(`CloseHostPopup`).
    // 트로피 팝업이 남아 있으면 진입 대상 컨텐츠의 UI(예: 광장 이벤트 오브젝트 미리보기) 와 겹치고,
    // 머지 보드 입력은 `UIBasePopup.ShouldLockInput`(기본 true) 때문에
    // `ControllerUserInputBlock.IsInputLockedByPopup()` 으로 차단된다(ISSUE-08).
    // 진입 실패(`NotifyShortCutUnavailable`) 분기에서는 팝업을 유지해 토스트만 노출한다.
    private void MoveToShortCut()
    {
        var row = viewModel.MissionTableRow;
        if (row == null) return;

        var shortCutType = row.shortCutType;
        switch (shortCutType)
        {
            // 라이브 이벤트 — EventCommonHelper 로 진입.
            // 활성 검사는 EventDataHelper.IsActiveEvent 로 한다 — 데이터 존재 + 시작 시간 도달 +
            // 기간 내 + 미완료 + 미스킵 을 모두 검사하므로 "시즌은 살아있지만 현재 미진행" 상태를
            // 정확히 걸러낸다. (EventCommonHelper.GetEventHandleState 는 시즌 핸들 등록 여부만 판정 —
            // 시즌이 끝나도 핸들은 남아 있어 미진행 케이스를 통과시켰던 ISSUE-09 원인.)
            case TrophyChallengeShortCutType.LuckyRoulette:
            case TrophyChallengeShortCutType.Plaza:
            case TrophyChallengeShortCutType.AI1vs1Event:
            case TrophyChallengeShortCutType.DreamBalloon:   // ISSUE-06 — 드림 벌룬 페스티벌
            {
                var liveEventType = shortCutType switch
                {
                    TrophyChallengeShortCutType.LuckyRoulette => LiveEventType.LUCKYROULETTE,
                    TrophyChallengeShortCutType.Plaza         => LiveEventType.PLAZA,
                    TrophyChallengeShortCutType.AI1vs1Event   => LiveEventType.AI1VS1EVENT,
                    TrophyChallengeShortCutType.DreamBalloon  => LiveEventType.DREAMBALLOON,
                    _                                         => LiveEventType.None,
                };
                if (EventDataHelper.IsActiveEvent(liveEventType))
                {
                    // [시작 팝업 노출 제어] 아직 시작 팝업을 안 본 이벤트면 시작 팝업으로 진입한다(기획 991526937)
                    EventCommonHelper.OpenEventByUserEntry(liveEventType);
                    CloseHostPopup();
                }
                // 행운의 룰렛은 라이브 이벤트(LiveEventData) 가 아니라 서프라이즈 이벤트(SubContent) 계열이라
                // EventDataHelper.IsActiveEvent / EventCommonHelper.OpenEvent 경로로는 진입이 불가하다.
                // (LUCKYROULETTE 는 LiveEventData 미등록 → IsActiveEvent 항상 false → 룰렛이 켜져 있어도
                //  바로가기가 항상 종료 토스트만 띄우던 문제.) 서프라이즈 이벤트 활성 판정(SurpriseEventHelper) +
                // 전용 팝업 직접 오픈(UIWindowNewMerge.OnClickRoulette 와 동일 경로)으로 분기한다.
                else if (liveEventType == LiveEventType.LUCKYROULETTE
                         && SurpriseEventHelper.IsActiveEvent(liveEventType))
                {
                    UIManager.OpenUIMsgAsync<UIPopupEventLuckyRoulette>().Forget();
                    CloseHostPopup();
                }
                else
                {
                    NotifyShortCutUnavailable(shortCutType);
                }
                break;
            }

            // 주간 임무(시즌 임무) 팝업.
            // ISSUE-09 (2026-05-26) — 시즌 임무가 종료된 상태(활성 시즌 테마 없음)에서 진입하면
            // UIPopupSeasonMission 이 내부에서 닫혀버려 빈 화면 + 트로피 팝업도 닫힌 상태가 된다.
            // 활성 시즌 테마 존재 여부를 사전 가드해 미진행 시 토스트(LIdx 31409) 노출 + 트로피 팝업 유지.
            case TrophyChallengeShortCutType.WeeklyMission:
            {
                var activeSeasonThemeId = TableManager.Instance.GetActiveSeasonTheme(DataManager.Instance.GetCurrentTime());
                if (activeSeasonThemeId > 0 && TableManager.GetData(activeSeasonThemeId, out SeasonThemeSettingTableData _))
                {
                    UIManager.OpenUIMsgAsync<UIPopupSeasonMission>().Forget();
                    CloseHostPopup();
                }
                else
                {
                    NotifyShortCutUnavailable(shortCutType);
                }
                break;
            }

            // 뱃지 컬렉션 — HUD 뱃지 버튼(BadgeCollectionButton)과 동일 진입.
            case TrophyChallengeShortCutType.BadgeCollection:
                BadgeCollectionManager.Instance.ShowBadgeMainPopup();
                CloseHostPopup();
                break;

            // 쇼핑 로드.
            case TrophyChallengeShortCutType.ShoppingRoad:
                ShoppingRoadHelper.OpenShoppingRoad();
                CloseHostPopup();
                break;

            // 시즌 패스 — UIOrderSeasonPasses(시즌 패스 버튼)와 동일 진입.
            case TrophyChallengeShortCutType.VipPass:
            {
                FsWebManager.GetProcess<FsProcessShop>().DirectApi_GetPassActiveSeason(out var seasonPass);
                if (seasonPass != null)
                {
                    UIManager.OpenUIMsgAsync<UIPopupSeasonPasses>(new PopupSeasonPassInfo { dataSeasonPass = seasonPass }).Forget();
                    CloseHostPopup();
                }
                else
                {
                    NotifyShortCutUnavailable(shortCutType);
                }
                break;
            }

            // 주간 패스 — UIWindowLobbyMain.UpdateWeeklyPassButton(주간 패스 버튼)와 동일 진입.
            // ISSUE-10 — 아이콘은 VIP 패스와 병용하되 진입 목적지는 주간 패스로 분리. 주간 패스는 다중 활성
            // 가능(List)이므로 첫 활성 패스를 연다(시즌 패스의 단일 활성 진입과 동일한 단순 진입 규칙).
            case TrophyChallengeShortCutType.WeekPass:
            {
                FsWebManager.GetProcess<FsProcessShop>().DirectApi_GetPassActiveWeekly(out var activeWeeklyPassList);
                if (activeWeeklyPassList != null && activeWeeklyPassList.Count > 0)
                {
                    UIManager.OpenUIMsgAsync<UIPopupWeeklyPasses>(new PopupWeeklyPassInfo { dataWeeklyPass = activeWeeklyPassList[0] }).Forget();
                    CloseHostPopup();
                }
                else
                {
                    NotifyShortCutUnavailable(shortCutType);
                }
                break;
            }

            // 머지 이벤트 계열 (열쇠 찾기 / 포인트 획득 / 최종 블록 / 보스 레이드 / 구름 보물 찾기)
            //   — UIPopupMergeEvent 가 활성 머지 이벤트를 자체 조회해 표시한다.
            //   (도감/포인트는 머지 이벤트 팝업 내부 UI 라 단독 진입 불가 — 도감을 품은 머지 이벤트 팝업으로 진입.)
            //   활성 머지 이벤트가 없으면 UIPopupMergeEvent.SetInfo 가 NRE 를 내므로 사전 가드한다.
            //   활성 검사는 프로젝트 표준 패턴 (UIOrderMergeEvent / MergeEvent / UIPopupMail 동일):
            //   `mergeEventState != null && mergeEventState.isActive` — isActive 검사를 빼면 종료된
            //   머지 이벤트 상태가 객체로 남아있을 때 미진행에도 진입을 시도해 토스트가 안 뜬다(ISSUE-09).
            //   ISSUE-11 — 머지 이벤트는 시즌 단위로 1종만 활성화되므로, 트로피 챌린지의 shortCutType
            //   (예: GetPoint)과 활성 머지 이벤트의 종류(MergeEventEvType)가 다른 경우 잘못된 이벤트로
            //   진입한다. 활성 종류 ↔ shortCutType 일치를 추가 가드한다.
            case TrophyChallengeShortCutType.FindKey:
            case TrophyChallengeShortCutType.GetPoint:
            case TrophyChallengeShortCutType.FinalBlock:
            case TrophyChallengeShortCutType.BossRaid:
            case TrophyChallengeShortCutType.MergeFestival:
            case TrophyChallengeShortCutType.FourDropItem:
            {
                FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_GetActiveMergeEvent(out var mergeEventState, false);
                var isActiveMatchingMergeEvent = mergeEventState != null
                                                 && mergeEventState.isActive
                                                 && MergeEventHelper.GetMergeEventType(mergeEventState.id) == ResolveExpectedMergeEventType(shortCutType);
                if (isActiveMatchingMergeEvent)
                {
                    UIManager.OpenUIMsgAsync<UIPopupMergeEvent>().Forget();
                    CloseHostPopup();
                }
                else
                {
                    NotifyShortCutUnavailable(shortCutType);
                }
                break;
            }

            // 인게임/쇼핑 질주 — 트로피 팝업을 닫고 메인 머지판으로 전환.
            case TrophyChallengeShortCutType.InGame:
            case TrophyChallengeShortCutType.ShoppingRush:
                CloseHostPopup();
                Message.Send(new OnChangeLobbyUITypeMsg(LobbyUIType.Merge));
                break;

            case TrophyChallengeShortCutType.None:
            default:
                break;
        }
    }

    // ISSUE-07 — 바로가기 진입 성공 시 호스트 트로피 챌린지 팝업을 닫는다.
    // 트로피 팝업이 남아 있으면 진입 컨텐츠 UI(예: 광장 이벤트 오브젝트 미리보기) 와 겹치고,
    // 머지 보드 입력은 UIBasePopup.ShouldLockInput 으로 차단된다(ISSUE-08).
    private void CloseHostPopup()
    {
        GetComponentInParent<UIBasePopup>()?.Close();
    }

    // 바로가기 대상 컨텐츠가 비활성(미오픈/종료) 일 때 공통 처리 (기획서 §5-3 / 명세서 §7.10).
    // 머지 이벤트·라이브 이벤트·시즌 패스 등 진입 실패 시점이 모두 본질적으로 같은 상황이므로
    // 동일한 형식의 에러 로그 + 사용자 안내 토스트를 한 곳에서 출력한다.
    private static void NotifyShortCutUnavailable(TrophyChallengeShortCutType shortCutType)
    {
        DLogger.Error($"[TrophyChallenge] 바로가기 — {shortCutType}: 대상 비활성, 진입 스킵");
        ToastHelper.ShowToastPopup(TableManager.GetText(LIDX_SHORTCUT_UNAVAILABLE));
    }

    // ISSUE-11 — TrophyChallengeShortCutType ↔ MergeEventEvType 매핑.
    // 머지 이벤트는 시즌 단위로 1종만 활성화되므로, 현재 활성 머지 이벤트의 종류가 트로피 챌린지가
    // 가리키는 종류와 다르면 진입을 차단한다(다른 머지 이벤트 노출 방지).
    private static MergeEventEvType ResolveExpectedMergeEventType(TrophyChallengeShortCutType shortCutType)
    {
        return shortCutType switch
        {
            TrophyChallengeShortCutType.FindKey       => MergeEventEvType.FindKey,
            TrophyChallengeShortCutType.GetPoint      => MergeEventEvType.GetPoint,
            TrophyChallengeShortCutType.FinalBlock    => MergeEventEvType.FinalBlockMerge,
            TrophyChallengeShortCutType.BossRaid      => MergeEventEvType.BossRaid,
            TrophyChallengeShortCutType.MergeFestival => MergeEventEvType.FinalBlockMerge,
            TrophyChallengeShortCutType.FourDropItem  => MergeEventEvType.FourDropItem,
            _                                         => MergeEventEvType.None,
        };
    }

    // 매니저에서 최신 뷰모델을 다시 빌드해 현재 챌린지 탭을 갱신 (보상 수령 직후 ③ → ④ 반영).
    private void ReapplyFromManager(long trophyId, int challengeId)
    {
        var models = TrophyChallengeManager.Instance.BuildViewModels(trophyId);
        var count = models.Count;
        for (var i = 0; i < count; ++i)
        {
            var model = models[i];
            if (model != null && model.ChallengeId == challengeId)
            {
                Apply(model);
                return;
            }
        }
    }

    // 기획서 §4-3 ② — shortCutType != 0 일 때만 '바로가기' 버튼이 노출된다.
    private bool HasShortCut()
    {
        var row = viewModel?.MissionTableRow;
        return row != null && row.shortCutType != TrophyChallengeShortCutType.None;
    }

    // BtnConfirm 버튼 GameObject 노출 토글. ② 진행 가능은 바로가기 유무에 따라, ③ 완료·미수령은 항상 노출.
    private void SetConfirmButtonActive(bool active)
    {
        if (confirmBtn == null) return;

        var go = confirmBtn.gameObject;
        if (go.activeSelf != active) go.SetActive(active);
    }

    // on=true: GameObject 활성(OnEnable) 후 Play. on=false: GameObject 비활성.
    private void ApplyFx(ParticleSystem fx, bool on)
    {
        if (fx == null) return;

        var go = fx.gameObject;
        if (on)
        {
            if (!go.activeSelf) go.SetActive(true);
            fx.Play();
        }
        else if (go.activeSelf)
        {
            go.SetActive(false);
        }
    }
}

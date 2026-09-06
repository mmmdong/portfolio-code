using System;
using System.Collections.Generic;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Management;
using GameLogic.TrophyChallenge;

using UnityEngine;
using UnityEngine.UI;

// 명세서.md §6.2 / 기획서.md §4-2 / §7.2 — 트로피 챌린지 메인 패널.
// 챌린지 리스트(LoopScroll), 진행도 게이지, 잔여 시간, 인포 버튼 등 _Main 영역의 UI 책임을 가진다.
// 닫기 버튼은 베이스의 closeBtn / OnCloseClicked 를 그대로 사용 — 부모가 UIBasePopup.Close 호출로 처리.
// 데이터 갱신 트리거(Info/State/Progress 메시지) 는 부모(UIPopupTrophyChallenge) 가 받아서
// OnDataChanged() 로 위임 호출한다.
public class PanelTrophyChallenge_Main : PanelTrophyChallenge, LoopScrollPrefabSource, LoopScrollDataSource
{
    // 명세서 §7.9 #6 — 챌린지 선택 조명 효과 DOTween 시간(초).
    private const float HIGHLIGHT_DURATION = 0.1f;
    // 보상받기 시 완료 스티커(CompletSticker) 연출 대기 시간(초, 고정).
    private const float CLAIM_STICKER_DURATION = 0.8f;
    // 도장 파티클(CompletSticker) 노출 후 슬롯 바운스 애니메이션을 재생하기까지의 대기 시간(초).
    // 도장 파티클이 충분히 보이도록 짧게 지연시킨다.
    private const float CLAIM_BUTTON_ANIM_DELAY = 0.25f;
    // ISSUE-12 (2026-05-26) — 최종 보상 진행도 게이지(UIPopupTrophyChallengeRewardBox01 의 Slider)
    // 1포인트 증가 시 트윈 길이(초). 개별 미션 클리어 카운트가 누적될 때마다 부드럽게 증가하도록 사용.
    private const float PROGRESS_GAUGE_DURATION = 0.4f;
    // 명세서.md §7.9 #1 — 챌린지 보상 획득 사운드(Confluence 870875235 §9, 2026-05-22).
    private const int SOUND_ID_REWARD_CLAIM = 1132;

    [SerializeField] private UIPopupTrophyChallengeLoopScroll loopScroll;
    // 기획서.md §4-2 / 명세서.md §6.2 — 메인 팝업 잔여 시간 영역. `TrophyInfo.endAt` 기준 카운트다운.
    [SerializeField] private UITimerSimple mainTimer;
    [SerializeField] private Slider progressGauge;
    [SerializeField] private UITextEx progressText;
    // 명세서.md §7.1 / §7.2 — 메인 팝업 인포 버튼. 클릭 시 부모(UIPopupTrophyChallenge) 가 OnInfoClicked
    // 이벤트를 받아 인포 팝업(UIInfoPopupController, 어드레서블 키 UIPopupTrophyChallenge_Info) 진입을 처리한다.
    [SerializeField] private UIButtonEx infoBtn;

    [Header("[미션 정보 영역 — 명세서 §7.3]")]
    // 단일 미션 정보 탭 — 클릭한 챌린지의 전 상태(②진행/③완료·미수령/④수령/⑤잠금/⑥종료) 를 처리.
    // 상태/문구 전환은 PanelMissionTab 이 MissionTabBase/PopupBG StatefulComponent 로 담당.
    [SerializeField] private PanelMissionTab missionTab;

    [Header("[최종 보상 — 명세서 §7.4.2 / UIPopupTrophyChallengeRewardBox01]")]
    // UIPopupTrophyChallengeRewardBox01.RewardBG 에 부착된 LoopHorizontalScrollRect.
    [SerializeField] private LoopScrollRect finalRewardScroll;
    // 가로 스크롤 셀로 사용할 CommonRewardItem 프리팹.
    [SerializeField] private GameObject finalRewardItemPrefab;
    // 명세서 §7.4.2 — 최종 보상 수령 완료 표시(UIPopupTrophyChallengeRewardBox01 하위 CheckBoxBG).
    // 최종 보상 수령 시 활성, 수령 전에는 비활성.
    [SerializeField] private GameObject finalRewardCheckBox;

    [Header("[셀 클릭 연출 — Fx_TrophyChallengeReward]")]
    // 트로피 셀(ScrollPanelTrophyChallenge.prefab 의 PanelTrophyIconBaseSlot) 클릭 시마다
    // 제 위치(프리팹에 배치된 자리)에서 재생하는 Fx_TrophyChallengeReward 이펙트. 인스펙터에서 바인딩.
    // playOnAwake 가 켜진 프리팹이므로 인스펙터에서 GameObject 는 비활성 상태로 두는 것을 권장.
    [SerializeField] private ParticleSystem cellClickFx;

    private readonly Stack<Transform> finalRewardPool = new();
    private int[] finalRewardItems;

    private Tween highlightTween;
    private int lastHighlightedRow = -1;
    // 명세서 §7.3 — 직전에 클릭된 슬롯. 새 클릭 시 TrophyClickClose 로 닫고 현재 슬롯을 TrophyClickOpen 으로 연다.
    private PanelTrophyIconBaseSlot lastClickedSlot;
    // 보상받기 연출(완료 스티커 → 보상팝업 → 재정렬) 진행 중 플래그. true 인 동안 OnDataChanged 의
    // 자동 RefreshList(재정렬)를 보류하고, 보상팝업이 닫힌 뒤 재정렬한다.
    private bool claimSequenceActive;
    // ISSUE-02 (2026-05-26) — 최종 보상 수령 메시지가 도착해 단일 보상 팝업 종료 후 종료 연출(_Clear)로
    // 넘어가야 하는 대기 상태. 단일 보상 팝업이 닫힐 때(OnClaimRewardPopupClosed) 본 플래그가 true 면
    // 종료 연출 → 최종 보상 팝업 순으로 직렬화한다.
    private bool pendingFinalRewardClaim;
    // ISSUE-12 (2026-05-26) — 최종 보상 진행도 게이지 DOValue 트윈 핸들. 개별 미션 클리어 카운트가
    // 변경될 때마다 이전 트윈을 Kill 하고 새 목표값으로 다시 트윈해 연속 증가를 자연스럽게 체이닝.
    private Tween progressGaugeTween;

    // 부모(UIPopupTrophyChallenge) 가 구독해 인포 팝업 진입을 처리.
    public event Action OnInfoClicked;

    protected override void Start()
    {
        base.Start();

        if (infoBtn != null) infoBtn.onClick.AddListener(OnInfoBtnClicked);

        // 명세서.md §7.3 — 챌린지 아이콘 클릭 시 슬롯이 발행하는 메시지를 받아 미션 정보 탭을 세팅.
        Message.AddListener<OnTrophyChallengeMissionSelectedMsg>(OnMissionSelected);
        // 명세서.md §7.4.1 — 보상받기 성공 메시지를 받아 완료 스티커 → 보상팝업 → 재정렬 연출 시퀀스를 실행.
        Message.AddListener<OnTrophyChallengeCompletedMsg>(OnChallengeRewardClaimed);

        // 명세서.md §7.8 / Confluence 880017421 §2-6 — 트로피 챌린지 최초 튜토리얼 시작 트리거
        // (4단계 시퀀스: 챌린지 아이콘 → 상세 정보 → 최종 보상 → 인포 버튼). _Main 패널이 노출되는
        // 시점이 튜토리얼 1단계 "챌린지 아이콘 포커싱" 의 진입점이므로 Start() 에서 트리거 호출.
        TrophyChallengeHelper.OnStartTutorial();
    }

    protected override void OnDestroy()
    {
        if (loopScroll != null)
            loopScroll.OnItemSelected = null;

        if (infoBtn != null) infoBtn.onClick.RemoveListener(OnInfoBtnClicked);

        Message.RemoveListener<OnTrophyChallengeMissionSelectedMsg>(OnMissionSelected);
        Message.RemoveListener<OnTrophyChallengeCompletedMsg>(OnChallengeRewardClaimed);

        highlightTween?.Kill();
        highlightTween = null;

        // ISSUE-12 — 진행 중 게이지 트윈도 함께 정리(패널 파괴 시 잔존 트윈 방지).
        progressGaugeTween?.Kill();
        progressGaugeTween = null;

        OnInfoClicked = null;
        base.OnDestroy();
    }

    public override void Setting(long trophyId, TrophyChallengeResourceTableData resource)
    {
        base.Setting(trophyId, resource);

        RefreshMainTimer();
        RefreshDefaultDesc();
        RefreshList();
        // ISSUE-12 — 진입 시 게이지는 현재 클리어 카운트를 즉시 표시(0 → 현재값 인트로 애니메이션 회피).
        // 클리어 발생 시점의 +1 증가는 OnDataChanged 경로에서 애니메이션됨.
        RefreshProgressGauge(animate: false);
        RefreshFinalRewards();

        // 진입 직후엔 선택된 챌린지가 없음 — 미션 탭을 디폴트(미선택) 상태로.
        if (missionTab != null) missionTab.ShowDefault();
        lastHighlightedRow = -1;

        // 이전 시즌 진입 시 남아있던 클릭 이펙트 슬롯 참조를 초기화. 풀링된 슬롯이라도 RefillCells 가 상태를 다시 세팅.
        if (lastClickedSlot != null)
        {
            lastClickedSlot.SetClickEffect(false);
            lastClickedSlot = null;
        }
    }

    // 부모가 Info/State/Progress 메시지를 수신했을 때 호출. _Main 의 리스트/게이지/디폴트 설명을 일괄 갱신.
    // 보상받기 연출 진행 중(claimSequenceActive)에는 트로피 재정렬(RefreshList)을 보류한다 —
    // 재정렬은 보상팝업이 닫힐 때 OnClaimRewardPopupClosed 에서 수행(명세서 §7.2 / 사용자 요구 흐름).
    public void OnDataChanged()
    {
        if (!claimSequenceActive) RefreshList();
        RefreshProgressGauge();
        RefreshDefaultDesc();
        RefreshFinalRewards();
    }

    // 명세서.md §7.3 ⑦⑧ — 최종 보상 수령 후 디폴트 영역 텍스트가 sub1 → sub2 → sub3 로 분기.
    public int ResolveDefaultDescLIdx()
    {
        if (currentResource == null) return 0;

        var manager = TrophyChallengeManager.Instance;
        if (!manager.IsFinalRewardClaimed(currentTrophyId))
            return currentResource.mainPopupSub1LIdx;

        var completed = manager.CountCompletedChallenges(currentTrophyId);
        var master = manager.GetMaster(currentTrophyId);
        var totalActive = master?.missionGroup != null ? master.missionGroup.Length : 0;
        return completed < totalActive
            ? currentResource.mainPopupSub2LIdx
            : currentResource.mainPopupSub3LIdx;
    }

    private void RefreshList()
    {
        if (loopScroll == null) return;
        loopScroll.SetData(TrophyChallengeManager.Instance.BuildViewModels(currentTrophyId));
    }

    // 기획서.md §4-2 / 명세서.md §7.3 ①⑦⑧ — 챌린지 상세 영역 디폴트 텍스트(mainPopupSub1/sub2/sub3 LIdx).
    // 미션 탭의 ① 미선택 상태 DefaultText 에 주입 — ① 상태면 즉시 반영, 챌린지 선택 중이면 캐싱.
    private void RefreshDefaultDesc()
    {
        if (missionTab != null) missionTab.SetDefaultDescLIdx(ResolveDefaultDescLIdx());
    }

    // 명세서.md §6.2 — 메인 팝업의 잔여 시간 영역. `TrophyInfo.endAt` 기준 카운트다운.
    private void RefreshMainTimer()
    {
        if (mainTimer == null) return;

        var info = TrophyChallengeManager.Instance.GetUserInfo(currentTrophyId);
        if (info == null || info.endAt <= 0) return;

        // 명세서.md §4-2 — 시간 만료 시 창 종료 후 종료 연출 진행 (콜백에서 OnSeasonTimerExpired).
        mainTimer.StartTimer(DateTimeUtils.GetUnixTime(info.endAt), UITimerSimple.ECompleteType.Inactive,
            callbackAction: OnSeasonTimerExpired);
    }

    // 명세서.md §7.4.2 — TrophyMaster.group.completeReward 를 RewardBox01 가로 스크롤에 채움.
    // master / group / completeReward 중 어느 것이 null 이어도 빈 배열로 폴백 → totalCount = 0.
    private void RefreshFinalRewards()
    {
        var manager = TrophyChallengeManager.Instance;

        // 명세서 §7.4.2 — 최종 보상 수령 시 CheckBoxBG(수령 완료 표시) 노출, 수령 전에는 숨김.
        if (finalRewardCheckBox != null)
            finalRewardCheckBox.SetActive(manager.IsFinalRewardClaimed(currentTrophyId));

        if (finalRewardScroll == null) return;

        var master = manager.GetMaster(currentTrophyId);
        finalRewardItems = master?.group?.completeReward;

        finalRewardScroll.prefabSource = this;
        finalRewardScroll.dataSource = this;
        finalRewardScroll.totalCount = finalRewardItems != null ? finalRewardItems.Length : 0;
        finalRewardScroll.RefillCells();
    }

    // LoopScrollPrefabSource — CommonRewardItem 셀 풀링.
    GameObject LoopScrollPrefabSource.GetObject(int index)
    {
        if (finalRewardPool.Count == 0)
            return Instantiate(finalRewardItemPrefab);

        var candidate = finalRewardPool.Pop();
        candidate.gameObject.SetActive(true);
        return candidate.gameObject;
    }

    void LoopScrollPrefabSource.ReturnObject(Transform trans)
    {
        trans.gameObject.SetActive(false);
        trans.SetParent(finalRewardScroll != null ? finalRewardScroll.transform : transform, false);
        finalRewardPool.Push(trans);
    }

    // LoopScrollDataSource — 셀에 보상 아이콘/수량 주입.
    // completeReward[index] 는 Event_Reward.index — EventRewardTableData(itemType/itemIdx/itemValue)로 푼다.
    void LoopScrollDataSource.ProvideData(Transform trans, int index)
    {
        if (finalRewardItems == null || index < 0 || index >= finalRewardItems.Length) return;

        var item = trans.GetComponent<CommonRewardItem>();
        if (item == null) return;

        if (!TableManager.GetData(finalRewardItems[index], out EventRewardTableData rewardRow))
        {
            item.gameObject.SetActive(false);
            return;
        }
        // 명세서 §7.2 최종 보상 아이콘 터치 타입 — Block(7) 클릭 시 인포 팝업, Facility(12) 클릭 시
        // 팝업을 닫고(InvokeCloseClicked) WorldManager.EnterFacilityEditAtFacilityId 로 장식 편집
        // 화면으로 이동(§14.2). CommonRewardItem.OnClickShowInfo 가 두 타입을 모두 처리한다.
        // SetInfoAutoHideInfo 는 Block / Facility / BlockLimitedTerm 일 때만 클릭 버튼을 노출한다 —
        // 공용 CommonRewardItem 프리팹의 baseButton.InfoDisplayType 가 비어 있어도 타입 기준으로
        // 버튼을 토글하므로, Facility 외 일반(머지 블록) 보상도 클릭 동작이 살아난다.
        var rewardItemType = (ItemType)rewardRow.itemType;
        item.SetInfoAutoHideInfo(rewardItemType, rewardRow.itemIdx, rewardRow.itemValue,
            clicked => CommonRewardItem.OnClickShowInfo(clicked, InvokeCloseClicked));
    }

    // 명세서.md §7.2 진행도 게이지 — 시즌 전체 트로피 누적 (완료 챌린지 수 / TrophyInfo.clearCount).
    // ISSUE-12 (2026-05-26) — 개별 미션 클리어 카운트에 맞춰 게이지가 부드럽게 움직이도록 DOValue 트윈 적용.
    // animate=false 면 즉시값 세팅(Setting 진입 시 초기 상태 노출용). animate=true(기본) 면 이전 트윈을
    // Kill 하고 목표값으로 다시 트윈 — 연속 +1 증가가 자연스럽게 체이닝된다.
    private void RefreshProgressGauge(bool animate = true)
    {
        var manager = TrophyChallengeManager.Instance;
        var completed = manager.CountCompletedChallenges(currentTrophyId);

        var info = manager.GetUserInfo(currentTrophyId);
        var total = info != null && info.clearCount > 0 ? info.clearCount : 0;

        if (progressGauge != null)
        {
            progressGauge.minValue = 0;
            progressGauge.maxValue = total > 0 ? total : 1;
            var targetValue = total > 0 ? Mathf.Min(completed, total) : 0;

            progressGaugeTween?.Kill();
            progressGaugeTween = null;
            if (animate && !Mathf.Approximately(progressGauge.value, targetValue))
            {
                progressGaugeTween = progressGauge.DOValue(targetValue, PROGRESS_GAUGE_DURATION).SetEase(Ease.OutQuad);
            }
            else
            {
                progressGauge.value = targetValue;
            }
        }
        if (progressText != null)
            progressText.SetText($"{completed}/{total}");
    }

    private void OnInfoBtnClicked() => OnInfoClicked?.Invoke();

    // 명세서.md §7.3 — 챌린지 아이콘 클릭 시 슬롯이 발행. 클릭된 챌린지 상태에 따라 Bottom stateful 상태 전환.
    //   ② InProgress → TrophyMissionPlaying
    //   ③ Completed   → ChallengeClear
    //   ④ Rewarded    → ChallengeAllClear
    //   ⑤ Locked      → TrophyLock
    //   ⑥ Expired     → TrophyMissionClose
    // 활성 시즌(currentTrophyId) 외의 슬롯에서 들어온 메시지는 무시.
    // 데이터 바인딩(Apply) 은 stateful 상태와 무관하게 각 탭에 채워 둠 — 디자이너가 상태별 활성 GameObject 매핑을 변경해도 텍스트는 최신.
    private void OnMissionSelected(OnTrophyChallengeMissionSelectedMsg msg)
    {
        var vm = msg?.ViewModel;
        if (vm == null || vm.TrophyId != currentTrophyId) return;

        // 단일 미션 정보 탭 — 전 상태(②~⑥)를 PanelMissionTab 이 PopupBG StatefulComponent 로 처리.
        if (missionTab != null) missionTab.Apply(vm);

        // 명세서 §7.3 — 클릭된 슬롯의 TrophyIconClickeffect 만 활성. 이전 슬롯은 TrophyClickClose 로 닫는다.
        var clickedSlot = msg.Slot as PanelTrophyIconBaseSlot;
        if (clickedSlot != lastClickedSlot)
        {
            if (lastClickedSlot != null) lastClickedSlot.SetClickEffect(false);
            lastClickedSlot = clickedSlot;
        }
        if (clickedSlot != null) clickedSlot.SetClickEffect(true);

        // 트로피 셀 클릭 시마다 Fx_TrophyChallengeReward 를 제 위치에서 재생.
        PlayCellClickFx();

        PlaySelectionHighlight(vm);
    }

    // 트로피 셀 클릭 시마다 호출 — Fx_TrophyChallengeReward 를 제 위치(프리팹에 배치된 자리)에서
    // 처음부터 재생한다. 이미 재생 중이어도 Stop+Clear 후 Play 로 깔끔하게 다시 시작한다(연타 대응).
    private void PlayCellClickFx()
    {
        if (cellClickFx == null) return;

        var fxObject = cellClickFx.gameObject;
        if (!fxObject.activeSelf) fxObject.SetActive(true);

        cellClickFx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        cellClickFx.Play(true);
    }

    // 명세서 §7.9 #6 — 챌린지 선택 조명 효과.
    //   같은 라인(행) → 0.1s 조명 이동 (DOMove)
    //   다른 라인 / 첫 클릭 → 0.1s 조명 등장 (Scale 0 → 1)
    // 클릭된 슬롯 RectTransform 은 viewModel 의 MissionIndex 행 인덱스(CELLS_PER_ROW 기준)로 비교.
    private void PlaySelectionHighlight(TrophyChallengeViewModel vm)
    {
        var rowIndex = vm.MissionIndex / TrophyChallengeRowViewModel.CELLS_PER_ROW;

        highlightTween?.Kill();
        lastHighlightedRow = rowIndex;
    }

    // 명세서.md §7.4.1 — 보상받기(RQTrophyChallengeComplete 성공) 연출 진입점.
    // NotifyChallengeComplete 가 OnTrophyChallengeCompletedMsg 를 OnTrophyChallengeStateChangedMsg 보다
    // 먼저 발행하므로, 여기서 claimSequenceActive 를 세우면 직후 도착하는 StateChanged 의 자동 재정렬
    // (OnDataChanged → RefreshList)이 보류된다 — 재정렬은 보상팝업이 닫힐 때 수행.
    private void OnChallengeRewardClaimed(OnTrophyChallengeCompletedMsg msg)
    {
        if (msg == null || msg.TrophyId != currentTrophyId) return;
        if (claimSequenceActive) return;

        claimSequenceActive = true;
        PlayClaimSequenceAsync(msg.ChallengeId).Forget();
    }

    // 보상받기 연출: 완료 스티커 재생(고정 CLAIM_STICKER_DURATION) → UIPopupRewardResult 보상팝업
    // → 보상팝업이 닫히면 트로피 리스트 재정렬.
    // 보상받기 연출: 완료 스티커 재생(고정 CLAIM_STICKER_DURATION) → UIPopupRewardResult 보상팝업
    // → 보상팝업이 닫히면 트로피 리스트 재정렬.
    private async UniTaskVoid PlayClaimSequenceAsync(int challengeId)
    {
        // 명세서.md §7.9 #1 — 챌린지 보상 받기 직후 사운드 재생(Confluence 870875235 §9).
        SoundManager.Instance.PlaySound(SOUND_ID_REWARD_CLAIM);

        var token = gameObject.GetCancellationTokenOnDestroy();

        // 1. 보상 파티클 — 클릭한 챌린지 슬롯의 완료 스티커(CompletSticker)를 그 자리에서 재생.
        if (lastClickedSlot != null) lastClickedSlot.PlayCompleteSticker();

        // 2. 도장 파티클이 충분히 보이도록 짧게 대기 후 슬롯 바운스 애니메이션 재생 — 보상받기 클릭 피드백.
        await UniTask.Delay(TimeSpan.FromSeconds(CLAIM_BUTTON_ANIM_DELAY), cancellationToken: token);
        if (lastClickedSlot != null) lastClickedSlot.ButtonClickAnimation();

        // 3. 잔여 대기(전체 CLAIM_STICKER_DURATION 까지) — 도장 + 바운스가 끝난 뒤 보상팝업 호출.
        var remaining = CLAIM_STICKER_DURATION - CLAIM_BUTTON_ANIM_DELAY;
        if (remaining > 0f)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(remaining), cancellationToken: token);
        }

        // 4. 명세서.md §7.4.1 — 수령한 챌린지 보상으로 UIPopupRewardResult 보상팝업 호출.
        // 트로피 재정렬은 팝업이 닫힌 시점(OnClaimRewardPopupClosed)에 수행한다.
        var models = TrophyChallengeManager.Instance.BuildViewModels(currentTrophyId);
        RewardHelper.OpenCommonRewardPopup(BuildClaimRewardInfo(models, challengeId), OnClaimRewardPopupClosed);
    }

    // 명세서.md §7.2 — 보상팝업이 닫힌 시점에 트로피 리스트를 재정렬(사용자 요구 흐름 — 팝업 종료 후 정렬).
    // 보상이 비어 팝업이 열리지 않은 경우엔 RewardHelper 가 콜백을 즉시 호출하므로 곧바로 재정렬된다.
    // ISSUE-02 (2026-05-26) — 단일 보상 수령으로 최종 보상까지 동시 발동된 케이스(pendingFinalRewardClaim)
    // 에서는 단일 보상 팝업이 닫힌 직후 종료 연출(_Clear)로 이어진다. 그 경우 재정렬은 EnterClearSequence
    // 에서 일괄 처리.
    private void OnClaimRewardPopupClosed()
    {
        if (pendingFinalRewardClaim)
        {
            EnterClearSequenceAfterCloseAsync().Forget();
            return;
        }

        claimSequenceActive = false;
        RefreshList();
    }

    // 수령한 챌린지의 보상(TrophyMissionGroup.rewards = Event_Reward 인덱스)을 보상팝업용 데이터로 변환.
    private RewardInfoData BuildClaimRewardInfo(List<TrophyChallengeViewModel> models, int challengeId)
    {
        var data = new RewardInfoData();

        TrophyChallengeViewModel claimed = null;
        var modelCount = models.Count;
        for (var i = 0; i < modelCount; ++i)
        {
            var model = models[i];
            if (model != null && model.ChallengeId == challengeId)
            {
                claimed = model;
                break;
            }
        }

        var rewards = claimed?.MissionGroupRow?.rewards;
        if (rewards == null) return data;

        var rewardLen = rewards.Length;
        for (var i = 0; i < rewardLen; ++i)
        {
            var rewardIndex = rewards[i];
            if (rewardIndex <= 0) continue;
            if (!TableManager.GetData(rewardIndex, out EventRewardTableData rewardRow)) continue;
            data.AddRewardInfo(new RewardInfo((ItemType)rewardRow.itemType, rewardRow.itemIdx, rewardRow.itemValue));
        }
        return data;
    }


    // ISSUE-02 (2026-05-26) — 부모(UIPopupTrophyChallenge.OnFinalRewardClaimed) 가 최종 보상 수령
    // 메시지를 받은 시점에 호출. 단일 보상 시퀀스가 진행 중이면 그 팝업이 닫힌 뒤 종료 연출로 넘어가도록
    // 플래그만 세우고, 이미 끝난 뒤 도착했다면(네트워크 응답 지연 케이스) 곧바로 종료 연출로 진입한다.
    public void HandleFinalRewardClaim()
    {
        pendingFinalRewardClaim = true;

        if (claimSequenceActive) return;

        // 단일 보상 시퀀스가 이미 종료된(또는 시작되지 않은) 케이스 — 곧바로 종료 연출로.
        // OnDataChanged 자동 재정렬을 보류하기 위해 claimSequenceActive 도 함께 활성.
        claimSequenceActive = true;
        EnterClearSequence();
    }

    // 최종 보상 수령 시퀀스 — 종료 연출(_Clear) 진입.
    //   기존: 최종 보상 팝업 → (닫힘) → 종료 연출. 종료 연출과 후속 연출(미라클 뱃지 등)이 겹쳤다.
    //   변경: 종료 연출(3단계) → ③ 캐릭터 연출이 닫히면서 최종 보상 팝업.
    // 최종 보상 팝업은 연출 3단계가 모두 끝난 뒤 부모(UIPopupTrophyChallenge.OnClearClosed) 가
    // OpenFinalRewardPopup 으로 노출한다.
    private void EnterClearSequence()
    {
        pendingFinalRewardClaim = false;
        claimSequenceActive = false;
        RefreshList();
        GetComponentInParent<UIPopupTrophyChallenge>()?.ShowClearPanel(withFinalRewardPopup: true);
    }

    // 단일 보상 팝업의 Closed 이벤트는 base.Close() 직전에 발화된다. 같은 콜백 안에서 곧바로 패널을
    // 전환하면 닫히는 중인 보상팝업 위로 종료 연출 ① 단계가 겹쳐 보이므로, 한 프레임 양보해
    // 보상팝업이 완전히 정리(풀 반환 + 비활성)된 뒤 종료 연출로 진입한다.
    private async UniTaskVoid EnterClearSequenceAfterCloseAsync()
    {
        await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, gameObject.GetCancellationTokenOnDestroy());
        EnterClearSequence();
    }

    // 종료 연출(③ 캐릭터 연출)이 닫히는 시점에 부모가 호출 — 최종 보상 팝업(UIPopupRewardResult) 노출.
    // 부모가 곧바로 자기 팝업을 닫으므로, 보상 데이터 구성은 여기서 동기로 끝내고 팝업 오픈만 넘긴다
    // (RewardHelper 는 static 이라 본 패널이 비활성/풀 반환되어도 오픈 흐름은 완결된다).
    // 보상에 미라클 뱃지가 있으면 보상팝업이 닫힌 뒤 UIPopupRewardResult 기본 흐름대로
    // 미라클 뱃지 획득 연출이 이어진다.
    public void OpenFinalRewardPopup()
    {
        RewardHelper.OpenCommonRewardPopup(BuildFinalRewardInfo());
    }

    // ISSUE-02 — 최종 보상(TrophyMaster.group.completeReward = Event_Reward 인덱스)을 보상팝업용
    // 데이터로 변환. BuildClaimRewardInfo 와 동일한 패턴이라 변환부만 별도 구성.
    private RewardInfoData BuildFinalRewardInfo()
    {
        var data = new RewardInfoData();
        var master = TrophyChallengeManager.Instance.GetMaster(currentTrophyId);
        var completeReward = master?.group?.completeReward;
        if (completeReward == null) return data;

        var rewardLen = completeReward.Length;
        for (var i = 0; i < rewardLen; ++i)
        {
            var rewardIndex = completeReward[i];
            if (rewardIndex <= 0) continue;
            if (!TableManager.GetData(rewardIndex, out EventRewardTableData rewardRow)) continue;
            data.AddRewardInfo(new RewardInfo((ItemType)rewardRow.itemType, rewardRow.itemIdx, rewardRow.itemValue));
        }
        return data;
    }

    // 명세서 §7.8 / Confluence 880017421 §2-6 — 최초 튜토리얼 포커싱 대상.
    //   actionCondition2 = 28(첫번째 챌린지 아이콘) / 29(상세 정보 영역) /
    //                      30(최종 보상 영역) / 31(인포 버튼).
    // TrophyChallengeHelper 가 본 메서드들을 통해 RectTransform 을 노출한다.

    // 28 / endCondition 54 — 첫번째 챌린지 아이콘. LoopScroll 의 첫 행 첫 셀.
    //   UIPopupTrophyChallengeLoopScroll 은 LoopScrollBase<T> 상속이라 content 가 직접 노출되지 않아
    //   RequireComponent 로 부착된 LoopScrollRect 의 content 를 GetComponent 로 우회 접근.
    public RectTransform GetFirstChallengeSlotRect()
    {
        if (loopScroll == null) return null;
        var scrollRect = loopScroll.GetComponent<LoopScrollRect>();
        var content = scrollRect != null ? scrollRect.content : null;
        if (content == null || content.childCount == 0) return null;
        var firstRow = content.GetChild(0).GetComponent<ScrollPanelTrophyChallenge>();
        return firstRow != null ? firstRow.GetTutorialClickSlot(0) : null;
    }

    // 29 — 챌린지 상세 정보 영역 (PanelMissionTab).
    public RectTransform GetMissionDetailRect()
        => missionTab != null ? missionTab.MissionTabTutorialRect : null;

    // 30 — 최종 보상 정보 영역 (UIPopupTrophyChallengeRewardBox01).
    public RectTransform GetFinalRewardRect()
        => finalRewardScroll != null ? finalRewardScroll.transform as RectTransform : null;

    // 31 — 인포 버튼.
    public RectTransform GetInfoButtonRect()
        => infoBtn != null ? infoBtn.transform as RectTransform : null;
}

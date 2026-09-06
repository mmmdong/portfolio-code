using System;
using System.Collections.Generic;

using GameCore.Utils;           // DLogger

using GameLogic.Extension;      // IsNullOrEmpty()
using GameLogic.Management;      // VibrationManager (경직 햅틱)

using MoreMountains.NiceVibrations;     // HapticTypes

using UnityEngine;
using UnityEngine.UI;           // GridLayoutGroup

/// <summary>
/// 당근 수확 대소동 인게임 보드 뷰(MonoBehaviour). 인게임 팝업 프리팹의 자식으로 배치한다.
///
/// 보드는 팝업 활성화 시 <b>N×N(N = Event_CarrotSetting.grid)</b> 슬롯을 런타임 동적 생성한다.
/// 프리팹의 단일 템플릿 슬롯(<see cref="holeTemplate"/>)을 복제해 N×N 을 채우고,
/// 같은 GameObject 의 <see cref="GridLayoutGroup"/> 로 셀을 배치한다.
/// 셀 크기는 보드 가용 영역에 N×N 을 맞춰 산출하므로(BOARD_FILL_RATIO) N 이 커져도 화면을 넘지 않고,
/// 당근 <c>localScale</c> 은 셀 크기에 비례해 함께 축소해 자연스러운 핏을 유지한다.
/// 각 슬롯(<see cref="EventCarrotHole"/>)은 영구 당근 뷰(<see cref="EventCarrotView"/>)를 보유하며,
/// 빈 상태(0_None) ↔ 등장(Appear)·대기(Idle)·복귀(Return)·뽑힘(Exit) 애니메이션으로 표현한다(풀링 없음).
///
/// 순수 로직(<see cref="EventCarrotBoardModel"/>/<see cref="EventCarrotScoreModel"/>)은
/// <see cref="ContentEventCarrot"/> 가 소유(보드 모델도 grid×grid 홀)하고, 본 뷰는
///  - 매 프레임 controller.TickInGame 을 구동해 결과(등장 spawned / 복귀 expired)를 슬롯 뷰로 표현하고,
///  - 구멍 터치를 controller.OnTouchHole 로 전달한 뒤 결과(점수/콤보/슈퍼)를 연출한다.
///
/// 호출 순서(팝업): Init(controller)[= N×N 보드 생성] → (3초 카운트 후) StartGame() → 시간 만료 시 자동 종료(StopGame).
/// </summary>
[DisallowMultipleComponent]
public sealed class EventCarrotBoardView : MonoBehaviour
{
    private const float STUN_SECONDS = 1f;      // 빈 구멍 터치 경직 시간(§6-2). TODO[table]: 경직 컬럼 생기면 교체
    private const float BOARD_FILL_RATIO = 0.92f;   // 보드 영역 대비 N×N 그리드가 차지할 비율(가장자리 여백) — 더 좁히려면 값↓
    private const float FALLBACK_BOARD_SIDE = 1000f;    // 보드 rect 미해결 시 폴백 한 변(Center 높이 기준)

    [Header("Grid")]
    [SerializeField] private GridLayoutGroup gridLayout;     // 셀 배치(이 GameObject) — 런타임에 N열 고정으로 구성. 미바인딩 시 Awake 에서 GetComponent 폴백
    [SerializeField] private EventCarrotHole holeTemplate;   // 복제 원본 슬롯(자식 1개) — N×N 만큼 복제. 미바인딩 시 Awake 에서 자식 탐색 폴백

    private EventCarrotHole[] holes;        // 런타임 생성된 N×N 슬롯(배열 순서 = 구멍 인덱스 0..N²-1)

    private readonly List<EventCarrotSpawnInfo> spawnedBuffer = new();
    private readonly List<int> expiredBuffer = new();

    private ContentEventCarrot controller;
    private bool running;
    private float stunTimer;        // 0보다 크면 경직 중(입력 무시)
    private float referenceCellSize;    // 당근 아트가 localScale=1 로 제작된 셀 한 변(프리팹 GridLayoutGroup.cellSize) — Awake 캐시, 스케일 환산 기준
    private Vector3 templateScale;  // 템플릿 슬롯의 원본 localScale(referenceCellSize 기준) — Awake 캐시

    /// <summary>구멍 터치 1회 결과 — 팝업이 구독해 점수판/콤보 배너/슈퍼 보상 연출에 사용.</summary>
    public event Action<EventCarrotTouchOutcome> Harvested;

    /// <summary>게임 시간 만료로 종료됐을 때 1회 발생 — 팝업이 구독해 결과 처리/HUD 정지에 사용.</summary>
    public event Action GameEnded;

    // 같은 GameObject 의 GridLayoutGroup·자식 템플릿 슬롯을 캐시(바인딩 우선, 미바인딩 시 폴백). 보드 생성은 Init 에서 수행.
    private void Awake()
    {
        if (gridLayout == null)
            gridLayout = GetComponent<GridLayoutGroup>();

        if (holeTemplate == null && gridLayout != null)
            holeTemplate = gridLayout.GetComponentInChildren<EventCarrotHole>(true);

        if (gridLayout != null)
            referenceCellSize = gridLayout.cellSize.x;      // 당근 아트 기준 셀 크기 캡처(localScale=1 일 때의 셀)

        templateScale = holeTemplate != null ? holeTemplate.transform.localScale : Vector3.one;
    }

    /// <summary>
    /// 컨트롤러(보드/점수 모델 소유) 참조 주입 + 보드 생성.
    /// 컨트롤러가 Event_CarrotSetting.grid 로 결정한 N 에 맞춰 N×N 슬롯을 즉시 생성한다(StartGame 이전).
    /// </summary>
    public void Init(ContentEventCarrot owner)
    {
        controller = owner;
        if (controller == null)
            return;

        BuildBoard(controller.BoardGridSize);
    }

    // 템플릿 슬롯을 복제해 N×N 보드를 구성한다. 셀 크기를 보드 가용 영역에 맞춰 산출하고 당근 localScale 을 셀에 비례해 축소한다.
    private void BuildBoard(int grid)
    {
        if (grid < 1)
            grid = 1;

        if (holeTemplate == null || gridLayout == null)
        {
            DLogger.Error($"[{nameof(EventCarrotBoardView)}] holeTemplate/gridLayout 미바인딩 — 보드 생성 실패. Center(GridLayoutGroup)·템플릿 슬롯 확인");
            return;
        }

        ClearGeneratedHoles();      // 재진입(다시/팝업 캐시) 대비: 직전 판 복제 슬롯을 정리하고 템플릿만 남긴다.

        // 보드 가용 영역(정사각 한 변)에 N×N 을 맞춰 채운다 → N 이 커져도 화면을 넘지 않는다.
        //  - 셀 크기   = (보드 한 변 × BOARD_FILL_RATIO) / N   → 전체 그리드가 항상 보드 안에 들어옴.
        //  - 당근 스케일 = 템플릿 스케일 × (셀 / referenceCellSize) → 셀과 같은 비율로 당근 아트도 축소(간격 ≈ 렌더 크기).
        var boardSide = ResolveBoardSide();
        var cell = boardSide * BOARD_FILL_RATIO / grid;
        var scaleRatio = referenceCellSize > 0f ? cell / referenceCellSize : 1f;

        gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        gridLayout.constraintCount = grid;
        gridLayout.cellSize = new Vector2(cell, cell);
        gridLayout.spacing = Vector2.zero;
        gridLayout.childAlignment = TextAnchor.MiddleCenter;

        DLogger.Log($"[Carrot] 보드 {grid}x{grid} 생성 — boardSide={boardSide:F0} cell={cell:F0} scale={scaleRatio:F2}");

        var carrotScale = templateScale * scaleRatio;
        var holeCount = grid * grid;
        var parent = gridLayout.transform;

        holes = new EventCarrotHole[holeCount];
        for (var i = 0; i < holeCount; i++)
        {
            // 0번은 프리팹 템플릿 슬롯을 그대로 사용하고, 나머지는 복제해 N×N 을 채운다(배열 순서 = 구멍 인덱스).
            var hole = i == 0 ? holeTemplate : Instantiate(holeTemplate, parent, false);
            hole.transform.localScale = carrotScale;
            hole.gameObject.SetActive(true);

            var holeIndex = i;      // 클로저 캡처
            hole.SetClickListener(() => OnHoleClicked(holeIndex));

            holes[i] = hole;
        }
    }

    // 보드 가용 정사각 한 변(px). 보드(Center) RectTransform 의 너비·높이 중 작은 값. 미해결 시 폴백.
    private float ResolveBoardSide()
    {
        if (transform is RectTransform rt)
        {
            Canvas.ForceUpdateCanvases();       // 팝업 오픈 직후 rect 가 아직 0 일 수 있어 강제 갱신
            var side = Mathf.Min(rt.rect.width, rt.rect.height);
            if (side > 1f)
                return side;
        }

        return FALLBACK_BOARD_SIDE;
    }

    // 직전 BuildBoard 가 생성한 복제 슬롯을 파괴한다(템플릿 holes[0] 은 보존). 재진입 시 중복 생성 방지.
    private void ClearGeneratedHoles()
    {
        if (holes == null)
            return;

        var count = holes.Length;
        for (var i = 0; i < count; i++)
        {
            var hole = holes[i];
            if (hole != null && hole != holeTemplate)
                Destroy(hole.gameObject);
        }

        holes = null;
    }

    /// <summary>게임 시작 — 모든 슬롯을 빈 상태로 두고 매 프레임 구동 시작. (모델 초기화는 controller.EnterInGame 에서 수행)</summary>
    public void StartGame()
    {
        EventCarrotInGameText.CacheTemplates();     // 콤보 포맷 템플릿 1회 캐싱(매 터치 GetText 조회 방지)
        ClearAll();
        stunTimer = 0f;
        running = true;
    }

    /// <summary>게임 정지 — 매 프레임 구동 중단.</summary>
    public void StopGame()
    {
        running = false;
    }

    private void Update()
    {
        if (!running || controller == null)
            return;

        var deltaTime = Time.deltaTime;

        if (stunTimer > 0f)
            stunTimer -= deltaTime;

        var stillRunning = controller.TickInGame(deltaTime, spawnedBuffer, expiredBuffer);

        ApplyExpired();
        ApplySpawned();

        if (!stillRunning)
        {
            running = false;        // 시간 만료 → controller 가 OnGameEnd 처리 완료
            GameEnded?.Invoke();
        }
    }

    /// <summary>경직 중 여부 — 인게임 팝업이 경직 UI(EventCarrot_StunBox) 표시 판단에 사용 가능.</summary>
    

    private void ApplySpawned()
    {
        var count = spawnedBuffer.Count;
        for (var i = 0; i < count; i++)
        {
            var info = spawnedBuffer[i];
            var hole = HoleOf(info.HoleIndex);
            if (hole == null)
            {
                // 모델 holeCount(grid×grid)가 뷰 슬롯 수보다 클 때(§10-A-9 grid 불일치) 뷰에 없는 구멍에 스폰될 수 있다.
                // 이 경우 모델 구멍을 즉시 비워, 보이지 않는 구멍이 영구 점유돼 스폰이 중단되는 것을 방지한다.
                controller.ReleaseHole(info.HoleIndex);
                continue;
            }

            var view = hole.View;
            if (view != null)
            {
                view.SetCarrotType(info.Type);
                view.PlayAppear();
            }

            // 슈퍼 레어는 머리 위 박스/HP 바 표시, 그 외는 숨김(이전 슈퍼 잔존 정리).
            if (info.Type == EventCarrotType.SuperRare)
                hole.Verdict?.Show();
            else
                hole.Verdict?.Hide();
        }
    }

    private void ApplyExpired()
    {
        var count = expiredBuffer.Count;
        for (var i = 0; i < count; i++)
        {
            var holeIndex = expiredBuffer[i];
            var hole = HoleOf(holeIndex);
            if (hole == null)
            {
                controller.ReleaseHole(holeIndex);      // 뷰에 없는 구멍(§10-A-9)도 비워 영구 점유·스폰 중단 방지
                continue;
            }

            var view = hole.View;
            if (view != null)
            {
                // 복귀(Return) 애니 동안에도 클릭 수확 가능. 애니 완료 시 None 전환 + 박스 숨김 + 모델 구멍 비움.
                view.PlayReturn(() =>
                {
                    view.PlayNone();
                    hole.Verdict?.Hide();
                    controller.ReleaseHole(holeIndex);
                });
            }
            else
            {
                controller.ReleaseHole(holeIndex);      // 뷰 없으면 즉시 비움(안전망)
            }
        }
    }

    private void OnHoleClicked(int holeIndex)
    {
        if (!running || controller == null)
            return;

        if (stunTimer > 0f)     // 경직 중 입력 무시(§6-2)
            return;

        var hole = HoleOf(holeIndex);
        if (hole == null)
            return;

        var view = hole.View;

        // 경직 판정(§6-2): 뷰가 None(빈 구멍) 상태일 때만 경직.
        // Exit/Return 애니 재생 중(None 아님)에는 경직하지 않는다.
        if (view == null || view.IsNone)
        {
            var miss = controller.OnTouchHole(holeIndex);   // 빈 구멍 → Empty(콤보 해제 포함)
            hole.ShowMiss();
            stunTimer = STUN_SECONDS;       // 1초 경직 — 입력 잠금
            VibrationManager.Instance.Haptic(HapticTypes.HeavyImpact);   // 경직 햅틱(ISSUE-64) — 빈 구멍 터치 페널티 피드백
            SoundManager.Instance.PlaySound(EventCarrotSoundDefine.STUN);    // 경직 사운드(ISSUE-49)
            Harvested?.Invoke(miss);        // Scored=false → 팝업 경직 UI
            return;
        }

        // 뷰는 None 이 아니지만 이미 수확된(Exit 재생 중) 구멍 → 경직/점수 없이 무시.
        if (!controller.IsHoleOccupied(holeIndex))
            return;

        var outcome = controller.OnTouchHole(holeIndex);
        var hit = outcome.Hit;      // 콤보 단계/연속수/획득 점수 캐싱(여러 분기에서 재사용)

        switch (outcome.Kind)
        {
            case EventCarrotTouchKind.Hit:
                view.PlayExit(view.PlayNone);   // 뽑힘 연출 후 빈 상태로
                hole.ShowScore(hit.GainedScore, hit.ComboTier, hit.ComboStreak);
                SoundManager.Instance.PlaySound(EventCarrotSoundDefine.TOUCH);   // 당근 터치 사운드(ISSUE-49)
                break;
            case EventCarrotTouchKind.SuperKilled:
                view.PlayExit(view.PlayNone);
                hole.Verdict?.PlayKill();           // 박스/HP 바 제거 + 폭발 이펙트(§7-2)
                hole.ShowScore(hit.GainedScore, hit.ComboTier, hit.ComboStreak);
                // Event_Reward.idx → (아이콘 타입/인덱스/수량) 해석 후 아이콘 보상 팝업 표시(§7-2).
                if (outcome.SuperRewardIndex >= 0
                    && controller.TryGetSuperReward(outcome.SuperRewardIndex, out var rewardType, out var rewardIndex, out var rewardCount))
                    hole.ShowReward(rewardType, rewardIndex, rewardCount);
                SoundManager.Instance.PlaySound(EventCarrotSoundDefine.SUPER_PULL);  // 슈퍼 레어 뽑힘 사운드(ISSUE-49)
                break;
            case EventCarrotTouchKind.SuperDamaged:
                view.PlayHit();                 // 피격 반응 후 Idle 복귀(당근 유지)
                hole.Verdict?.SetHp(outcome.SuperHpRatio);   // HP 바 감소(§7-2)
                hole.ShowScore(hit.GainedScore, hit.ComboTier, hit.ComboStreak);
                SoundManager.Instance.PlaySound(EventCarrotSoundDefine.TOUCH);   // 슈퍼 레어 피격(터치) 사운드(ISSUE-49)
                break;
        }

        Harvested?.Invoke(outcome);
    }

    // 게임 시작 시 모든 슬롯을 빈 구멍(None) 상태로 정렬.
    private void ClearAll()
    {
        if (holes.IsNullOrEmpty())
            return;

        var count = holes.Length;
        for (var i = 0; i < count; i++)
        {
            var hole = HoleOf(i);
            if (hole == null)
                continue;

            hole.View?.PlayNone();
            hole.Verdict?.Hide();
        }
    }

    private EventCarrotHole HoleOf(int holeIndex)
    {
        if (holes == null || holeIndex < 0 || holeIndex >= holes.Length)
            return null;

        return holes[holeIndex];
    }

    
}

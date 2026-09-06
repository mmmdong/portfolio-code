using System.Collections.Generic;

using GameLogic.Extension;   // IsNullOrEmpty()

using UnityEngine;

/// <summary>
/// 당근 수확 대소동 — 한 판(기본 20초) 인게임 보드 상태 머신(순수 로직).
/// 기획서 886177888 §5-5 인게임 / §7-1 등장·복귀 / Event_CarrotSetting(grid·maxSpawn·objLife·spawnGap·gameTime·superHP).
///
/// 책임 분리:
///  - 등장 "종류/위치" 추첨   → <see cref="EventCarrotSpawnSelector"/> (본 모델이 소유·구동)
///  - "터치 → 점수/콤보"      → <see cref="EventCarrotScoreModel"/>
///  - 화면 표현/풀링/입력     → 후속 MonoBehaviour 보드 뷰(SetCarrotType/PlayAppear/PlayReturn/PlayExit)
///  - 본 모델                 → 구멍 점유 상태 + spawnGap 주기 스폰 + objLife 만료 복귀 + 슈퍼 HP 판정 (UnityEngine.Random 외 비의존)
///
/// 매 프레임 <see cref="Tick"/> 가 "이번 틱에 새로 등장한 당근(spawned)"과 "노출 시간이 끝나 복귀하는 구멍(expired)"을
/// 호출측 재사용 버퍼에 채워 반환한다(GC 회피). 입력은 <see cref="TryHarvest"/> 로 구멍 단위 처리.
/// </summary>
public sealed class EventCarrotBoardModel
{
    // 구멍 1칸의 런타임 상태.
    private struct Hole
    {
        public bool Occupied;           // 당근 점유 중 여부
        public EventCarrotType Type;    // 점유 당근 종류
        public float RemainLife;        // 남은 노출 시간(0 이하 → 복귀 시작)
        public int RemainHp;            // 남은 타격 수(일반/레어 1, 슈퍼 superHp)
        public bool Returning;          // 복귀(Return) 애니 재생 중 — 점유 유지(수확 가능), 완료 시 ReleaseHole 로 비움
    }

    private readonly EventCarrotSpawnSelector spawnSelector;
    private readonly Hole[] holes;
    private readonly int maxSpawn;
    private readonly float objLifeMin;
    private readonly float objLifeMax;
    private readonly float spawnGapMin;
    private readonly float spawnGapMax;
    private readonly float gameTime;
    private readonly int superHp;

    private readonly List<int> freeHoleBuffer = new();      // CollectFreeHoles 재사용 버퍼
    private readonly List<int> spawnPositionBuffer = new(); // PickSpawnPositions 결과 재사용 버퍼

    private float remainGameTime;
    private float spawnTimer;
    private bool running;

    
    public float RemainGameTime => remainGameTime;

    // 남은 시간 비율(1→0) — 인게임 타임 슬라이더 게이지용(ISSUE-41). gameTime 미설정 시 0.
    public float RemainTimeRatio => gameTime > 0f ? Mathf.Clamp01(remainGameTime / gameTime) : 0f;

    public int SuperHpMax => superHp;       // 슈퍼 레어 최대 타격 수(HP 바 비율 계산용)

    /// <param name="spawnSelector">종류/위치 추첨기(본 모델이 소유·ResetGame 까지 책임)</param>
    /// <param name="holeCount">구멍 수(grid × grid)</param>
    /// <param name="maxSpawn">한 번에 등장시킬 최대 수(Event_CarrotSetting.maxSpawn)</param>
    /// <param name="objLife">노출 시간 [최소, 최대] (Event_CarrotSetting.objLife)</param>
    /// <param name="spawnGap">등장 간격 [최소, 최대] (Event_CarrotSetting.spawnGap)</param>
    /// <param name="gameTime">게임 시간(초, Event_CarrotSetting.gameTime)</param>
    /// <param name="superHp">슈퍼 레어 타격 수(Event_CarrotSetting.superHP). 탭 수 단일 기준 = superHP 확정(§11-1)</param>
    public EventCarrotBoardModel(EventCarrotSpawnSelector spawnSelector, int holeCount, int maxSpawn,
        float[] objLife, float[] spawnGap, float gameTime, int superHp)
    {
        this.spawnSelector = spawnSelector;
        holes = new Hole[holeCount];
        this.maxSpawn = maxSpawn;
        objLifeMin = objLife[0];
        objLifeMax = objLife[1];
        spawnGapMin = spawnGap[0];
        spawnGapMax = spawnGap[1];
        this.gameTime = gameTime;
        this.superHp = superHp;
    }

    /// <summary>한 판 시작/재시작 — 추첨기·구멍·타이머 초기화. 시작 즉시 첫 스폰이 일어난다.</summary>
    public void StartGame()
    {
        spawnSelector.ResetGame();
        ClearHoles();
        remainGameTime = gameTime;
        spawnTimer = 0f;        // 0 → 첫 Tick 에서 즉시 첫 스폰
        running = true;
    }

    /// <summary>"다시"로 동일 조건 재시작.</summary>
    public void ResetGame()
    {
        StartGame();
    }

    /// <summary>
    /// 매 프레임 1회 진행. spawned/expired 는 호출측 재사용 버퍼(매 호출 Clear 후 채움).
    /// 반환: 게임 진행 중이면 true, 시간 만료로 종료되면 false.
    /// </summary>
    public bool Tick(float deltaTime, List<EventCarrotSpawnInfo> spawned, List<int> expired)
    {
        spawned.Clear();
        expired.Clear();
        if (!running)
            return false;

        // 1) 노출 시간 만료 → 복귀(Return) '시작'. 복귀 애니 동안에도 클릭 수확이 가능하도록 점유는 유지하고,
        //    실제 비움(None)은 복귀 애니 완료 시 ReleaseHole 로 처리한다. 이미 복귀 중(Returning)인 구멍은 건너뛴다.
        var holeCount = holes.Length;
        for (var i = 0; i < holeCount; i++)
        {
            if (!holes[i].Occupied || holes[i].Returning)
                continue;

            holes[i].RemainLife -= deltaTime;
            if (holes[i].RemainLife <= 0f)
            {
                holes[i].Returning = true;      // 점유 유지 — 복귀 애니 재생 동안에도 수확 가능
                expired.Add(i);
            }
        }

        // 2) 게임 시간 차감 — 만료 시 더 이상 스폰하지 않고 종료
        remainGameTime -= deltaTime;
        if (remainGameTime <= 0f)
        {
            remainGameTime = 0f;
            running = false;
            return false;
        }

        // 3) 스폰 주기 도래 → 빈 구멍에 maxSpawn 까지 새 당근 등장
        spawnTimer -= deltaTime;
        if (spawnTimer <= 0f)
        {
            SpawnWave(spawned);
            spawnTimer = Random.Range(spawnGapMin, spawnGapMax);
        }

        return true;
    }

    /// <summary>
    /// 구멍 터치 처리. 빈 구멍이면 Empty(경직), 일반/레어면 Hit(즉시 제거),
    /// 슈퍼 레어면 HP 차감 후 남으면 SuperDamaged / 0이면 SuperKilled(제거).
    /// </summary>
    public EventCarrotTouchKind TryHarvest(int holeIndex, out EventCarrotType type, out int remainHp)
    {
        type = default;
        remainHp = 0;
        if (holeIndex < 0 || holeIndex >= holes.Length || !holes[holeIndex].Occupied)
            return EventCarrotTouchKind.Empty;

        type = holes[holeIndex].Type;
        if (type != EventCarrotType.SuperRare)
        {
            holes[holeIndex].Occupied = false;
            holes[holeIndex].Returning = false;
            return EventCarrotTouchKind.Hit;
        }

        holes[holeIndex].RemainHp--;
        remainHp = holes[holeIndex].RemainHp;
        if (remainHp <= 0)
        {
            holes[holeIndex].Occupied = false;
            holes[holeIndex].Returning = false;
            return EventCarrotTouchKind.SuperKilled;
        }

        // 슈퍼 레어 생존: 복귀(Return) 중에 맞았다면 대기 상태로 되돌리고 노출 시간을 다시 부여(계속 클릭 가능).
        holes[holeIndex].Returning = false;
        holes[holeIndex].RemainLife = Random.Range(objLifeMin, objLifeMax);
        return EventCarrotTouchKind.SuperDamaged;
    }

    /// <summary>구멍 점유 여부 — 뷰가 None 이 아니어도(Exit 재생 중 등) 모델상 비점유면 수확 대상이 아님을 판별.</summary>
    public bool IsOccupied(int holeIndex)
    {
        return holeIndex >= 0 && holeIndex < holes.Length && holes[holeIndex].Occupied;
    }

    /// <summary>복귀(Return) 애니가 완료된 구멍을 비운다(뷰가 완료 콜백에서 호출). 이미 수확/비점유면 false.</summary>
    public bool ReleaseHole(int holeIndex)
    {
        if (holeIndex < 0 || holeIndex >= holes.Length || !holes[holeIndex].Occupied)
            return false;

        holes[holeIndex].Occupied = false;
        holes[holeIndex].Returning = false;
        return true;
    }

    private void SpawnWave(List<EventCarrotSpawnInfo> spawned)
    {
        CollectFreeHoles(freeHoleBuffer);
        if (freeHoleBuffer.IsNullOrEmpty())
            return;

        // 동시 등장 상한(maxSpawn): 화면에 동시에 떠 있는 당근 수가 maxSpawn 을 넘지 않도록,
        // 현재 점유 수를 뺀 남은 여유만큼만 새로 등장시킨다(기획 §8-4 "한번에 등장 가능한 당근 수").
        var allowed = maxSpawn - (holes.Length - freeHoleBuffer.Count);
        if (allowed <= 0)
            return;     // 이미 동시 상한 도달 → 이번 웨이브는 스폰 없음

        // 위치(빈 구멍 셔플) → 앞에서 allowed 개까지, 각 위치에 종류 추첨
        spawnSelector.PickSpawnPositions(freeHoleBuffer, allowed, spawnPositionBuffer);
        var count = spawnPositionBuffer.Count;
        for (var i = 0; i < count; i++)
        {
            var holeIndex = spawnPositionBuffer[i];
            var type = spawnSelector.Next();
            OccupyHole(holeIndex, type);
            spawned.Add(new EventCarrotSpawnInfo(type, holeIndex));
        }
    }

    private void OccupyHole(int holeIndex, EventCarrotType type)
    {
        // TODO[table]: 슈퍼 레어는 별도 대기 시간(기획 §7-1 "슈퍼 레어 대기 시간") 컬럼이 생기면 분리 적용.
        holes[holeIndex].Occupied = true;
        holes[holeIndex].Type = type;
        holes[holeIndex].RemainLife = Random.Range(objLifeMin, objLifeMax);
        holes[holeIndex].RemainHp = type == EventCarrotType.SuperRare ? superHp : 1;
        holes[holeIndex].Returning = false;
    }

    private void CollectFreeHoles(List<int> buffer)
    {
        buffer.Clear();
        var holeCount = holes.Length;
        for (var i = 0; i < holeCount; i++)
        {
            if (!holes[i].Occupied)
                buffer.Add(i);
        }
    }

    private void ClearHoles()
    {
        var holeCount = holes.Length;
        for (var i = 0; i < holeCount; i++)
        {
            holes[i].Occupied = false;
            holes[i].RemainLife = 0f;
            holes[i].RemainHp = 0;
            holes[i].Returning = false;
        }
    }
}

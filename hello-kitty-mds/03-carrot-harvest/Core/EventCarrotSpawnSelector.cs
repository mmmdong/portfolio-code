using System.Collections.Generic;

using GameLogic.Extension;   // int[].GetWeightedRandomIndex(), IList<T>.ShuffleRandom()

using UnityEngine;

/// <summary>
/// 당근 수확 대소동 — 한 판(기본 20초) 동안 유지되는 당근 등장 추첨기.
/// 기획서 886177888 §5-5 / §6-4 인게임 스폰 로직 구현.
///
/// 등장 "종류"(가중치 랜덤 + 천장/최대치 제약)와 등장 "위치"(빈 구멍 셔플)를 분리해 처리한다.
/// - 종류 추첨: 누적 가중치 + 선형 스캔. 항목 3종뿐이라 Alias/이분탐색은 불필요(오버엔지니어링).
/// - 순수 가중치 랜덤만으로는 superMax(게임당 최대)·superPityCount(천장) 스펙을 충족하지 못하므로
///   기존 가중치 유틸 위에 제약 로직을 래핑한다.
/// - 가중치는 appearRate(만분율)를 정수 그대로 사용(부동소수 누적오차 방지).
///
/// 기존 유틸 재사용: GenericExtension(GameLogic.Extension) 의
///   int[].GetWeightedRandomIndex() / IList&lt;T&gt;.ShuffleRandom().
/// </summary>
public sealed class EventCarrotSpawnSelector
{
    private const int TYPE_COUNT = 3;   // EventCarrotType 개수 (Normal/Rare/SuperRare)

    private readonly int[] baseWeights;     // appearRate(만분율) [일반, 레어, 슈퍼]
    private readonly int superPityCount;    // 슈퍼 레어 등장 천장 카운트
    private readonly int superMax;          // 한 판당 슈퍼 레어 최대 등장 수
    private readonly int[] effectiveWeights = new int[TYPE_COUNT];   // 매 스폰 재사용 버퍼

    private int pityCounter;        // 슈퍼 미등장 누적(일반/레어 등장마다 +1)
    private int superSpawnedCount;  // 이번 판 슈퍼 등장 수

    /// <param name="appearRate">Event_MoleSetting.appearRate (만분율, [일반, 레어, 슈퍼])</param>
    /// <param name="superPityCount">Event_MoleSetting.superPityCount (천장)</param>
    /// <param name="superMax">Event_MoleSetting.superMax (게임당 슈퍼 최대 등장 수)</param>
    public EventCarrotSpawnSelector(int[] appearRate, int superPityCount, int superMax)
    {
        baseWeights         = appearRate;
        this.superPityCount = superPityCount;
        this.superMax       = superMax;
    }

    /// <summary>다음에 등장할 당근 종류를 추첨한다. 매 스폰 1회 호출.</summary>
    public EventCarrotType Next()
    {
        var superExhausted = superSpawnedCount >= superMax;

        // 천장 도달 시 슈퍼 강제 등장(아직 최대 등장 수에 여유가 있을 때만)
        if (!superExhausted && superPityCount > 0 && pityCounter >= superPityCount)
        {
            return MarkSpawn(EventCarrotType.SuperRare);
        }

        // 유효 가중치 구성 — 슈퍼가 소진되면 슈퍼 가중치를 0으로 제외
        effectiveWeights[(int)EventCarrotType.Normal]    = baseWeights[(int)EventCarrotType.Normal];
        effectiveWeights[(int)EventCarrotType.Rare]      = baseWeights[(int)EventCarrotType.Rare];
        effectiveWeights[(int)EventCarrotType.SuperRare] = superExhausted ? 0 : baseWeights[(int)EventCarrotType.SuperRare];

        var idx = effectiveWeights.GetWeightedRandomIndex();        // 기존 가중치 랜덤 유틸 재사용
        var picked = idx >= 0 ? (EventCarrotType)idx : EventCarrotType.Normal;   // 합이 0이면 일반으로 폴백(빈 스폰 방지)
        return MarkSpawn(picked);
    }

    /// <summary>
    /// 비어 있는 구멍 후보에서 이번에 스폰할 위치를 maxSpawn개까지 선택한다.
    /// 가중치 랜덤이 아니라 부분 Fisher–Yates 셔플(ShuffleRandom) 후 앞에서 N개 취득.
    /// </summary>
    /// <param name="freeHoles">현재 비어 있는 구멍 인덱스(셔플로 내부 순서가 변경됨)</param>
    /// <param name="count">이번에 스폰할 최대 수(Event_MoleSetting.maxSpawn)</param>
    /// <param name="result">선택된 구멍 인덱스를 채워 반환(out 버퍼, 호출측에서 재사용 가능)</param>
    public void PickSpawnPositions(List<int> freeHoles, int count, List<int> result)
    {
        result.Clear();
        if (freeHoles.IsNullOrEmpty() || count <= 0)
            return;

        freeHoles.ShuffleRandom();      // 기존 셔플 유틸 재사용
        var pick = Mathf.Min(count, freeHoles.Count);
        for (var i = 0; i < pick; i++)
        {
            result.Add(freeHoles[i]);
        }
    }

    /// <summary>"다시"로 동일 조건 재시작 시 호출 — 천장/슈퍼 등장 카운트 초기화.</summary>
    public void ResetGame()
    {
        pityCounter       = 0;
        superSpawnedCount = 0;
    }

    private EventCarrotType MarkSpawn(EventCarrotType type)
    {
        if (type == EventCarrotType.SuperRare)
        {
            superSpawnedCount++;
            pityCounter = 0;
        }
        else
        {
            // 천장은 "일반/레어 등장" 기준 누적(기획 §5-4 superPityCount 정의). 빈 구멍 미스/경직은 대상 아님.
            pityCounter++;
        }

        return type;
    }
}

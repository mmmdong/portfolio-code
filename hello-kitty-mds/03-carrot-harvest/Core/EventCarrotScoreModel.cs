/// <summary>
/// 당근 수확 대소동 — 한 판(기본 20초) 동안의 점수·콤보 계산 모델(순수 로직).
/// 기획서 886177888 §6-3 콤보 / §7-4 콤보 게이지 / Event_MoleSetting.scoreSet·comboCount·comboBonus.
///
/// 역할 분리: 등장 추첨은 <see cref="EventCarrotSpawnSelector"/>, 화면 표현은 <see cref="EventCarrotView"/>,
///            본 클래스는 "터치 성공/실패 → 점수·콤보" 계산만 담당한다(UnityEngine 비의존, 단위테스트 가능).
///
/// 콤보 규칙:
///  - 터치당 최종 점수 = scoreSet[당근 종류] + 현재 콤보 보너스.
///  - 연속 성공 수가 comboCount[0](일반) 이상이면 일반 콤보(+comboBonus[0]),
///    comboCount[1](메가) 이상이면 메가 콤보(+comboBonus[1]).
///  - 매 명중 시 유지 시간(comboKeepTime, 기본 2초)으로 리셋, Tick 으로 감소.
///    유지 시간 내 다음 명중이 없거나 빈 구멍 터치(경직)면 콤보 해제.
/// </summary>
public sealed class EventCarrotScoreModel
{
    private const int TIER_NORMAL_INDEX = 0;    // comboCount/comboBonus 배열의 일반 콤보 인덱스
    private const int TIER_MEGA_INDEX = 1;      // comboCount/comboBonus 배열의 메가 콤보 인덱스

    private readonly int[] scoreSet;            // 터치당 점수 [일반, 레어, 슈퍼]
    private readonly int normalComboCount;      // 일반 콤보 발동 연속 성공 수
    private readonly int megaComboCount;        // 메가 콤보 발동 연속 성공 수
    private readonly int normalComboBonus;      // 일반 콤보 가산점
    private readonly int megaComboBonus;        // 메가 콤보 가산점
    private readonly float comboKeepTime;       // 콤보 유지 시간(초)

    private int currentScore;       // 이번 판 누적 점수
    private int comboStreak;        // 현재 연속 성공 수
    private float comboTimer;       // 콤보 유지 잔여 시간

    public int CurrentScore => currentScore;
    
    public EventCarrotComboTier ComboTier => ResolveComboTier(comboStreak);

    /// <summary>콤보 게이지 비율(1→0). UI 게이지 연출용(기획 §7-4 2초 드레인).</summary>
    public float ComboGaugeRatio => comboStreak > 0 && comboKeepTime > 0f ? comboTimer / comboKeepTime : 0f;

    /// <param name="scoreSet">Event_MoleSetting.scoreSet (터치당 점수, [일반, 레어, 슈퍼])</param>
    /// <param name="comboCount">Event_MoleSetting.comboCount ([일반 발동, 메가 발동])</param>
    /// <param name="comboBonus">Event_MoleSetting.comboBonus ([일반 가산, 메가 가산])</param>
    /// <param name="comboKeepTime">콤보 유지 시간(초). 테이블 컬럼 미정 시 기본 2초(기획 §6-3 / §11).</param>
    public EventCarrotScoreModel(int[] scoreSet, int[] comboCount, int[] comboBonus, float comboKeepTime)
    {
        this.scoreSet = scoreSet;
        normalComboCount = comboCount[TIER_NORMAL_INDEX];
        megaComboCount = comboCount[TIER_MEGA_INDEX];
        normalComboBonus = comboBonus[TIER_NORMAL_INDEX];
        megaComboBonus = comboBonus[TIER_MEGA_INDEX];
        this.comboKeepTime = comboKeepTime;
    }

    /// <summary>당근 터치 성공 — 점수·콤보를 갱신하고 결과를 반환한다.</summary>
    public EventCarrotHitResult OnHit(EventCarrotType type)
    {
        comboStreak++;
        comboTimer = comboKeepTime;

        var tier = ResolveComboTier(comboStreak);
        var bonus = ResolveComboBonus(tier);
        var gained = scoreSet[(int)type] + bonus;
        currentScore += gained;

        return new EventCarrotHitResult(gained, comboStreak, tier, bonus);
    }

    /// <summary>빈 구멍 터치(경직, MISS) — 콤보 해제. 점수 감점은 없음(기획상 감점 규칙 부재).</summary>
    public void OnMiss()
    {
        BreakCombo();
    }

    /// <summary>매 프레임 콤보 타이머 갱신. 유지 시간 경과로 콤보가 해제되면 true 반환(UI 연출용).</summary>
    public bool Tick(float deltaTime)
    {
        if (comboStreak <= 0)
            return false;

        comboTimer -= deltaTime;
        if (comboTimer <= 0f)
        {
            BreakCombo();
            return true;
        }

        return false;
    }

    /// <summary>"다시"로 동일 조건 재시작 시 호출 — 점수·콤보 초기화.</summary>
    public void ResetGame()
    {
        currentScore = 0;
        BreakCombo();
    }

    private EventCarrotComboTier ResolveComboTier(int streak)
    {
        if (streak >= megaComboCount)
            return EventCarrotComboTier.Mega;
        if (streak >= normalComboCount)
            return EventCarrotComboTier.Combo;
        return EventCarrotComboTier.None;
    }

    private int ResolveComboBonus(EventCarrotComboTier tier)
    {
        return tier switch
        {
            EventCarrotComboTier.Mega => megaComboBonus,
            EventCarrotComboTier.Combo => normalComboBonus,
            _ => 0,
        };
    }

    private void BreakCombo()
    {
        comboStreak = 0;
        comboTimer = 0f;
    }
}

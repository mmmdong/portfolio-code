// 당근 수확 대소동 — 구멍에서 등장하는 당근 종류.
// 기획서 886177888 §6-1 / Event_MoleSetting.scoreSet·appearRate 의 배열 순서(일반→레어→슈퍼)와 1:1 대응.
// enum 정수값이 곧 가중치/점수 배열의 인덱스이므로 순서를 바꾸지 말 것.
public enum EventCarrotType
{
    Normal = 0,     // 일반 당근 (EventCarrot_Normal)
    Rare = 1,       // 레어 당근 (EventCarrot_Rare)
    SuperRare = 2,  // 슈퍼 레어 당근 (EventCarrot_SuperRare) — 보호 게이지 격파형
}

/// <summary>인게임 스폰 1건 — 어떤 당근(Type)을 어느 구멍(HoleIndex)에 등장시킬지.</summary>
public readonly struct EventCarrotSpawnInfo
{
    public readonly EventCarrotType Type;
    public readonly int HoleIndex;

    public EventCarrotSpawnInfo(EventCarrotType type, int holeIndex)
    {
        Type = type;
        HoleIndex = holeIndex;
    }
}

/// <summary>
/// 당근 SkeletonGraphic 애니메이션 상태. enum 정수값 + 이름이 곧 Spine 애니메이션 트랙명과 1:1 대응한다.
/// 트랙명 규칙: "{(int)값}_{이름}" → "0_None", "1_Appear", "2_Idle", "3_Return", "4_Exit", "5_Hit".
/// (Spine 에디터의 애니메이션 이름을 이 규칙으로 작성/유지할 것.)
/// </summary>
public enum EventCarrotAnimation
{
    None = 0,       // 아무것도 없는 상태
    Appear = 1,     // 당근 등장
    Idle = 2,       // 등장 후 클릭 대기
    Return = 3,     // 클릭되지 않아 다시 땅으로 들어감
    Exit = 4,       // 클릭되어 뽑힘
    Hit = 5,        // 탭 피격 반응(슈퍼 레어 — HP 남아 있어 처치되지 않은 매 탭)
}

/// <summary>
/// 콤보 단계 — 한 번에 이어진 연속 성공 수로 결정(기획 §6-3 / §7-4).
/// Event_MoleSetting.comboCount([일반 발동, 메가 발동]) 임계와 1:1 대응.
/// </summary>
public enum EventCarrotComboTier
{
    None = 0,       // 콤보 미발동
    Combo = 1,      // 일반 콤보 (COMBO!)
    Mega = 2,       // 메가 콤보 (MEGA COMBO!)
}

/// <summary>
/// 당근 1회 터치 성공 결과 — 점수/콤보 UI 연출에 필요한 정보를 한 번에 전달한다.
/// (점수 팝업 "+{0}", 콤보 배너 "COMBO ×N", 게이지 등에서 사용)
/// </summary>
public readonly struct EventCarrotHitResult
{
    public readonly int GainedScore;                    // 이번 터치 획득 점수(scoreSet + 콤보 보너스)
    public readonly int ComboStreak;                    // 현재 연속 성공 수
    public readonly EventCarrotComboTier ComboTier;     // 현재 콤보 단계
    public readonly int ComboBonus;                     // 이번 터치에 가산된 콤보 보너스

    public EventCarrotHitResult(int gainedScore, int comboStreak,
        EventCarrotComboTier comboTier, int comboBonus)
    {
        GainedScore = gainedScore;
        ComboStreak = comboStreak;
        ComboTier = comboTier;
        ComboBonus = comboBonus;
    }
}

/// <summary>구멍 터치 판정(보드 차원). 점수/콤보는 별도(EventCarrotScoreModel)에서 처리.</summary>
public enum EventCarrotTouchKind
{
    Empty = 0,          // 빈 구멍/이미 사라진 당근 터치 → 경직(MISS)
    Hit = 1,            // 일반/레어 당근 처치(즉시 뽑힘)
    SuperDamaged = 2,   // 슈퍼 레어 탭(HP 남음 — 매 탭 점수)
    SuperKilled = 3,    // 슈퍼 레어 처치(HP 0 → 보상 드롭)
}

/// <summary>
/// 구멍 터치 1회의 종합 결과 — 보드 판정 + 점수/콤보 + 슈퍼 보상까지 묶어 UI 연출에 전달한다.
/// </summary>
public readonly struct EventCarrotTouchOutcome
{
    public readonly EventCarrotTouchKind Kind;
    public readonly bool Scored;                    // 점수 가산 여부(Empty=false)
    public readonly EventCarrotHitResult Hit;       // Scored일 때 점수/콤보 결과
    public readonly int SuperRewardIndex;           // SuperKilled 시 추첨된 보상 idx, 그 외 -1
    public readonly float SuperHpRatio;             // 슈퍼 레어 탭 후 HP 비율(0~1). 비슈퍼/Empty/처치면 0

    public EventCarrotTouchOutcome(EventCarrotTouchKind kind,
        bool scored, EventCarrotHitResult hit, int superRewardIndex, float superHpRatio)
    {
        Kind = kind;
        Scored = scored;
        Hit = hit;
        SuperRewardIndex = superRewardIndex;
        SuperHpRatio = superHpRatio;
    }
}

/// <summary>
/// 한 판 종료 결과 — 결과 팝업(§5-6 / 상태별 §5-6-1) 표시용.
/// 노출 우선순위: 첫 도전 → 최고 점수 변동 → (누적 점수/보상) → 일반.
/// </summary>
public readonly struct EventCarrotGameResult
{
    public readonly int GainedScore;    // 이번 판 획득 점수
    public readonly int BestScore;      // (갱신 반영된) 최고 점수
    public readonly int PreviousBest;   // 이번 판 반영 전 최고 점수(결과 팝업 "{0} → {1}" §5-6-1, 0=이전 최고)
    public readonly bool IsNewBest;     // 이번 판이 최고 점수 갱신인지
    public readonly bool IsFirstPlay;   // 첫 도전 여부
    public readonly bool RewardGained;  // 이번 판에 새 누적 보상 티어를 달성했는지(결과 팝업 "보상 획득" §5-6 5)
    public readonly long RewardGoalScore;   // 새로 달성한 티어 중 최고 골 점수(43240 "누적 {0}점 달성")

    public EventCarrotGameResult(int gainedScore, int bestScore, int previousBest, bool isNewBest, bool isFirstPlay,
        long rewardGoalScore)
    {
        GainedScore = gainedScore;
        BestScore = bestScore;
        PreviousBest = previousBest;
        IsNewBest = isNewBest;
        IsFirstPlay = isFirstPlay;
        RewardGained = rewardGoalScore > 0;
        RewardGoalScore = rewardGoalScore;
    }
}

using System.Text;

using GameLogic.Management;     // TableManager

/// <summary>
/// 당근 수확 대소동 인게임 고빈도 텍스트(점수 "+N", 콤보 배너) 생성 전용 유틸.
///
/// 점수/콤보 텍스트는 매 터치마다 새로 만들어지므로(가변성 높음), 매번 문자열 보간으로 새 문자열을 빚으면
/// 인게임 도중 GC 할당이 누적된다. 따라서
///   ① 콤보 포맷 템플릿(LIdx)을 게임 시작 시 1회 캐싱(매 터치 GetText 조회 방지)하고,
///   ② 공유 <see cref="StringBuilder"/> 버퍼를 재사용해 중간 할당을 줄인다.
///
/// 단일 스레드(Unity 메인 루프)에서 각 호출이 즉시 빌드→반환을 마치므로 공유 버퍼는 안전하다.
/// </summary>
public static class EventCarrotInGameText
{
    private const string COMBO_STAR = " ★";     // 콤보 가산 점수 표기(§7-3)
    private const string COMBO_MULTIPLIER_PREFIX = "x";     // 콤보 배수 표기 "x{N}" (ComboText/ComboMegeText 그라데이션 텍스트, 슬라이드10)

    private static readonly StringBuilder BUILDER = new(32);

    private static string comboFormat;          // 43220 "COMBO! x{0}"
    private static string megaComboFormat;      // 43222 "MEGA COMBO x{0}"
    private static string comboBonusFormat;     // 43221 "콤보 보너스 점수 +{0}"
    private static bool templatesCached;

    /// <summary>
    /// 콤보 포맷 템플릿을 캐싱한다(게임 시작 시 1회). 언어 변경 후 재진입 시에도 갱신되도록 매 게임 시작에 호출한다.
    /// </summary>
    public static void CacheTemplates()
    {
        comboFormat = TableManager.GetText(EventCarrotStringDefine.INGAME_COMBO);
        megaComboFormat = TableManager.GetText(EventCarrotStringDefine.INGAME_MEGA_COMBO);
        comboBonusFormat = TableManager.GetText(EventCarrotStringDefine.INGAME_COMBO_BONUS);
        templatesCached = true;
    }

    /// <summary>터치 성공 점수 "+{score}" / 콤보 시 "+{score} ★" (§7-3). 포맷 인자가 숫자뿐이라 직접 Append.</summary>
    public static string Score(int gainedScore, bool combo)
    {
        var builder = BUILDER;
        builder.Clear();
        builder.Append('+').Append(gainedScore);

        if (combo)
            builder.Append(COMBO_STAR);

        return builder.ToString();
    }

    /// <summary>콤보 배수 "x{streak}"(ComboText/ComboMegeText 그라데이션 텍스트, 슬라이드10). 인자가 숫자뿐이라 직접 Append.</summary>
    public static string ComboMultiplier(int comboStreak)
    {
        var builder = BUILDER;
        builder.Clear();
        builder.Append(COMBO_MULTIPLIER_PREFIX).Append(comboStreak);
        return builder.ToString();
    }

    /// <summary>콤보 배너 본문 — 일반 "COMBO! x{0}"(43220) / 메가 "MEGA COMBO x{0}"(43222). 0 = 콤보 수.</summary>
    public static string Combo(int comboStreak, bool mega)
    {
        EnsureTemplates();

        var builder = BUILDER;
        builder.Clear();
        builder.AppendFormat(mega ? megaComboFormat : comboFormat, comboStreak);
        return builder.ToString();
    }

    /// <summary>콤보 가산 점수 "콤보 보너스 점수 +{0}"(43221). 0 = 콤보 보너스 점수.</summary>
    public static string ComboBonus(int comboBonus)
    {
        EnsureTemplates();

        var builder = BUILDER;
        builder.Clear();
        builder.AppendFormat(comboBonusFormat, comboBonus);
        return builder.ToString();
    }

    private static void EnsureTemplates()
    {
        if (!templatesCached)
            CacheTemplates();
    }
}

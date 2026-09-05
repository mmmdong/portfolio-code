using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class COMMON
{
	private static bool currencyDeductCheck = false;

	public static void IncreaseSummonSkillExp(int exp)
	{
		DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.TotalExp += exp;

		while (LevelUpCheck())
			SummonSkillLevelUp();
	}

	public static bool IncreaseSummonCount(string key, int count, TABLE.Summon summonTableData)
	{
		return DBManager.Instance.playerData._UserData.SummonCountUp(key, count, summonTableData);
	}

	public static int GetCurrentNeedExp()
	{
		var curLv = DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.Lv;
		var needExp = GetNeedExp(curLv);

		//0이면 만렙
		return needExp;
	}

	public static int GetSummonTypeIndex(Define.eSummonType type)
	{
		return (int)type - 1;
	}

	public static int GetSummonParterTypeIndex(Define.eSummonPartnerType type)
	{
		return (int)type - 1;
	}

	public static int GetNeedExp(int level)
	{
		var needExp = TABLE.Summon.SummonList.Find(x => x.GachaType == (int)Define.eSummonType.Skill && x.Level == level).NextLevelExp;

		return needExp;
	}

	private static void SummonSkillLevelUp()
	{
		DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.Lv++;
		GameEventSubject.SendGameEvent(GameEventType.SKILL_SUMMON_LEVEL_UP);
	}

	public static int GetSummonSkillLevel()
	{
		return DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.Lv;
	}

	public static bool CheckCanGetSkillLevelReward()
	{
		var curSkillLevel = GetSummonSkillLevel();
		if (curSkillLevel == 1)
			return false;

		var rewardedLevel = GetSummonRewardedLevel();

		return curSkillLevel - rewardedLevel >= 1;
	}

	public static int GetSummonRewardedLevel()
	{
		return DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.rewardedLv;
	}

	public static void SetSummonRewardedLevel(int level)
	{
		DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.rewardedLv = level;
	}

	private static bool LevelUpCheck()
	{
		if (GetCurrentNeedExp() > 0)
			return GetCurrentNeedExp() <= DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.CurrentExp;
		else
			return false;
	}

	public static void SetAutoSummon(string key, bool isOn)
	{
		DBManager.Instance.playerData._UserData.summonInfo.summonOptionInfoDict[key].autoSummon = isOn;
	}
	public static bool GetAutoSummon(string key)
	{
		if (DBManager.Instance.playerData._UserData.summonInfo.summonOptionInfoDict.TryGetValue(key, out var value))
			return value.autoSummon;
		else
		{
			var newValue = new SummonOptionInfo();
			DBManager.Instance.playerData._UserData.summonInfo.summonOptionInfoDict.Add(key, newValue);
			return newValue.autoSummon;
		}
	}

	public static void SetSkipEffect(string key, bool isOn)
	{
		DBManager.Instance.playerData._UserData.summonInfo.summonOptionInfoDict[key].skipSummonEffect = isOn;
	}
	public static bool GetSkipEffect(string key)
	{
		if (DBManager.Instance.playerData._UserData.summonInfo.summonOptionInfoDict.TryGetValue(key, out var value))
			return value.skipSummonEffect;
		else
		{
			var newValue = new SummonOptionInfo();
			DBManager.Instance.playerData._UserData.summonInfo.summonOptionInfoDict.Add(key, newValue);
			return newValue.skipSummonEffect;
		}
	}

	public static void SetSummonCurrencyDeductCheck(bool isOn)
	{
		currencyDeductCheck = isOn;
	}
	public static bool GetSummonCurrencyDeductCheck()
	{
		return currencyDeductCheck;
	}

	public static bool GetPickUpSummonOpen(string key, out DateTime startTime, out DateTime endTime)
	{
		var curTime = GetNTPTime();
		var summonData = GetSummonInfoData(key);
		startTime = summonData.startDateTime;
		endTime = summonData.endDateTime;

		var result = curTime > startTime && curTime < endTime;

		return result;
	}

	public static void InitPartnerSummonCount(Define.eSummonPartnerType pickUpType)
	{
		DBManager.Instance.playerData._UserData.summonInfo.summonLimitInfoDict[$"{pickUpType}"].Init();
	}

	public static SummonLimitInfoData GetSummonInfoData(string key)
	{
		return DBManager.Instance.playerData._UserData.summonInfo.summonLimitInfoDict[key];
	}

	/// <summary>
	/// 디버깅용 함수
	/// </summary>
	/// <param name="pickUpType"></param>
	public static void PickUpEndDayCountDown(Define.eSummonPartnerType pickUpType, int type = 1, int count = 1)
	{
		var summonInfo = GetSummonInfoData($"{pickUpType}");
		var date = summonInfo.endDateTime;

		switch (type)
		{
			case 1:
				date = date.AddDays(-count);
				break;
			case 2:
				date = date.AddHours(-count);
				break;
			case 3:
				date = date.AddMinutes(-count);
				break;
			case 4:
				date = date.AddSeconds(-count);
				break;
		}
		summonInfo.endDay = $"{date.Year},{date.Month},{date.Day}";
		summonInfo.endDateTime = date;

		GameEventSubject.SendGameEvent(GameEventType.PICK_UP_SUMMON_INIT);
	}

#if UNITY_EDITOR || DEVELOPMENT_BUILD
	#region Development
	public static void SetSummonSkillLevel(int level)
	{
		if (level <= 0) { return; }
		DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.Lv = level;
		DBManager.Instance.playerData._UserData.summonInfo.skillSummonInfo.TotalExp = GetCurrentNeedExp();
		COMMON.UpdateCurQuestInfo();
	}
	#endregion // Development
#endif // UNITY_EDITOR || DEVELOPMENT_BUILD
}

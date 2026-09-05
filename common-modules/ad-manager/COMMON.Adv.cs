using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using TABLE;

public partial class COMMON
{
	public static void ShowAD(Action action)
	{
		AdManager.Instance.ShowRewardedAD(action);
	}

	/// <summary>
	/// 광고 배속 증가
	/// </summary>
	public static void ShowSpeedBuffAd()
	{
		AdManager.Instance.ShowRewardedAD(() =>
		{
			COMMON.Ad_Log("배속 증가 시점");
		});
	}

	public static Dictionary<string, long> GetADBuffInfo() => DBManager.Instance.playerData._UserData.adInfo.adBuffDict;
	public static long GetADBuffInfoLastTime(Define.eADBuffType buffKey) => GetADBuffInfo()[$"{buffKey}"];
	public static bool GetNoAds() => DBManager.Instance.playerData._UserData.noAds;
	public static bool SetNoAds() => DBManager.Instance.playerData._UserData.noAds = true;
	public static Dictionary<int, bool> GetFreeAdsInfo() => DBManager.Instance.playerData._UserData.tutorialInfo.FreeAds;


	public static bool GetFreeAdBuff(Define.eADBuffType buffKey)
	{
		if (DBManager.Instance.playerData._UserData.tutorialInfo.FreeAds.TryGetValue((int)buffKey, out var hasUsedFreeAd))
			return !hasUsedFreeAd;

		return true;
	}

	public static void SetFreeAdBuff(Define.eADBuffType buffKey, bool hasUsedFreeAd)
	{
		DBManager.Instance.playerData._UserData.tutorialInfo.FreeAds[(int)buffKey] = hasUsedFreeAd;
	}

	/// <summary>
	/// 광고 버프 버튼 클릭
	/// </summary>
	public static void ShowStatBuffAD(Define.eADBuffType buffKey)
	{
		if(GetFreeAdBuff(buffKey))
		{
			ApplyAdBuff(buffKey);
			SetFreeAdBuff(buffKey, true);
			return;
		}

		AdManager.Instance.ShowRewardedAD(() =>
		{
			ApplyAdBuff(buffKey);
		});
	}

	private static void ApplyAdBuff(Define.eADBuffType buffKey)
	{
		var endTime = GetCurrentTimeToLong() + (long)TimeSpan.FromMinutes(ADBuff.ADBuffList[0].Time).TotalSeconds;

		DBManager.Instance.playerData._UserData.adInfo.adBuffDict[$"{buffKey}"] = endTime;

		BattleManager.Instance.UpdatePlayerStats();
		GameEventSubject.SendGameEvent(GameEventType.START_AD_BUFF, (int)buffKey);
	}


	/// <summary>
	/// 광고 버프 시작
	/// </summary>
	/// <param name="startTime"></param>
	public static void StartADBuff(Define.eADBuffType buffKey)
	{
		BattleManager.Instance.UpdatePlayerStats();
	}
}

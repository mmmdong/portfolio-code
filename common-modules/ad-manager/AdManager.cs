using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

public class AdManager : Singleton<AdManager>, IInstantiable
{
    public string adUnitId;
    private Action action;
    private CancellationTokenSource adCts;
    private bool isOnAd;
    public override void Init()
    {
        if (MaxSdk.IsInitialized())
            return;

        base.Init();

#if ONESTORE
        adUnitId = "<ONESTORE_REWARDED_AD_UNIT_ID>";
#elif UNITY_EDITOR || UNITY_ANDROID
        adUnitId = "<ANDROID_REWARDED_AD_UNIT_ID>";
#elif UNITY_IOS
        adUnitId = "<IOS_REWARDED_AD_UNIT_ID>";
#else
        adUnitId = "<ANDROID_REWARDED_AD_UNIT_ID>";
#endif

        // Attach callback
        MaxSdkCallbacks.Rewarded.OnAdLoadedEvent += OnRewardedAdLoadedEvent;
        MaxSdkCallbacks.Rewarded.OnAdLoadFailedEvent += OnRewardedAdLoadFailedEvent;
        MaxSdkCallbacks.Rewarded.OnAdDisplayedEvent += OnRewardedAdDisplayedEvent;
        MaxSdkCallbacks.Rewarded.OnAdClickedEvent += OnRewardedAdClickedEvent;
        MaxSdkCallbacks.Rewarded.OnAdRevenuePaidEvent += OnRewardedAdRevenuePaidEvent;
        MaxSdkCallbacks.Rewarded.OnAdHiddenEvent += OnRewardedAdHiddenEvent;
        MaxSdkCallbacks.Rewarded.OnAdDisplayFailedEvent += OnRewardedAdFailedToDisplayEvent;
        MaxSdkCallbacks.Rewarded.OnAdReceivedRewardEvent += OnRewardedAdReceivedRewardEvent;

        // Load the first rewarded ad
        var strArr = new string[] { adUnitId };
        MaxSdk.InitializeSdk(strArr);
        LoadAdAsync().Forget();
    }

    /// <summary>
    /// 보상형 광고 호출 함수
    /// </summary>
    /// <param name="act">광고 시청 후 콜백할 함수</param>
    public void ShowRewardedAD(Action act = null)
    {
        LoadAdAsync().Forget();
        if (!MaxSdk.IsRewardedAdReady(adUnitId))
        {
			COMMON.OnPopUpToast("ALERT_NO_ADS_TXT");
            COMMON.Ad_Log("아직 광고가 로드된게 없음");
        }

        action = act;
        if (!DBManager.Instance.playerData._UserData.noAds)
        {
            MaxSdk.ShowRewardedAd(adUnitId);
            isOnAd = true;
        }
        else
        {
            action?.Invoke();
        }
    }

    private void LoadRewardedAd()
    {
        MaxSdk.LoadRewardedAd(adUnitId);
    }

    private void OnRewardedAdLoadedEvent(string adUnitId, MaxSdkBase.AdInfo adInfo)
    {
        // Reset retry attempt
        COMMON.Ad_Log("보상광고 준비 완료");
    }

    private void OnRewardedAdLoadFailedEvent(string adUnitId, MaxSdkBase.ErrorInfo errorInfo)
    {
        // Rewarded ad failed to load 
        // AppLovin recommends that you retry with exponentially higher delays, up to a maximum delay (in this case 64 seconds).


        //Debug.LogError("보상광고 준비 실패");
        LoadAdAsync().Forget();
    }

    private async UniTask LoadAdAsync()
    {
        if (adCts != null)
        {
            if (!adCts.IsCancellationRequested)
                return;
        }
        adCts = new CancellationTokenSource();

        await UniTask.WaitUntil(() => !isOnAd, cancellationToken: adCts.Token);
        LoadRewardedAd();
        adCts?.Cancel();
    }

    private void OnRewardedAdDisplayedEvent(string adUnitId, MaxSdkBase.AdInfo adInfo)
    {
    }

    private void OnRewardedAdFailedToDisplayEvent(string adUnitId, MaxSdkBase.ErrorInfo errorInfo, MaxSdkBase.AdInfo adInfo)
    {
        // Rewarded ad failed to display. AppLovin recommends that you load the next ad.
        LoadAdAsync().Forget();
    }

    private void OnRewardedAdClickedEvent(string adUnitId, MaxSdkBase.AdInfo adInfo)
    {

    }

    private void OnRewardedAdHiddenEvent(string adUnitId, MaxSdkBase.AdInfo adInfo)
    {
        // Rewarded ad is hidden. Pre-load the next ad
        //Debug.LogError("광고 종료");
        isOnAd = false;
    }

    private void OnRewardedAdReceivedRewardEvent(string adUnitId, MaxSdk.Reward reward, MaxSdkBase.AdInfo adInfo)
    {
        // The rewarded ad displayed and the user should receive the reward.
        action?.Invoke();
    }

    private void OnRewardedAdRevenuePaidEvent(string adUnitId, MaxSdkBase.AdInfo adInfo)
    {
        // Ad revenue paid. Use this callback to track user revenue.

        // Update is called once per frame
        void Update()
        {

        }
    }

    protected override void OnDestroy()
    {
        adCts?.Cancel();
    }
}

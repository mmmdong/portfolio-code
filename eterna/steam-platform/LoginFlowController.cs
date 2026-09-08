using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace REIW
{
    public class LoginFlowController
    {
        private PlatformManager PlatformManager => PlatformManager.Singleton;
        
        public bool IsLoginInProgress => PlatformManager.Singleton.IsInLoginProgress();
        public bool IsAuthLogIned => PlatformManager.Singleton.IsAuthLogIn();
        
        public Task InitializeAsync()
        {
            //TEST Code
           // Debug.LogWarning(ApiKeyManager.TestEncryptedData("J52INW9WBiVOGnkr9SFel3v6ehD8RSnFz6h1Y18S4IWfL85n"));
            return PlatformManager.InitPlatformAsync();
        }

        public ePlatformType GetPlatformType()
        {
            return PlatformManager.Platform.CurrentPlatformType;
        }
        

        public async Task<bool> LoginAsync(eAccountType accountType)
        {
            if (IsLoginInProgress)
            {
                Debug.LogWarning("[LoginFlowController] Login already in progress.");
                return false;
            }
            
            if (accountType == eAccountType.Voyager)
            {
                Debug.LogWarning("[LoginFlowController] Voyager 로그인은 별도의 UI 플로우가 필요합니다.");
                return false;
            }

            var isSuccess  = await PlatformManager.Singleton.AuthenticateAsync(accountType);
            
            LogUtil.LogWarning("[FB or Line LoginAsync] recv Success:" + isSuccess );
            
            if (isSuccess )
            {
                return true;
            }

            ETPlayerPrefs.SignOut(accountType);
            return false;
        }
    }
}

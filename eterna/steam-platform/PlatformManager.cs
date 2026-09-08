using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using REIW.Network;
using UnityEngine;
using UnityEngine.Networking;

#if UNITY_ANDROID
using Google; // GoogleSignIn SDK
#endif

namespace REIW
{
    public class PlatformManager : SingletonBase<PlatformManager>
    {
        [SerializeField] private bool forceAndroidInEditor = false;
        [SerializeField] private bool forceSteamInEditor = false;
        public bool ForceSteamInEditor => forceSteamInEditor;

        private Platform _platform;
        public Platform Platform => _platform;

        private bool isInLoginProgress;
        public bool IsInLoginProgress() => isInLoginProgress;
        public void SetIsInLoginProgress(bool state) => isInLoginProgress = state;

        public async Task InitPlatformAsync()
        {
#if UNITY_EDITOR
            if (forceAndroidInEditor)
            {
                if (forceSteamInEditor)
                    _platform = new SteamPlatform();
                else
                    _platform = new AndroidPlatform(); // Android 동작을 흉내내는 클래스
            }
            else
                _platform = new SteamPlatform();


#elif STEAMWORKS_NET
                _platform = new SteamPlatform();
#elif UNITY_ANDROID
                _platform = new AndroidPlatform();
#elif UNITY_IOS
                _platform = new IOSPlatform();
#endif
            if (_platform == null)
            {
                Debug.LogError("Platform Manager not found");
                throw new InvalidOperationException("[PlatformManager] Failed to create platform instance.");
            }

            await _platform.Init();

            Debug.Log("[PlatformManager] " + _platform.CurrentPlatformType.ToString() + " Platform initialized.");
        }

        public bool IsAuthLogIn()
        {
            if (_platform != null && _platform.LoginState == ePlatformLoginState.VoyagerLoggedIn)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// async/await 기반 인증. true/false만 리턴.
        /// </summary>
        public async UniTask<bool> AuthenticateAsync(eAccountType accountType)
        {
            if (_platform == null)
            {
                Debug.LogError("PlatformManager.AuthenticateAsync called before platform initialization.");
                return false;
            }

            if (isInLoginProgress)
            {
                Debug.LogWarning("[PlatformManager] AuthenticateAsync called while login is already in progress.");
                return false;
            }

            SetIsInLoginProgress(true); // 로그인 프로세스 Ing 임을 나타냄

            bool result = false;
            try
            {
                result = await _platform.Authentication(accountType);

                if (result)
                {
                    LogUtil.Log("[PlatformManager] Login Success");
                    await _platform.PostAuthentication();
                }
                else
                {
                    Debug.LogWarning($"[PlatformManager] Login failed for accountType: {accountType}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[PlatformManager] Login Exception: " + ex);
            }
            finally
            {
                SetIsInLoginProgress(false);
            }

            return result;
        }

        public async UniTask<bool> GetPhotoGranted()
        {
            bool isCheck = _platform.HasPermission(ePermissionType.PhotoLibrary);
            if (!isCheck)
            {
                bool photoGranted = await _platform.EnsurePermission(ePermissionType.PhotoLibrary);

                if (!photoGranted)
                {
                    Debug.Log("사진 라이브러리 권한 없음 -> 설정 페이지 안내");

                    _platform.GetPhotoGranted();
                    return false;
                }
            }

            return true;
        }

        public void SignOut()
        {
            eAccountType accountType = ETPlayerPrefs.GetAccountType();

            if (accountType == eAccountType.Google || accountType == eAccountType.Apple)
            {
                if (Singleton.Platform.LoginState == ePlatformLoginState.VoyagerLoggedIn)
                {
                    Singleton.Platform?.OnFirebaseSignOut();
                }
                else
                {
                    Debug.LogError("Exception : Unknown platform login state.");
                }
            }
            else if (accountType == eAccountType.Voyager)
            {
                ;
            }
            else if (accountType == eAccountType.Line)
            {
                Debug.LogError("not impl");
            }
            else
            {
                Debug.LogError("[PlatformManager] SignOut called before platform initialization.:" + accountType);
            }

            Singleton.ResetAuthState();
            ETPlayerPrefs.SignOut(accountType);
        }

        public void ResetAuthState()
        {
            _platform?.ResetAuthState();
            SetIsInLoginProgress(false);
        }

        public ePlatformType GetPlatformType()
        {
            return _platform != null ? _platform.CurrentPlatformType : ePlatformType.PC;
        }

        //보이저 회원 갱신 토큰 발급 (클라이언트 사용)
        public async Task<PlatformApiResult> RefreshTokenAsync(string refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken))
            {
                return PlatformApiResult.Fail("[RefreshTokenAsync] Voyager account refresh token is empty.", -1);
            }

            // 최종 URL
            string url = PlatformConstClass.GetAuthTokenRefreshUrl();

            // 요청 바디 만들기
            var reqBody = new TokenRefreshRequest
            {
                gameCode = PlatformConstClass.GetEternaGameCode(),
                refreshToken = refreshToken,
                deviceId = PlatformConstClass.GetDeviceId()
            };

            // 3) JSON 직렬화
            string json = JsonUtility.ToJson(reqBody);

            return await SendPostAsync(
                url: url,
                bodyJson: json,
                logTag: "RefreshTokenAsync",
                setVoyagerLoggedInOnSuccess: true);
        }
        
         private async UniTask<PlatformApiGetProfileResult> SendAuthProfilePostAsync(
            string url,
            string header,
            string logTag)
        {
            Debug.LogError("SendAuthProfilePostAsync");
            
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                // req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();

                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("Authorization", $"Bearer {header}");
                
                Debug.Log($"[{logTag}] POST {url}\n Header: {header}");

                try
                {
                    Debug.Log($"[REQ] 1");
                    
                    await req.SendWebRequest().ToUniTask();

                    
                    Debug.Log($"[REQ] 2 :" +req.result);
                    
                    
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        string responseText = req.downloadHandler.text;
                        
                        
                        
                        UserProfileResponse response = JsonUtility.FromJson<UserProfileResponse>(responseText);
                        
                        Debug.Log($"[{logTag}] Success: {req.responseCode}\n{responseText}");

                        return PlatformApiGetProfileResult.Success(response, req.responseCode);
                    }

                    if (req.responseCode == 400)
                    {
                        Platform400Error(req.downloadHandler.text);
                    }
                    else
                    {
                        Debug.LogError(req.downloadHandler.text);
                    }

                    string error = $"[{req.responseCode}] {req.error}\n{req.downloadHandler.text}";
                    Debug.LogError($"[{logTag}] Error: {error}");

                    return PlatformApiGetProfileResult.Fail(error, req.responseCode);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{logTag}] Exception: {ex}");
                    return PlatformApiGetProfileResult.Fail(ex.ToString(), -1);
                }
            }
        }
         
         
        [Serializable]
        public class UserProfile
        {
            public long userno;
            public string userid;
            public string nickname;
            public string usersex;
            public int userstatus;
            public string userbirthday;
            public string secedetag;
            public string userexpiretime;
        }

        [Serializable]
        public class UserProfileResponse
        {
            public UserProfile profile;
            public string message;
            public int resultcode;
        }

        
        
        private async UniTask<PlatformApiProfileUpdate> SendAuthUpdateProfilePostAsync(
            string url,
            string header,
            string bodyJson,
            string logTag)
        {
            Debug.LogError("SendAuthUpdateProfilePostAsync");
            
            
            byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);
            
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();

                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("Authorization", $"Bearer {header}");
                
                Debug.Log($"[{logTag}] POST {url}\n Header: {header}");

                try
                {
                    await req.SendWebRequest().ToUniTask();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        string responseText = req.downloadHandler.text;
                        Debug.Log($"[{logTag}] Success: {req.responseCode}\n{responseText}");

                        return PlatformApiProfileUpdate.Success(responseText, req.responseCode);
                    }

                    if (req.responseCode == 400)
                    {
                        Platform400Error(req.downloadHandler.text);
                    }
                    else
                    {
                        Debug.LogError(req.downloadHandler.text);
                    }

                    string error = $"[{req.responseCode}] {req.error}\n{req.downloadHandler.text}";
                    Debug.LogError($"[{logTag}] Error: {error}");

                    return PlatformApiProfileUpdate.Fail(error, req.responseCode);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{logTag}] Exception: {ex}");
                    return PlatformApiProfileUpdate.Fail(ex.ToString(), -1);
                }
            }
        }

        

        /// <summary>
        /// 
        /// </summary>
        /// <param name="url">엔드포인트 url</param>
        /// <param name="bodyJson">Request Body</param>
        /// <param name="logTag"></param>
        /// <param name="setVoyagerLoggedInOnSuccess"></param>
        /// <returns></returns>
private async UniTask<PlatformApiResult> SendPostAsync(
    string url,
    string bodyJson,
    string logTag,
    bool setVoyagerLoggedInOnSuccess = false)
{
    byte[] bodyRaw = Encoding.UTF8.GetBytes(bodyJson);

    using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
    {
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();

        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Accept", "application/json");

        Debug.Log($"[{logTag}] POST {url}\nBody: {bodyJson}");

        try
        {
            await req.SendWebRequest().ToUniTask();

            if (req.result == UnityWebRequest.Result.Success)
            {
                if (setVoyagerLoggedInOnSuccess && Platform != null)
                {
                    Platform.LoginState = ePlatformLoginState.VoyagerLoggedIn;
                }

                string responseText = req.downloadHandler.text;
                Debug.Log($"[{logTag}] Success: {req.responseCode}\n{responseText}");

                return PlatformApiResult.Success(responseText, req.responseCode);
            }

            if (req.responseCode == 400)
            {
                Platform400Error(req.downloadHandler.text);
            }
            else
            {
                Debug.LogError(req.downloadHandler.text);
            }

            string error = $"[{req.responseCode}] {req.error}\n{req.downloadHandler.text}";
            Debug.LogError($"[{logTag}] Error: {error}");

            return PlatformApiResult.Fail(error, req.responseCode);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{logTag}] Exception: {ex}");
            return PlatformApiResult.Fail(ex.ToString(), -1);
        }
    }
}

/// <summary>
/// 토큰 교환 바디 엔드포인트 전송
/// </summary>
/// <param name="type">소셜 로그인 타입</param>
/// <param name="transferToken">교환 토큰</param>
/// <returns></returns>
public async UniTask<PlatformApiResult> TokenExchangeAsync(eAccountType type, string transferToken)
{
    if (string.IsNullOrEmpty(transferToken))
    {
        return PlatformApiResult.Fail("[TokenExchangeAsync] Empty transfer token.", -1);
    }

    if (!TryBuildTokenExchangeRequest(type, transferToken, out string url, out string reqJson))
    {
        return PlatformApiResult.Fail($"[TokenExchangeAsync] Unsupported account type: {type}", -1);
    }

    Debug.Log($"[TokenExchangeAsync] Type: {type}");

    return await SendPostAsync(
        url: url,
        bodyJson: reqJson,
        logTag: "TokenExchangeAsync",
        setVoyagerLoggedInOnSuccess: true);
}

/// <summary>
/// 스팀에서 받은 토큰으로 플랫폼에 전달할 바디 생성
/// </summary>
/// <param name="type">로그인 소셜 타입</param>
/// <param name="transferToken">바디에 들어갈 토큰</param>
/// <param name="url">플랫폼 엔드포인트</param>
/// <param name="reqJson">엔드포인트로 날릴 바디</param>
/// <returns></returns>
private bool TryBuildTokenExchangeRequest(
    eAccountType type,
    string transferToken,
    out string url,
    out string reqJson)
{
    url = string.Empty;
    reqJson = string.Empty;

    string gameCode = PlatformConstClass.GetEternaGameCode();
    string countryCode = Main.Singleton.ISO3166_ContryCode;
    string deviceId = PlatformConstClass.GetDeviceId();

    switch (type)
    {
        case eAccountType.Google:
        case eAccountType.Apple:
        {
            url = PlatformConstClass.GetFirebaseTokenChangeUrl();

            var body = new FirebaseTokenExchangeRequest
            {
                gameCode = gameCode,
                idToken = transferToken,
                countryCode = countryCode,
                deviceId = deviceId
            };

            reqJson = JsonUtility.ToJson(body);
            
            LogUtil.LogWarning("[TokenExchangeAsync]  - " + reqJson);
            
            return true;
        }

        case eAccountType.Line:
        {
            url = PlatformConstClass.GetLineTokenExchangeUrl();

            var body = new FirebaseTokenExchangeRequest
            {
                gameCode = gameCode,
                idToken = transferToken,
                countryCode = countryCode,
                deviceId = deviceId
            };

            reqJson = JsonUtility.ToJson(body);
            
            LogUtil.LogWarning("[TokenExchangeAsync]  - " + reqJson);
            
            return true;
        }

        case eAccountType.Steam:
        {
            url = PlatformConstClass.GetSteamTokenChangeUrl();

            var body = new SteamTokenExchangeRequest
            {
                gameCode = gameCode,
                SteamTicket = transferToken,
#if STEAMWORKS_NET
                appid = Steamworks.SteamUtils.GetAppID().ToString(),
#endif
                countryCode = countryCode,
                deviceId = deviceId
            };

            reqJson = JsonUtility.ToJson(body);
            
            LogUtil.LogWarning("[TokenExchangeAsync]  - " + reqJson);
            
            return true;
        }

        case eAccountType.Voyager:
        {
            url = PlatformConstClass.GetAuthTokenChangerUrl();

            var body = new VoyagerTokenExchangeRequest
            {
                gameCode = gameCode,
                transferToken = transferToken,
                countryCode = countryCode,
                deviceId = deviceId
            };

            reqJson = JsonUtility.ToJson(body);
            return true;
        }

        default:
            Debug.LogError($"[PlatformManager] Unsupported token exchange type: {type}");
            return false;
    }
}
        
        
        
        public async UniTask<PlatformApiGetProfileResult> AuthProfileAsync(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                return PlatformApiGetProfileResult.Fail("[AuthProfileAsync] Empty access token.", -1);
            }

            string url = PlatformConstClass.GetAuthProfileUrl();

            Debug.Log($"[AuthProfileAsync] Type");

            return await SendAuthProfilePostAsync(
                url: url,
                header: accessToken,
                logTag: "AuthProfileAsync");
        }

        
        public async UniTask<PlatformApiProfileUpdate> AuthUpdateProfileAsync(string accessToken,int year, int month, int day)
        {
            if (string.IsNullOrEmpty(accessToken))
            {
                return PlatformApiProfileUpdate.Fail("[AuthUpdateProfileAsync] Empty access token.", -1);
            }

            string birthday = $"{year}-{month}-{day}";
            if (!TryAuthUpdateProfileRequest(birthday, out string url, out string reqJson))
            {
                return PlatformApiProfileUpdate.Fail($"[TryAuthUpdateProfileRequest] ", -1);
            }

            Debug.Log($"[AuthProfileAsync] Type");

            return await SendAuthUpdateProfilePostAsync(
                url: url,
                header: accessToken,
                bodyJson: reqJson,
                logTag: "AuthUpdateProfileAsync");
        }
        
        
        public bool TryAuthUpdateProfileRequest(string birthday, out string url, out string reqJson)
        {
            url = PlatformConstClass.GetAuthUpdateProfileUrl();

            var body = new VoyagerProfileRequest
            {
                userBirthday = birthday,   // "2000-01-01" 형식
                // userNickname = nickname,
                // userEmail = email

            };

            reqJson = JsonUtility.ToJson(body);
            return true;
        }
        
        

        private void OnDestroy()
        {
            _platform?.Destroy();
        }

        private void Update()
        {
            _platform?.Update();
        }

        [Serializable]
        public class PlatformErrorResponse
        {
            public string message;
            public int resultcode;
        }

        //Platform 400 Error Code
        public void Platform400Error(string error)
        {
            PlatformErrorResponse response = JsonUtility.FromJson<PlatformErrorResponse>(error);

            if (response != null)
            {
                CommonAlertUI.ShowOneButton(("error_" + response.resultcode).ToSystemText());
            }
            else
            {
                Debug.LogError("[PlatformManager] Failed to parse 400 error response: " + error);
            }

            Debug.LogError("Platform400Error :" + error);
        }
    }
}
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Firebase.Auth;
using Line.LineSDK;
using UnityEngine;
using UnityEngine.Android;

#if UNITY_ANDROID
using Firebase.Extensions;
#endif

namespace REIW
{
    public class AndroidPlatform : Platform
    {
        private readonly IPermissionManager permissionManager = new AndroidPermissionManager();
        public override string StoreUrl => $"market://details?id={Application.identifier}";

        // ----------------------------------------------------
        // 초기화
        // ----------------------------------------------------
        public override async Task Init()
        {
            LoginProfiler.MeasureStep("[AndroidPlatform] Initialize start");

            CurrentPlatformType = ePlatformType.ANDROID;
            LoginState = ePlatformLoginState.None;

            await InitializeFirebase();

            LoginProfiler.MeasureStep("[AndroidPlatform] InitializeFirebase end");
        }

        // ----------------------------------------------------
        // GOOGLE 디바이스 로그인 프로세스
        // ----------------------------------------------------
        public override async Task<bool> Authentication(eAccountType accountType)
        {
            LoginState = ePlatformLoginState.None;

            return accountType switch
            {
                eAccountType.Google => await AuthenticateWithFirebase(SignInGoogleAsync, "Google"),
                eAccountType.Apple => await AuthenticateWithFirebase(SignInAppleAsync, "Apple"),
                eAccountType.Line => await SignInLineAsync(),
                eAccountType.Voyager => Fail("여기서 관여 안함."),
                _ => Fail($"[AndroidPlatform] Unsupported account type: {accountType}", warn: true)
            };
        }

        private async Task<bool> AuthenticateWithFirebase(Func<Task<bool>> signInFunc, string providerName)
        {
            if (!firebaseReady)
            {
                Debug.LogError($"[AndroidPlatform] Firebase is not initialized. Provider={providerName}");
                LoginState = ePlatformLoginState.LogFail;
                return false;
            }

            return await signInFunc();
        }
        
        private bool Fail(string message, bool warn = false)
        {
            if (warn) Debug.LogWarning(message);
            else Debug.LogError(message);

            LoginState = ePlatformLoginState.LogFail;
            return false;
        }

        private async Task<bool> SignInAppleAsync()
        {

#if !UNITY_ANDROID
    await Task.Yield();
    return false;
#else
            try
            {
                Debug.Log("[Apple][Android] Start sign in...");
                bool nativeLoginResult = await StartAppleSignInWithFirebaseAsync();
                if (!nativeLoginResult)
                {
                    LoginState = ePlatformLoginState.LogFail;
                    return false;
                }

                LoginState = ePlatformLoginState.FBLoggedIn;
                return await FinalizeFirebaseLoginFromCurrentUserAsync("Apple");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Apple][Android] Exception: {e}");
                LoginState = ePlatformLoginState.LogFail;
                return false;
            }
#endif
        }
        
        private async Task<bool> FinalizeFirebaseLoginFromCurrentUserAsync(string providerName)
        {
            try
            {
                FirebaseUser user = FirebaseAuth.DefaultInstance.CurrentUser;

                // Android 네이티브 로그인 직후 Unity Firebase에 반영이 한 템포 늦을 수 있어서 잠깐 대기
                int retry = 0;
                while (user == null && retry < 20)
                {
                    await Task.Delay(100);
                    user = FirebaseAuth.DefaultInstance.CurrentUser;
                    retry++;
                }

                if (user == null)
                {
                    Debug.LogError($"[Firebase][{providerName}] CurrentUser is null after native sign-in.");
                    LoginState = ePlatformLoginState.LogFail;
                    return false;
                }

                Debug.Log($"[Firebase][{providerName}] Login Success: {user.Email}");

                var token = await user.TokenAsync(true);
                RawPlatformAccessToken = token;
                LoginState = ePlatformLoginState.FBLoggedIn;

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Firebase][{providerName}] Finalize login failed: {ex}");
                LoginState = ePlatformLoginState.LogFail;
                return false;
            }
        }

        // ----------------------------------------------------
        // Google → One Tap → IdToken → Firebase Auth
        // ----------------------------------------------------
        private async Task<bool> SignInGoogleAsync()
        {
#if !UNITY_ANDROID
    await Task.Yield();
    return false;
#else
            try
            {
                Debug.Log("[Google] Requesting ID Token (One Tap)...");

                string idToken = await GetGoogleIdTokenAsync();
                if (string.IsNullOrEmpty(idToken))
                {
                    Debug.LogError("[Google] Failed to get ID Token");
                    LoginState = ePlatformLoginState.LogFail;
                    return false;
                }

                Debug.Log("[Google] Got ID Token → Firebase Login...");
                return await FirebaseLoginWithToken(idToken);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Google] Exception: {e}");
                LoginState = ePlatformLoginState.LogFail;
                return false;
            }
#endif
        }
        
        


        private readonly string[] scopes = { "profile", "openid" };

        public async Task<bool> SignInLineAsync()
        {
#if UNITY_ANDROID || UNITY_IOS
            var tcs = new TaskCompletionSource<bool>();

            LineSDK.Instance.Login(scopes, result =>
            {
                result.Match(
                    value =>
                    {
                        Debug.Log("[Line] Login OK. User: " + value.UserProfile.DisplayName);

                        var currentToken = LineSDK.Instance.CurrentAccessToken;
                        if (currentToken == null)
                        {
                            Debug.LogError("[Line] No access token available.");
                            LoginState = ePlatformLoginState.LogFail;
                            tcs.SetResult(false);
                            return;
                        }

                        Debug.Log("[Line] Current token: " + currentToken.Value);

                        LineAPI.VerifyAccessToken(verifyResult =>
                        {
                            verifyResult.Match(
                                verifyValue =>
                                {
                                    Debug.Log("[Line] Token valid. ChannelId: " + verifyValue.ChannelId);
                                    LoginState = ePlatformLoginState.LineLoggedIn;
                                    SetLineToken(currentToken.Value, "");
                                    tcs.SetResult(true);
                                },
                                error =>
                                {
                                    Debug.LogError(
                                        $"[Line] Token verify failed. Code={error.Code}, Msg={error.Message}");

                                    if (error.Code == 4) // AUTHENTICATION_ERROR
                                    {
                                        LineAPI.RefreshAccessToken(refreshResult =>
                                        {
                                            refreshResult.Match(
                                                token =>
                                                {
                                                    Debug.Log("[Line] Token refreshed. New token: " + token.Value);
                                                    // 새 토큰을 얻었으므로 성공 처리
                                                    SetLineToken(token.Value, token.RefreshToken);
                                                    LoginState = ePlatformLoginState.LineLoggedIn;
                                                    tcs.SetResult(true);
                                                },
                                                refreshError =>
                                                {
                                                    Debug.LogError("[Line] Token refresh failed: " +
                                                                   refreshError.Message);
                                                    LoginState = ePlatformLoginState.LogFail;
                                                    tcs.SetResult(false);
                                                }
                                            );
                                        });
                                    }
                                    else
                                    {
                                        LoginState = ePlatformLoginState.LogFail;
                                        tcs.SetResult(false);
                                    }
                                }
                            );
                        });
                    },
                    error =>
                    {
                        Debug.LogError("[Line] Login failed: " + error.Message);

                        LoginState = ePlatformLoginState.LogFail;
                        tcs.SetResult(false);
                    }
                );
            });

            return await tcs.Task;
#else
        await Task.Yield();
        return false;
#endif
        }


        private AppleSignInCallback _appleSignInCallback;
        private AndroidJavaObject _appleHelper;
        private Task<bool> StartAppleSignInWithFirebaseAsync()
        {
#if UNITY_ANDROID
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                _appleHelper?.Dispose();
                _appleHelper = new AndroidJavaObject("com.eterna.apple.AppleSignInHelper", activity);

                Debug.Log("[Apple][Android] Helper created");

                activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                {
                    Debug.Log("[Apple][Android] Calling startSignIn");

                    _appleSignInCallback = new AppleSignInCallback(
                        (uid, email, displayName, idToken, accessToken) =>
                        {
                            Debug.Log($"[Apple][Android] Native sign-in success uid={uid}, email={email}");
                            _appleSignInCallback = null;
                            tcs.TrySetResult(true);
                        },
                        error =>
                        {
                            Debug.LogError("[Apple][Android] Native sign-in failed: " + error);
                            _appleSignInCallback = null;
                            tcs.TrySetResult(false);
                        }
                    );

                    _appleHelper.Call("startSignIn", _appleSignInCallback);
                }));
            }
            catch (Exception e)
            {
                Debug.LogError("[Apple][Android] Exception before Java callback: " + e);
                _appleSignInCallback = null;
                tcs.TrySetResult(false);
            }

            return tcs.Task;
#else
    return Task.FromResult(false);
#endif
        }
        
        private class AppleSignInCallback : AndroidJavaProxy
        {
            private readonly Action<string, string, string, string, string> _onSuccess;
            private readonly Action<string> _onFail;

            public AppleSignInCallback(
                Action<string, string, string, string, string> onSuccess,
                Action<string> onFail)
                : base("com.eterna.apple.AppleSignInHelper$SignInCallback")
            {
                _onSuccess = onSuccess;
                _onFail = onFail;
            }

            public void onSuccess(string uid, string email, string displayName, string idToken, string accessToken)
            {
                Debug.Log($"[AppleSignInCallback] onSuccess uid={uid}, email={email}, displayName={displayName}");

                if (_onSuccess == null)
                {
                    Debug.LogError("[AppleSignInCallback] _onSuccess is null");
                    return;
                }

                _onSuccess(uid, email, displayName, idToken, accessToken);
            }

            public void onFail(string error)
            {
                Debug.LogError($"[AppleSignInCallback] onFail error={error}");

                if (_onFail == null)
                {
                    Debug.LogError("[AppleSignInCallback] _onFail is null");
                    return;
                }

                _onFail(error);
            }
        }
        
        
        // ----------------------------------------------------
        // One Tap Google 로그인 → ID Token 얻기
        // ----------------------------------------------------
        private Task<string> GetGoogleIdTokenAsync()
        {
#if UNITY_ANDROID
            var tcs = new TaskCompletionSource<string>();

            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            AndroidJavaObject helper =
                new AndroidJavaObject("com.eterna.google.GoogleSignInHelper", activity, googleWebAPI);

            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                helper.Call(
                    "startSignIn",
                    new OneTapSignInCallback(
                        token =>
                        {
                            Debug.Log("[Google Java] Success Token Received");
                            tcs.TrySetResult(token);
                        },
                        err =>
                        {
                            Debug.LogError("[Google Java] Error: " + err);
                            tcs.TrySetResult(null);
                        }
                    )
                );
            }));

            return tcs.Task;
#else
    return Task.FromResult<string>(null);
#endif
        }

        protected override Credential GetFirebaseCredential(string idToken, string accessToken, string nonce)
        {
            return GoogleAuthProvider.GetCredential(idToken, accessToken);
        }

        // 구식 SDK랑 아무 상관 없는, 우리가 직접 정의한 OneTap 콜백
        private class OneTapSignInCallback : AndroidJavaProxy
        {
            private readonly Action<string> _onSuccess;
            private readonly Action<string> _onFail;

            public OneTapSignInCallback(Action<string> onSuccess, Action<string> onFail)
                : base("com.eterna.google.GoogleSignInHelper$SignInCallback") // 자바 인터페이스 이름
            {
                _onSuccess = onSuccess;
                _onFail = onFail;
            }

            // ⚠ 이 메서드 이름은 Java 쪽 인터페이스 메서드 이름이랑 맞추는 거라서 이렇게 둬야 됨
            void onSuccess(string token)
            {
                _onSuccess?.Invoke(token);
            }

            void onFail(string error)
            {
                _onFail?.Invoke(error);
            }
        }

        // ----------------------------------------------------
        // 로그아웃
        // ----------------------------------------------------

        public override void OnFirebaseSignOut()
        {
#if UNITY_ANDROID
            // 1) Firebase 로그아웃
            auth?.SignOut();

            // 2) Google One Tap / Sign-In 로그아웃
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                var helper = new AndroidJavaObject("com.eterna.google.GoogleSignInHelper", activity, googleWebAPI);
                helper.Call("signOut"); // ← 여기서 Java 로그아웃 호출됨!!!
            }
#endif
        }


        // ----------------------------------------------------
        // 후처리
        // ----------------------------------------------------
        public override async Task<bool> PostAuthentication()
        {
            await Task.Delay(100);

            return true;
        }

        public override void Destroy()
        {
        }

        public override void Update()
        {
        }

        public override void FixedUpdate()
        {
        }

        public override async Task InitPermissionPopup()
        {
            var eValue = Enum.GetValues(typeof(ePermissionType));
            foreach (ePermissionType type in eValue)
            {
                if (type == ePermissionType.Speech)
                    continue;

                var result = await EnsurePermission(type);
                Debug.Log($"{type} 권한 결과: {result}");
            }

            #region Useless Code

            /*var notifGranted = await EnsurePermission(ePermissionType.Notification);//.RequestNotificationPermission);
            var micGranted   = await EnsurePermission(ePermissionType.Microphone);//.RequestMicrophonePermission);
            var camGranted   = await EnsurePermission(ePermissionType.Camera);//.RequestCameraPermission);
            var photoGranted = await EnsurePermission(ePermissionType.PhotoLibrary);//.RequestPhotoLibraryPermission);
            var btGranted    = await EnsurePermission(ePermissionType.Bluetooth);//.RequestBluetoothPermission);
            var findLocation   = await EnsurePermission(ePermissionType.FindLocation);//.RequestCameraPermission);

            Debug.Log($"권한 결과: 알림={notifGranted}, 마이크={micGranted}, 카메라={camGranted}, 사진={photoGranted}, 블루투스={btGranted}, 위치 ={findLocation}");*/

            #endregion
        }

        public override bool HasPermission(ePermissionType permission)
        {
            Debug.Log($"권한 체크 : {permission}");
            return permissionManager.HasPermission(permission);
        }

        public bool ShouldShowRequestPermissionRationale(string permission)
        {
            return Permission.ShouldShowRequestPermissionRationale(permission);
        }

        public override void GetPhotoGranted()
        {
            var perm = GetSdkInt() >= 33
                ? AndroidPermissionManager.PHOTO_READ_MEDIA_33UP
                : AndroidPermissionManager.PHOTO_READ_MEDIA_33Down;
            if (!ShouldShowRequestPermissionRationale(perm))
            {
                OpenAppSetting();
            }
        }

        public int GetSdkInt() => ((AndroidPermissionManager)permissionManager).GetSdkInt();

        public override Task<bool> EnsurePermission(ePermissionType permission)
            => permissionManager.EnsurePermission(permission);

        public override void OpenAppSetting()
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using var uriClass = new AndroidJavaClass("android.net.Uri");
            using var uri =
                uriClass.CallStatic<AndroidJavaObject>("fromParts", "package", Application.identifier, null);
            using var intent = new AndroidJavaObject("android.content.Intent",
                "android.settings.APPLICATION_DETAILS_SETTINGS", uri);

            intent.Call<AndroidJavaObject>("addFlags", 0x10000000); // FLAG_ACTIVITY_NEW_TASK
            activity.Call("startActivity", intent);
        }

        public override void PlatformGetStore()
        {
            try
            {
                Application.OpenURL(StoreUrl);
            }
            catch (System.Exception e)
            {
                Debug.LogError("스토어 열기 실패, 웹 브라우저로 시도합니다.");
                // market:// 대신 https:// 주소로 재시도
                Application.OpenURL($"https://play.google.com/store/apps/details?id={Application.identifier}");
            }
        }
    }
    public static class QueryStringParser
    {
        public static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(query))
                return dict;

            if (query.StartsWith("?"))
                query = query.Substring(1);

            var pairs = query.Split('&');
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=');
                if (kv.Length == 2)
                {
                    string key = Uri.UnescapeDataString(kv[0]);
                    string value = Uri.UnescapeDataString(kv[1]);
                    dict[key] = value;
                }
            }

            return dict;
        }
    }

}
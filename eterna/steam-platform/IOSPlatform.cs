using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Firebase.Auth;
using Line.LineSDK;
using UnityEngine;
using Google;

#if UNITY_IOS
using AppleAuth;
using AppleAuth.Enums;
using AppleAuth.Extensions;
using AppleAuth.Interfaces;
using AppleAuth.Native;
#endif

using Google;

namespace REIW
{
    public class IOSPlatform : Platform
    {
        private readonly IPermissionManager permissionManager = new IOSPermissionManager();
        
        private const string appId = "APP_ID"; // TODO : 부여받은 고유 App ID
        public override string StoreUrl => $"itms-apps://itunes.apple.com/app/id{appId}";
        
#if UNITY_IOS
        private AppleAuthManager appleAuthManager = null;
#endif
        
        // ------------------------ ----------------------------
        // 초기화
        // ----------------------------------------------------
        public override async Task Init()
        {
            Debug.Log("[IOSPlatform] Init");

            CurrentPlatformType = ePlatformType.IOS;
            LoginState = ePlatformLoginState.None;

            await InitializeFirebase();
#if UNITY_IOS
            IosSettings.IosSilentAccess();
            if (AppleAuthManager.IsCurrentPlatformSupported)
            {
                var deserializer = new PayloadDeserializer();
                appleAuthManager = new AppleAuthManager(deserializer);
            }
#endif
        }

        // ----------------------------------------------------
        // IOS 디바이스 로그인 프로세스
        // ----------------------------------------------------

        private eAccountType _currentAuthAccountType = eAccountType.Voyager;
        
        public override async Task<bool> Authentication(eAccountType accountType)
        {
            if (!firebaseReady)
            {
                Debug.LogError("[IOSPlatform] Firebase is not initialized.");
                return false;
            }

            LoginState = ePlatformLoginState.None;

            _currentAuthAccountType = accountType;
            
            switch (accountType)
            {
                case eAccountType.Apple:
                    if (firebaseReady)
                    {
                        bool fBLogin = await SignInWithAppleAsync();
                        return fBLogin;    
                    }
                    else
                    {
                        Debug.LogError("[AndroidPlatform] Firebase is not initialized.");
                        return false;
                    }
                    break;
                
                case eAccountType.Google:
                    
                    Debug.LogError("Not implemented");
                    
                    // return await SignInGoogleAsync();
                    return false;
                    
                    break;
                
                case eAccountType.Voyager:
                    Debug.LogError("여기서 관여 안함.");
                    // TemporaryVoyagerLogin();
                    return false;
                case eAccountType.Line:
                    Debug.LogError("Line 클릭.");
                    return await SignInLineAsync();
                    
                default:
                    Debug.LogWarning($"[IOSPlatform] Unsupported account type: {accountType}");
                    LoginState = ePlatformLoginState.LogFail;
                    return false;
            }
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
                                    //SetLineToken(currentToken.Value);
                                    SetLineToken(currentToken.Value, "");
                                    
                                    tcs.SetResult(true);
                                },
                                error =>
                                {
                                    Debug.LogError($"[Line] Token verify failed. Code={error.Code}, Msg={error.Message}");

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
                                                    Debug.LogError("[Line] Token refresh failed: " + refreshError.Message);
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


        // ----------------------------------------------------
        // Apple → Firebase Auth
        // ----------------------------------------------------
#if UNITY_IOS
        private async Task<bool> SignInWithAppleAsync()
        {
            var rawNonce = GenerateRandomString(32);
            var nonce = Sha256Hex(rawNonce);
            
            var loginArgs = new AppleAuthLoginArgs(LoginOptions.IncludeEmail | LoginOptions.IncludeFullName, nonce: nonce);
            var tcs = new TaskCompletionSource<(string, string, string)>();
            
            appleAuthManager.LoginWithAppleId(
                loginArgs,
                credential =>
                {
                    try
                    {
                        var appleIdCredential = credential as IAppleIDCredential;
                        if (appleAuthManager == null || appleIdCredential.IdentityToken.Length == 0)
                        {
                            Debug.LogError("[Apple Id Login] appleIdCredential is null.");
                            tcs.TrySetResult((string.Empty, null, null));
                            return;
                        }

                        var identityToken = Encoding.UTF8.GetString(
                        appleIdCredential.IdentityToken,
                        0,
                        appleIdCredential.IdentityToken.Length);

                        string authorizationCode = null;
                        if (appleIdCredential.AuthorizationCode != null && appleIdCredential.AuthorizationCode.Length > 0)
                        {
                            authorizationCode = Encoding.UTF8.GetString(
                                appleIdCredential.AuthorizationCode,
                                0,
                                appleIdCredential.AuthorizationCode.Length);
                        }

                        Debug.Log("[Apple Id Login] Success callback received.");
                        tcs.TrySetResult((identityToken, authorizationCode, rawNonce));

                        // var appleIdCredential = credential as IAppleIDCredential;
                        // if (appleIdCredential != null)
                        // {
                        //     // Identity token
                        //     var identityToken = Encoding.UTF8.GetString(
                        //         appleIdCredential.IdentityToken,
                        //         0,
                        //         appleIdCredential.IdentityToken.Length);
                        //     
                        //     var authorizationCode = Encoding.UTF8.GetString(
                        //         appleIdCredential.AuthorizationCode,
                        //         0,
                        //         appleIdCredential.AuthorizationCode.Length);
                        //
                        //     Debug.Log($"[Apple Id Login] appleIdCredential identityToken : {identityToken}");
                        //     tcs.TrySetResult((identityToken, authorizationCode, rawNonce));
                        // }
                        // else
                        // {
                        //     Debug.Log($"[Apple Id Login] appleIdCredential is null.");
                        //     tcs.TrySetResult((string.Empty, null, null));
                        // }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[Apple Id Login] Exception: {e}");
                        tcs.TrySetResult((string.Empty, null, null));
                    }
                },
                error =>
                {
                   var authorizationErrorCode = error.GetAuthorizationErrorCode();
                    Debug.LogError($"[Apple Id Login] ErrorCode: {authorizationErrorCode}");
                    Debug.LogError($"[Apple Id Login] Error detail: {error}");
                    Debug.LogError($"[Apple Id Login] Error Code: {error.Code}");
                    Debug.LogError($"[Apple Id Login] Error Desc: {error.LocalizedDescription}");
                    tcs.TrySetResult((string.Empty, null, null));
                });

            var(idToken, accessToken, loginNonce) = await tcs.Task;
            if (string.IsNullOrEmpty(idToken))
            {
                return false;
            }
            else
            {
                Debug.Log("[Apple] Got ID Token → Firebase Login...");
                return await FirebaseLoginWithToken(idToken, accessToken, loginNonce);
            }
        }

        private async Task<bool> SignInWithAppleQuickAsync()
        {
            var quickLoginArgs = new AppleAuthQuickLoginArgs();
            var tcs = new TaskCompletionSource<string>();
            appleAuthManager.QuickLogin(
                quickLoginArgs,
                credential =>
                {
                    var appleIdCredential = credential as IAppleIDCredential;
                    if (appleIdCredential != null)
                    {
                        // Identity token
                        var identityToken = Encoding.UTF8.GetString(
                            appleIdCredential.IdentityToken,
                            0,
                            appleIdCredential.IdentityToken.Length);
        
                        var authorizationCode = Encoding.UTF8.GetString(
                            appleIdCredential.AuthorizationCode,
                            0,
                            appleIdCredential.AuthorizationCode.Length);
                        
                        Debug.Log($"[Apple Quick Login] appleIdCredential identityToken : {identityToken}");
                        tcs.TrySetResult(identityToken);
                    }
                    else
                    {
                        Debug.Log($"[Apple Quick Login] appleIdCredential is null.");
                        tcs.TrySetResult(string.Empty);
                    }
                },
                error =>
                {
                    var authorizationErrorCode = error.GetAuthorizationErrorCode();
                    Debug.LogError($"[Apple Quick Login] Exception: {authorizationErrorCode}");
                    tcs.TrySetResult(string.Empty);
                });
            
            var idToken = await tcs.Task;
            if (string.IsNullOrEmpty(idToken))
            {
                return false;
            }
            else
            {
                Debug.Log("[Apple] Got ID Token → Firebase Login...");
                return await FirebaseLoginWithToken(idToken);
            }
        }
#else
        private async Task<bool> SignInWithAppleAsync()
        {
            await Task.Yield();
            return false;
        }
#endif
        
        protected override Credential GetFirebaseCredential(string idToken, string accessToken, string nonce)
        {
            return OAuthProvider.GetCredential("apple.com", idToken, nonce , accessToken);
        }
        
        public static string GenerateRandomString(int length = 32)
        {
            const string charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-._";
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var chars = bytes.Select(b => charset[b % charset.Length]).ToArray();
            return new string(chars);
        }

        // nonce: rawNonce의 SHA256 해시를 hex string으로 (애플 로그인 요청에 넣는 값)
        public static string Sha256Hex(string input)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2")); // lowercase hex
            return sb.ToString();
        }
        
        // ----------------------------------------------------
        // 로그아웃
        // ----------------------------------------------------

        public override void OnFirebaseSignOut()
        {
            // Firebase 로그아웃
            auth?.SignOut();
        }
        
        // ----------------------------------------------------
        // 후처리
        // ----------------------------------------------------
        public override async Task<bool> PostAuthentication()
        {
            await Task.Delay(10);
            return true;
        }

        public override void Update()
        {
#if UNITY_IOS
            if (appleAuthManager != null)
                appleAuthManager.Update();
#endif
        }
        
        public override async Task InitPermissionPopup()
        {
            Debug.Log("iOS InitPlatformAsync 시작");
            
            var eValue = Enum.GetValues(typeof(ePermissionType));
            foreach (ePermissionType type in eValue)
            {
                var result = await EnsurePermission(type);
                Debug.Log($"{type} 권한 : {result}");
            }
            /*// 알림 → 마이크 → 카메라 → 사진 → 블루투스 순서
            var notifGranted = await EnsurePermission(ePermissionType.Notification);
            Debug.Log("알림 권한: " + notifGranted);

            var micGranted = await EnsurePermission(ePermissionType.Microphone);

            Debug.Log("마이크 권한: " + micGranted);

            var camGranted = await EnsurePermission(ePermissionType.Camera);
            Debug.Log("카메라 권한: " + camGranted);

            var photoGranted = await EnsurePermission(ePermissionType.PhotoLibrary);
            Debug.Log("사진 권한: " + photoGranted);

            var location = await EnsurePermission(ePermissionType.FindLocation);
            Debug.Log("위치 권한: " + location);

            var btGranted = await EnsurePermission(ePermissionType.Bluetooth);
            Debug.Log("블루투스 권한: " + btGranted);*/

            
            Debug.Log("iOS InitPlatformAsync 완료");
        }
        

        public override bool HasPermission(ePermissionType permission)
        {
            return permissionManager.HasPermission(permission);
        }
        public override void GetPhotoGranted()
        {
            if (!HasPermission(ePermissionType.PhotoLibrary))
            {
                OpenAppSetting();
            }
        }
        
        public override Task<bool> EnsurePermission(ePermissionType permission)
            => permissionManager.EnsurePermission(permission);

        public override void OpenAppSetting()
        {
#if UNITY_IOS
                IosSettings.IosOpenSettings();
#endif
        }

        public override void Destroy() { }
        public override void FixedUpdate() { }

        public override void PlatformGetStore()
        {
            Application.OpenURL(StoreUrl);
        }
    }
}

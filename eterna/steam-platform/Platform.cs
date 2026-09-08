using System;
using System.Threading.Tasks;
using Firebase;
using Firebase.Auth;
using Firebase.Messaging;
using Line.LineSDK;
using UnityEngine;

namespace REIW
{
    
    public enum ePlatformType
    {
        None,
        ANDROID = 1,
        IOS,
        Steam,
        PC,
    }
    
    public enum ePlatformLoginState
    {
        None,
        TryLogin,
        FBLoggedIn,
        VoyagerLoggedIn,
        SteamLoggedIn,
        LineLoggedIn,
        
        LogFail,
    }
    public enum eAccountType
    {
        None,
        Google,
        Apple,
        Steam,
        Voyager,
        Line,
    }
    
    public abstract class Platform
    {
        public ePlatformType CurrentPlatformType = ePlatformType.None ;
        public ePlatformLoginState LoginState = ePlatformLoginState.None;
        public string RawPlatformAccessToken { get; protected set; } // Line,FireBase, Steam의 원본 토큰
        public string GameServerAccessToken = string.Empty; // Voyager Platform 서버의 인증 토큰
     
        public virtual string StoreUrl => "";
        
        //Line
        public void SetLineToken(string accessToken, string refreshToken)
        {
            RawPlatformAccessToken = accessToken; // Line Token
            // ETPlayerPrefs.SaveLinAccessToken(RawPlatformAccessToken, refreshToken);
        }
        //>

        protected FirebaseAuth auth;
        protected bool       firebaseReady;

        protected string googleWebAPI = "<GOOGLE_OAUTH_WEB_CLIENT_ID>";
        
        public abstract Task  Init();

        public abstract void Destroy();

        public abstract void Update();
        
        public abstract void FixedUpdate();
        
        //로그인(인증)
        public abstract Task<bool> Authentication(eAccountType accountType);

        //로그인 성공 한다면 후속처리
        public abstract Task<bool> PostAuthentication();
        
        //권한 설정 함수
        public abstract Task<bool> EnsurePermission(ePermissionType permission);

        // 권한 설정 창 오픈
        public abstract void OpenAppSetting();
        
        public virtual void ResetAuthState()
        {
            LoginState = ePlatformLoginState.None;
            RawPlatformAccessToken = string.Empty; // Token Reset
            SetLineToken(string.Empty, string.Empty);
        }
 
        
        public abstract void PlatformGetStore();
        
        public abstract Task InitPermissionPopup();
        public abstract bool HasPermission(ePermissionType permission);
        public abstract void GetPhotoGranted();
        public virtual void  OnFirebaseSignOut()
        {
            
        }
        
        // ----------------------------------------------------
        // Firebase 초기화
        // ----------------------------------------------------
        
        protected async Task InitializeFirebase()
        {
            if (firebaseReady)
            {
                LogUtil.Log("[Platform] Firebase 이미 초기화됨");
                return;
            }
            LogUtil.Log("[Platform] Firebase Init...");

            PushToken =  ETPlayerPrefs.GetString(ETPlayerPrefs.PushTokenKey, string.Empty);
            
            var dep = await FirebaseApp.CheckAndFixDependenciesAsync();
            if (dep != DependencyStatus.Available)
            {
                LogUtil.LogError($"[Firebase] Dependencies not available: {dep}");
                firebaseReady = false;
                return;
            }

            auth = FirebaseAuth.DefaultInstance;
            auth.StateChanged += AuthStateChanged;
            auth.IdTokenChanged += IdTokenChanged; // FirebaseAuth 로그인 사용자 ID 토큰 갱신
            FirebaseMessaging.TokenReceived += OnTokenReceived; // FCM - 기기등록 토큰 발급 및 갱신
            FirebaseMessaging.MessageReceived += OnMessageReceived; 

            firebaseReady = true;
            LogUtil.Log("[Platform] Firebase Ready!");
            
            
        }
        
        public string PushToken { get; private set; }
        public bool IsPushTokenChanged = false;
        
        private void OnTokenReceived(object sender, TokenReceivedEventArgs e)
        {
            LogUtil.Log("[Platform] 새로운 FCM 토큰 수신: " + e.Token);
            String pushTokenKey =  ETPlayerPrefs.GetString(ETPlayerPrefs.PushTokenKey, string.Empty);
            if (pushTokenKey == string.Empty || pushTokenKey != e.Token)
            {
                PushToken = e.Token;
                IsPushTokenChanged = true;
                LogUtil.Log("[Platform] PushToken 임시저장: " + PushToken);    
            }
        }
        
        

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            LogUtil.Log("[Platform] 푸시 메시지 수신!");
            if (e.Message.Notification != null)
            {
                Debug.Log("Title: " + e.Message.Notification.Title);
                Debug.Log("Body: " + e.Message.Notification.Body);
            }

            if (e.Message.Data != null)
            {
                LogUtil.Log("[Platform] Message.Data.Count 도착: " + e.Message.Data.Count);
                
            }
            // 게임 내에서 처리할 로직 (알림 팝업, 보상 지급 등) 추가
        }

        protected virtual void AuthStateChanged(object sender, System.EventArgs eventArgs)
        {
            
        }

        protected virtual void IdTokenChanged(object sender, System.EventArgs eventArgs)
        {
            
        }

        protected virtual Credential GetFirebaseCredential(string idToken, string accessToken, string nonce)
        {
            return null;
        }
        
        // ----------------------------------------------------
        // Firebase Auth with Google
        // ----------------------------------------------------
        protected async Task<bool> FirebaseLoginWithToken( string idToken, string accessToken = null, string nonce = null)
        {
            if (string.IsNullOrEmpty(idToken) ||
                idToken.StartsWith("dummy-", StringComparison.Ordinal))
            {
                Debug.LogError("[Firebase] Skip login, invalid idToken: " + idToken);
                LoginState = ePlatformLoginState.LogFail;
                return false;
            }
            
            try
            {
                var credential = GetFirebaseCredential(idToken, accessToken, nonce);//

                // 🔥 여기서 바로 FirebaseUser가 리턴됨
                FirebaseUser user = await auth.SignInWithCredentialAsync(credential);

                Debug.Log($"[Firebase] Login Success: {user.Email}");

                var token = await user.TokenAsync(true);
                RawPlatformAccessToken = token; // Firebase Token
                LoginState = ePlatformLoginState.FBLoggedIn;
                
                return true;
                
            }
            catch (Exception ex)
            {
                Debug.LogError("[Firebase] Login failed: " + ex);
                LoginState = ePlatformLoginState.LogFail;
            }
            return false;
        }
    }
    
 
    
    
    // ----------------------------------------------------
// 토큰 검증/갱신/재검증 담당 클래스
// ----------------------------------------------------
    public class LineTokenHandler
    {
        public void VerifyToken(TaskCompletionSource<bool> tcs)
        {
            LineAPI.VerifyAccessToken(verifyResult =>
            {
                verifyResult.Match(
                    verifyValue =>
                    {
                        Debug.Log("[Line] Token valid. ChannelId: " + verifyValue.ChannelId);
                        tcs.SetResult(true);
                    },
                    error =>
                    {
                        Debug.LogError($"[Line] Token verify failed. Code={error.Code}, Msg={error.Message}");

                        if (error.Code == 4) // AUTHENTICATION_ERROR
                        {
                            RefreshToken(tcs);
                        }
                        else
                        {
                            tcs.SetResult(false);
                        }
                    }
                );
            });
        }

        private void RefreshToken(TaskCompletionSource<bool> tcs)
        {
            LineAPI.RefreshAccessToken(refreshResult =>
            {
                refreshResult.Match(
                    token =>
                    {
                        Debug.Log("[Line] Token refreshed. New token: " + token.Value);
                        ReVerifyToken(tcs);
                    },
                    error =>
                    {
                        Debug.LogError("[Line] Token refresh failed: " + error.Message);
                        tcs.SetResult(false);
                    }
                );
            });
        }

        private void ReVerifyToken(TaskCompletionSource<bool> tcs)
        {
            LineAPI.VerifyAccessToken(reVerifyResult =>
            {
                reVerifyResult.Match(
                    reVerifyValue =>
                    {
                        Debug.Log("[Line] Re-verified token OK. ChannelId: " + reVerifyValue.ChannelId);
                        tcs.SetResult(true);
                    },
                    reError =>
                    {
                        Debug.LogError("[Line] Re-verify failed: " + reError.Message);
                        tcs.SetResult(false);
                    }
                );
            });
        }

    }
}
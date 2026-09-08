using System;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
#if STEAMWORKS_NET
using System.Linq;
using System.Text;
using Steamworks;
#endif
using UnityEngine;
using UnityEngine.Networking;

namespace REIW
{
    public class SteamPlatform : Platform
    {
#if STEAMWORKS_NET
        public override string StoreUrl => $"https://store.steampowered.com/app/{SteamUtils.GetAppID().ToString()}";
        private HAuthTicket _ticketHandle = HAuthTicket.Invalid;
#endif

/// <summary>
/// 스팀 로그인
/// </summary>
public override async Task Init()
{
#if STEAMWORKS_NET
    CurrentPlatformType = ePlatformType.Steam;
    LoginState = ePlatformLoginState.None;

    Debug.Log("Steam Initializing...");
    await UniTask.WaitUntil(() => SteamManager.Initialized);
    Debug.Log("Steam Initialized!!!");
    var userId = SteamUser.GetSteamID();
    var userName = SteamFriends.GetPersonaName();
    Debug.Log("Logged in as: " + userName + " (ID: " + userId + ")");
    LoginState = ePlatformLoginState.SteamLoggedIn;

    Subscribe();
    RequestSteamTicketAndLogin();
#endif
}


#if STEAMWORKS_NET
/// <summary>
/// 콜백 등록
/// </summary>
private void Subscribe()
{
    // 콜백 등록
    Debug.Log("Subscribe Callbacks...");
    Callback<PersonaStateChange_t>.Create(OnPersonaStateChange);
    Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
    Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
    Callback<GameOverlayActivated_t>.Create(OnGameOverlayActivated);
    Callback<GetTicketForWebApiResponse_t>.Create(OnGetTicketForWebApiResponse);
}

/// <summary>
/// 스팀 로그인 티켓 요청
/// </summary>
private void RequestSteamTicketAndLogin()
{
    if (!SteamManager.Initialized)
    {
        Debug.LogError("Steam not initialized.");
        return;
    }

    // 로그인 시도마다 새 티켓 발급 권장
    _ticketHandle = SteamUser.GetAuthTicketForWebApi(Application.identifier);

    if (_ticketHandle == HAuthTicket.Invalid)
        Debug.LogError("GetAuthTicketForWebApi failed.");
}

        #region Callbacks

        private void OnPersonaStateChange(PersonaStateChange_t data)
        {
            Debug.Log($"[Steam] 친구 상태 변경: {data.m_ulSteamID} -> {data.m_nChangeFlags}");
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            Debug.Log($"[Steam] 로비 초대 수신: {data.m_steamIDLobby}");
        }

        private void OnGameRichPresenceJoinRequested(GameRichPresenceJoinRequested_t data)
        {
            Debug.Log($"Join request from {data.m_steamIDFriend} with connect string: {data.m_rgchConnect}");
        }

        private void OnGameOverlayActivated(GameOverlayActivated_t data)
        {
            Debug.Log($"Game overlay activated: {data.m_bActive}");
        }
        
/// <summary>
/// 로그인 티켓 콜백
/// </summary>
/// <param name="cb"></param>
private void OnGetTicketForWebApiResponse(GetTicketForWebApiResponse_t cb)
{
    if (cb.m_eResult != EResult.k_EResultOK)
    {
        Debug.LogError($"GetTicketForWebApiResponse failed: {cb.m_eResult}");
        return;
    }

    // IMPORTANT: m_rgubTicket 전체가 아니라, m_cubTicket 길이만큼만 사용
    string ticketHex = BitConverter.ToString(cb.m_rgubTicket, 0, cb.m_cubTicket)
        .Replace("-", string.Empty);

    Debug.Log($"Steam ticket ok. size={cb.m_cubTicket} hexLen={ticketHex.Length}");

    RawPlatformAccessToken = ticketHex;
}

        #endregion

#endif

        #region Abstract Methods

        public override void Destroy()
        {
        }

        public override void Update()
        {
        }

        public override void FixedUpdate()
        {
        }

        public override async Task<bool> Authentication(eAccountType accountType)
        {
            return true;
        }

        public override async Task<bool> PostAuthentication()
        {
            return true;
        }

        public override async Task<bool> EnsurePermission(ePermissionType permission)
        {
            return true;
        }

        public override void OpenAppSetting()
        {
        }

        public override void PlatformGetStore()
        {
            Application.OpenURL(StoreUrl);
        }

        public override async Task InitPermissionPopup()
        {
            Debug.LogError("스팀은 퍼미션이 필요한가요?");
        }

        public override bool HasPermission(ePermissionType permission)
        {
            Debug.LogError("스팀은 Permission All true");
            return true;
        }

        public override void GetPhotoGranted()
        {
            
        }

        #endregion
    }
}
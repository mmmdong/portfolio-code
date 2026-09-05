using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using PlayFab.AdminModels;
using System.Linq;
using System;
using System.Threading;
using Cysharp.Threading.Tasks.Triggers;
using System.Numerics;

public class ArenaPopup : UI_PopUp
{
    private enum PopupTxt
    {
        RankText_Me,
        ScoreText_Me,
        NumberText,
        RankingText,
        ShopText,
        SkillText,
        CharacterText,
        ResetBtnText,
        ResetTimeText,
        TitleNameText,

        FreeChallengeCountText,
        ChallengeCountText,
        FreeChallengeCount,
        ChallengeCount,

        ResetCostText,
        ResetNumberText,
    }

    private enum PopupImg
    {
        MyProfile,
    }

    private enum PopupBtn
    {
        DetaillBtn_Me,
        CharacterBtn,
        SkillBtn,
        ResetBtn,
        Shop_Btn,
        Rank_Btn,
    }

    private enum PopupObj
    {
        LoadingPanel,
        LoadingIcon,
        ResetCost,
    }

    private bool refreshing;
    private Item_ArenaUserInfo[] arenaUserInfoArr;
    private CancellationTokenSource timeCkCts = new CancellationTokenSource();
    [SerializeField] private Button ticketShortCutBtn;

    private void Awake()
    {
        Bind<Image>(typeof(PopupImg));
        Bind<Button>(typeof(PopupBtn));
        Bind<TextMeshProUGUI>(typeof(PopupTxt));
        Bind<GameObject>(typeof(PopupObj));

        GetButton((int)PopupBtn.SkillBtn).onClick.AddListener(OnClickSkillSetting);
        GetButton((int)PopupBtn.CharacterBtn).onClick.AddListener(OnClickCharacterSetting);
        GetButton((int)PopupBtn.ResetBtn).onClick.AddListener(() => ResetList().Forget());
        GetButton((int)PopupBtn.Shop_Btn).onClick.AddListener(ShopShortCutBtn);
        GetButton((int)PopupBtn.Rank_Btn).onClick.AddListener(OnClickRankingPopup);

        ticketShortCutBtn.onClick.AddListener(TicketShortCut);

        arenaUserInfoArr = GetComponentsInChildren<Item_ArenaUserInfo>();
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        timeCkCts?.Cancel();
    }


    public override void Setting(params object[] args)
    {
        base.Setting(args);
        DBManager.Instance.playerData._DungeonStageData.dungeonInfo.IsAutoProgressing = false;

        timeCkCts = new CancellationTokenSource();
        PanelResetTimeCk().Forget();

        RefreshUI().Forget();
    }

    private async UniTask RefreshUI()
    {
        Get<GameObject>((int)PopupObj.LoadingPanel).SetActive(true);
        Get<GameObject>((int)PopupObj.LoadingIcon).transform.rotation = UnityEngine.Quaternion.identity;
        Get<GameObject>((int)PopupObj.LoadingIcon).transform.DORotate(UnityEngine.Vector3.forward * 360f, 1f, RotateMode.FastBeyond360).SetEase(Ease.InFlash).SetLoops(-1);

        InitText();

        GameEventSubject.SendGameEvent(GameEventType.ECONOMY_UI_UPDATE, (int)Define.eCURRENCYTYPE.ePVPTicket);
        GameEventSubject.SendGameEvent(GameEventType.ECONOMY_UI_UPDATE, (int)Define.eCURRENCYTYPE.ePVPToken);

        var subTxt = "";
        switch (COMMON.Instance.WeekPVPType)
        {
            case Define.ePVPType.eAttackArena:
                subTxt = TextManager.Instance.GetText("ARENA_CONCEPT_01_TXT");
                break;
            case Define.ePVPType.eDefenseArena:
                subTxt = TextManager.Instance.GetText("ARENA_CONCEPT_02_TXT");
                break;
            case Define.ePVPType.eDamageArena:
                subTxt = TextManager.Instance.GetText("ARENA_CONCEPT_03_TXT");
                break;
            case Define.ePVPType.eSkillArena:
                subTxt = TextManager.Instance.GetText("ARENA_CONCEPT_04_TXT");
                break;
        }

        var split = string.Format(TextManager.Instance.GetText("ARENA_TITLE_TXT"), PlayFabManager.Instance.DungeonSeasonNumber, subTxt).Split(':');

        GetText((int)PopupTxt.TitleNameText).text = split[0];

        await PlayFabManager.Instance.GetMyPVPRankingData();
        if (PlayFabManager.Instance.pvpPlayerList.Count <= 0)
            await PlayFabManager.Instance.GetPVPRankingData();

        SetMyInfo();


        /*if (PlayFabManager.Instance.pvpPlayerList.Count < 4 || PlayFabManager.Instance.pvpRankDataList.Count < 4)
        {
            refreshing = false;
            await ResetList(true);
            return;
        }*/

        for (var i = 0; i < arenaUserInfoArr.Length; i++)
        {
            if (PlayFabManager.Instance.pvpPlayerList.Count <= i)
            {
                arenaUserInfoArr[i].Setting();
                continue;
            }
            if (PlayFabManager.Instance.pvpPlayerList[i]._UserData.userInfo.UserUID == string.Empty)
                PlayFabManager.Instance.pvpPlayerList[i]._UserData.userInfo.UserUID = DBManager.Instance.playerData._UserData.pvpInfo.pvpUIDList[i];

            arenaUserInfoArr[i].Setting(i, PlayFabManager.Instance.pvpPlayerList[i]);
        }
        if (DBManager.Instance.playerData._UserData.pvpInfo.pvpResultList.Count(x => x == 0) == 0)
        {
            refreshing = false;
            await ResetList(true);
            return;
        }

        Get<GameObject>((int)PopupObj.LoadingIcon).transform.DOKill();
        Get<GameObject>((int)PopupObj.LoadingPanel).SetActive(false);
        refreshing = false;
    }

    private void OnClickSkillSetting()
    {
        var view = ViewManager.Instance.OnView(Define.eVIEW.SkillView);
        view.callPopup = () => ViewManager.Instance.OnPopUp(Define.ePOPUP.ArenaPopUp, ViewManager.Instance.GetUIView(Define.eVIEW.MainView), null);
        OnClick_Close();
    }

    private void OnClickCharacterSetting()
    {
        var view = ViewManager.Instance.OnView(Define.eVIEW.CharacterView);
        view.callPopup = () => ViewManager.Instance.OnPopUp(Define.ePOPUP.ArenaPopUp, ViewManager.Instance.GetUIView(Define.eVIEW.MainView), null);
        OnClick_Close();
    }

    private void SetMyInfo()
    {
        var ranking = DBManager.Instance.playerData._UserData.pvpInfo.myRankData.Ranking + 1;
        GetText((int)PopupTxt.RankText_Me).text = string.Format(TextManager.Instance.GetText("RANK_0_TXT"), ranking);
        GetText((int)PopupTxt.ScoreText_Me).text = string.Format(TextManager.Instance.GetText("SCORE_0_TXT"), DBManager.Instance.playerData._UserData.pvpInfo.myRankData.PVPScore);
        var avatarId = DBManager.Instance.playerData._UserData.characterInfo.CharacterList[0].ViewEquipAvataID;
        if (avatarId == -1)
            avatarId = 591101;
        GetImage((int)PopupImg.MyProfile).sprite = ResourcesManager.Instance.GetAvatarPixelImg($"{avatarId}");
    }

    public async UniTask ResetList(bool isForced = false)
    {
        if (!isForced)
        {
            if (refreshing)
                return;

            if (DBManager.Instance.playerData._DungeonStageData.dungeonInfo.freeRefreshCount <= 0)
            {
                ViewManager.Instance.OnPopUp(Define.ePOPUP.AllInfoPopup, null,
                AllInfoPopup.ViewType.YESNO,
                TextManager.Instance.GetText("Notice_TXT"),
                string.Format(TextManager.Instance.GetText("COST_USE_ALARM_TXT"), TextManager.Instance.GetText("EMERALD_TXT"), GetText((int)PopupTxt.ResetCostText).text),
                (System.Action)(() =>
                {
                    ViewManager.Instance.ClosePopUp(Define.ePOPUP.AllInfoPopup);
                    var emeraldCost = BigInteger.Parse(GetText((int)PopupTxt.ResetCostText).text);

                    if (COMMON.Instance.GetEconomyCount(9000002) >= emeraldCost)
                    {
                        COMMON.Instance.EconomyAdd(9000002, -emeraldCost);
                        ResetList(true).Forget();
                    }
                    else
                    {
                        ViewManager.Instance.OnPopUp(Define.ePOPUP.ToastPopUp, null, string.Format(TextManager.Instance.GetText("COST_ALARM_TXT"), TextManager.Instance.GetText("EMERALD_TXT")));
                    }
                }),
                (System.Action)(() => { ViewManager.Instance.ClosePopUp(Define.ePOPUP.AllInfoPopup); })
                , 0, true, Define.eCURRENCYTYPE.eEmerald, GetText((int)PopupTxt.ResetCostText).text);
                return;
            }
            else
            {
                DBManager.Instance.playerData._DungeonStageData.dungeonInfo.freeRefreshCount--;
            }
        }

        refreshing = true;
        DBManager.Instance.playerData._UserData.pvpInfo.Init();
        PlayFabManager.Instance.pvpPlayerList.Clear();
        await RefreshUI();
    }

    private void ShopShortCutBtn()
    {
        OnClick_Close();
        ViewManager.Instance.OnView(Define.eVIEW.ShopView, Define.eShopType.eExchage, Define.eMenuIdx.eType02);
    }

    private void OnClickRankingPopup()
    {
        ViewManager.Instance.OnPopUp(Define.ePOPUP.ArenaRankingPopup, this);
    }

    private async UniTask PanelResetTimeCk()
    {
        while (true)
        {
            var timeUntilOpenday = COMMON.Instance.GetTimeNextWeeklyDunGeonReset();

            if (BattleManager.Instance.dungeonCalTime)
            {
                GetText((int)PopupTxt.ResetTimeText).gameObject.SetActive(false);
            }
            else
            {
                GetText((int)PopupTxt.ResetTimeText).gameObject.SetActive(true);
                GetText((int)PopupTxt.ResetTimeText).text = $"{TextManager.Instance.GetText("SOULFILTER_INIT_TIME_TXT")} {string.Format(TextManager.Instance.GetText("PACKAGE_REFRESH_TIME_TXT"), timeUntilOpenday.Days, timeUntilOpenday.Hours, timeUntilOpenday.Minutes)}";
            }

            await UniTask.Delay(0, cancellationToken: timeCkCts.Token);
        }
    }

    private void InitText()
    {
        GetText((int)PopupTxt.RankingText).text = TextManager.Instance.GetText("RANKING_TXT");
        GetText((int)PopupTxt.ShopText).text = TextManager.Instance.GetText("SHOP_EXCHANGE_HONORMARKET_TXT");
        GetText((int)PopupTxt.CharacterText).text = TextManager.Instance.GetText("CHARACTER_SETTING_TXT");
        GetText((int)PopupTxt.SkillText).text = TextManager.Instance.GetText("SKILL_SETTING_TXT");
        GetText((int)PopupTxt.ResetBtnText).text = TextManager.Instance.GetText("REFRESH_TXT");
        GetText((int)PopupTxt.FreeChallengeCountText).text = $"{TextManager.Instance.GetText("FREE_TXT")} {TextManager.Instance.GetText("CHALLENGE_TXT")}";
        GetText((int)PopupTxt.ChallengeCountText).text = TextManager.Instance.GetText("CHALLENGE_TXT");
        GetText((int)PopupTxt.FreeChallengeCount).text = $"{DBManager.Instance.playerData._DungeonStageData.dungeonInfo.freePVPTicket} / 10";
        GetText((int)PopupTxt.ChallengeCount).text = $"{DBManager.Instance.playerData._DungeonStageData.dungeonInfo.PVPTicket} / 10";

        if (DBManager.Instance.playerData._DungeonStageData.dungeonInfo.freeRefreshCount <= 0)
        {
            Get<GameObject>((int)PopupObj.ResetCost).SetActive(true);
            GetText((int)PopupTxt.ResetNumberText).gameObject.SetActive(false);
        }
        else
        {
            Get<GameObject>((int)PopupObj.ResetCost).SetActive(false);
            GetText((int)PopupTxt.ResetNumberText).gameObject.SetActive(true);
            GetText((int)PopupTxt.ResetNumberText).text = $"{DBManager.Instance.playerData._DungeonStageData.dungeonInfo.freeRefreshCount} / 3";
        }
    }

    private void TicketShortCut()
    {
        OnClick_Close();
        ViewManager.Instance.OnView(Define.eVIEW.ShopView, Define.eShopType.eNormal, Define.eMenuIdx.eType03);

    }
}

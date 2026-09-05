using Cysharp.Threading.Tasks;
using PlayFab;
using PlayFab.SharedModels;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Xml;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class WorldBossPopup : UI_PopUp
{
    enum Sliders
    {
        RaidHPBar
    }
    enum Images
    {

    }
    enum Texts
    {
        TitleText,
        //RewardButtonText,
        HpPercent,

        ScoreTotalText,
        ScoreBowText,
        ScoreSwordText,
        ScoreDaggerText,

        RaidInfoButtonText,
        RankingButtonText,
        DayText,
        ScoreTitleText,

        WipeButtonText,
        WipeButtonCountText,
        EnterButtonText,
        EnterButtonCountText,
        DescriptionTitleText,
    }
    enum GameObjects
    {
        WorldRaidRankingPanel,
        WorldRaidInfoPanel,
        DescriptionTitleImage,
    }
    enum Buttons
    {
        RewardButton,
        ExitButton,

        EnterButton,
        WipeButton,

        RaidInfoButton,
        RankingButton,
    }



    [SerializeField] private Panel_WorldRaidRankReward rankRewardPanel;
    private bool enterBtnOpen, wipeBtnOpen, isInit;

    private void Awake()
    {
        Bind<TextMeshProUGUI>(typeof(Texts));
        Bind<Image>(typeof(Images));
        Bind<Button>(typeof(Buttons));
        Bind<Slider>(typeof(Sliders));
        Bind<GameObject>(typeof(GameObjects));

        ButtonsSetting();
        rankRewardPanel.gameObject.SetActive(false);
    }

    private void Update()
    {
        var curLastTime = PlayManager.Instance.WorldRaidLastTime();
        var nextLastTime = PlayManager.Instance.NextWorldRaidLastTime();
        if (PlayManager.Instance.isWorldBossOpen)
        {
            GetText((int)Texts.ScoreTitleText).text = string.Format(TextManager.Instance.GetText("WROLDRAID_PLAY_TIME_TXT"), curLastTime);

            if (curLastTime.TotalSeconds <= 1)
            {
                ViewManager.Instance.OffPopUp(POPUP.WorldBossPopup);
            }
        }
        else
        {
            GetText((int)Texts.DayText).text = string.Format(TextManager.Instance.GetText("WROLDRAID_OPEN_TIME_TXT"), nextLastTime);

            if (nextLastTime.TotalSeconds <= 1)
            {
                ViewManager.Instance.OffPopUp(POPUP.WorldBossPopup);
            }
        }
    }

    public override void Setting(params object[] args)
    {
        base.Setting(args);
        isInit = false;
        OpenSetting();

        GetText((int)Texts.TitleText).text = TextManager.Instance.GetText("WORLDRAID_MAIN_TXT");

        //정보
        GetText((int)Texts.RaidInfoButtonText).text = TextManager.Instance.GetText("INFO_TXT");
        //랭킹
        GetText((int)Texts.RankingButtonText).text = TextManager.Instance.GetText("RANKING_TXT");

        //유료 입장 버튼 텍스트
        GetText((int)Texts.WipeButtonText).text = TextManager.Instance.GetText("WORLDRAID_ITEM_BUTTEN_TXT");
        //무료 입장 버튼 텍스트
        GetText((int)Texts.EnterButtonText).text = TextManager.Instance.GetText("ENTER_TXT");
        //누적 점수 텍스트
        GetText((int)Texts.DescriptionTitleText).text = TextManager.Instance.GetText("SCORE_TITLE_TXT");

        OnSwitch(true);
        ScoreSetting();

        enterBtnOpen = false;
        wipeBtnOpen = false;
        BossSetting().Forget();
    }

    /// <summary>
    /// 레이드 보스가 오픈됐는지
    /// </summary>
    /// <param name="isOpen"></param>
    private void OpenSetting()
    {
        GetText((int)Texts.DayText).text = string.Format(TextManager.Instance.GetText("WROLDRAID_OPEN_TIME_TXT"), PlayManager.Instance.NextWorldRaidLastTime());
        GetText((int)Texts.ScoreTitleText).text = string.Format(TextManager.Instance.GetText("WROLDRAID_PLAY_TIME_TXT"), PlayManager.Instance.WorldRaidLastTime());
        GetText((int)Texts.EnterButtonCountText).text = $"{DBManager.Instance._StageData.WorldRaidChallengeCnt}/1";
        GetText((int)Texts.WipeButtonCountText).text = $"{DBManager.Instance._ConsumableItemData.ConsumItemList[8099999]}";

        GetText((int)Texts.DayText).gameObject.SetActive(!PlayManager.Instance.isWorldBossOpen);
        Get<GameObject>((int)GameObjects.DescriptionTitleImage).SetActive(PlayManager.Instance.isWorldBossOpen);

        SetActiveButtons(GetButton((int)Buttons.EnterButton), GetText((int)Texts.EnterButtonText), GetText((int)Texts.EnterButtonCountText), false);
        SetActiveButtons(GetButton((int)Buttons.WipeButton), GetText((int)Texts.WipeButtonText), GetText((int)Texts.WipeButtonCountText), false);

    }

    /// <summary>
    /// 서버별 보스세팅
    /// </summary>
    /// <returns></returns>
    private async UniTask BossSetting()
    {
        await PlayFabManager.Instance.GetWorldBossData();

        var serverIdx = 0;
        switch (PlayFabSettings.staticSettings.TitleId)
        {
            case "<PLAYFAB_TITLE_ID_DEV>":     // 개발
                serverIdx = 1;
                break;
            case "<PLAYFAB_TITLE_ID_LIVE_01>": // 라이브 1서버
                serverIdx = 1;
                break;
            case "<PLAYFAB_TITLE_ID_LIVE_02>": // 라이브 2서버
                serverIdx = 2;
                break;
            case "<PLAYFAB_TITLE_ID_LIVE_03>": // 라이브 3서버
                serverIdx = 3;
                break;
        }

        var tableData = STAGE.WorldRaidBattle.WorldRaidBattleList.Find(x => x.diff == DBManager.Instance._StageData.WorldBossLevel && x.Server == serverIdx);
        var fullHp = BigInteger.Parse(tableData.monsterHP);
        var curHp = DBManager.Instance._StageData.WorldBossHp;

        var doubleValue = (float)curHp / (float)fullHp;

        GetSlider((int)Sliders.RaidHPBar).value = doubleValue;

        var lastHpPer = GetSlider((int)Sliders.RaidHPBar).value * 100;

        if (lastHpPer > 0f && lastHpPer <= 1f)
            lastHpPer = 1f;

        GetText((int)Texts.HpPercent).text = $"HP {(int)lastHpPer}%";

        enterBtnOpen = curHp != 0 && PlayManager.Instance.isWorldBossOpen && DBManager.Instance._StageData.WorldRaidChallengeCnt >= 1;
        wipeBtnOpen = curHp != 0 && PlayManager.Instance.isWorldBossOpen && DBManager.Instance._ConsumableItemData.ConsumItemList[8099999] >= 1;

        SetActiveButtons(GetButton((int)Buttons.EnterButton), GetText((int)Texts.EnterButtonText), GetText((int)Texts.EnterButtonCountText), enterBtnOpen);
        SetActiveButtons(GetButton((int)Buttons.WipeButton), GetText((int)Texts.WipeButtonText), GetText((int)Texts.WipeButtonCountText), wipeBtnOpen);
    }

    /// <summary>
    /// 본인 점수 확인
    /// </summary>
    private void ScoreSetting()
    {
        GetText((int)Texts.ScoreTotalText).text = $"{TextManager.Instance.GetText("WORLDRAID_TOTAL_CUMULATIVESCORE_TXT")} : {DBManager.Instance._StageData.WorldRaidScoreTotal}";
        GetText((int)Texts.ScoreBowText).text = $"{TextManager.Instance.GetText("WORLDRAID_BOW_CUMULATIVESCORE_TXT")} : {DBManager.Instance._StageData.WorldRaidScoreBow}";
        GetText((int)Texts.ScoreSwordText).text = $"{TextManager.Instance.GetText("WORLDRAID_SWORD_CUMULATIVESCORE_TXT")} : {DBManager.Instance._StageData.WorldRaidScoreSword}";
        GetText((int)Texts.ScoreDaggerText).text = $"{TextManager.Instance.GetText("WORLDRAID_DAGGER_CUMULATIVESCORE_TXT")} : {DBManager.Instance._StageData.WorldRaidScoreDagger}";
    }

    /// <summary>
    /// 버튼 세팅 -> Setting 함수에서 리스너를 지우고 새로 추가하지 않고 Awake에서 한번만 실행시켜준다.
    /// </summary>
    private void ButtonsSetting()
    {
        GetButton((int)Buttons.ExitButton).onClick.AddListener(OnExit);
        GetButton((int)Buttons.WipeButton).onClick.AddListener(OnEnterWipe);
        GetButton((int)Buttons.EnterButton).onClick.AddListener(OnEnter);
        GetButton((int)Buttons.RaidInfoButton).onClick.AddListener(() => OnSwitch(true));
        GetButton((int)Buttons.RankingButton).onClick.AddListener(() => OnSwitch(false));
    }

    private void OnExit()
    {
        gameObject.SetActive(false);
    }


    /// <summary>
    /// 레이드 입장 - 재화, 하루 도전권 확인
    /// </summary>
    private void OnEnter()
    {
        OnExit();
        PlayManager.Instance.partner.PartnerEnable(false);

        PlayManager.Instance.stageType = eStageType.eWorldRaidDungeon;
        ViewManager.Instance.OnPopUp(POPUP.LoadingPopup, null, true);

        PlayManager.Instance.player.StageChange();
        PlayManager.Instance.player.ChangeState(Define.STATE_TYPE.IDLE);

        ViewManager.Instance.mainView.Setting(eStageType.eWorldRaidDungeon);
        ViewManager.Instance.mainView.SetChapterBossHP(GetSlider((int)Sliders.RaidHPBar).value);

        for (int i = 0; i < MapManager.Instance.transform.childCount; i++)
            MapManager.Instance.transform.GetChild(i).gameObject.SetActive(false);

        MapManager.Instance.transform.GetChild((int)PlayManager.Instance.stageType).gameObject.SetActive(true);

        MapManager.Instance.transform.GetChild(0).gameObject.SetActive(false);

        DBManager.Instance._StageData.WorldRaidDateTime = COMMON.Instance.GetCurrentTimeToLong();

        MapManager.Instance.GetComponentInChildren<Map_WorldRaidDungeon>().isFreeTicketUse = true;
    }

    private void OnEnterWipe()
    {
        OnExit();
        PlayManager.Instance.partner.PartnerEnable(false);

        PlayManager.Instance.stageType = eStageType.eWorldRaidDungeon;
        ViewManager.Instance.OnPopUp(POPUP.LoadingPopup, null, true);

        PlayManager.Instance.player.StageChange();
        PlayManager.Instance.player.ChangeState(Define.STATE_TYPE.IDLE);

        ViewManager.Instance.mainView.Setting(eStageType.eWorldRaidDungeon);
        ViewManager.Instance.mainView.SetChapterBossHP(GetSlider((int)Sliders.RaidHPBar).value);

        for (int i = 0; i < MapManager.Instance.transform.childCount; i++)
            MapManager.Instance.transform.GetChild(i).gameObject.SetActive(false);

        MapManager.Instance.transform.GetChild((int)PlayManager.Instance.stageType).gameObject.SetActive(true);

        MapManager.Instance.transform.GetChild(0).gameObject.SetActive(false);


        DBManager.Instance._StageData.WorldRaidDateTime = COMMON.Instance.GetCurrentTimeToLong();

        MapManager.Instance.GetComponentInChildren<Map_WorldRaidDungeon>().isFreeTicketUse = false;
    }

    public void OnRankReward(int rankType)
    {
        rankRewardPanel.gameObject.SetActive(true);
        rankRewardPanel.Setting(rankType);
    }

    /// <summary>
    /// 랭킹 혹은 던전 정보를 확인하도록 함
    /// </summary>
    /// <param name="isDungeon"></param>
    private void OnSwitch(bool isDungeon)
    {
        Color selectColor = Color.white;
        Color unSelectColor = Color.white;
        ColorUtility.TryParseHtmlString("#FEE6A4", out selectColor);
        ColorUtility.TryParseHtmlString("#C8C1B6", out unSelectColor);


        if (isDungeon)
        {
            GetButton((int)Buttons.RaidInfoButton).GetComponent<Image>().color = Color.white;
            GetText((int)Texts.RaidInfoButtonText).color = selectColor;

            GetButton((int)Buttons.RankingButton).GetComponent<Image>().color = Color.clear;
            GetText((int)Texts.RankingButtonText).color = unSelectColor;
        }
        else
        {
            GetButton((int)Buttons.RankingButton).GetComponent<Image>().color = Color.white;
            GetText((int)Texts.RankingButtonText).color = selectColor;

            GetButton((int)Buttons.RaidInfoButton).GetComponent<Image>().color = Color.clear;
            GetText((int)Texts.RaidInfoButtonText).color = unSelectColor;

            Get<GameObject>((int)GameObjects.WorldRaidRankingPanel).GetComponent<Panel_WorldRaidRanking>().Setting();
        }
        Get<GameObject>((int)GameObjects.WorldRaidInfoPanel).SetActive(isDungeon);
        Get<GameObject>((int)GameObjects.WorldRaidRankingPanel).SetActive(!isDungeon);
    }

    /// <summary>
    /// 버튼 활성/비활성 관리
    /// </summary>
    /// <param name="btn"></param>
    /// <param name="text"></param>
    /// <param name="text2"></param>
    private void SetActiveButtons(Button btn, TextMeshProUGUI text, TextMeshProUGUI text2, bool active)
    {
        btn.interactable = active;
        var color = active ? Color.white : Color.gray;
        btn.targetGraphic.color = color;
        text.color = color;
        text2.color = color;
    }
}


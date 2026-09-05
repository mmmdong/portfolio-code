using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

/// <summary>
/// 스테이지 랭킹 매뉴 타입
/// </summary>
public enum eStageRankingMenuType
{
    /// <summary>
    /// 스테이지 랭킹 패널
    /// </summary>
    eStageRankingPanel = 0,
    /// <summary>
    /// 스테이지 랭킹 보상 패널
    /// </summary>
    eStageRankingRewardPanel,
}

public class StageRankingPopUp : UI_PopUp
{
    enum PopupText
    {
        TitleNameText,

        StageRankingButtonText,
        StageRankingRewardButtonText,

        RankInfoText,
    }

    enum PopupImg
    {
        StageRankingButton,
        StageRankingRewardButton,
    }

    enum PopupBtn
    {
        StageRankingButton,
        StageRankingRewardButton,
    }

    enum PopupGameObj
    {
        TopRankingObj,
        RankListObj,
        RankListRewardObj,
    }

    /// <summary>
    /// 현재 타입
    /// </summary>
    private eStageRankingMenuType curType = eStageRankingMenuType.eStageRankingPanel;
    /// <summary>
    /// 매뉴 패널 리스트
    /// </summary>
    [SerializeField] private List<GameObject> menuPanelList = new List<GameObject>();
    /// <summary>
    /// 스테이지 랭킹 내 주위 유저의 이미지 리스트
    /// </summary>
    [SerializeField] private List<Image> stageRankingImgList = new List<Image>();
    /// <summary>
    /// 나의 스테이지 랭킹 정보
    /// </summary>
    [SerializeField] private Item_DungeonRankTopInfo myStageRankingInfo = null;
    /// <summary>
    /// 나의 스테이지 랭킹 이미지
    /// </summary>
    [SerializeField] private Image myStageRankinImg = null;

    /// <summary>
    /// 스테이지 상위 랭커 리스트
    /// </summary>
    private List<Item_DungeonRankTopInfo> stageTopRankingList = new List<Item_DungeonRankTopInfo>();
    /// <summary>
    /// 스테이지 랭킹 내 주위 유저 리스트
    /// </summary>
    private List<Item_DungeonRankInfo> stageRankingList = new List<Item_DungeonRankInfo>();
    /// <summary>
    /// 스테이지 랭킹 보상 리스트
    /// </summary>
    private List<Item_StageRankingReward> stageRankingRewardList = new List<Item_StageRankingReward>();

    #region 이벤트 등록 & 이벤트 콜 함수
    protected override List<GameEventType> EventList => new List<GameEventType>()
    { 
    };

    public override void HandleGameEvent(GameEvent ge)
    {
        if (!gameObject.activeInHierarchy)
            return;


        switch (ge.eventType)
        {
        }
    }
    #endregion

    private void Awake()
    {
        Bind<TextMeshProUGUI>(typeof(PopupText));
        Bind<Image>(typeof(PopupImg));
        Bind<Button>(typeof(PopupBtn));
        Bind<GameObject>(typeof(PopupGameObj));

        stageTopRankingList = new List<Item_DungeonRankTopInfo>(Get<GameObject>((int)PopupGameObj.TopRankingObj).GetComponentsInChildren<Item_DungeonRankTopInfo>());
        stageRankingList = new List<Item_DungeonRankInfo>(Get<GameObject>((int)PopupGameObj.RankListObj).GetComponentsInChildren<Item_DungeonRankInfo>());

        stageRankingRewardList = new List<Item_StageRankingReward>(Get<GameObject>((int)PopupGameObj.RankListRewardObj).GetComponentsInChildren<Item_StageRankingReward>());

        GetButton((int)PopupBtn.StageRankingButton).onClick.AddListener(() => { OnClick_SetPanelChange(eStageRankingMenuType.eStageRankingPanel); });
        GetButton((int)PopupBtn.StageRankingRewardButton).onClick.AddListener(() => { OnClick_SetPanelChange(eStageRankingMenuType.eStageRankingRewardPanel); });
    }

    protected override void OnDisable()
    {
        base.OnDisable();

        ViewManager.Instance.OffPopUp(POPUP.UserInfoPopUp);
    }

    public override void Setting(params object[] args)
    {
        GetText((int)PopupText.TitleNameText).text = TextManager.Instance.GetText("STAGERANKING_TXT");

        OnClick_SetPanelChange(eStageRankingMenuType.eStageRankingPanel);
    }

    /// <summary>
    /// 셋팅 패널
    /// </summary>
    private void SetPanel()
    {
        switch (curType)
        {
            case eStageRankingMenuType.eStageRankingPanel:
                {
                    SetStageRankingPanel();
                    break;
                }
            case eStageRankingMenuType.eStageRankingRewardPanel:
                {
                    SetStageRankingRewardPanel();
                    break;
                }
        }
    }

    /// <summary>
    /// 스테이지 랭킹 패널 셋팅
    /// </summary>
    private void SetStageRankingPanel()
    {
        GetText((int)PopupText.StageRankingButtonText).text = TextManager.Instance.GetText("RANKING_TXT");
        GetText((int)PopupText.StageRankingRewardButtonText).text = TextManager.Instance.GetText("DAILY_TOP_CHAPTER_REWARD_TXT");

        GetText((int)PopupText.RankInfoText).text = string.Format(TextManager.Instance.GetText("RANKING_DATA_REFRESH_INFO_TXT"), "10");

        //스테이지 탑 랭킹 정보 셋팅
        for(int i = 0; i < stageTopRankingList.Count; i++)
            stageTopRankingList[i].Setting(eStageType.eStage, i);

        //스테이지 랭킹 정보 셋팅
        for (int i = 0; i < stageRankingList.Count; i++)
        {
            stageRankingList[i].Setting(eStageType.eStage, i);
            GetRankingImg(i, PlayFabManager.Instance.StageRankList.Count > i ? PlayFabManager.Instance.StageRankList[i].Ranking : -1);
        }

        SetMyRankingInfo();
    }
    
    /// <summary>
    /// 랭킹 이미지 가져오기
    /// </summary>
    /// <param name="rankVal"></param>
    /// <returns></returns>
    private void GetRankingImg(int listIdx , int rankVal)
    {
        switch(rankVal)
        {
            case 0: //1위
                {
                    stageRankingList[listIdx].SetRankingText(false);
                    stageRankingImgList[listIdx].gameObject.SetActive(true);
                    stageRankingImgList[listIdx].sprite = Resources.Load<SpriteAtlas>("Atlas/Common").GetSprite("Common_Rank01Icon");
                    break;
                }
            case 1: //2위
                {
                    stageRankingList[listIdx].SetRankingText(false);
                    stageRankingImgList[listIdx].gameObject.SetActive(true);
                    stageRankingImgList[listIdx].sprite = Resources.Load<SpriteAtlas>("Atlas/Common").GetSprite("Common_Rank02Icon");
                    break;
                }
            case 2: //3위
                {
                    stageRankingList[listIdx].SetRankingText(false);
                    stageRankingImgList[listIdx].gameObject.SetActive(true);
                    stageRankingImgList[listIdx].sprite = Resources.Load<SpriteAtlas>("Atlas/Common").GetSprite("Common_Rank03Icon");
                    break;
                }
            default:
                {
                    stageRankingList[listIdx].SetRankingText(true);
                    stageRankingImgList[listIdx].gameObject.SetActive(false);
                    break;
                }
        }
    }

    /// <summary>
    /// 내 스테이지 랭킹 정보
    /// </summary>
    private void SetMyRankingInfo()
    {
        myStageRankingInfo.SetMyRanking(eStageType.eStage);

        myStageRankingInfo.SetRankingText(true);
        myStageRankinImg.gameObject.SetActive(false);

        var infolist = PlayFabManager.Instance.StageRankList.Where(item => item.Value.nickName == DBManager.Instance._UserData.Name).ToList();
        if (infolist.Count > 0)
        {
            foreach (var info in infolist)
            {
                switch (info.Value.Ranking)
                {
                    case 0: //1위
                        {
                            myStageRankingInfo.SetRankingText(false);
                            myStageRankinImg.gameObject.SetActive(true);
                            myStageRankinImg.sprite = Resources.Load<SpriteAtlas>("Atlas/Common").GetSprite("Common_Rank01Icon");
                            break;
                        }
                    case 1: //2위
                        {
                            myStageRankingInfo.SetRankingText(false);
                            myStageRankinImg.gameObject.SetActive(true);
                            myStageRankinImg.sprite = Resources.Load<SpriteAtlas>("Atlas/Common").GetSprite("Common_Rank02Icon");
                            break;
                        }
                    case 2: //3위
                        {
                            myStageRankingInfo.SetRankingText(false);
                            myStageRankinImg.gameObject.SetActive(true);
                            myStageRankinImg.sprite = Resources.Load<SpriteAtlas>("Atlas/Common").GetSprite("Common_Rank03Icon");
                            break;
                        }
                    default:
                        {
                            myStageRankingInfo.SetRankingText(true);
                            myStageRankinImg.gameObject.SetActive(false);
                            break;
                        }
                }

                break;
            }
        }
    }

    /// <summary>
    /// 스테이지 랭킹 보상 패널 셋팅
    /// </summary>
    private void SetStageRankingRewardPanel()
    {
        for (int i = 0; i < stageRankingRewardList.Count; i++)
            stageRankingRewardList[i].Setting(i);
    }

    /// <summary>
    /// 버튼 활성화 여부에 따른 컬러값 가져오기
    /// </summary>
    /// <param name="enable"></param>
    /// <returns></returns>
    private Color GetBtnTextColor(bool enable)
    {
        var color = new Color();
        if (enable)
        {
            ColorUtility.TryParseHtmlString("#FEE6A4", out color);
            return color;
        }
        else
        {
            ColorUtility.TryParseHtmlString("#C8C1B6", out color);
            return color;
        }
    }

    /// <summary>
    /// 매뉴 클릭 이벤트를 받아 패널 교체 
    /// </summary>
    /// <param name="menuType"></param>
    public void OnClick_SetPanelChange(eStageRankingMenuType menuType)
    {
        curType = menuType;

        menuPanelList[(int)eStageRankingMenuType.eStageRankingPanel].gameObject.SetActive(curType == eStageRankingMenuType.eStageRankingPanel);
        GetImage((int)PopupBtn.StageRankingButton).color = new Color(1, 1, 1, curType == eStageRankingMenuType.eStageRankingPanel ? 1 : 0);
        GetText((int)PopupText.StageRankingButtonText).color = GetBtnTextColor(curType == eStageRankingMenuType.eStageRankingPanel);

        menuPanelList[(int)eStageRankingMenuType.eStageRankingRewardPanel].gameObject.SetActive(curType == eStageRankingMenuType.eStageRankingRewardPanel);
        GetImage((int)PopupBtn.StageRankingRewardButton).color = new Color(1, 1, 1, curType == eStageRankingMenuType.eStageRankingRewardPanel ? 1 : 0);
        GetText((int)PopupText.StageRankingRewardButtonText).color = GetBtnTextColor(curType == eStageRankingMenuType.eStageRankingRewardPanel);

        SetPanel();
    }

}

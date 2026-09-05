
using System;
using System.Collections.Generic;
using System.Linq;
using Revolution.Core;
using UnityEngine;
using UnityEngine.UI;
using static COMMON;
using static Define;

public class ViewManager : SceneSingleton<ViewManager>
{
    #region 변수

    private int mainViewIndex = (int)eVIEW.Main;

    /// <summary>
    /// 팝업 UI에 사용되는 정렬 오더 값 
    /// </summary>
    public int order = 4;
    /// <summary>
    /// 현재 열려 있는 View UID 값
    /// </summary>
    [HideInInspector] public int curViewUID;
    /// <summary>
    /// 조이스틱
    /// </summary>
    //[HideInInspector] public UI_JoyStick joyStick;
    /// <summary>
    /// 캐릭터 초상화
    /// </summary>

    /// <summary>
    /// 뷰 Dictionary 변수
    /// </summary>
    [SerializeField] private List<UI_View> viewList = new List<UI_View>();
    [SerializeField] private GameObject mainMenuPanel = null;
    //[SerializeField] private List<UI_View> mainMenuList = new List<UI_View>();

    /// <summary>
    /// 팝업 Dictionary 변수
    /// </summary>
    private Dictionary<ePopup, UI_PopUp> popUpCache = new Dictionary<ePopup, UI_PopUp>();

    /// <summary>
    /// 메인씬 세이프 에어리어 오브젝트 변수
    /// </summary>
    [HideInInspector] public RectTransform mainSceneSafeArea = null;


    #endregion  // 변수

    #region Lift Cycle

    protected override void Awake()
    {
        base.Awake();

        // safe area
        mainSceneSafeArea = GameObject.Find("SafeArea")?.GetComponent<RectTransform>();

        //joyStick = GetComponentInChildren<UI_JoyStick>(true);
        //joyStick.StickOnOff(false);
    }

    private void OnEnable()
    {
        GameEventSubject.RegisterHandler(GameEventType.GAME_EXIT_EVENT, GameExit);
    }

    private void OnDisable()
    {
        GameEventSubject.UnregisterHandler(GameEventType.GAME_EXIT_EVENT, GameExit);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        popUpCache.Clear();
    }

    /// <summary>
    /// 게임 종료 이벤트 함수
    /// </summary>
    /// <param name="ge"></param>
    private void GameExit(GameEvent ge)
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(); // 어플리케이션 종료
#endif
    }

    #endregion  // Lift Cycle

    #region View 관련

    /// <summary>
    /// 모든 View를 검사하여 하나의 View라도 열려있는지 체크
    /// </summary>
    /// <param name="viewUID">확인 하려는 View UID</param>
    /// <returns></returns>
    public bool IsAnyViewOpen_ExcludingMainView()
    {
        foreach (eVIEW view in Enum.GetValues(typeof(eVIEW)))
        {
            if (view != eVIEW.Main && GetViewState(view))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 특정 View의 상태 정보 가져오기
    /// </summary>
    /// <param name="viewUID">확인 하려는 View UID</param>
    /// <returns></returns>
    public bool GetViewState(eVIEW viewUID)
    {
        if (viewList.Count >= (int)viewUID)
            return viewList[(int)viewUID].gameObject.activeInHierarchy;

        return false;
    }

    /// <summary>
    /// 특정 View 게임오브젝트 가져오기
    /// </summary>
    /// <param name="viewUID">가져오려는 View UID</param>
    /// <returns></returns>
    public GameObject GetView(eVIEW viewUID)
    {
        if (viewList.Count >= (int)viewUID)
            return viewList[(int)viewUID].gameObject;

        return null;
    }

    public UI_View GetCurView()
    {
        return viewList[curViewUID];
        //return viewList[(int)eVIEW.Main];
    }

    /// <summary>
    /// 특정 View 가져오기
    /// </summary>
    /// <param name="viewUID"></param>
    /// <returns></returns>
    public UI_View GetUIView(eVIEW viewUID)
    {
        return viewList[(int)viewUID];
    }

    /// <summary>
    /// View 닫기
    /// </summary>
    /// <param name="viewUID"></param>
    public void SetOffView(eVIEW viewType)
    {
        int viewIndex = (int)viewType; 
        if (viewIndex <= viewList.Count)
        {
            viewList[viewIndex]?.gameObject?.SetActive(false);
            //View를 닫을 때 curViewUID 값을 MainUID 값으로 변경 해 준다
            curViewUID = (int)Define.eVIEW.Main;
            if (EffectManager.Instance != null)
            {
                EffectManager.Instance.attractCurrencyEffectEnable = true;
            }
            GameEventSubject.SendGameEvent(GameEventType.ATTRACTCURRENCYEFFECT_ENABLE_EVENT);
        }
        else
            Debug.LogError($"viewDic 에 {viewType.ToString()}가 등록되지 않았다");

    }

    //TODO: Mr.Song - int -> enum.
    public UI_View OnViewMainMenu(eVIEW viewType, int subViewType = 0)
    //public UI_View OnViewMainMenu<T>(eVIEW mainViewType, T subViewType) where T : Enum
    {
        int viewIndex = (int)viewType;
        bool isMainViewType = (viewType == eVIEW.Main);
        curViewUID = viewIndex;
        for (int i = 0; i < viewList.Count; i++)
        {
            if (mainViewIndex == i) { continue; }
            viewList[i]?.gameObject?.SetActive(viewIndex == i);
        }

        // main menu
        mainMenuPanel.gameObject.SetActive(!isMainViewType);

        // setting
        if (viewList[viewIndex] == null) { return null; }
        var view = viewList[viewIndex];
        view.viewID = viewType;
		//view.curSubViewIdx.Value = subViewType;
		if (subViewType != 0 || view.curSubViewIdx.Value <= 0)
		{
			view.curSubViewIdx.Value = subViewType;
		}
        view.Init();
        view.RefreshUI();
        //view.subViewID = (int)subViewType;
        //view.subViewID = Convert.ToInt32(subViewType);

        // etc
        EffectManager.Instance.attractCurrencyEffectEnable = isMainViewType;
        GameEventSubject.SendGameEvent(GameEventType.ATTRACTCURRENCYEFFECT_ENABLE_EVENT);
		GameEventSubject.SendGameEvent(GameEventType.PARTNER_NOTI);
		GameEventSubject.SendGameEvent(GameEventType.CHARACTER_NOTI);
		return view;
    }

    /// <summary>
    /// 현재 열려있는 뷰를 끈다
    /// </summary>
    public void OffCurView()
    {
        viewList[curViewUID].gameObject.SetActive(false);
        curViewUID = (int)eVIEW.Main;
        EffectManager.Instance.attractCurrencyEffectEnable = true;
        GameEventSubject.SendGameEvent(GameEventType.ATTRACTCURRENCYEFFECT_ENABLE_EVENT);
    }

    /// <summary>
    /// 메인뷰 캔버스 활성화 셋팅
    /// </summary>
    /// <param name="enable"></param>
    public void SetMainViewCanvasEnable(bool enable)
    {
        var view = viewList[(int)eVIEW.Main] as MainView;
        if (view == null) { return; }
        view.enabled = enable;
    }

    #endregion  // View 관련

    #region PopUp 관련

    public enum ePopupLayer
    {
        LayerMiddle = 0,
        LayerTop = 1,
        LayerBottom = 2, 
    }

    public UI_PopUp OnPopUp(ePopup popupUID, ePopupLayer popupLayer)
    {
        var view = viewList[(int)eVIEW.Main] as MainView;
        switch (popupLayer)
        {
            case ePopupLayer.LayerMiddle:
                return OnPopUp(popupUID, view.LayerMiddle);
            case ePopupLayer.LayerTop:
                return OnPopUp(popupUID, view.LayerTop);
            case ePopupLayer.LayerBottom:
                return OnPopUp(popupUID, view.LayerBottom);
        }

        return OnPopUp(popupUID);
    }

    /// <summary>
    /// PopUp 관련 UI 오픈
    /// </summary>
    /// <param name="popupUID">열고자 하는 popup UID</param>
    /// <param name="masterUI">부모 popupUI</param>
    /// <param name="args">UI 열때 필요한 정보값</param>
    public UI_PopUp OnPopUp(ePopup popupUID, UI masterUI = null) //, params object[] args)
    {
        //TODO: Mr.Song - 현재 강제로 Main View 에 띄우도록 해놓은 상태,
        // Growth, Partner, Character, Content, Shop
        // 이후 위 eVIEW 들이 Panel 형태로 변경시 GetUIView(eVIEW.Main) -> GetCurView() 
        if (masterUI == null)
        {
            var view = viewList[(int)eVIEW.Main] as MainView;
            masterUI = view.LayerMiddle;
        }

        //if (masterUI == null) { masterUI = GetCurView(); }
        if (!popUpCache.TryGetValue(popupUID, out var popup))
        {
            var path = $"{ResPath.UI_POPUP}/{popupUID}";
            var popupObj = Resources.Load<UI_PopUp>(path);
            if (popupObj != null)
            {
                //popUpCache.Add(popupUID, Instantiate(popupObj));
                popUpCache.Add(popupUID, Instantiate(popupObj, masterUI.transform));
                popup = popUpCache[popupUID];
            }
            else
            {
                Debug.LogError($"팝업 생성 실패 : {path}");
                return null;
            }
        }

        popup.transform.SetAsLastSibling();
        popup.gameObject.SetActive(true);
        popup.sortingOrder = ++order;
        popup.masterUI = masterUI;
        //popup.Setting(args);
        popup.Init();
        return popup;
    }

    public PopUp_Toast OnPopUpToast(string msg, float time = 1.5f)
    {
        var popup = OnPopUp(ePopup.PopUp_Toast, ViewManager.ePopupLayer.LayerTop) as PopUp_Toast;
        popup.SetData(msg, time);
        return popup;
    }

    /// <summary>
    /// PopUp 관련 UI 오픈 직접 선택 (이미 할달되어있는 (변수로) popup을 열때
    /// </summary>
    /// <param name="popupUID">열고자 하는 popup UID</param>
    /// <param name="masterUI">부모 popupUI</param>
    /// <param name="args">UI 열때 필요한 정보값</param> 
    public void OnPopUp_Direct(ePopup popupUID, UI_PopUp popup, UI masterUI = null)
    {
        if (popup == null) { return; }
        if (!popUpCache.TryGetValue(popupUID, out var cachedPopup))
        {
            popUpCache.Add(popupUID, popup);
            cachedPopup = popup;
        }
        if (!cachedPopup.gameObject.activeInHierarchy) { cachedPopup.gameObject.SetActive(true); }
        cachedPopup.sortingOrder = ++order;
        cachedPopup.masterUI = masterUI;
        //cachedPopup.Setting(args);
        cachedPopup.Init();
    }

    /// <summary>
    /// 특정 팝업 닫기
    /// </summary>
    /// <param name="popupUID"></param>
    public void OffPopUp(ePopup popupUID)
    {
        if (popUpCache.TryGetValue(popupUID, out var popup))
        {
            if (popup.gameObject.activeInHierarchy)
                popup.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// 특정 팝업의 Close 함수 호출
    /// /// </summary>
    /// <param name="popupUID"></param>
    public void ClosePopUp(ePopup popupUID)
    {
        if (popUpCache.TryGetValue(popupUID, out var popup))
        {
            if (popup.gameObject.activeInHierarchy)
            {
                popup.OnClick_Close();
            }
        }
    }

    /// <summary>
    /// 모든 팝업 닫기 (로딩 팝업 제외)
    /// </summary>
    public void ClosePopUpAll()
    {
        foreach (var popup in popUpCache)
        {
            //if (popup.Key == ePopup.LoadingPopup) { continue; }
            if (popup.Value.gameObject.activeInHierarchy)
            {
                popup.Value.OnClick_Close();
            }
        }
    }

    /// <summary>
    /// 특정 팝업이 살아 있는지 체크
    /// </summary>
    /// <param name="popupUID">팝업 UID</param>
    /// <returns></returns>
    public bool CurPopUpEnable(ePopup popupUID)
    {
        if (popUpCache.TryGetValue(popupUID, out var popup))
            return popup.gameObject.activeInHierarchy;

        return false;
    }

    /// <summary>
    /// 팝업 클래스 가져오기
    /// </summary>
    /// <param name="popupUID"></param>
    /// <returns></returns>
    public UI_PopUp GetPopUp(ePopup popupUID)
    {
        if (popUpCache.TryGetValue(popupUID, out var popup))
            return popup;

        return null;
    }

    /// <summary>
    /// 뒤로가기가 적용 안되는 팝업 체크
    /// </summary>
    /// <returns></returns> 
    public bool NotEscapePopUpCk(ePopup popup)
    {
        //if (popup == ePOPUP.NamePopUp || popup == ePOPUP.SummonResultPopUp || popup == ePOPUP.PowersavingmodePopUp ||
        //    popup == ePopup.PopUp_Toast || popup == ePOPUP.BattleResultClearPopup || popup == ePOPUP.BattleResultFailPopUp ||
        //    popup == ePOPUP.ShouterPopUp || popup == ePOPUP.OfflineCompensationPopUp || popup == ePOPUP.BossPopUp || popup == ePOPUP.ChangeInfoListPopUp
        //     || popup == ePOPUP.BattleClearPopUp || popup == ePOPUP.BattleFailPopUp || popup == ePOPUP.TutorialFocusRectPopup)
        //    return true;

        if (popup == ePopup.PopUp_Toast || popup == ePopup.PopUp_Shouter || popup == ePopup.TutorialFocusRectPopup
        || popup == ePopup.PopUp_ChangeName || popup == ePopup.PopUp_SummonResult || popup == ePopup.PopUp_Battle_Loading
        || popup == ePopup.Power_Saving_Mode_PopUp || popup == ePopup.Popup_Login || popup == ePopup.PopUp_Loading
        || popup == ePopup.PopUp_Battle_Loading || popup == ePopup.PopUp_Battle_Clear_Result || popup == ePopup.PopUp_Battle_Fail_Result
        || popup == ePopup.PopUp_Dungeon_Clear_Result || popup == ePopup.PopUp_Dungeon_Fail_Result)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 열려있는 팝업 체크하는 것중 적용 안되는것들
    /// </summary>
    public bool ShouldExcludePopupOpenCheck(ePopup popup)
    {
        //if (popup == ePopup.PopUp_Toast || popup == ePOPUP.ShouterPopUp) { return true; }
        if (popup == ePopup.PopUp_Toast  || popup == ePopup.PopUp_Shouter) { return true; }
        return false;
    }

    public bool IsMainViewActive()
    {
        if (IsAnyPopupOpen() == false
        && IsAnyViewOpen_ExcludingMainView() == false
        && TutorialManager.Instance.IsInProgress() == false)
            return true;

        return false;
    }


    /// <summary>
    /// 어떤 팝업이 열려있는지 체크
    /// </summary>
    public bool IsAnyPopupOpen(ePopup excludePopup = ePopup.None)
    {
        bool popupck = false;
        var popupList = popUpCache.OrderByDescending(x => x.Value.sortingOrder).ToList();
        foreach (var popup in popupList)
        {
            if (ShouldExcludePopupOpenCheck(popup.Key))
                continue;

            if (popup.Key == excludePopup)
                continue;

            if (popup.Value.gameObject.activeInHierarchy)
            {
                popupck = true;
                break;
            }
        }
        return popupck;
    }

    /// <summary>
    /// 뒤로가기 버튼 클릭시 호출 함수
    /// </summary>
    public void EscapeBtnClick()
    {
        if (TutorialManager.Instance.IsInProgress())
            return;

        bool popupck = false;
        //정렬 순서가 높은 순으로 리스트화 하여 가져온다
        var popupList = popUpCache.OrderByDescending(x => x.Value.sortingOrder).ToList();
        foreach (var popup in popupList)
        {
            if (!NotEscapePopUpCk(popup.Key))
            {
                if (popup.Value.gameObject.activeInHierarchy)
                {
                    popup.Value.OnClick_Close();
                    popupck = true;
                    break;
                }
            }
        }

        if (!popupck && curViewUID != (int)Define.eVIEW.Main)
            OffCurView();
        else if (!popupck)
        {
            //OnPopUp(Define.ePOPUP.AllInfoPopup, null,
            //AllInfoPopup.ViewType.YESNO,
            //TextManager.Instance.GetText("Notice_TXT"),
            //TextManager.Instance.GetText("EXITPOPUP_MSG_TXT"),
            //(Action)(() =>
            //{
            //    Application.Quit();
            //}),
            //(Action)(() => { ViewManager.Instance.ClosePopUp(Define.ePOPUP.AllInfoPopup); }));
        }
    }
 
    #endregion

    #region KAMI 테스트

    //TODO: Mr.Song - 이후 아래 변수/함수 들을 제거 혹은 다른 class 로 이동 고려.

    /// <summary>
    /// 메인씬 세이프 에어리어 오브젝트 변수
    /// </summary>
    [SerializeField] private List<GameObject> useSkillEffect = null;



    /// <summary>
    /// 메인씬 세이프 에어리어 오브젝트 변수
    /// </summary>
    public RectTransform uiEffectObj = null;

    //kami 테스트
    public Image skillBgImg = null;
    public Image skillCharacterImg = null;
    public Animator ani = null;
    public float scalUnit = 0.015f;
    public float posPanddingX = -50f;
    public float posPanddingY = -10f;
/*

    private ePlayerClass pc = ePlayerClass.Healer;
    //KAMI 테스트
    public void SkillImageStart(ePlayerClass playerclass)
    {
        if (GameManager.Instance.SaveModeEnableCk || !DBManager.Instance.playerData._UserData.settingInfo.bSkillIllustCk)
            return;

        //위치 초기
        pc = playerclass;

        ImageChange((int)pc);
        for (int i = 0; i < useSkillEffect.Count; i++)
        {
            useSkillEffect[i].SetActive(false);
            useSkillEffect[i].SetActive(true);
        }
        ani.SetTrigger("AniStart");
    }

    public void ImageChange(int classId)
    {
        //skillCharacterImg.sprite = ResourcesManager.Instance.GetImg(string.Format("SkillIllustration\\{0}", 2050));
        var chaimg = "";
        var bgimg = "";

        var avatarId = DBManager.Instance.playerData._UserData.characterInfo.CharacterList[classId - 1].ViewEquipAvataID;
        if (avatarId != -1)
        {
            chaimg = $"I_{pc}\\{avatarId}";
        }
        else
        {
            switch (pc)
            {
                case ePlayerClass.SwordMan:
                    {
                        chaimg = "1050_Skill";
                        break;
                    }
                case ePlayerClass.Archer:
                    {
                        chaimg = "2050_Skill";
                        break;
                    }
                case ePlayerClass.Mage:
                    {
                        chaimg = "3050_Skill";
                        break;
                    }
                case ePlayerClass.Healer:
                    {
                        chaimg = "4050_Skill";
                        break;
                    }
            }
        }

        switch (pc)
        {
            case ePlayerClass.SwordMan:
                {
                    bgimg = "1050_Skill_BG";
                    break;
                }
            case ePlayerClass.Archer:
                {
                    bgimg = "2050_SKILL_BG";
                    break;
                }
            case ePlayerClass.Mage:
                {
                    bgimg = "3050_Skill_BG";
                    break;
                }
            case ePlayerClass.Healer:
                {
                    bgimg = "4050_Skill_BG";
                    break;
                }
        }

        skillCharacterImg.sprite = ResourcesManager.Instance.GetImg(string.Format("Illustration\\{0}", chaimg));
        if (skillCharacterImg.sprite == null)
        {
            switch (pc)
            {
                case ePlayerClass.SwordMan:
                    {
                        chaimg = "1050_Skill";
                        break;
                    }
                case ePlayerClass.Archer:
                    {
                        chaimg = "2050_Skill";
                        break;
                    }
                case ePlayerClass.Mage:
                    {
                        chaimg = "3050_Skill";
                        break;
                    }
                case ePlayerClass.Healer:
                    {
                        chaimg = "4050_Skill";
                        break;
                    }
            }
            skillCharacterImg.sprite = ResourcesManager.Instance.GetImg(string.Format("Illustration\\{0}", chaimg));
        }
        skillBgImg.sprite = ResourcesManager.Instance.GetImg(string.Format("Illustration\\{0}", bgimg));
    }
    */
}
#endregion    //KAMI 테스트


using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Panel_Appraise : UI_SubView
{
	/// <summary>
	/// 아이템 선택 여부
	/// </summary>
	[SerializeField] private bool isSelected;
	/// <summary>
	/// 감정할 아이템 박스
	/// </summary>
	[SerializeField] Item_Identify_Box identifyBox;
	/// <summary>
	/// 감정 확률 버튼
	/// </summary>
	[SerializeField] Button probabilityBtn;
	[SerializeField] CustomButton infoBtn;
	/// <summary>
	/// 연출 스킵 토글
	/// </summary>
	[SerializeField] Item_Toggle skipEffectToggle;

	/// <summary>
	/// 장비 uid
	/// </summary>
	private int uid;
	/// <summary>
	/// 장비 아이템
	/// </summary>
	private EquipItem equipItem;

	protected override List<GameEventType> EventList => new List<GameEventType>()
	{
		GameEventType.ONCLICK_EQUIP_ITEM,
		GameEventType.GET_APPRAISE_RESULT,
		//GameEventType.APPRAISE_VIEW_UPDATE,
	};
	public override void HandleGameEvent(GameEvent ge)
	{
		if (!gameObject.activeInHierarchy)
			return;

		switch (ge.eventType)
		{
			//case GameEventType.APPRAISE_VIEW_UPDATE:
			//	{
			//		RefreshUI();
			//	}
			//	break;
			case GameEventType.ONCLICK_EQUIP_ITEM:
				{
					var uid = ge.ReadInt;
					SelectItem(uid);
				}
				break;
			case GameEventType.GET_APPRAISE_RESULT:
				{
					SetAppraise();
				}
				break;
		}
	}

	private void Start()
	{
		Initialize();
	}

	public override void Setting(params object[] args)
	{
		base.Setting(args);
		RefreshUI();
	}

	public override void RefreshUI()
	{
		base.RefreshUI();

		skipEffectToggle.ForcedSwitchMove(COMMON.GetSkipEffect("Appraise"));
		SelectItem();
		if (subViewArr != null && curSubViewIdx.Value < subViewArr.Length)
		{
			subViewArr[curSubViewIdx.Value].Setting();
		}
		else { COMMON.Etc_LogError("Check - subViewArr"); }


		DBManager.Instance.playerData._UserData.userInfo.DropUnidentifiedCount = 0;
		GameEventSubject.SendGameEvent(GameEventType.UNIDENTIFIED_DROP);

		GameEventSubject.SendGameEvent(GameEventType.SET_APPRAISE_INIT);
		GameEventSubject.SendGameEvent(GameEventType.EQUIPMENT_ACQUIRED);
	}

	public override void Initialize()
	{
		base.Initialize();
		probabilityBtn.onClick.AddListener(OnClickProbabilityButton);
		infoBtn?.onClick.AddListener(OnClick_InfoButton);
		skipEffectToggle.AddListener(OnValueChangeSkipToggle);
	}

	protected override void OnChangeSubViewIndex(int index)
	{
		base.OnChangeSubViewIndex(index);
		subViewArr[index].Setting(equipItem);
	}

	private void OnClickProbabilityButton()
	{
		var popup = ViewManager.Instance.OnPopUp(Define.ePopup.PopUp_AppraiseProbability);
		popup.Setting();
	}

	private void OnClick_InfoButton()
	{
		COMMON.OnPopUpAllHelpInfo("APPRAISAL_QUESTION_TXT");
	}

	private void SelectItem(int uid = -1)
	{
		isSelected = uid != -1;
		this.uid = uid;

		if (isSelected)
		{
			equipItem = COMMON.GetEquipItem(uid);

			// - 미확인 아이템
			if (!equipItem.IsUnidentified())
			{
				this.uid = -1;
				isSelected = false;
				equipItem = null;
				return;
			}
		}
		else
		{
			equipItem = null;
		}
		identifyBox.SetData(equipItem);
		curSubViewIdx.Value = isSelected ? 1 : 0;
	}

	public void SetAppraise()
	{
		if (COMMON.IsDevBuild())
		{
			if (!equipItem.IsUnidentified())
			{
				COMMON.Dev_LogError("미확인 오픈 - 미확인 상태 아님");
			}
			if (!equipItem.HasUnidentifiedOpenCurrency())
			{
				COMMON.Dev_LogError("미확인 오픈 - 재화 부족");
			}
		}

		GameEventSubject.SendGameEvent(GameEventType.SET_SLOTMACHINE, uid);
		subViewArr[(int)Define.eAppraiseSubView.CheckBtn].gameObject.SetActive(false);
		/*if (equipItem.OpenUnidentified())
		{
		}
		else if (COMMON.IsDevBuild())
		{
			COMMON.Dev_LogError("미확인 오픈 - 실패");
		}*/
	}

	private void OnValueChangeSkipToggle(bool isOn)
	{
		COMMON.SetSkipEffect("Appraise", isOn);
		COMMON.Save();
	}
}

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Panel_AppraiseCheck : UI_SubView
{
	[SerializeField] private Button costBtn;
	[SerializeField] private TextMeshProUGUI costValue;
	[SerializeField] private Button cancelBtn;

	private EquipItem equipItem = null;

	private void Start()
	{
		Initialize();
	}
	public override void Initialize()
	{
		base.Initialize();
		costBtn.onClick.AddListener(() =>
		{
			OnClick_CostBtn();
		});
		cancelBtn.onClick.AddListener(() =>
		{
			GameEventSubject.SendGameEvent(GameEventType.ONCLICK_EQUIP_ITEM, -1);
		});
	}

	public override void Setting(params object[] args)
	{
		base.Setting(args);
		equipItem = args[0] as EquipItem;
		if (equipItem == null) { return; }

		// - 재화 부족
		//TODO: select 는 되지만, 감정 버튼 비활성화 방향으로.
		var interactable = equipItem.HasUnidentifiedOpenCurrency();
		costBtn.interactable = interactable;
		costValue.color = interactable ? Color.white : Color.red;
	}

	private void OnClick_CostBtn()
	{
		if (equipItem == null) { return; }
		var popup = COMMON.OnPopUp_All_Caution();
		popup.SetData(
			Define.eCautionPopupType.ConsumeCurrency,
			Define.eCautionPopupButtonCountType.Two,
			TextManager.Instance.GetText("APPRAISE_POPUP_TITLE_TXT"),
			TextManager.Instance.GetText("APPRAISE_POPUP_CONTENTS_TXT"));
		popup.SetCurrencyData(equipItem.GetUnidentifiedOpenCurrency());
		popup.SetAction(() =>
		{
			GameEventSubject.SendGameEvent(GameEventType.GET_APPRAISE_RESULT);
		});
	}
}

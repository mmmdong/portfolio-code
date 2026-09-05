using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using static Define;

public class Panel_ShopSummon : Panel_ShopMain
{
	[SerializeField] private Toggle[] tabTogs;

	protected override List<GameEventType> EventList => new List<GameEventType>()
	{
		GameEventType.SUMMON_PARTNER_ECONOMIES,
	};
	public override void HandleGameEvent(GameEvent ge)
	{
		if (!gameObject.activeInHierarchy)
			return;

		switch (ge.eventType)
		{
			case GameEventType.SUMMON_PARTNER_ECONOMIES:
				{
					var summonPartnerType = (eSummonPartnerType)ge.ReadInt;
					SetEconomies(summonPartnerType);
					break;
				}
		}
	}

	public override void Initialize()
	{
		base.Initialize();
		for (var i = 0; i < tabTogs.Length; i++)
		{
			var index = i;
			var btn = tabTogs[index];
			btn.onValueChanged.AddListener(isOn => OnClickTabToggle(index, isOn));

#if TODO_UPDATE
#else
			if (i == 2)
			{
				//btn.interactable = true;
				var animator = btn.GetComponent<Animator>();
				if (animator != null)
				{
					animator.runtimeAnimatorController = null;
				}
				btn.graphic.gameObject.SetActive(false);
			}
#endif // TODO_UPDATE
		}
	}

	public override void Setting(params object[] args)
	{
		base.Setting(args);
		var summonType = (eSummonType)(curSubViewIdx.Value + 1);
		subViewArr[curSubViewIdx.Value].Setting(summonType);
		SetEconomies(summonType);
	}

	protected override void OnChangeSubViewIndex(int index)
	{
		base.OnChangeSubViewIndex(index);
		var summonType = (eSummonType)(index + 1);
		subViewArr[index].Setting(summonType);
		SetEconomies(summonType);
	}

	private void SetEconomies(eSummonType summonType)
	{
		switch (summonType)
		{
			case eSummonType.Skill:
				SetEconomies(eCurrencyType.Gold, eCurrencyType.Gem, eCurrencyType.SkillSummonTicket);
				break;
			case eSummonType.Partner:
				SetEconomies(eCurrencyType.Gold, eCurrencyType.Emerald, eCurrencyType.PartnerSummonTicket);
				break;
			case eSummonType.Card:
				SetEconomies(eCurrencyType.Gold, eCurrencyType.Emerald, eCurrencyType.CardSummonTicket);
				break;
		}
	}

	private void SetEconomies(eSummonPartnerType summonPartnerType)
	{
		switch (summonPartnerType)
		{
			case eSummonPartnerType.Partner:
				SetEconomies(eSummonType.Partner);
				break;
			/*case eSummonPartnerType.PickUp_1:
				SetEconomies(eCurrencyType.Gold, eCurrencyType.Emerald, eCurrencyType.PickPartnerSummonTicket1);
				break;
			case eSummonPartnerType.PickUp_2:
				SetEconomies(eCurrencyType.Gold, eCurrencyType.Emerald, eCurrencyType.PickPartnerSummonTicket2);*/
			case eSummonPartnerType.PickUp_1:
			case eSummonPartnerType.PickUp_2:
				SetEconomies(eCurrencyType.Gold, eCurrencyType.Emerald, eCurrencyType.PickPartnerSummonTicket1);
				break;
		}
	}

	private void OnClickTabToggle(int index, bool isOn)
	{
		//tabTogs[index].graphic.gameObject.SetActive(isOn);
		if (isOn)
		{
#if TODO_UPDATE
#else
			//TODO: Mr.Song : [CARD] - 카드 상점 막기.
			//TODO: Mr.Song - 임시 로직.
			if (index == 2)
			{
				tabTogs[index].graphic.gameObject.SetActive(false);
				COMMON.OnPopUp_UpdateToast();
				return;
			}
#endif // TODO_UPDATE
			curSubViewIdx.Value = index;
		}
		else
		{

		}
	}

	public override void ForcedChangeSubView(int index)
	{
		base.ForcedChangeSubView(index);
		tabTogs[index].isOn = true;
	}
}

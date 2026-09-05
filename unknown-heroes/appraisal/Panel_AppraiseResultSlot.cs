using AssetKits.ParticleImage;
using Cysharp.Threading.Tasks;
using DATA;
using EnhancedUI.EnhancedScroller;
using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class ResultEffect
{
	public ParticleImage[] effects;

	public void Init()
	{
		for (var i = 0; i < effects.Length; i++)
		{
			var index = i;
			var ef = effects[index];
			ef.onStop.AddListener(() => StopCallBack(index));
			ef.onLastParticleFinish.AddListener(() => StopCallBack(index));
		}
	}
	public void Play()
	{
		for (var i = 0; i < effects.Length; i++)
		{
			effects[i].gameObject.SetActive(true);
			effects[i].Play();
		}
	}

	public void Stop()
	{
		for (var i = 0; i < effects.Length; i++)
		{
			effects[i].Stop();
		}
	}

	public void StopCallBack(int index)
	{
		effects[index].gameObject.SetActive(false);
	}
}
public class Panel_AppraiseResultSlot : UI_SubView, IEnhancedScrollerDelegate
{
	/// <summary>
	/// 슬롯 스크롤 뷰
	/// </summary>
	[Space(20)]
	[Header("Slot Machine")]
	[SerializeField] private EnhancedScroller scroller;
	/// <summary>
	/// 스크롤 뷰 아이템
	/// </summary>
	[SerializeField] private Item_AppraiseSlotResult item;
	/// <summary>
	/// 슬롯머신 파티클
	/// </summary>
	[SerializeField] private ParticleImage[] slotParticle;
	/// <summary>
	/// 미확인 체크 전, 미확인 체크 후 아이템
	/// </summary>
	private EquipItem beforeItem, resultItem;
	[SerializeField] private ResultEffect[] resultEffect;

	protected override List<GameEventType> EventList => new List<GameEventType>()
	{
		GameEventType.SET_SLOTMACHINE,
		GameEventType.SET_APPRAISE_INIT,
	};
	public override void HandleGameEvent(GameEvent ge)
	{
		if (!gameObject.activeInHierarchy)
			return;

		switch (ge.eventType)
		{
			case GameEventType.SET_SLOTMACHINE:
				{
					var uid = ge.ReadInt;
					resultItem = COMMON.GetEquipItem(uid);
					beforeItem = resultItem.Clone();
					resultItem.OpenUnidentified();
					//장비 감정 신화 장비 이상일 경우 시스템 메세지 전달
					if (resultItem.equipData.Grade >= (int)Define.eGrade.SS)
					{
						ChattingManager.Instance.SendSystemMSG("CHAT_GAINITEM_IDENTIFIED_TXT", resultItem.id, Define.eDataType.Equip, resultItem.openResult);
					}
					PlaySlotMachine();
					break;
				}
			case GameEventType.SET_APPRAISE_INIT:
				{
					SetEffectInit();
					break;
				}
		}
	}

	private void Start()
	{
		SetSlotEffect(false);

		for (var i = 0; i < resultEffect.Length; i++)
			resultEffect[i].Init();

		scroller.Delegate = this;
		scroller.ReloadData();
		scroller.scrollerSnapped = ScrollerSnapped;
	}


	public EnhancedScrollerCellView GetCellView(EnhancedScroller scroller, int dataIndex, int cellIndex)
	{
		var cellView = scroller.GetCellView(item);
		cellView.Setting(dataIndex);
		return cellView;
	}

	public float GetCellViewSize(EnhancedScroller scroller, int dataIndex)
	{
		return 90f;
	}

	public int GetNumberOfCells(EnhancedScroller scroller)
	{
		return System.Enum.GetValues(typeof(Define.eUnidentifiedOpenResult)).Length - 1;
	}

	private void ScrollerSnapped(EnhancedScroller scroller, int cellIndex, int dataIndex, EnhancedScrollerCellView cellView)
	{
		if (dataIndex != (int)resultItem.openResult)
		{
			COMMON.Etc_LogError("Whyrano;;;;");
			return;
		}

		SlotEndAction();
	}

	/// <summary>
	/// 슬롯머신 이펙트 시작
	/// </summary>
	private void PlaySlotMachine()
	{
		scroller.SetScrollPositionImmediately(0);

		//Debug.LogError($"Target : {result}");
		var speed = UnityEngine.Random.Range(-50f, -100f);
		AccelatingSlot(speed).Forget();

		if (!COMMON.GetSkipEffect("Appraise"))
			SetSlotEffect(true);

		GameEventSubject.SendGameEvent(GameEventType.EQUIPMENT_ACQUIRED);
	}

	/// <summary>
	/// 결과가 도출 이후 처리할 함수
	/// </summary>
	private void SlotEndAction()
	{
		var popup = ViewManager.Instance.OnPopUp(Define.ePopup.PopUp_AppraiseResult);
		popup.Setting(beforeItem, resultItem);
		GameEventSubject.SendGameEvent(GameEventType.APPRAISE_INVEN_UPDATE);
	}

	/// <summary>
	/// 슬롯의 과속 관리
	/// </summary>
	/// <param name="speed"></param>
	private async UniTask AccelatingSlot(float speed)
	{
		resultEffect[(int)resultItem.openResult].Stop();
		if (!COMMON.GetSkipEffect("Appraise"))
		{
			while (speed < 0)
			{
				scroller.ScrollPosition += speed;
				if (speed < -5)
					speed += Time.timeScale;
				else
				{
					SetSlotEffect(false);
					// 결과 값을 들고있는 노드의 y 좌표
					var dataPos = scroller.GetScrollPositionForDataIndex((int)resultItem.openResult, EnhancedScroller.CellViewPositionEnum.Before);
					var mag = dataPos - scroller.ScrollPosition;
					if (mag < 60 && mag > 0)
					{
						speed = 0;
						break;
					}
				}

				await UniTask.Delay(0);
			}
			scroller.Snap();
		}
		else
		{
			//var dataPos = scroller.GetScrollPositionForDataIndex(, EnhancedScroller.CellViewPositionEnum.Before);
			scroller.JumpToDataIndex((int)resultItem.openResult);
			scroller.Snap();
		}

		resultEffect[(int)resultItem.openResult].Play();
		GameEventSubject.SendGameEvent(GameEventType.ONCLICK_EQUIP_ITEM, -1);
	}

	private void SetSlotEffect(bool isPlaying)
	{
		if (isPlaying)
		{
			for (var i = 0; i < slotParticle.Length; i++)
			{
				if (slotParticle[i] == null) { continue; }
				slotParticle[i].gameObject.SetActive(isPlaying);
				slotParticle[i].Play();
			}
		}
		else
		{
			for (var i = 0; i < slotParticle.Length; i++)
			{
				if (slotParticle[i] == null) { continue; }
				slotParticle[i].Stop();
				slotParticle[i].gameObject.SetActive(isPlaying);
			}
		}
	}

	private void SetEffectInit()
	{
		for (var i = 0; i < resultEffect.Length; i++)
		{
			resultEffect[i].Stop();
		}
	}
}

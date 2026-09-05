using AssetKits.ParticleImage;
using Cysharp.Threading.Tasks;
using DATA;
using EnhancedUI.EnhancedScroller;
using System;
using System.Collections.Generic;
using UnityEngine;
using static Define;

public class Panel_UnknownResultSlot : UI_SubView, IEnhancedScrollerDelegate
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
	[SerializeField] private Item_UnknownSlotResult item;
	/// <summary>
	/// 슬롯머신 파티클
	/// </summary>
	[SerializeField] private ParticleImage[] slotParticle;
	[SerializeField] private ResultEffect[] resultEffect;

	public eUnknownDungeonType resultItem;
	public Action onSlotEndAction;

	protected override List<GameEventType> EventList => new List<GameEventType>()
	{
		GameEventType.SET_UNKNOWN_SLOTMACHINE,
		GameEventType.SET_UNKNOWN_INIT,
	};
	public override void HandleGameEvent(GameEvent ge)
	{
		if (!gameObject.activeInHierarchy)
			return;

		switch (ge.eventType)
		{
			case GameEventType.SET_UNKNOWN_SLOTMACHINE:
				{
					resultItem = GetRandomDungeonResult();
					PlaySlotMachine();
					break;
				}
			case GameEventType.SET_UNKNOWN_INIT:
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
		return System.Enum.GetValues(typeof(Define.eUnknownDungeonType)).Length - 1;
	}

	private void ScrollerSnapped(EnhancedScroller scroller, int cellIndex, int dataIndex, EnhancedScrollerCellView cellView)
	{
		SlotEndAction();
	} 

	private void PlaySlotMachine()
	{
		scroller.SetScrollPositionImmediately(0);

		//Debug.LogError($"Target : {result}");
		var speed = UnityEngine.Random.Range(-50f, -100f);
		AccelatingSlot(speed).Forget();

		SetSlotEffect(true);
	}

	/// <summary>
	/// 결과가 도출 이후 처리할 함수
	/// </summary>``
	private void SlotEndAction()
	{
		onSlotEndAction?.Invoke();
	}

	private async UniTask AccelatingSlot(float speed)
	{
		while (speed < 0)
		{
			scroller.ScrollPosition += speed;
			if (speed < -5)
				speed += Time.timeScale;
			else
			{
				SetSlotEffect(false);
				var dataPos = scroller.GetScrollPositionForDataIndex((int)resultItem, EnhancedScroller.CellViewPositionEnum.Before);
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
	
	private eUnknownDungeonType GetRandomDungeonResult()
	{
		// 테이블에서 확률 데이터 가져오기
		var probabilityData = new List<(eUnknownDungeonType resultType, float probability)>();

		probabilityData.Add((eUnknownDungeonType.Fire, 0.3f));
		probabilityData.Add((eUnknownDungeonType.Water, 0.3f));
		probabilityData.Add((eUnknownDungeonType.Wind, 0.3f));
		probabilityData.Add((eUnknownDungeonType.Jackpot, 0.1f));
				
		if (probabilityData == null || probabilityData.Count == 0)
		{
			Debug.LogError("[Panel_UnknownResultSlot] 확률 테이블 데이터가 없습니다.");
			return eUnknownDungeonType.Fire; // 기본값
		}
		
		// 총 가중치 계산
		float totalWeight = 0f;
		foreach (var data in probabilityData)
		{
			totalWeight += data.probability;
		}
		
		// 랜덤 값 생성
		float randomValue = UnityEngine.Random.Range(0f, totalWeight);
		float currentWeight = 0f;
		
		// 가중치 기반 선택
		foreach (var data in probabilityData)
		{
			currentWeight += data.probability;
			if (randomValue <= currentWeight)
			{
				return (eUnknownDungeonType)data.resultType;
			}
		}
		
		// Fallback
		return eUnknownDungeonType.Fire;
	}
}

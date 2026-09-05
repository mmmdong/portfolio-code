using EnhancedUI.EnhancedScroller;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UniRx;
using UnityEngine;
using UnityEngine.UI;
using static Define;

public class PopUp_WorldMap : UI_PopUp, IEnhancedScrollerDelegate
{
	#region 변수

	/// <summary>
	/// 난이도 텍스트
	/// </summary>
	[Space(20)] [Header("Top")] [SerializeField]
	private TextMeshProUGUI difficulty;

	[SerializeField] private TextMeshProUGUI stageText;
	[SerializeField] private Image elementIcon;
	[SerializeField] private Button nextBtn;
	[SerializeField] private Button prevBtn;
	[SerializeField] private ToggleGroup toggleGroup;

	/// <summary>
	/// 권장 전투력
	/// </summary>
	[SerializeField] private TextMeshProUGUI recommendPowerText;

	/// <summary>
	/// 권장 속성
	/// </summary>
	[SerializeField] private TextMeshProUGUI elementText;

	/// <summary>
	/// 가져올 셀뷰
	/// </summary>
	[Space(20)] [Header("Middle")] [SerializeField]
	private Item_Result_Stage_CellView cellViewItem;

	[SerializeField] private EnhancedScroller scroller;

	/// <summary>
	/// 클리어 게이지
	/// </summary>
	[Space(20)] [Header("Bottom")] [SerializeField]
	private Image clearGauge;

	/// <summary>
	/// 챕터 보상 아이템
	/// </summary>
	[SerializeField] private Item_WorldMap_Gauge_Reward[] rewardItems;

	/// <summary>
	/// 클리어 보상 획득 버튼
	/// </summary>
	[SerializeField] private Button rewardBtn;

	/// <summary>
	/// 스테이지 입장 버튼
	/// </summary>
	[SerializeField] private Button joinBtn;

	/// <summary>
	/// 스테이지 선택 토글 배열
	/// </summary>
	private Toggle[] stageToggle;

	/// <summary>
	/// 토글에 있는 스테이지 텍스트
	/// </summary>
	private TextMeshProUGUI[] stageNumText;

	/// <summary>
	/// 토글에 있는 스테이지
	/// </summary>
	private int[] stageNumArr;

	/// <summary>
	/// 현재 선택된 스테이지
	/// </summary>
	private IntReactiveProperty curSelectedChapter = new IntReactiveProperty(1);

	private int stageIndex;
	private STAGE.StageInfo tableData;
	private STAGE.StageInfo maxStageData;


	private List<int> dropItemList = new List<int>();
	private int itemCount = 0;
	private int itemsPerRow = 1;
	private float cellViewSize = 0f;
	private int numberOfCells = -1;
	//private int minRowCount = 1;

	// - economy
	[SerializeField] private Item_Economy economy_01;
	[SerializeField] private Item_Economy economy_02;
	[SerializeField] private Item_Economy economy_03;

	#endregion // 변수

	protected override void Awake()
	{
		base.Awake();
		toggleGroup.allowSwitchOff = true;
		stageToggle = toggleGroup.GetComponentsInChildren<Toggle>();
		stageNumArr = new int[stageToggle.Length];
		stageNumText = new TextMeshProUGUI[stageToggle.Length];

		for (var i = 0; i < stageToggle.Length; i++)
		{
			var index = i;
			var tog = stageToggle[index];
			tog.onValueChanged.AddListener(isOn => OnClickToggle(index, isOn));
			stageNumArr[i] = (i + 1) * 5;
			stageNumText[i] = tog.GetComponentInChildren<TextMeshProUGUI>();
			stageNumText[i].text = $"{stageNumArr[i]}";
		}
	}

	private void Start()
	{
		curSelectedChapter.TakeUntilDestroy(this).Subscribe(OnChangeSelectedChapter);
		nextBtn.onClick.AddListener(OnClickNextButton);
		prevBtn.onClick.AddListener(OnClickPrevButton);
		rewardBtn.onClick.AddListener(OnClickRewardButton);
		joinBtn.onClick.AddListener(OnClickStageJoinButton);
		scroller.Delegate = this;
		itemsPerRow = cellViewItem.GetItemsPerRow();
		cellViewSize = cellViewItem.GetCellViewSize();

		// - economy
		economy_01.SetEconomyType(eCurrencyType.Gold, update: true, usePlusIcon: true);
		economy_02.SetEconomyType(eCurrencyType.Gem, update: true, usePlusIcon: true);
		economy_03.SetEconomyType(eCurrencyType.Emerald, update: true, usePlusIcon: true);
	}

	public override void Setting(params object[] args)
	{
		base.Setting(args);
		SetToggleInit();
		stageIndex = COMMON.GetCurStageIndex();
		BattleManager.Instance.RegisterBattleEndCallback(BattleClearCallBack);
		RefreshUI();
	}

	/// <summary>
	/// 팝업 새로 고침
	/// </summary>
	public override void RefreshUI()
	{
		base.RefreshUI();

		// - economy
		economy_01.RefreshUI();
		economy_02.RefreshUI();
		economy_03.RefreshUI();

		SettingUI();
		SetChapterButton();

		GameEventSubject.SendGameEvent(GameEventType.WORLD_MAP_REFRESH);
	}

	private void SetBottomButtons()
	{
		rewardBtn.interactable = IsCanGetReward();
		joinBtn.gameObject.SetActive(maxStageData.chapter - tableData.chapter <= 1);
	}

	/// <summary>
	/// 닫기버튼 클릭
	/// </summary>
	public override void OnClick_Close()
	{
		base.OnClick_Close();
		BattleManager.Instance.UnregisterBattleEndCallback(BattleClearCallBack);
	}

	/// <summary>
	/// 스테이지 토글 클릭 시 (5, 10, 15, 20, 25)
	/// </summary>
	/// <param name="index"></param>
	/// <param name="isOn"></param>
	private void OnClickToggle(int index, bool isOn)
	{
		if (isOn)
		{
			toggleGroup.allowSwitchOff = false;
			stageIndex = COMMON.GetStageIndex(tableData.chapter, stageNumArr[index]);
			RefreshUI();
		}
	}

	/// <summary>
	/// 스테이지 토글 초기화
	/// </summary>
	private void SetToggleInit()
	{
		toggleGroup.allowSwitchOff = false;
		for (var i = 0; i < stageNumArr.Length; i++)
			stageToggle[i].isOn = false;
	}

	/// <summary>
	/// 챕터 버튼 세팅
	/// </summary>
	private void SetChapterButton()
	{
		maxStageData = STAGE.StageInfo.GetStageIndexInfo(COMMON.GetMaxStageIndex());
		var tryingStageListIndex = STAGE.StageInfo.StageInfoList.IndexOf(maxStageData) + 1;

		if (tryingStageListIndex >= STAGE.StageInfo.StageInfoList.Count)
			return;
		var tryingStageData = STAGE.StageInfo.StageInfoList[tryingStageListIndex];
		nextBtn.gameObject.SetActive(curSelectedChapter.Value < tryingStageData.chapter);
		prevBtn.gameObject.SetActive(curSelectedChapter.Value > 1);

		if (tableData.chapter == tryingStageData.chapter)
		{
			for (var i = 0; i < stageToggle.Length; i++)
			{
				stageToggle[i].interactable = tryingStageData.stage >= stageNumArr[i];
				stageNumText[i].color = tryingStageData.stage >= stageNumArr[i]
					? COMMON.ColorSO.NORMAL_COLOR
					: COMMON.ColorSO.TOGGLE_OFF;
			}

			SetClearGauge(false);
		}
		else
		{
			for (var i = 0; i < stageToggle.Length; i++)
			{
				stageToggle[i].interactable = true;
				stageNumText[i].color = COMMON.ColorSO.NORMAL_COLOR;
			}

			SetClearGauge(true);
		}

		SetRewardItemCheck();
		SetBottomButtons();
	}

	/// <summary>
	/// 스테이지 클리어 게이지
	/// </summary>
	/// <param name="allClear"></param>
	private void SetClearGauge(bool allClear)
	{
		clearGauge.fillAmount = 0f;

		if (allClear)
		{
			clearGauge.fillAmount = 1f;
			return;
		}

		if (tableData.chapter == maxStageData.chapter)
		{
			if (maxStageData.stage <= stageNumArr[0])
			{
				clearGauge.fillAmount = maxStageData.stage * 0.01f;
			}
			else
			{
				if (maxStageData.stage == stageNumArr[stageNumArr.Length - 1])
				{
					clearGauge.fillAmount = 1f;
					return;
				}

				var sub = maxStageData.stage - stageNumArr[0];
				var gaugePerStage = (1f - 0.1f) / (stageNumArr.Length - 1) / 5f;
				var gauge = sub * gaugePerStage + 0.05f;
				clearGauge.fillAmount = gauge;
			}
		}
	}

	/// <summary>
	/// 스테이지 클리어 보상 획득 가능한 아이템
	/// </summary>
	private void SetRewardItemCheck()
	{
		var rewardGetStageIndex = COMMON.GetRewardStageIndex();
		for (var i = 0; i < stageNumArr.Length; i++)
		{
			var stageIndex = COMMON.GetStageIndex(tableData.chapter, stageNumArr[i]);
			rewardItems[i].OnCheckMark(stageIndex <= rewardGetStageIndex);
			var onGrayScale = stageIndex <= maxStageData.StageIndex;
			rewardItems[i].OnGrayScaleUI(!onGrayScale);
		}
	}

	/// <summary>
	/// 선택된 챕터 변경시 콜백
	/// </summary>
	/// <param name="chapter"></param>
	private void OnChangeSelectedChapter(int chapter)
	{
		var lastStage = STAGE.StageInfo.StageInfoList.Last(x => x.chapter == chapter).StageIndex;
		if (lastStage > maxStageData.StageIndex)
		{
			var maxListIdx = STAGE.StageInfo.StageInfoList.FindIndex(x => x.ID == maxStageData.ID);
			var maxData = STAGE.StageInfo.StageInfoList[maxListIdx + 1];
			lastStage = maxData.StageIndex;

			var anyInStageArr = stageNumArr.Any(x => x == maxData.stage);

			toggleGroup.allowSwitchOff = !anyInStageArr;
			if (anyInStageArr)
			{
				for (var i = 0; i < stageNumArr.Length; i++)
				{
					if (maxData.stage == stageNumArr[i])
					{
						stageToggle[i].isOn = true;
						break;
					}
				}
			}
			else
			{
				for (var i = 0; i < stageNumArr.Length; i++)
				{
					stageToggle[i].isOn = false;
				}
			}
		}
		else
		{
			toggleGroup.allowSwitchOff = false;
			stageToggle.Last().isOn = true;
		}

		stageIndex = lastStage;

		RefreshUI();
	}

	/// <summary>
	/// 팝업 UI 세팅
	/// </summary>
	private void SettingUI()
	{
		tableData = STAGE.StageInfo.GetStageIndexInfo(stageIndex);
		SetDropItemList();

		curSelectedChapter.Value = tableData.chapter;

		var landName = STAGE.ChapterInfo.ChapterInfoMap[tableData.chapter].LandName;
		difficulty.text = TextManager.Instance.GetText(landName);
		var colorIndex = GetDifficultyColorIndex(landName);
		difficulty.colorGradient = new VertexGradient()
		{
			topLeft = COMMON.ColorSO.DIFFICULTY_TEXT_COLOR[colorIndex].startColor,
			topRight = COMMON.ColorSO.DIFFICULTY_TEXT_COLOR[colorIndex].startColor,
			bottomLeft = COMMON.ColorSO.DIFFICULTY_TEXT_COLOR[colorIndex].endColor,
			bottomRight = COMMON.ColorSO.DIFFICULTY_TEXT_COLOR[colorIndex].endColor,
		};
		stageText.text = $"{tableData.chapter}-{tableData.stage}";
		var battleData = STAGE.StageBattle.StageBattleMap[COMMON.GetStageKey(stageIndex)];
		var elementSprite = COMMON.GetElementalIcon((Common.Enums.eElementalType)battleData.elemental);

		elementText.text = COMMON.GetElementCounterText((Common.Enums.eElementalType)battleData.elemental);
		elementIcon.gameObject.SetActive(elementSprite != null);
		elementIcon.sprite = elementSprite;

		var chapterData = STAGE.ChapterInfo.ChapterInfoMap[tableData.chapter];
		for (var i = 0; i < rewardItems.Length; i++)
		{
			var rewardInfo =
				new COMMON.ItemRewardInfo(chapterData.ChapterRewardIDs[i], chapterData.ChapterRewardValues[i]);
			rewardItems[i].SetData(rewardInfo);
		}

		recommendPowerText.text =
			TextManager.Instance.ConvertToNumberString($"{STAGE.StageBattle.StageBattleMap[tableData.ID].BattlePower}");

		scroller.ReloadData();
	}

	/// <summary>
	/// 스테이지별 드랍 아이템리스트
	/// DFS로 동작
	/// </summary>
	private void SetDropItemList()
	{
		dropItemList.Clear();
		var hashSet = new HashSet<int>();
		if (STAGE.StageDrop.DropItemIndexMap.TryGetValue(stageIndex, out var dropTable))
		{
			for (var i = 0; i < dropTable.DropRates.Count; i++)
			{
				if (dropTable.DropRates[i] <= 0)
					continue;

				GetDropId(dropTable.DropIDs[i], hashSet);
			}
		}

		DropItemListOrderBy(hashSet);
	}

	/// <summary>
	/// 드랍아이템 정렬함수
	/// </summary>
	/// <param name="hashSet"></param>
	private void DropItemListOrderBy(HashSet<int> hashSet)
	{
		var costList = hashSet.Where(x => TABLE.Cost.CostMap.ContainsKey(x));
		var equipList = hashSet.Where(x => DATA.Equip.EquipMap.ContainsKey(x))
			.OrderByDescending(x => DATA.Equip.EquipMap[x].Grade);
		dropItemList.AddRange(costList);
		dropItemList.AddRange(equipList);
		itemCount = dropItemList.Count;
		numberOfCells = Mathf.CeilToInt((float)itemCount / itemsPerRow);
	}


	/// <summary>
	/// DFS 재귀로 스테이지별 드랍 아이템 확인
	/// </summary>
	/// <param name="ID"></param>
	/// <param name="hashSet"></param>
	private void GetDropId(int ID, HashSet<int> hashSet)
	{
		var dropId = ID;
		if (DATA.LinkItem.LinkItemMap.TryGetValue(dropId, out var value))
		{
			for (var i = 0; i < value.LinkItemPer.Count; i++)
			{
				if (value.LinkItemPer[i] <= 0)
					continue;
				GetDropId(value.LinkItemID[i], hashSet);
			}
		}
		else
			hashSet.Add(dropId);
	}


	private void OnClickNextButton()
	{
		curSelectedChapter.Value++;
	}

	private void OnClickPrevButton()
	{
		curSelectedChapter.Value--;
	}

	/// <summary>
	/// 난이도 색상 인덱스
	/// </summary>
	/// <param name="landName"></param>
	/// <returns></returns>
	private int GetDifficultyColorIndex(string landName)
	{
		var split = landName.Split('_');
		return int.Parse(split[1]) - 1;
	}

	/// <summary>
	/// 보상받기 버튼 클릭
	/// </summary>
	private void OnClickRewardButton()
	{
		if (!IsCanGetReward())
			return;
		GetChapterRewards();
		RefreshUI();
	}

	/// <summary>
	/// 스테이지 클리어 보상 획득
	/// </summary>
	private void GetChapterRewards()
	{
		var lastGetRewardStageData = STAGE.StageInfo.GetStageIndexInfo(COMMON.GetRewardStageIndex());

		var rewardDict = new Dictionary<int, int>();
		var maxRewardChapter = 1;
		var maxRewardStage = 1;

		//마지막 챕터 전까지 보상 모두 받기
		for (var i = lastGetRewardStageData.chapter; i < maxStageData.chapter; i++)
		{
			maxRewardChapter = i;
			var chapterInfo = STAGE.ChapterInfo.ChapterInfoMap[maxRewardChapter];
			maxRewardStage = 1;
			for (var j = 0; j < chapterInfo.ChapterRewardIDs.Count; j++)
			{
				maxRewardStage = stageNumArr[j];
				var curStageIdx = 100 * maxRewardChapter + maxRewardStage;
				if (curStageIdx <= lastGetRewardStageData.StageIndex)
					continue;

				var currencyId = chapterInfo.ChapterRewardIDs[j];
				var currencyCount = chapterInfo.ChapterRewardValues[j];

				if (rewardDict.ContainsKey(currencyId))
					rewardDict[currencyId] += currencyCount;
				else
					rewardDict.Add(currencyId, currencyCount);
			}
		}

		//마지막 챕터 보상 받기
		for (var i = 0; i < stageNumArr.Length; i++)
		{
			if (maxStageData.stage < stageNumArr[i])
				break;

			maxRewardChapter = maxStageData.chapter;
			maxRewardStage = stageNumArr[i];
		}

		var maxRewardStageIndex = COMMON.GetStageIndex(maxRewardChapter, maxRewardStage);

		var maxRewardStageInfo = STAGE.StageInfo.GetStageIndexInfo(maxStageData.StageIndex);
		var maxChapterInfo = STAGE.ChapterInfo.ChapterInfoMap[maxRewardStageInfo.chapter];

		for (var i = 0; i < stageNumArr.Length; i++)
		{
			if (maxRewardStageInfo.stage < stageNumArr[i])
				break;
			if (maxRewardChapter == lastGetRewardStageData.chapter)
			{
				if (lastGetRewardStageData.stage >= stageNumArr[i])
				{
					continue;
				}
			}

			var currencyId = maxChapterInfo.ChapterRewardIDs[i];
			var currencyCount = maxChapterInfo.ChapterRewardValues[i];

			if (rewardDict.ContainsKey(currencyId))
				rewardDict[currencyId] += currencyCount;
			else
				rewardDict.Add(currencyId, currencyCount);
		}

		var rewardList = new List<COMMON.ItemRewardInfo>();
		foreach (var item in rewardDict)
		{
			var rewardItem = new COMMON.ItemRewardInfo(item.Key, item.Value);
			rewardList.Add(rewardItem);
		}

		// - check inven
		if (!COMMON.CanReceiveRewardItems(rewardList))
		{
			return;
		}

		// - normal flow
		var rewardItemList = COMMON.RewardItems(rewardList);
		var popup = COMMON.OnPopup_Reward();
		popup.SetData(rewardItemList);

		DBManager.Instance.playerData._DungeonStageData.stageInfo.gotRewardStageIdx = maxRewardStageIndex;
	}

	/// <summary>
	/// 보상받기 버튼 Interact
	/// </summary>
	/// <returns></returns>
	private bool IsCanGetReward()
	{
		var gotRewardStageIdx = COMMON.GetRewardStageIndex();
		var maxRewardChapter = 0;
		var maxRewardStage = 0;
		var maxRewardStageIndex = 0;

		var breakCheck = false;
		for (var i = 1; i <= maxStageData.chapter; i++)
		{
			for (var j = 0; j < stageNumArr.Length; j++)
			{
				breakCheck = GetStageIndex(i, stageNumArr[j]) > maxStageData.StageIndex;
				if (breakCheck)
				{
					if (j == 0)
						i--;
					break;
				}

				maxRewardChapter = i;
				maxRewardStage = stageNumArr[j];
			}

			if (breakCheck)
				break;
		}

		maxRewardStageIndex = COMMON.GetStageIndex(maxRewardChapter, maxRewardStage);
		return maxRewardStageIndex > 0 && maxRewardStageIndex != gotRewardStageIdx;


		int GetStageIndex(int chapter, int stage)
		{
			return COMMON.GetStageIndex(chapter, stage);
		}
	}

	/// <summary>
	/// 스테이지 변경
	/// </summary>
	private void OnClickStageJoinButton()
	{
		if (tableData.StageIndex == COMMON.GetCurStageIndex())
		{
			OnClick_Close();
			return;
		}

		DBManager.Instance.playerData._DungeonStageData.stageInfo.curStageIdx = stageIndex;
		COMMON.RestartStage();
		COMMON.Save();
		OnClick_Close();
	}

	private void BattleClearCallBack(Define.eBattleType battleType)
	{
		if (battleType == Define.eBattleType.Stage)
			SetChapterButton();
	}

	#region Interface Enhanced Scroller

	public int GetNumberOfCells(EnhancedScroller scroller)
	{
		return numberOfCells;
	}

	public float GetCellViewSize(EnhancedScroller scroller, int dataIndex)
	{
		return cellViewSize;
	}

	public EnhancedScrollerCellView GetCellView(EnhancedScroller scroller, int dataIndex, int cellIndex)
	{
		var cellView = scroller.GetCellView(cellViewItem) as Item_Result_Stage_CellView;
		cellView.SetData(dropItemList, dataIndex);
		return cellView;
	}

	#endregion
}
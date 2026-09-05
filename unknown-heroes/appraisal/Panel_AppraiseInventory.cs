using EnhancedUI.EnhancedScroller;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Define;

public class Panel_AppraiseInventory : UI_SubView, IEnhancedScrollerDelegate
{
	#region 변수
	[SerializeField] private EnhancedScroller scroller;
	[SerializeField] private Item_Equipment_CellView equipItemCellView;

	[SerializeField] private CustomButton[] tabBtns;
	private eEquipItemType curEquipFilterType = eEquipItemType.All;

	private List<EquipItem> itemList = new List<EquipItem>();
	private int itemCount = 0;
	private int itemsPerRow = 1;
	private float cellViewSize = 0;
	private int numberOfCells = -1;
	private int minRowCount = 1;
	private CustomButton firstItemButton;

	#endregion // 변수

	#region Event
	protected override List<GameEventType> EventList => new List<GameEventType>()
	{
		GameEventType.APPRAISE_INVEN_UPDATE,
	};

	public override void HandleGameEvent(GameEvent ge)
	{
		if (!gameObject.activeInHierarchy) { return; }
		switch (ge.eventType)
		{
			case GameEventType.APPRAISE_INVEN_UPDATE:
				{
					RefreshUI();
				}
				break;
		}
	}
	#endregion // Event

	#region 기본 함수
	private void Awake()
	{
		for (var i = 0; i < tabBtns.Length; i++)
		{
			var index = i;
			var btn = tabBtns[index];
			btn.onClick.AddListener(() => OnClick_EquipTypeTab(index));
		}
	}

	private void Start()
	{
		scroller.Delegate = this;
		itemsPerRow = equipItemCellView.GetItemsPerRow();
		cellViewSize = equipItemCellView.GetCellViewSize();
		minRowCount = equipItemCellView.GetMinRowCount();
		RefreshUI();
	}

	public override void Setting(params object[] args)
	{
		base.Setting(args);
		tabBtns[(int)curEquipFilterType].Select();
		RefreshUI();
	}

	public override void RefreshUI()
	{
		base.RefreshUI();
		var rawList = COMMON.GetEquipItemList_Appraise(filterType: curEquipFilterType);
		itemList = equipItemCellView.PadEquipItemList(rawList, itemsPerRow, minRowCount);
		itemCount = itemList.Count;
		numberOfCells = Mathf.CeilToInt((float)itemCount / itemsPerRow);
		scroller.ReloadData();
	}

	public EnhancedScrollerCellView GetCellView(EnhancedScroller scroller, int dataIndex, int cellIndex)
	{
		var cellView = scroller.GetCellView(equipItemCellView) as Item_Equipment_CellView;
		cellView.SetData(
			list: itemList,
			item: null,
			rowIndex: dataIndex,
			itemsPerRow: itemsPerRow,
			showPresetInfo: false);

		if (dataIndex == 0)
        {
			var items = cellView.GetItems();
            firstItemButton = items.Count() > 0 ? cellView.GetItems()[0].GetComponent<CustomButton>() : null;
        }
		
		return cellView;
	}

	public CustomButton GetFirstItemButton()
    {
        return firstItemButton;
    }

	public float GetCellViewSize(EnhancedScroller scroller, int dataIndex)
	{
		return cellViewSize;
	}

	public int GetNumberOfCells(EnhancedScroller scroller)
	{
		return numberOfCells;
	}
	#endregion // 기본 함수

	#region 사용자 함수
	private void OnClick_EquipTypeTab(int index)
	{
		if (!EnumCache<eEquipItemType>.IntValues.Contains(index)) { return; }
		curEquipFilterType = (eEquipItemType)index;
		RefreshUI();
	}
	#endregion // 사용자 함수
}

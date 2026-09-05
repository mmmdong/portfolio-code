using DATA;
using EnhancedUI.EnhancedScroller;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

/// <summary>
/// 소환 확률 패널
/// </summary>
public class Panel_SummonMainProbability : UI_SubView, IEnhancedScrollerDelegate
{
	/// <summary>
	/// 등급별 확률 오브젝트
	/// </summary>
	[SerializeField] private GameObject[] tierDropProbabilityObjs;
	/// <summary>
	/// 등급별 확률 텍스트
	/// </summary>
	[SerializeField] private TextMeshProUGUI[] tierDropProbabilityTexts;
	/// <summary>
	/// 무한 스크롤 뷰로 사용될 프리팹(스킬, 카드 이름 확률 등장)
	/// </summary>
	[SerializeField] private Item_ProbabilityList_CellView cellViewItem;
	/// <summary>
	/// 등급
	/// </summary>
	[SerializeField] private TextMeshProUGUI gradeTitleName;
	/// <summary>
	/// 이름
	/// </summary>
	[SerializeField] private TextMeshProUGUI itemNameTitleName;
	/// <summary>
	/// 확률
	/// </summary>
	[SerializeField] private TextMeshProUGUI probabilityTitleName;
	/// <summary>
	/// 스크롤뷰
	/// </summary>
	[SerializeField] protected EnhancedScroller scroller;

	private List<KeyValuePair<int, float>> dropKeyValuePairList = new List<KeyValuePair<int, float>>();
	protected TABLE.Summon summonData;

	protected virtual void Start()
	{
		scroller.Delegate = this;
	}

	/// <summary>
	/// 데이터 세팅
	/// DFS 재귀로 확률 계산
	/// </summary>
	/// <param name="summonData"></param>
	public virtual void SetData(TABLE.Summon summonData)
	{
		var itemDict = new Dictionary<int, float>();
		this.summonData = summonData;
		for (var i = 0; i < tierDropProbabilityObjs.Length; i++)
		{
			if (summonData.DropPers[i] == 0f)
			{
				tierDropProbabilityObjs[i].SetActive(false);
				continue;
			}
			tierDropProbabilityObjs[i].SetActive(true);
			tierDropProbabilityTexts[i].text = $"{summonData.DropPers[i] * 100:N2}%";
			var dropPer = summonData.DropPers[i];
			GetDropId(summonData.DropIDs[i], itemDict, dropPer);
		}
		dropKeyValuePairList = itemDict.OrderByDescending(x => x.Key).ToList();
		scroller.ReloadData();

	}

	/// <summary>
	/// DFS 재귀 함수
	/// </summary>
	/// <param name="ID">아이템 ID(스킬, 동료, 카드 or LinkItem)</param>
	/// <param name="dict">ID 별로 확률 평탄화 할 Dictionary</param>
	/// <param name="dropPer">현재 ID의 확률</param>
	private void GetDropId(int ID, Dictionary<int, float> dict, float dropPer)
	{
		var linkItem = LinkItem.LinkItemMap[ID];
		for (var i = 0; i < linkItem.LinkItemID.Count; i++)
		{
			if (linkItem.LinkItemPer[i] == 0f)
				continue;

			var dropId = linkItem.LinkItemID[i];
			var tempDropPer = dropPer * linkItem.LinkItemPer[i];

			if (LinkItem.LinkItemMap.ContainsKey(dropId))
				GetDropId(dropId, dict, tempDropPer);
			else
			{
				if (dict.ContainsKey(dropId))
					dict[dropId] += tempDropPer;
				else
					dict.Add(dropId, tempDropPer);
			}
		}
	}

	#region EnhancedScroller Interface
	public virtual EnhancedScrollerCellView GetCellView(EnhancedScroller scroller, int dataIndex, int cellIndex)
	{
		var cellView = scroller.GetCellView(cellViewItem) as Item_ProbabilityList_CellView;
		cellView.SetData(dropKeyValuePairList[dataIndex]);
		return cellView;
	}

	public virtual float GetCellViewSize(EnhancedScroller scroller, int dataIndex)
	{
		return cellViewItem.CellSize;
	}

	public virtual int GetNumberOfCells(EnhancedScroller scroller)
	{
		return dropKeyValuePairList.Count;
	}
	#endregion
}

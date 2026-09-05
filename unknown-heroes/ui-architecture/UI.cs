using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using UniRx;
using UnityEngine;

public class UI : GameEventHandler
{
	#region 변수
	protected Dictionary<Type, UnityEngine.Object[]> objects = new Dictionary<Type, UnityEngine.Object[]>();
	protected TextLabelUpdater textLabelUpdater = new();

	[HideInInspector] public IntReactiveProperty curSubViewIdx = new IntReactiveProperty(0);
	[SerializeField] protected UI_SubView[] subViewArr;
	[SerializeField] private Item_SelectMenu selectMenu;
	public Item_SelectMenu SelectMenu => selectMenu;

	protected IDisposable subject; //UniRx 할당 해제 할 인터페이스 / 혹시 몰라 만들어 놓음.
	#endregion // 변수

	#region 이벤트 등록 & 이벤트 콜 함수
	protected override List<GameEventType> EventList => new List<GameEventType>() { };
	public override void HandleGameEvent(GameEvent ge) { }
	#endregion // 이벤트 등록 & 이벤트 콜 함수

	/// <summary>
	/// 서브뷰 인덱스 강제로 변경
	/// </summary>
	/// <param name="index"></param>
	public virtual void ForcedChangeSubView(int index)
	{
		curSubViewIdx.Value = index;
		//SelectMenuInit(index);
		SelectMenuInit(0);
	}

	public void SelectMenuInit(int index)
	{
		selectMenu?.OnClickButton(index);
	}

	public UI GetSubView()
	{
		if (subViewArr.Length > 0)
			return subViewArr[curSubViewIdx.Value];
		else
			return null;
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		subject?.Dispose();
	}

	/// <summary>
	/// 셋팅에 필요한 파라미터 전달 후 셋팅을 하기 위한 함수
	/// </summary>
	/// <param name="args">셋팅에 필요한 파라미터 배열형식으로 전달</param>
	virtual public void Setting(params object[] args) { }
	virtual public void Initialize()
	{
		textLabelUpdater.Initialize(gameObject);
		curSubViewIdx = new IntReactiveProperty(0);
		subject = curSubViewIdx.TakeUntilDestroy(this).Subscribe(OnChangeSubViewIndex);
		//RefreshUI();

		/*if (this is not Item_SelectMenu)
			selectMenu = GetComponentInChildren<Item_SelectMenu>();*/
	}

	protected virtual void OnChangeSubViewIndex(int index)
	{
		if (subViewArr == null || subViewArr.Length <= 0) { return; }
		for (var i = 0; i < subViewArr.Length; i++)
		{
			subViewArr[i].gameObject.SetActive(i == index);
		}
	}

	virtual public void Init()
	{
		//textLabelUpdater.UpdateLabels();
	}

	virtual public void RefreshUI() { SetDetail(); }
	//virtual public void RefreshUI() {  }

	virtual public void SetDetail()
	{
		textLabelUpdater.UpdateLabels();
	}
}

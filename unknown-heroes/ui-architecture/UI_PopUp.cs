using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

public class UI_PopUp : UI
{
	public UI masterUI { get; set; }
	public RectTransform rctTr = null;
	public int sortingOrder = 0;

	[SerializeField] protected Button closeBtn;

	protected virtual void Awake()
	{
		Initialize();
		closeBtn?.onClick.AddListener(OnClick_Close);
	}

	protected override void OnEnable() { base.OnEnable(); }

	public override void Initialize() { base.Initialize(); }
	public override void Init()
	{
		base.Init();
		if (rctTr != null)
		{
			rctTr.DORewind();
			rctTr.localScale = UnityEngine.Vector3.zero;
			rctTr.DOScale(UnityEngine.Vector3.one, 0.15f).SetEase(Ease.InBack).Play().SetUpdate(true).OnComplete(OnPopupCompleteAction);
		}
	}
	public override void Setting(params object[] args) { base.Setting(args); }
	public override void SetDetail() { base.SetDetail(); }
	public override void RefreshUI() { base.RefreshUI(); }
	public virtual void OnClick_Close()
	{
		if (rctTr != null)
		{
			rctTr.DORewind();
			rctTr.DOScale(UnityEngine.Vector3.zero, 0.15f) // 
			.SetEase(Ease.InBack)
			.OnComplete(() =>
			{
				OnClose();
			})
			.Play().SetUpdate(true); // 애니메이션 시작
		}
		else
		{
			OnClose();
		}
	}

	protected void OnClose()
	{
		if (gameObject.activeInHierarchy) { gameObject.SetActive(false); }
	}

	protected virtual void OnPopupCompleteAction()
	{
		//pass
	}
}
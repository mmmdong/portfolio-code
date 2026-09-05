using UnityEngine;

//public class UI_View<T> : UI where T : Enum
public class UI_View : UI
{
    [HideInInspector] public Define.eVIEW viewID = Define.eVIEW.Main;
    //[HideInInspector] public T subViewID;

    protected override void OnDisable()
    {
        base.OnDisable();

        if (ViewManager.HasInstance())
            ViewManager.Instance.SetOffView(viewID);
    }

    public virtual void OnClick_Close()
    {
        gameObject.SetActive(false);
    }
}

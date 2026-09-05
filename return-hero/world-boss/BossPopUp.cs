using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class BossPopUp : UI_PopUp
{
    enum PopUpText
    {

    }
    enum PopUpImage
    {

    }
    enum PopUpButton
    {

    }

    private void Awake()
    {
        Bind<TextMeshProUGUI>(typeof(PopUpText));
        Bind<Image>(typeof(PopUpImage));
        Bind<Button>(typeof(PopUpButton));

    }



    public override void Setting(params object[] args)
    {
        base.Setting(args);

        Invoke("EndAction", 3f);
    }

    private void EndAction()
    {
        gameObject.SetActive(false);
    }
}

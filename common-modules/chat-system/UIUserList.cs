using UnityEngine;
using UnityEngine.UI;

public class UIUserList : MonoBehaviour
{
    public Image Avatar = null;

    public Text Name = null;

    public void SetData(string avatar, string name, bool is_my = false)
    {
        Avatar.sprite = Resources.Load<Sprite>("Images/" + avatar);

        if (avatar == string.Empty || avatar == "default")
        {
            Avatar.sprite = Resources.Load<Sprite>("Images/Girl_5");
        }
        else
        {
            Avatar.sprite = Resources.Load<Sprite>("Images/" + avatar);
        }

        if (is_my)
        {
            Name.text = name + " (You)";
        }
        else
        {
            Name.text = name;
        }
    }
}

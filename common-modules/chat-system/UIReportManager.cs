using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UI;

public class UIReportManager : MonoBehaviour
{
    public Dropdown Dropdown = null;

    public InputField InputField = null;

    public Button Button = null;

    public Button CloseButton = null;

    private UInt64 Index = 0;

    private string Tag = string.Empty;

    private Dictionary<string, string> Reasons = null;

    private Action<UInt64, string, string, string> OnReport = null;

    public void SetData(UInt64 index, string tag, Dictionary<string, string> reasons, Action<UInt64, string, string, string> onReport)
    {
        Index = index;
        Tag = tag;
        Reasons = reasons;
        OnReport = onReport;

        Button.onClick.RemoveAllListeners();
        Button.onClick.AddListener(OnClickReport);

        CloseButton.onClick.RemoveAllListeners();
        CloseButton.onClick.AddListener(OnClickClose);

        Dropdown.ClearOptions();

        Dropdown.onValueChanged.RemoveAllListeners();
        Dropdown.onValueChanged.AddListener(OnDropdownValueChanged);

        List<Dropdown.OptionData> options = new List<Dropdown.OptionData>();
        foreach (KeyValuePair<string, string> pair in Reasons)
        {
            Dropdown.OptionData option = new Dropdown.OptionData(pair.Key);
            options.Add(option);
        }

        Dropdown.AddOptions(options);

        if (Dropdown.options.Count > 0)
        {
            InputField.text = Reasons[Dropdown.options[0].text];
        }

        gameObject.SetActive(true);
    }

    public void OnDropdownValueChanged(int index)
    {
        InputField.text = Reasons[Dropdown.options[index].text];
    }

    public void OnClickClose()
    {
        gameObject.SetActive(false);
    }

    public void OnClickReport()
    {
        if (Dropdown)
        {
            if (Dropdown.options.Count <= 0)
            {
                return;
            }

            string keyword = Dropdown.options[Dropdown.value].text;
            string reason = InputField.text;
            if (OnReport != null)
            {
                OnReport(Index, Tag, keyword, reason);
            }
        }

        gameObject.SetActive(false);
    }
}

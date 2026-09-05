using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UI;

public class UIJoinChannelManager : MonoBehaviour
{
    public Dropdown ChannelTypeDropdown = null;

    public Button CreateButton = null;

    public Button JoinButton = null;

    public Button CloseButton = null;

    public InputField ChannelGroupInput = null;

    public InputField ChannelNameInput = null;

    public Text ChannelNumberLabel = null;

    public InputField ChannelNumberInput = null;

    public Text ChannelPasswordLabel = null;

    public InputField ChannelPasswordInput = null;

    public Text ChannelMaxCountLabel = null;

    public InputField ChannelMaxCountInput = null;

    private Action<string, UInt64, string, uint, string> OnCreateChannel = null;

    private Action<string, string> OnJoinOpenChannel = null;

    private Action<string, UInt64, string> OnJoinPrivateChannel = null;

    public void SetData(Action<string, UInt64, string, uint, string> onCreateChannel, Action<string, string> onJoinOpenChannel, Action<string, UInt64, string> onJoinPrivateChannel)
    {
        OnCreateChannel = onCreateChannel;

        OnJoinOpenChannel = onJoinOpenChannel;
        OnJoinPrivateChannel = onJoinPrivateChannel;

        ChannelTypeDropdown.ClearOptions();

        ChannelTypeDropdown.onValueChanged.RemoveAllListeners();
        ChannelTypeDropdown.onValueChanged.AddListener(OnDropdownValueChanged);

        List<Dropdown.OptionData> options = new List<Dropdown.OptionData>();

        Dropdown.OptionData optionData = new Dropdown.OptionData("Open Type");
        options.Add(optionData);

        optionData = new Dropdown.OptionData("Private Type");
        options.Add(optionData);

        ChannelTypeDropdown.AddOptions(options);

        CreateButton.onClick.RemoveAllListeners();
        CreateButton.onClick.AddListener(OnClickCreateSend);

        JoinButton.onClick.RemoveAllListeners();
        JoinButton.onClick.AddListener(OnClickJoinSend);

        CloseButton.onClick.RemoveAllListeners();
        CloseButton.onClick.AddListener(OnClickClose);

        ChannelNumberLabel.gameObject.SetActive(false);
        ChannelNumberInput.gameObject.SetActive(false);

        ChannelPasswordLabel.gameObject.SetActive(false);
        ChannelPasswordInput.gameObject.SetActive(false);

        ChannelMaxCountLabel.gameObject.SetActive(false);
        ChannelMaxCountInput.gameObject.SetActive(false);

        CreateButton.gameObject.SetActive(false);

        gameObject.SetActive(true);
    }

    public void OnDropdownValueChanged(int index)
    {
        if (index == 0)
        {
            ChannelNumberLabel.gameObject.SetActive(false);
            ChannelNumberInput.gameObject.SetActive(false);

            ChannelPasswordLabel.gameObject.SetActive(false);
            ChannelPasswordInput.gameObject.SetActive(false);

            ChannelMaxCountLabel.gameObject.SetActive(false);
            ChannelMaxCountInput.gameObject.SetActive(false);

            CreateButton.gameObject.SetActive(false);
        }
        else
        {
            ChannelNumberLabel.gameObject.SetActive(true);
            ChannelNumberInput.gameObject.SetActive(true);

            ChannelPasswordLabel.gameObject.SetActive(true);
            ChannelPasswordInput.gameObject.SetActive(true);

            ChannelMaxCountLabel.gameObject.SetActive(true);
            ChannelMaxCountInput.gameObject.SetActive(true);

            CreateButton.gameObject.SetActive(true);
        }
    }

    public void OnClickClose()
    {
        ChannelGroupInput.text = string.Empty;
        ChannelNameInput.text = string.Empty;
        ChannelPasswordInput.text = string.Empty;
        ChannelMaxCountInput.text = string.Empty;
        ChannelNumberInput.text = string.Empty;

        gameObject.SetActive(false);
    }

    public void OnClickCreateSend()
    {
        if (ChannelGroupInput && ChannelNameInput)
        {
            string channelGroup = ChannelGroupInput.text;
            string channelName = ChannelNameInput.text;
            string channelPassword = ChannelPasswordInput.text;

            UInt64 channelNumber = 0;
            UInt64.TryParse(ChannelNumberInput.text, out channelNumber);

            uint channelMaxCount = 0;
            uint.TryParse(ChannelMaxCountInput.text, out channelMaxCount);

            if (channelGroup.Length > 0 && channelMaxCount > 1)
            {
                if (OnCreateChannel != null)
                {
                    if (channelName.Length == 0)
                    {
                        channelName = "default";
                    }

                    OnCreateChannel(channelGroup, channelNumber, channelName, channelMaxCount, channelPassword);
                }
            }

            ChannelGroupInput.text = string.Empty;
            ChannelNameInput.text = string.Empty;
            ChannelPasswordInput.text = string.Empty;
            ChannelMaxCountInput.text = string.Empty;
            ChannelNumberInput.text = string.Empty;
        }

        gameObject.SetActive(false);
    }

    public void OnClickJoinSend()
    {
        if (ChannelGroupInput && ChannelNameInput)
        {
            string channelGroup = ChannelGroupInput.text;
            string channelName = ChannelNameInput.text;
            string channelPassword = ChannelPasswordInput.text;

            UInt64 channelNumber = 0;
            UInt64.TryParse(ChannelNumberInput.text, out channelNumber);

            if (channelGroup.Length > 0)
            {
                if (ChannelTypeDropdown.value == 1)
                {
                    if (OnJoinPrivateChannel != null)
                    {
                        OnJoinPrivateChannel(channelGroup, channelNumber, channelPassword);
                    }
                }
                else
                {
                    if (OnJoinOpenChannel != null)
                    {
                        if (channelName.Length == 0)
                        {
                            channelName = "default";
                        }

                        OnJoinOpenChannel(channelGroup, channelName);
                    }
                }
            }

            ChannelGroupInput.text = string.Empty;
            ChannelNameInput.text = string.Empty;
            ChannelPasswordInput.text = string.Empty;
            ChannelMaxCountInput.text = string.Empty;
            ChannelNumberInput.text = string.Empty;
        }

        gameObject.SetActive(false);
    }
}

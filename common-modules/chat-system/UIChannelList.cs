using System;

using UnityEngine;
using UnityEngine.UI;

public class UIChannelList : MonoBehaviour
{
    public Button ChannelButton = null;

    public Action<string, string, UInt64> OnChannelSelected = null;

    public void AddChannel(string channelGroup, string channelName, UInt64 channelNumber, Action<string, string, UInt64> callback)
    {
        OnChannelSelected = callback;

        if (ChannelButton != null)
        {
            ChannelButton.GetComponentInChildren<Text>().text = channelGroup;
            ChannelButton.onClick.AddListener(() => { OnChannelSelected.Invoke(channelGroup, channelName, channelNumber); });
        }
    }
}

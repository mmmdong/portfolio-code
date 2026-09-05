using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class COMMON
{
	public static List<MsgData> GetChatMsgDataList(ChatType chatType)
	{
		var result = new List<MsgData> { (new MsgData() { cellType = CellType.Spacer }) };

		var userChat = new List<MsgData>(ChattingManager.Instance.chatUserMsgInfoList);
		userChat = userChat.OrderBy(x => x.msgInfo.Time).ToList();

		var systemChat = new List<MsgData>(ChattingManager.Instance.chatSystemMsgInfoList);
		systemChat = systemChat.OrderBy(x => x.msgInfo.Time).ToList();

		switch (chatType)
		{
			case ChatType.All:
				{
					var allChat = new List<MsgData>(userChat);
					allChat.AddRange(systemChat);
					allChat = allChat.OrderBy(x => x.msgInfo.Time).ToList();
					result.AddRange(allChat);
					break;
				}
			case ChatType.General:
				{
					result.AddRange(userChat);
					break;
				}
			case ChatType.System:
				{
					result.AddRange(systemChat);
					break;
				}
		}

		return result;
	}

	public static void SendChatMsg(string msg)
	{
		ChattingManager.Instance.SendChatMSG(msg);
	}

	public static List<ChatIgnoreData> GetIgnoreList()
	{
		return DBManager.Instance.playerData._UserData.chattingInfo.ignoreList;
	}

	public static void UserIgnore(ChatIgnoreData data)
	{
		GetIgnoreList().Add(data);
	}

	public static void UserIgnoreRelease(ChatIgnoreData data)
	{
		GetIgnoreList().Remove(data);
	}

	public static void UserIgnoreListClear()
	{
		GetIgnoreList().Clear();
	}
}

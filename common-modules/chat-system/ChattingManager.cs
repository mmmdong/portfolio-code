using BackndChat;
using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class SendMsgClass
{
	public string nickName;
	public int ranking;
	public string chatMsg;
	public int awakenGrade;
	public string uid;
}

public class ChattingManager : Singleton<ChattingManager>, IChatClientListener, IInstantiable
{
	public string CurrentChannelGroup = string.Empty;
	public string CurrentChannelName = string.Empty;
	public UInt64 CurrentChannelNumber = 0;
	public List<string> SelectMessageKey = new List<string>();
	public Dictionary<string, Dictionary<string, Dictionary<UInt64, ChannelInfo>>> ChannelList = new Dictionary<string, Dictionary<string, Dictionary<UInt64, ChannelInfo>>>();
	public Token chattingToken;
	public ChatClient ChatClient = null;
	public string lastSystemMsg;

	public List<MsgData> chatUserMsgInfoList = new();
	public List<MsgData> chatSystemMsgInfoList = new();
	public List<MsgData> consoleSystemMsgInfoList = new();

	public bool chattingReady = false;
	public bool isAlarm = false;

	private MainView mainView;

	private void Start()
	{
		InitChattingToken().Forget();

		mainView = ViewManager.Instance.GetUIView(Define.eVIEW.Main) as MainView;
	}

	private void Update()
	{
		ChatClient?.Update();
	}

	protected override void OnApplicationQuit()
	{
		base.OnApplicationQuit();
		ChatClient?.Dispose();
	}

	/// <summary>
	/// 채팅 로그인
	/// </summary>
	public async UniTask InitChattingToken()
	{
		if (ChatClient != null)
		{
			ReDirectionOpenChannel();
			return;
		}
		await UniTask.WaitUntil(() => COMMON.GetUserUID() != null);
		chattingToken = DBManager.Instance.playerData._UserData.chattingInfo.chattingToken;
		if (string.IsNullOrEmpty(chattingToken.nickname))
		{
			await UniTask.WaitUntil(() => !string.IsNullOrEmpty(COMMON.GetUserNickName()));
			DBManager.Instance.playerData._UserData.chattingInfo.chattingToken.nickname = Crypto.Base64Encode(COMMON.GetUserNickName());
			DBManager.Instance.playerData._UserData.chattingInfo.chattingToken.uid = COMMON.GetUserUID();
			COMMON.Save();
		}
		ChatClient = new ChatClient(this, new ChatClientArguments
		{
			UUID = Define.CHATTING_UUID,
			Avatar = Define.CHATTING_AVATAR,
			CustomAccessToken = JsonUtility.ToJson(chattingToken)
		});
	}

	private void SendJoinOpenChannel()
	{
		if (chattingReady) { return; }
		ChatClient.SendJoinOpenChannel("channel", Define.ChattingChannel);
	}

	private void ReDirectionOpenChannel()
	{
		chattingReady = false;
		SendJoinOpenChannel();
	}
	
	/// <summary>
	/// 일반 채팅 메세지
	/// </summary>
	/// <param name="chatMsg"></param>
	public void SendChatMSG(string chatMsg)
	{
		try
		{
			if (!chattingReady)
			{
				COMMON.OnPopUpToast("CHATTING_REDIRECTION_TXT");
				return;
			}

			if (chatMsg == string.Empty) return;

			if (ChatClient == null) return;

			if (CurrentChannelName == string.Empty) return;

			if (!ChannelList.ContainsKey(CurrentChannelGroup))
			{
				return;
			}

			if (!ChannelList[CurrentChannelGroup].ContainsKey(CurrentChannelName)) return;

			if (!ChannelList[CurrentChannelGroup][CurrentChannelName].ContainsKey(CurrentChannelNumber)) return;

			ChannelInfo channelInfo = ChannelList[CurrentChannelGroup][CurrentChannelName][CurrentChannelNumber];

			//COMMON.Chat_LogError($"{channelInfo} : {CurrentChannelGroup}, {CurrentChannelName}, {CurrentChannelNumber}");

			if (channelInfo == null) return;

			var rankData = PlayFabManager.Instance.stageRankInfoList.FirstOrDefault(x => x.Value.UID == DBManager.Instance.playerData._UserData.chattingInfo.chattingToken.uid);

			if (rankData.Value == null) return;

			var nickName = DBManager.Instance.playerData._UserData.userInfo.NickName;
			var msgClass = new SendMsgClass()
			{
				nickName = nickName,
				ranking = rankData.Value.Ranking + 1,
				chatMsg = chatMsg,
				awakenGrade = COMMON.GetUserAwakenLevel(),
				uid = chattingToken.uid
			};

			var msg = JsonUtility.ToJson(msgClass);

			ChatClient.SendChatMessage(channelInfo.ChannelGroup, channelInfo.ChannelName, channelInfo.ChannelNumber, msg);
		}
		catch (Exception e)
		{
			COMMON.Chat_LogError(e);
			return;
		}
	}

	public void SendSystemMSG(string msgIndex, params object[] args)
	{
		try
		{
			if (msgIndex == string.Empty) return;

			if (ChatClient == null) return;

			if (CurrentChannelName == string.Empty) return;

			if (!ChannelList.ContainsKey(CurrentChannelGroup)) return;

			if (!ChannelList[CurrentChannelGroup].ContainsKey(CurrentChannelName)) return;

			if (!ChannelList[CurrentChannelGroup][CurrentChannelName].ContainsKey(CurrentChannelNumber)) return;

			ChannelInfo channelInfo = ChannelList[CurrentChannelGroup][CurrentChannelName][CurrentChannelNumber];
			if (channelInfo == null) return;

			var rankData = PlayFabManager.Instance.stageRankInfoList.FirstOrDefault(x => x.Value.UID == DBManager.Instance.playerData._UserData.chattingInfo.chattingToken.uid);

			if (rankData.Value == null) return;

			var ranking = rankData.Value.Ranking + 1;
			var nickName = COMMON.GetUserNickName();
			var itemIndex = (int)args[0];
			var dropType = (Define.eDataType)args[1];
			var identifyType = args.Length <= 2 ? 0 : (Define.eUnidentifiedOpenResult)args[2];
			//system, rank, nick, textIndex, 0,1
			var msg = string.Format("{0},{1},{2},{3},{4},{5},{6}", "SYSTEM", msgIndex, ranking, nickName, itemIndex, (int)dropType, (int)identifyType);

			ChatClient.SendChatMessage(channelInfo.ChannelGroup, channelInfo.ChannelName, channelInfo.ChannelNumber, msg);
		}
		catch (Exception e)
		{
			COMMON.Chat_LogError(e);
			return;
		}
	}
	private void OnChannelSelected(string channelGroup, string channelName, UInt64 channelNumber)
	{
		if (!ChannelList.ContainsKey(channelGroup)) return;

		if (!ChannelList[channelGroup].ContainsKey(channelName)) return;

		if (!ChannelList[channelGroup][channelName].ContainsKey(channelNumber)) return;

		ChannelInfo channelInfo = ChannelList[channelGroup][channelName][channelNumber];
		if (channelInfo == null) return;

		SelectMessageKey.Clear();


		CurrentChannelGroup = channelGroup;
		CurrentChannelName = channelName;
		CurrentChannelNumber = channelNumber;

		chattingReady = true;
	}

	public void OnJoinChannel(ChannelInfo channelInfo)
	{
		if (ChannelList.ContainsKey(channelInfo.ChannelGroup))
		{
			if (ChannelList[channelInfo.ChannelGroup].ContainsKey(channelInfo.ChannelName))
			{
				if (ChannelList[channelInfo.ChannelGroup][channelInfo.ChannelName].ContainsKey(channelInfo.ChannelNumber))
				{
					chattingReady = true;
					CallLastSystemMsg(channelInfo, true);
					return;
				}
			}
		}

		if (!ChannelList.ContainsKey(channelInfo.ChannelGroup))
		{
			ChannelList.Add(channelInfo.ChannelGroup, new Dictionary<string, Dictionary<UInt64, ChannelInfo>>());
			ChannelList[channelInfo.ChannelGroup].Add(channelInfo.ChannelName, new Dictionary<UInt64, ChannelInfo>());
		}
		else
		{
			if (!ChannelList[channelInfo.ChannelGroup].ContainsKey(channelInfo.ChannelName))
			{
				ChannelList[channelInfo.ChannelGroup].Add(channelInfo.ChannelName, new Dictionary<UInt64, ChannelInfo>());
			}
		}

		ChannelList[channelInfo.ChannelGroup][channelInfo.ChannelName].Add(channelInfo.ChannelNumber, channelInfo);

		OnChannelSelected(channelInfo.ChannelGroup, channelInfo.ChannelName, channelInfo.ChannelNumber);

		CallLastSystemMsg(channelInfo);
	}

	private void CallLastSystemMsg(ChannelInfo channelInfo, bool isRejoin = false)
	{
		//메세지 리스트를 linq로 구분해서 마지막 메세지를 가져온 다음, 플레이어 프리퍼런스에 저장된 시간과 같은지 비교함.
		var messageInfoes = channelInfo.Messages.Where(x => CurrentChannelGroup == x.ChannelGroup
														&& CurrentChannelName == x.ChannelName
														&& CurrentChannelNumber == x.ChannelNumber).ToArray();

		var lastMsgInfo = messageInfoes.LastOrDefault(x => x.MessageType == MESSAGE_TYPE.SYSTEM_MESSAGE);
		if (lastMsgInfo != null)
		{
			var msg = lastMsgInfo.Message;
			//var lastTime = Convert.ToDateTime(lastMsgInfo.Time);

			lastSystemMsg = msg;
			//PlayerPrefs.SetString("LAST_SYSTEM_MSG", $"{lastSystemMsg},{lastTime}");

			var textArr = lastSystemMsg.Split('#');
			if (textArr.Length > 1)
				msg = textArr[1];
			else
				msg = COMMON.GetText(lastSystemMsg);

			var msgData = new MsgData()
			{
				cellType = CellType.SystemText,
				cellSize = 130,
				msgInfo = lastMsgInfo
			};

			consoleSystemMsgInfoList.Add(msgData);
			mainView.SetLastChattingMsg(msgData);
			if (!isRejoin)
				GameEventSubject.SendGameEvent(GameEventType.SYSTEM_CHAT_NOTI, true);
		}
	}

	public void OnLeaveChannel(ChannelInfo channelInfo)
	{
		ReDirectionOpenChannel();
	}

	public void OnJoinChannelPlayer(string channelGroup, string channelName, ulong channelNumber, PlayerInfo player)
	{
		if (!ChannelList.ContainsKey(channelGroup)) return;

		if (!ChannelList[channelGroup].ContainsKey(channelName)) return;

		if (!ChannelList[channelGroup][channelName].ContainsKey(channelNumber)) return;

		ChannelInfo channelInfo = ChannelList[channelGroup][channelName][channelNumber];
		if (channelInfo == null) return;

		if (channelInfo.Players.ContainsKey(player.GamerName)) return;

		channelInfo.Players.Add(player.GamerName, player);

		if (CurrentChannelGroup == channelGroup &&
			 CurrentChannelName == channelName &&
			 CurrentChannelNumber == channelNumber)
		{

		}
	}

	public void OnLeaveChannelPlayer(string channelGroup, string channelName, ulong channelNumber, PlayerInfo player)
	{
		if (!ChannelList.ContainsKey(channelGroup)) return;

		if (!ChannelList[channelGroup].ContainsKey(channelName)) return;

		if (!ChannelList[channelGroup][channelName].ContainsKey(channelNumber)) return;

		ChannelInfo channelInfo = ChannelList[channelGroup][channelName][channelNumber];
		if (channelInfo == null) return;

		if (!channelInfo.Players.ContainsKey(player.GamerName)) return;

		channelInfo.Players.Remove(player.GamerName);

		if (CurrentChannelGroup == channelGroup &&
			 CurrentChannelName == channelName &&
			 CurrentChannelNumber == channelNumber)
		{

		}
	}

	/// <summary>
	/// 메세지 수신 됐을 때
	/// </summary>
	/// <param name="messageInfo"></param>
	public void OnChatMessage(MessageInfo messageInfo)
	{
		if (!ChannelList.ContainsKey(messageInfo.ChannelGroup)) return;

		if (!ChannelList[messageInfo.ChannelGroup].ContainsKey(messageInfo.ChannelName)) return;

		if (!ChannelList[messageInfo.ChannelGroup][messageInfo.ChannelName].ContainsKey(messageInfo.ChannelNumber)) return;

		ChannelInfo channelInfo = ChannelList[messageInfo.ChannelGroup][messageInfo.ChannelName][messageInfo.ChannelNumber];
		if (channelInfo == null) return;

		channelInfo.Messages.Add(messageInfo);

		if (CurrentChannelGroup == messageInfo.ChannelGroup &&
			 CurrentChannelName == messageInfo.ChannelName &&
			 CurrentChannelNumber == messageInfo.ChannelNumber)
		{
			//여기에 채팅 매세지 띄우는 부분임 무한 스크롤 뷰 초기화 시켜주면서 채팅 올려줄것

			var msgSplit = messageInfo.Message.Split(',');

			var msgData = new MsgData();

			var msg = "";
			//콘솔에서 날린 시스템 메세지 혹은 유저가 날린 시스템 메세지(아이템 획득 등)
			if (messageInfo.GamerName == "SYSTEM" || msgSplit[0] == "SYSTEM")
			{
				if (chatSystemMsgInfoList.Count > 50)
					chatSystemMsgInfoList.Remove(chatSystemMsgInfoList[0]);

				msgData.cellType = CellType.SystemText;
				msgData.cellSize = 130;
				msgData.msgInfo = messageInfo;

				chatSystemMsgInfoList.Add(msgData);

				if (messageInfo.GamerName == "SYSTEM")
				{
					consoleSystemMsgInfoList.Add(msgData);
					isAlarm = true;
					GameEventSubject.SendGameEvent(GameEventType.SYSTEM_MSG_ALARM);
					msgSplit = msgData.msgInfo.Message.Split('#');
					if (msgSplit.Length > 1)
						msg = msgSplit[1];
					else
						msg = COMMON.GetText(msgData.msgInfo.Message);

					//PlayerPrefs.SetString("LAST_SYSTEM_MSG", $"{msg},{messageInfo.Time}");
					GameEventSubject.SendGameEvent(GameEventType.SYSTEM_CHAT_NOTI, true);
				}
			}
			else
			{
				if (chatUserMsgInfoList.Count > 50)
					chatUserMsgInfoList.Remove(chatUserMsgInfoList[0]);

				msgData.cellType = CellType.ChatText;
				msgData.cellSize = 180;
				msgData.msgInfo = messageInfo;

				var msgClass = JsonUtility.FromJson<SendMsgClass>(msgData.msgInfo.Message);
				//다른 유저가 날린 메세지를 판단하여, 차단 목록에서 UID 확인
				if (msgClass.uid != chattingToken.uid)
				{
					if (COMMON.GetIgnoreList().Count(x => x.uid == msgClass.uid) > 0)
						return;
				}

				chatUserMsgInfoList.Add(msgData);
				msg = msgClass.chatMsg;
			}

			//채팅 팝업에서 처리할 이벤트 날리기
			mainView.SetLastChattingMsg(msgData);

			var chattingPopup = ViewManager.Instance.GetPopUp(Define.ePopup.PopUp_Chatting);
			if (chattingPopup == null || !chattingPopup.gameObject.activeSelf)
				return;

			chattingPopup.RefreshUI();
		}
	}

	#region 뒤끝 채팅관련 미사용 중인 인터페이스들
	public void OnWhisperMessage(WhisperMessageInfo messageInfo)
	{

	}

	public void OnTranslateMessage(List<MessageInfo> messages)
	{

	}

	public void OnHideMessage(MessageInfo messageInfo)
	{

	}

	public void OnDeleteMessage(MessageInfo messageInfo)
	{

	}

	public void OnSuccess(SUCCESS_MESSAGE success, object param)
	{
		COMMON.Chat_LogError(success);
	}

	public void OnError(ERROR_MESSAGE error, object param)
	{
		COMMON.Chat_LogError(error);
		switch (error)
		{
			case ERROR_MESSAGE.UNKNOWN_ERROR:
				COMMON.OnPopUpToast("CHATTING_UNKNOWN_ERROR_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_UNKNOWN_ERROR_TXT"));
				break;
			case ERROR_MESSAGE.WHISPER_OFFLINE:
				break;
			case ERROR_MESSAGE.TOO_MANY_REPORT:
				break;
			case ERROR_MESSAGE.NOT_MY_REPORT:
				break;
			case ERROR_MESSAGE.INVALID_PARAMETER:
				COMMON.OnPopUpToast("CHATTING_INVALID_PARAMETER_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_INVALID_PARAMETER_TXT"));
				break;
			case ERROR_MESSAGE.CHAT_BAN:
				break;
			case ERROR_MESSAGE.DISABLED_CHANNEL:
				ReDirectionOpenChannel();
				break;
			case ERROR_MESSAGE.MESSAGE_TOO_LONG:
				COMMON.OnPopUpToast("CHATTING_MESSAGE_TOO_LONG_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_MESSAGE_TOO_LONG_TXT"));
				break;
			case ERROR_MESSAGE.MESSAGE_TOO_SHORT:
				COMMON.OnPopUpToast("CHATTING_MESSAGE_TOO_SHORT_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_MESSAGE_TOO_SHORT_TXT"));
				break;
			case ERROR_MESSAGE.MESSAGE_FILTERED:
				COMMON.OnPopUpToast("CHATTING_MESSAGE_FILTERED_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_MESSAGE_FILTERED_TXT"));
				break;
			case ERROR_MESSAGE.MESSAGE_SPAM:
				COMMON.OnPopUpToast("CHATTING_SPAM_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_SPAM_TXT"));
				break;
			case ERROR_MESSAGE.NOT_NICKNAME:
				break;
			case ERROR_MESSAGE.DISABLED_SERVICE:
				COMMON.OnPopUpToast("CHATTING_DISABLE_SERVICE_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_DISABLE_SERVICE_TXT"));
				break;
			case ERROR_MESSAGE.CHANNEL_FULL:
				COMMON.OnPopUpToast("CHATTING_CHANNEL_FULL_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_CHANNEL_FULL_TXT"));
				break;
			case ERROR_MESSAGE.NOT_JOIN_CHANNEL:
				COMMON.OnPopUpToast("CHATTING_NOT_JOIN_CHANNEL_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_NOT_JOIN_CHANNEL_TXT"));
				break;
			case ERROR_MESSAGE.DUPLICATE_CHANNEL_GROUP:
				break;
			case ERROR_MESSAGE.CHANNEL_GROUP_TOO_LONG:
				break;
			case ERROR_MESSAGE.CHANNEL_GROUP_TOO_SHORT:
				break;
			case ERROR_MESSAGE.CHANNEL_GROUP_FILTERED:
				break;
			case ERROR_MESSAGE.DUPLICATE_CHANNEL_NAME:
				break;
			case ERROR_MESSAGE.CHANNEL_NAME_TOO_LONG:
				break;
			case ERROR_MESSAGE.CHANNEL_NAME_TOO_SHORT:
				break;
			case ERROR_MESSAGE.CHANNEL_NAME_FILTERED:
				break;
			case ERROR_MESSAGE.INVALID_PASSWORD:
				break;
			case ERROR_MESSAGE.PASSWORD_TOO_LONG:
				break;
			case ERROR_MESSAGE.CHAT_SERVER_FULL:
				COMMON.OnPopUpToast("CHATTING_CHAT_SERVER_FULL_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_CHAT_SERVER_FULL_TXT"));
				break;
			case ERROR_MESSAGE.ALREADY_CREATED_CHANNEL:
				break;
			case ERROR_MESSAGE.LIMIT_REPORT_MESSAGE_DAYS:
				break;
			case ERROR_MESSAGE.ALREADY_JOIN_CHANNEL:
				break;
			case ERROR_MESSAGE.NOT_AUTHENTICATION:
				COMMON.OnPopUpToast("CHATTING_NOT_AUTHENTICATION_TXT");
				//ViewManager.Instance.OnPopUpToast(TextManager.Instance.GetText("CHATTING_NOT_AUTHENTICATION_TXT"));
				break;
			default:
				break;
		}
	}

	public void OnUpdatePlayerInfo(string channelGroup, string channelName, ulong channelNumber, PlayerInfo player)
	{
		throw new NotImplementedException();
	}

	public void OnChangeGamerName(string oldGamerName, string newGamerName)
	{
		throw new NotImplementedException();
	}

	#endregion
}

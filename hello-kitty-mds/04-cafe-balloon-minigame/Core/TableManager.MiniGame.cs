using System;
using System.Collections.Generic;
using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Network;

namespace GameLogic.Management
{
    public class MiniGameManagerTableData
    {
        public int index;       // seq
        public int infoIdx;
        public int missionGidx;
        public int cardGIdx;
        public int rewardGIdx;
        public MasterTimeInfo masterTime;
    }
    public class MiniGameInfoTableData
    {
        public int index;             // 고유 번호
        public int maxBuy;            // 최대 구매 수
        public int buyChargeCooltime; // 구매 가능 충전 시간
        public string miniGameIcon;   // 미니 게임 아이콘	
        public string miniGameMedia;  // 미니 게임 영상 가이드
        public int miniGameAddDate;   // 미니 게임 추가 운영일
        public int openPoint;         // 미니 게임 시작 포인트
    }
    public class MiniGameMissionTableData
    {
        public int index;             // 고유 번호
        public int missionGroupId;    // 그룹 ID
        public string missionImg;
        public MissionConditionType conditionType;
        public int conditionValue;
        public int conditionCount;    // 타입 카운트
        public int missionLIdx;       // 미션 설명
        public int missionDetailLIdx; // 미션 상세 설명
        public int miniGamePoint;     // 미니 게임 포인트
    }
    public class MiniGameCardInfoTableData
    {
        public int index;                   // 고유 번호
        public int cardGroupId;             // 그룹 ID
        public MiniGameCardImgType imgType; // 이미지 타입
        public int shopPackageGroupIdx;     // 이미지 순서
        public string imgPath;              // 이미지 경로
    }
    public class MiniGameRewardTableData
    {
        public int index;         // 고유 번호
        public int rewardGroupId; // 그룹 ID
        public int stepType;      // 보상 스탭 타입
        public int stepNum;       // 보상 스탭 번호
        public ItemType itemType; // 아이템 타입
        public int itemIdx;       // 아이템 번호
        public int itemValue;     // 아이템 수량


        private readonly List<RewardPacketData> rewardPacketDatas = new();
        public List<RewardPacketData> GetRewardPacketDatas()
        {
            if (rewardPacketDatas.IsNullOrEmpty())
            {
                rewardPacketDatas.Add(new RewardPacketData()
                {
                    id = itemIdx,
                    quantity = itemValue,
                    type = itemType
                });
            }
            
            
            return rewardPacketDatas;
        }

        private readonly List<RewardInfo> rewardInfos = new();
        public List<RewardInfo> GetRewardInfos()
        {
            if (rewardInfos.IsNullOrEmpty())
            {
                rewardInfos.Add(new RewardInfo(itemType, itemIdx, itemValue));
            }
            
            
            return rewardInfos;
        }
    }
    
    public partial class TableManager
    {
        private void LoadMiniGameTable()
        {
            AddTableFromResources<MiniGameManagerTableData>(TableDefine.GetTableFileName(TableDefine.ETableName.MiniGame_Manager));
            AddTableFromResources<MiniGameInfoTableData>(TableDefine.GetTableFileName(TableDefine.ETableName.MiniGame_Info));
            AddTableFromResources<MiniGameMissionTableData>(TableDefine.GetTableFileName(TableDefine.ETableName.MiniGame_Mission));
            AddTableFromResources<MiniGameCardInfoTableData>(TableDefine.GetTableFileName(TableDefine.ETableName.MiniGame_CardInfo));
            AddTableFromResources<MiniGameRewardTableData>(TableDefine.GetTableFileName(TableDefine.ETableName.MiniGame_Reward));
            
            WrapperMiniGameTableData();
        }
        
        private void WrapperMiniGameTableData()
        {
        }

        public bool TryGetMiniGameMissionTableData(int missionGroupId, out List<MiniGameMissionTableData> missionTableData)
        {
            missionTableData = new();
            Dictionary<int, MiniGameMissionTableData> table = FindTable<MiniGameMissionTableData>();
            foreach (var data in table.Values)
            {
                if(data.missionGroupId != missionGroupId) continue;
                missionTableData.Add(data);
            }
            return missionTableData.Count > 0;
        }
        public bool TryGetMiniGameCardInfoTableData(int cardGroupId, out List<MiniGameCardInfoTableData> cardInfoTableData)
        {
            cardInfoTableData = new();
            Dictionary<int, MiniGameCardInfoTableData> table = FindTable<MiniGameCardInfoTableData>();
            foreach (var data in table.Values)
            {
                if(data.cardGroupId != cardGroupId) continue;
                cardInfoTableData.Add(data);
            }
            return cardInfoTableData.Count > 0;
        }
        public bool TryGetMiniGameRewardTableData(int rewardGroupId, out List<MiniGameRewardTableData> rewardTableData)
        {
            rewardTableData = new();
            Dictionary<int, MiniGameRewardTableData> table = FindTable<MiniGameRewardTableData>();
            foreach (var data in table.Values)
            {
                if(data.rewardGroupId != rewardGroupId) continue;
                rewardTableData.Add(data);
            }
            return rewardTableData.Count > 0;
        }
    }
}  

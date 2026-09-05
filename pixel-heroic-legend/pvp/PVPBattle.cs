using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using UniRx;
using UnityEngine;
using static Define;

public class PVPBattle : Battle
{
    public enum Difficulty
    {
        easy = -1,
        normal,
        hard,
    }
    #region 변수

    /// <summary>
    /// 초 단위 기믹을 효과를 주기 위해 체크되는 시간 변수
    /// </summary>
    private int TimeTick = 0;
    /// <summary>
    /// 기믹을 주기위한 기준 시간 (초)
    /// </summary>
    private int IntervalTimeVal = 5;

    /// <summary>
    /// 내 체력
    /// </summary>
    public ReactiveProperty<BigInteger> myHp;
    /// <summary>
    /// 상대방 체력
    /// </summary>
    public ReactiveProperty<BigInteger> otherHp;
    /// <summary>
    /// 현재 PVP 난이도
    /// </summary>
    public Difficulty curDifficulty = Difficulty.normal;

    /// <summary>
    /// 배틀 준비
    /// </summary>
    public bool battleReady;

    /// <summary>
    /// 현재 교전 중인 유저의 랭킹데이터
    /// </summary>
    public RankData currentRankData;
    #endregion


    public override void InitBattle()
    {
        PlayFabManager.Instance.GetMyPVPRankingData().Forget();
        base.InitBattle();
        TimeTick = 0;
        battleTime = STAGE.Ranking_Dungeon.Ranking_DungeonList[0].ClearTime;

        SetPvpType();

        for (var i = 0; i < PlayerManager.Instance.players.Count; i++)
        {
            SetUnit(PlayerManager.Instance.players[i], i, UnitType.Player);
            SetUnit(PlayerManager.Instance.PVP_Players[i], i, UnitType.Enemy);
        }

        BattleManager.Instance.battleChange = false;

    }

    /// <summary>
    /// 유닛 위치, 방향, 스킬 설정
    /// </summary>
    /// <param name="unit"></param>
    /// <param name="type"></param>
    public void SetUnit(Player unit, int idx, UnitType type)
    {
        unit.Init();
        unit.ChangeState(State.NONE);

        switch (type)
        {
            case UnitType.Player:
                {
                    unit.transform.position = EnemyManager.Instance.PvpBattle_PlayerSpawnPoint[idx].position;
                    unit.rigid.position = EnemyManager.Instance.PvpBattle_PVP_PlayerSpawnPoint[idx].position;

                    var val = unit.hpBackBar.transform.localScale.x;
                    unit.img.transform.localScale = new UnityEngine.Vector3(0.01f * val, unit.img.transform.localScale.y, unit.img.transform.localScale.z);

                    var lv = DBManager.Instance.playerData._UserData.characterInfo.CharacterList[idx].LvVal;
                    var logValue = (decimal)0.000000000003 * (decimal)Math.Pow(lv, 5f);
                    if (logValue <= 1)
                        logValue = 1;
                    unit.data.buff_HP += (BigInteger)((decimal)unit.data.Hp * logValue);

                    var defValue = (decimal)unit.data.Def * (decimal)0.00000103 * (decimal)Math.Pow(lv, 2.8f);
                    unit.data.buff_DEF = (BigInteger)defValue;

                    var atkValue = (decimal)unit.data.Atk * (decimal)0.3f;
                    unit.data.buff_ATK -= (BigInteger)atkValue;
                    break;
                }
            case UnitType.Enemy:
                {
                    unit.data.isAdBuffTarget = false;

                    unit.gameObject.SetActive(true);

                    unit.transform.position = EnemyManager.Instance.PvpBattle_PVP_PlayerSpawnPoint[idx].position;
                    unit.rigid.position = EnemyManager.Instance.PvpBattle_PVP_PlayerSpawnPoint[idx].position;
                    
                    var pvpplayer = unit as PVP_Player;
                    var info = PlayFabManager.Instance.pvpPlayerData._UserData.characterInfo.CharacterList[idx];
                    var avataSkinID = info.ViewEquipAvataID == -1 ? info.EquipAvataID : info.ViewEquipAvataID;
                    var mainWeaponSkinID = info.ViewEquipMainWeaponID == -1 ? info.EquipMainWeaponID : info.ViewEquipMainWeaponID;
                    var subWeaponSkinID = info.ViewEquipSubWeaponID == -1 ? info.EquipSubWeaponID : info.ViewEquipSubWeaponID;
                    pvpplayer.SetSkin(avataSkinID, mainWeaponSkinID, subWeaponSkinID);

                    var lv = PlayFabManager.Instance.pvpPlayerData._UserData.characterInfo.CharacterList[idx].LvVal;
                    var logValue = (decimal)0.000000000003 * (decimal)Math.Pow(lv, 5f);
                    if (logValue <= 1)
                        logValue = 1;
                    unit.data.buff_HP += (BigInteger)((decimal)unit.data.Hp * logValue);

                    var defValue = (decimal)unit.data.Def * (decimal)0.00000103 * (decimal)Math.Pow(lv, 2.8f);
                    unit.data.buff_DEF = (BigInteger)defValue;

                    var atkValue = (decimal)unit.data.Atk * (decimal)0.3f;
                    unit.data.buff_ATK -= (BigInteger)atkValue;
                    break;
                }
        }

        unit.StatInit();

        var size = unit.unitAnim.transform.localScale.y;
        unit.unitAnim.transform.localScale = type == UnitType.Player ? UnityEngine.Vector3.one * size : UnityEngine.Vector3.one * size + UnityEngine.Vector3.left * size * 2f;
        unit.hpBackBar.transform.localScale = type == UnitType.Player ? new UnityEngine.Vector3(1, 1, 1) : new UnityEngine.Vector3(-1, 1, 1);
        unit.mpBar.transform.localScale = type == UnitType.Player ? new UnityEngine.Vector3(1, 1, 1) : new UnityEngine.Vector3(-1, 1, 1);
    }

    public override void LoadBattleGround()
    {
        base.LoadBattleGround();

        BattleManager.Instance.light.intensity = 2f;
    }

    public override async void BattleStart()
    {
        mainview?.SetStageInfo();
        //플레이어 상태 초기화
        foreach (var player in PlayerManager.Instance.players)
        {
            player.ChangeState(State.NONE);
            player.ChangeState(State.IDLE);

            PlayerManager.Instance.playerGroup.AddMember(player.transform, 1f, 0);
            PlayerManager.Instance.AddCameraTarget(player.transform);
            player.selectEffect.gameObject.SetActive(true);
        }
        //PVP 플레이어 상태 초기화
        foreach (var player in PlayerManager.Instance.PVP_Players)
        {
            player.ChangeState(State.NONE);
            player.ChangeState(State.IDLE);

            PlayerManager.Instance.playerGroup.AddMember(player.transform, 1f, 0);
            PlayerManager.Instance.AddCameraTarget(player.transform);
            player.selectEffect.gameObject.SetActive(true);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        for (int i = 0; i < PlayerManager.Instance.players.Count; i++)
        {
            if (Define.CharacterUnlockIDs[i] == -1)
                continue;

            if (TutorialManager.Instance.IsUnlockContents(Define.CharacterUnlockIDs[i]) == false)
            {
                PlayerManager.Instance.players[i].SetVisible(false);
                PlayerManager.Instance.players[i].ChangeState(State.DEAD);
            }
            else
            {
                PlayerManager.Instance.players[i].SetVisible(true);
            }
        }

        for (int i = 0; i < PlayerManager.Instance.PVP_Players.Count; i++)
        {
            if (Define.pvpCharacterUnlockIDs[i] == -1)
                continue;

            if (TutorialManager.Instance.IsUnlockContents(Define.pvpCharacterUnlockIDs[i]) == false)
            {
                PlayerManager.Instance.PVP_Players[i].SetVisible(false);
                PlayerManager.Instance.PVP_Players[i].ChangeState(State.DEAD);
            }
            else
            {
                PlayerManager.Instance.PVP_Players[i].SetVisible(true);
            }
        }

#endif 

        //몬스터 소환 시작
        EnemyManager.Instance.SpawnStart(Define.eBATTLETYPE.ePVPBattle);

        PlayerManager.Instance.ViewOneTarget(PlayerManager.Instance.players[0].transform);

        await UniTask.WaitUntil(() => battleReady);

        base.BattleStart();
    }

    public override void BattleClear(Define.eBattleClearType type)
    {
        var otherUid = PlayFabManager.Instance.pvpPlayerUID;
        var idx = DBManager.Instance.playerData._UserData.pvpInfo.pvpUIDList.IndexOf(otherUid);

        DBManager.Instance.playerData._DungeonStageData.dungeonInfo.dailyPVPCount++;
        if (DBManager.Instance.playerData._DungeonStageData.dungeonInfo.freePVPTicket > 0)
        {
            DBManager.Instance.playerData._DungeonStageData.dungeonInfo.freePVPTicket--;
        }
        else
        {
            DBManager.Instance.playerData._DungeonStageData.dungeonInfo.PVPTicket--;
            COMMON.Instance.EconomyAdd(9000302, -1, true);
        }

        switch (type)
        {
            case Define.eBattleClearType.Victory:
                {
                    DBManager.Instance.playerData._DungeonStageData.dungeonInfo.winCount++;
                    var winningPoint = 0;
                    switch (curDifficulty)
                    {
                        case Difficulty.easy:
                            winningPoint = 8;
                            break;
                        case Difficulty.normal:
                            winningPoint = 12;
                            break;
                        case Difficulty.hard:
                            winningPoint = 20;
                            break;
                    }
                    DBManager.Instance.playerData._UserData.pvpInfo.pvpResultList[idx] = 1;
                    var pointText = string.Format(TextManager.Instance.GetText("SCORE_0_TXT"), winningPoint);


                    ViewManager.Instance.OnPopUp(Define.ePOPUP.BattleClearPopUp, null, Define.eBATTLETYPE.ePVPBattle, "VICTORY_TXT", $" + {pointText}", curDifficulty,
                    (Action)(() =>
                    {

                    }),
                    (Action)(() =>
                    {
                        BattleManager.Instance.SetBattle(Define.eBATTLETYPE.eStage);
                        ViewManager.Instance.OnPopUp(Define.ePOPUP.ArenaPopUp, ViewManager.Instance.GetUIView(Define.eVIEW.MainView), null);
                    })
                    );

                    DBManager.Instance.playerData._UserData.MissionClear(Define.eMissionType.eAll, Define.eMissionClearType.eArenaVictory, 1);
                    DBManager.Instance.playerData._UserData.pvpInfo.myRankData.PVPScore += winningPoint;
                    LogManager.Instance.LogWrite(eLogTypeIdx.ePVPVictory, string.Format("{0}에게 승리, 승점 {1}추가 [총 승점{2}]", currentRankData.NickName, winningPoint, DBManager.Instance.playerData._UserData.pvpInfo.myRankData.PVPScore));
                    break;
                }
            case Define.eBattleClearType.Faile:
                {
                    DBManager.Instance.playerData._DungeonStageData.dungeonInfo.loseCount++;
                    var winningPoint = 0;
                    switch (curDifficulty)
                    {
                        case Difficulty.easy:
                            winningPoint = 1;
                            break;
                        case Difficulty.normal:
                            winningPoint = 5;
                            break;
                        case Difficulty.hard:
                            winningPoint = 8;
                            break;
                    }

                    DBManager.Instance.playerData._UserData.pvpInfo.pvpResultList[idx] = -1;
                    var pointText = string.Format(TextManager.Instance.GetText("SCORE_0_TXT"), winningPoint);

                    ViewManager.Instance.OnPopUp(Define.ePOPUP.BattleFailPopUp, null, Define.eBATTLETYPE.ePVPBattle, "LOSE_TXT", $"+ {pointText}", (int)curDifficulty,
                    (Action)(() =>
                    {

                    }),
                    (Action)(() =>
                    {
                        BattleManager.Instance.SetBattle(Define.eBATTLETYPE.eStage);
                        ViewManager.Instance.OnPopUp(Define.ePOPUP.ArenaPopUp, ViewManager.Instance.GetUIView(Define.eVIEW.MainView), null);
                    }));
                    DBManager.Instance.playerData._UserData.pvpInfo.myRankData.PVPScore += winningPoint;
                    LogManager.Instance.LogWrite(eLogTypeIdx.ePVPVictory, string.Format("{0}에게 패배, 승점 {1}추가 [총 승점{2}]", currentRankData.NickName, winningPoint, DBManager.Instance.playerData._UserData.pvpInfo.myRankData.PVPScore));
                    break;
                }
        }

        Time.timeScale = 1f;

        PlayFabManager.Instance.SetPVPRankingData(DBManager.Instance.playerData._UserData.pvpInfo.myRankData.PVPScore);
        PlayFabManager.Instance.DataSave(true);

        //PVP 전투 플레이 카운트 업
        DBManager.Instance.playerData._UserData.MissionClear(Define.eMissionType.eAll, Define.eMissionClearType.ePVPBattlePlay);
        battleReady = false;
    }

    public override void TimeCheck()
    {
        base.TimeCheck();

        TimeTick++;
    }

    private void SetPvpType()
    {
        var nowTime = COMMON.Instance.GetNTPTime();
        var weeklyTime = COMMON.Instance.TimeToDateTime(DBManager.Instance.playerData._UserData.missionInfo.WeeklyDateTime);
        int nowWeekOfYear = CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(nowTime, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
        int weeklyWeekOfYear = CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(weeklyTime, CalendarWeekRule.FirstDay, DayOfWeek.Monday);

        if (((nowTime.Year == weeklyTime.Year + 1 && nowWeekOfYear == 1 && weeklyWeekOfYear == 53) ||
                           (weeklyTime.Year == nowTime.Year + 1 && weeklyWeekOfYear == 1 && nowWeekOfYear == 53)))
            COMMON.Instance.WeekPVPType = (Define.ePVPType)(weeklyWeekOfYear % 4);
        else
            COMMON.Instance.WeekPVPType = (Define.ePVPType)(nowWeekOfYear % 4);
    }
}

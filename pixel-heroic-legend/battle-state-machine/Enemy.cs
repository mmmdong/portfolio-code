using Cysharp.Threading.Tasks;
using DG.Tweening;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using UnityEngine;

/// <summary>
/// 몬스터를 포함한 모든 적
/// </summary>
public class Enemy : Unit
{
    /// <summary>
    /// 공격받았을 때 속도 증가 구분
    /// </summary>
    public bool isAggressive;
    /// <summary>
    /// 자신이 속한 부대
    /// </summary>
    public Squad squad;
    /// <summary>
    /// 몬스터 등급
    /// </summary>
    public Define.eMonsterGradeType monsterGrade = Define.eMonsterGradeType.eNormal;

    public override void Init(int unitID = 0)
    {
        this.unitID = unitID;

        base.Init();

        //절전모드 사용 여부에 따른 스파인 셋팅
        SetPowerSaveMode(GameManager.Instance.SaveModeEnableCk);

        ChangeState(State.IDLE);
    }

    public override async UniTask IdleAsync()
    {
        await base.IdleAsync();

        await UniTask.WaitUntil(() => PlayerManager.Instance.players.Count(x => x.state.Value != State.DEAD) > 0, cancellationToken: cts.Token);

        FindEnemy();
    }

    public override async UniTask DeadAsync()
    {
        DisAggressive();
        var targetListContainThis = PlayerManager.Instance.players.Where(x => x.targetEnemy == this).ToList();
        for (var i = 0; i < targetListContainThis.Count; i++)
        {
            targetListContainThis[i].targetEnemy = null;
        }

        DBManager.Instance.playerData._UserData.QuestTypeToCountUp(Define.eQuestType.eMonsterKill);

        //성장 던전일 경우 아이템 드랍을 하지 않는다.
        if (BattleManager.Instance.curBattleType != Define.eBATTLETYPE.eStage)
        {
            await base.DeadAsync();
            return;
        }

        var stageDropRate = EnemyManager.Instance.StageDropRate;
        var stageGoldRate = EnemyManager.Instance.StageGoldRate;
        var chapterDifference = EnemyManager.Instance.ChapterDifference;

        //드랍 아이템 떨구기
        if (STAGE.Monster.MonsterMap.TryGetValue(unitID, out var monster))
        {
            //var monster = STAGE.Monster.MonsterMap[unitID];
            //카드 드랍
            if (monster.CardDropID != 0 && monster.CardDrop != 0)
            {
                var stageCountMap = DBManager.Instance.playerData._UserData.cardFeverInfo.StageCountData;
                var dic = Random.Range(0.000000f, 1.000000f);
                var per = Mathf.Min(monster.CardDrop * PlayerManager.Instance.currentCharacter.data.Final_Card_Drop_Up, 1) * stageDropRate;
                bool isDrop = 1f - (per) <= dic;

                if (isDrop == false && monster.CardFeverMax > 0)
                {
                    var userValue = stageCountMap.TryGetValue(monster.ID, out var value) ? value : 0;
                    float feverValue = (float)userValue / monster.CardFeverMax;
                    dic = Random.Range(0.000000f, 1.000000f);

                    if (chapterDifference <= 5)
                    {
                        isDrop = 1f - Mathf.Pow(feverValue, 5.0f) <= dic;
                    }
                    else
                    {
                        var chapterValue = Mathf.Pow(chapterDifference - 5, 2);
                        var dropPer = Mathf.Pow(userValue / (monster.CardFeverMax + chapterValue), 5);
                        isDrop = 1f - dropPer <= dic;
                    }

                    if (isDrop)
                    {
                        LogManager.Instance.LogWrite(eLogTypeIdx.eSystem, string.Format("카드 피버 드랍 획득 {0}/{1}", userValue, monster.CardFeverMax));
                    }
                }

                if (isDrop)
                {
                    COMMON.Instance.RewardItemAdd(new Dictionary<int, BigInteger> { { monster.CardDropID, 1 } });
                    var mainView = ViewManager.Instance.GetUIView(Define.eVIEW.MainView) as MainView;
                    mainView.ViewOnDropCard(monster.CardDropID);

                    if (DBManager.Instance.playerData._UserData.tutorialInfo.FirstCardObtained == 1)
                    {
                        DBManager.Instance.playerData._UserData.tutorialInfo.FirstCardObtained = 0;
                        TutorialManager.Instance.CheckAndExecuteTutorial(eTutorialActiveLocation.MainView);
                    }


                    stageCountMap[monster.ID] = 0;
                    mainView.UpdateFeverCard(monster.ID);
                    PlayFabManager.Instance.DataSave(true);
                }
            }

            //유물 드랍
            if (monster.RelicDropID != 0)
            {
                var dic = Random.Range(0.000000f, 1.000000f);
                if (1f - (monster.RelicDropPer * stageDropRate) <= dic)
                {
                    StageBattle.AddStageDroppedItems(monster.RelicDropID, 1);
                    COMMON.Instance.RewardItemAdd(monster.RelicDropID, 1);
                    if (DBManager.Instance.playerData._UserData.tutorialInfo.FirstRelicObtained == 1)
                    {
                        DBManager.Instance.playerData._UserData.tutorialInfo.FirstRelicObtained = 0;
                        TutorialManager.Instance.CheckAndExecuteTutorial(eTutorialActiveLocation.MainView);
                    }

                    PlayFabManager.Instance.DataSave(true);
                }
            }

            //영혼석 드랍
            if (BattleManager.Instance.curBattleType == Define.eBATTLETYPE.eStage)
            {
                if (monster.SoulPoint != 0)
                {
                    var addSoul = COMMON.Instance.StatusCal(monster.SoulPoint, new List<float>() { PlayerManager.Instance.currentCharacter.data.Final_Spirit_Stones_Drop_Up != 0 ? PlayerManager.Instance.currentCharacter.data.Final_Spirit_Stones_Drop_Up * 100 : 100f });
                    var dic = Random.Range(0.000000f, 1.000000f);
                    var per = Mathf.Min(monster.SoulPointPer * PlayerManager.Instance.currentCharacter.data.Final_Spirit_Stones_Drop_Up, 1) * stageDropRate;

                    if (1f - (per) <= dic)
                    {
                        DBManager.Instance.playerData._UserData.AddSoulPoint(monster.SoulPoint);
                        EffectManager.Instance.GetParticleImgEffect_Monster("AttractSoulStoneEffect", rigid.position, 1, 9200000 /*영혼 이미지*/);
                    }
                }
            }

            var costMap = TABLE.Cost.CostMap;
            Debug.Assert(STAGE.MonsterCostume.DropItemList.ContainsKey(unitID), string.Format("몬스터 테이블에 ID가 없다 {0}", unitID));
            if (STAGE.MonsterCostume.DropItemList.TryGetValue(unitID, out var dropItemList))
            {
                for (int i = 0; i < dropItemList.Count; i++)
                {
                    var dic = Random.Range(0.000000f, 1.000000f);
                    var per = dropItemList[i].Per;
                    var id = dropItemList[i].ID;

                    if (costMap.ContainsKey(id))
                    {
                        var cost = costMap[id];
                        if (COMMON.Instance.IsExpStones((Define.eCURRENCYTYPE)id))
                        {
                            per *= PlayerManager.Instance.currentCharacter.data.Final_Exp_Stones_Drop_Up;
                        }
                        else if (cost.Type == (int)Define.eEconomyType.eConsumItem_Box)
                        {
                            per *= PlayerManager.Instance.currentCharacter.data.Final_Box_Drop_Up;
                        }
                    }

                    per = Mathf.Min(per, 1) * stageDropRate;

                    if (1f - per <= dic)
                    {
                        StageBattle.AddStageDroppedItems(dropItemList[i].ID, 1);
                        COMMON.Instance.RewardItemAdd(dropItemList[i].ID, 1);
                    }
                }
            }

            //골드 드랍 
            //스테이지에서 얻은 골드는 로그를 바로바로 찍지 않고 모아서 한번에 찍는다
            var addGold = COMMON.Instance.StatusCal(monster.GlodDrop, new List<float>() { PlayerManager.Instance.currentCharacter.data.Final_Gold_Drop_Up != 0 ? PlayerManager.Instance.currentCharacter.data.Final_Gold_Drop_Up * 100 : 100f });
            addGold = COMMON.Instance.MultiplyBigIntegerAndFloat(addGold, stageGoldRate);
            COMMON.Instance.EconomyAdd((int)Define.eCURRENCYTYPE.eGold, addGold, false);
            StageBattle.stageGetTotalGoldVal += addGold;
            StageBattle.AddStageDroppedItems((int)Define.eCURRENCYTYPE.eGold, addGold);

            //치우의 축복이 활성화 되었다면 치우의 축복 카운트 체크
            if (DBManager.Instance.playerData._UserData.BlessChiwooApplyCk())
                DBManager.Instance.playerData._UserData.BlessChiwooCountCk(monster.BlessConsumeVal);

            //골드 날아가는 이펙트 기능
            switch (BattleManager.Instance.curBattleType)
            {
                case Define.eBATTLETYPE.eStage:
                    {
                        var count = 0;
                        switch (monsterGrade)
                        {
                            case Define.eMonsterGradeType.eNormal:
                                {
                                    //일반 몬스터 처리
                                    DBManager.Instance.playerData._UserData.MissionClear(Define.eMissionType.eAll, Define.eMissionClearType.eNormalMonsterKillCount);
                                    break;
                                }
                            case Define.eMonsterGradeType.eElite:
                                {
                                    count = 5;
                                    //엘리트 몬스터 처리
                                    DBManager.Instance.playerData._UserData.MissionClear(Define.eMissionType.eAll, Define.eMissionClearType.eEliteMonsterKillCount);
                                    break;
                                }
                            case Define.eMonsterGradeType.eStageBoss:
                                {
                                    count = 10;
                                    //스테이지 보스 몬스터 처리
                                    DBManager.Instance.playerData._UserData.MissionClear(Define.eMissionType.eAll, Define.eMissionClearType.eBossMonsterKillCount);
                                    break;
                                }
                            case Define.eMonsterGradeType.eChapterBoss:
                                {
                                    count = 20;
                                    //챕터 보스 몬스터 처리
                                    DBManager.Instance.playerData._UserData.MissionClear(Define.eMissionType.eAll, Define.eMissionClearType.eChapterBossMonsterKillCount);
                                    break;
                                }
                        }

                        if (count != 0)
                            EffectManager.Instance.GetParticleImgEffect_Monster("AttractCurrencyEffect", rigid.position, count, (int)Define.eCURRENCYTYPE.eGold);
                        //몬스터 사망시 카운트 업
                        DBManager.Instance.playerData._UserData.MissionClear(Define.eMissionType.eAll, Define.eMissionClearType.eAllMonsterKillCount);
                        break;
                    }
            }
        }
        else
            Debug.LogError(string.Format("몬스터 테이블에 ID가 없다 {0}", unitID));

        await base.DeadAsync();
    }

    protected override void OnDeadEffect()
    {
        base.OnDeadEffect();
        if (this is not Monster)
            EnemyManager.Instance.DestroyEnemy(this);
    }

    public override void FindEnemy()
    {
        base.FindEnemy();
        var targetList = new HashSet<Unit>();

        //가장 가까운 거리에 있는 타겟 찾는 함수
        //전투 타입에 따라 분기 된다
        switch (BattleManager.Instance.curBattleType)
        {
            case Define.eBATTLETYPE.eWarriorGrowthDungeon:
            case Define.eBATTLETYPE.eMageGrowthDungeon:
            case Define.eBATTLETYPE.eArcherGrowthDungeon:
            case Define.eBATTLETYPE.eHealerGrowthDungeon:
                {
                    var battle = BattleManager.Instance.battle as GrowthDungeonBattle;
                    var tower = battle.TowerUnit.Where(x => x.state.Value != State.DEAD || x.hp.Value > 0).OrderBy(x => (rigid.position - x.rigid.position).sqrMagnitude).ToHashSet();
                    if (tower.Count > 0)
                    {
                        targetEnemy = tower.FirstOrDefault();
                        ChangeState(State.MOVE);
                        return;
                    }

                    targetList = COMMON.Instance.GetEnemyList(this).Where(x => (x.state.Value != State.DEAD || x.state.Value != State.NONE)).OrderBy(x => (rigid.position - x.rigid.position).sqrMagnitude).ToHashSet();
                    break;
                }
            default:
                {
                    targetList = COMMON.Instance.GetEnemyList(this, isDisarray.Value).Where(x => x.state.Value != State.DEAD && x != this).OrderBy(x => (rigid.position - x.rigid.position).sqrMagnitude).ToHashSet();
                    break;
                }
        }


        //더 이상 잔존하는 적이 없으면 IDLE
        if (targetList.Count == 0)
        {
            ChangeState(State.IDLE);
            return;
        }

        targetEnemy = targetList.FirstOrDefault();

        ChangeState(State.MOVE);
    }



    /// <summary>
    /// 공격 받았을 때 이속 증가 (부대 단위로 이속이 증가함)
    /// </summary>
    public virtual void OnAggressive()
    {
        if (isAggressive || state.Value == State.DEAD) return;

        unitSpeed *= 3f;

        isAggressive = true;
    }

    /// <summary>
    /// 죽었을 시 다시 속도 초기화
    /// </summary>
    protected virtual void DisAggressive()
    {
        if (!isAggressive) return;

        isAggressive = false;
    }

    /// <summary>
    /// 절전 모드 설정 
    /// </summary>
    /// <param name="enable">활성화 여부</param>
    private void SetPowerSaveMode(bool enable)
    {
        meshRenderer.enabled = !enable;
        //if (enable)
        //{
        //    meshRenderer.enabled = !enable;
        //}
        //else
        //    unitAnim.skeletonDataAsset = unitAnim.skeletonDataAsset;
    }
}

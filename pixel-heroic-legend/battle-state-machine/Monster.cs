using Cysharp.Threading.Tasks;
using DG.Tweening;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using UnityEngine;

public class Monster : Enemy
{
    #region
    /// <summary>
    /// 스킬을 사용하는 몬스터의 쿨타임
    /// </summary>
    //public float skillCoolTime;
    /// <summary>
    /// 체력 퍼센트에 따른 체력 구간 별 스킬 사용 여부
    /// </summary>
    protected bool[] isSkillUse;
    /// <summary>
    /// 몬스터 속성값
    /// </summary>
    public int elementalType = (int)Define.eELMTYPE.eNormalElm;
    /// <summary>
    /// 몬스터 체력UI
    /// </summary>
    private MonsterHpUI monsterHpUI;
    #endregion
    #region 사용자 정의 함수

    protected override void Awake()
    {
        base.Awake();
        monsterHpUI = GetComponentInChildren<MonsterHpUI>();
    }

    public override void Init(int unitID = 0)
    {
        var monsterInfo = STAGE.Monster.MonsterMap[unitID];
        attackType = (AttackType)monsterInfo.MonsterType;
        projectileName = monsterInfo.MonsterProjectile;
        base.Init(unitID);

        unitAnim.transform.localRotation = UnityEngine.Quaternion.Euler(30f, 0f, 0f);

        var mpb = new MaterialPropertyBlock();
        var splitValue = 0f;

        var color = Color.clear;
        ColorUtility.TryParseHtmlString("#FFBF00", out color);
        mpb.SetColor("_GlowColor", color);

        DOTween.To(() => splitValue, x => splitValue = x, 1f, 1f).SetEase(Ease.Linear).OnUpdate(() =>
        {
            mpb.SetFloat("_SplitValue", splitValue);
            meshRenderer.SetPropertyBlock(mpb);
        });

        if (BattleManager.Instance.curBattleType == Define.eBATTLETYPE.eUnderground_Maze)
            unitAnim.transform.localRotation = UnityEngine.Quaternion.Euler(0f, 0f, 0f);
        else
            unitAnim.transform.localRotation = UnityEngine.Quaternion.Euler(30f, 0f, 0f);

        if (this is Monster_Normal || this is Monster_Elite)
            monsterHpUI.Init(attackType, monsterGrade);

    }

    protected override void AttackEventCallBack(bool isHeavyAtk)
    {
        //일반 공격일 때
        if (!isHeavyAtk)
        {
            if (attackEffect != string.Empty)
            {
                var effect = EffectManager.Instance.GetEffect(attackEffect);
                effect.transform.position = muzzleFollower.transform.position;
                if (targetEnemy != null)
                    effect.transform.LookAt(targetEnemy.rigid.position);
                effect.Play();
            }
        }

        switch (attackType)
        {
            case AttackType.Melee:
            case AttackType.Mix:
                if (targetEnemy == null)
                    return;

                if ((targetEnemy.rigid.position - rigid.position).magnitude > attackRange)
                    return;
                break;
        }

        base.AttackEventCallBack(isHeavyAtk);
    }

    public override async UniTask DeadAsync()
    {
        await base.DeadAsync();

        var mpb = new MaterialPropertyBlock();
        var splitValue = 1f;
        var duration = unitAnim.AnimationState.Tracks.Items[0].AnimationEnd;

        var color = Color.clear;
        ColorUtility.TryParseHtmlString("#FF4B00", out color);
        mpb.SetColor("_GlowColor", color);

        DOTween.To(() => splitValue, x => splitValue = x, 0f, duration).SetEase(Ease.Linear).OnUpdate(() =>
        {
            mpb?.SetFloat("_SplitValue", splitValue);
            meshRenderer?.SetPropertyBlock(mpb);
        }).OnComplete(() =>
        {
            EnemyManager.Instance.DestroyEnemy(this);
        });

        var deadEffect = EffectManager.Instance.GetEffect("DeadEffect");
        deadEffect.transform.position = rigid.position;
        deadEffect.Play();
    }

    /// <summary>
    /// 스테이지에 설정된 스탯값 몬스터에게 설정
    /// </summary>
    public void SetMonsterStat()
    {
        var monsterInfo = STAGE.Monster.MonsterMap[unitID];
        
        switch (BattleManager.Instance.curBattleType)
        {
            case Define.eBATTLETYPE.eStage:
                {
                    var stageinfo = STAGE.StageBattle.StageBattleList.Find(item => item.StageIndex == DBManager.Instance.playerData._DungeonStageData.stageInfo.curStageIdx);
                    if (stageinfo != null)
                    {
                        //유닛 데이터값 전체 초기화
                        data = new UnitData(this);

                        data.b_ACU = BigInteger.Parse(stageinfo.needacu);
                        data.b_PEN = BigInteger.Parse(stageinfo.needpen);

                        switch (monsterGrade)
                        {
                            case Define.eMonsterGradeType.eNormal:
                                {
                                    fullHp = data.b_HP = BigInteger.Parse(stageinfo.nomalHP);
                                    hp.Value = fullHp;
                                    data.b_ATK = BigInteger.Parse(stageinfo.nomalATK);
                                    SetElmDmg((Define.eELMTYPE)stageinfo.elemental, BigInteger.Parse(stageinfo.nomalEATK));

                                    break;
                                }
                            case Define.eMonsterGradeType.eElite:
                                {
                                    fullHp = data.b_HP = BigInteger.Parse(stageinfo.eliteHP);
                                    hp.Value = fullHp;
                                    data.b_ATK = BigInteger.Parse(stageinfo.eliteATK);
                                    SetElmDmg((Define.eELMTYPE)stageinfo.elemental, BigInteger.Parse(stageinfo.eliteEATK));

                                    break;
                                }
                            case Define.eMonsterGradeType.eStageBoss:
                                {
                                    fullHp = data.b_HP = BigInteger.Parse(stageinfo.bossHP);
                                    hp.Value = fullHp;
                                    data.b_ATK = BigInteger.Parse(stageinfo.bossATK);
                                    SetElmDmg((Define.eELMTYPE)stageinfo.elemental, BigInteger.Parse(stageinfo.bossEATK));

                                    SkillSetting();

                                    break;
                                }
                            case Define.eMonsterGradeType.eChapterBoss:
                                {
                                    fullHp = data.b_HP = BigInteger.Parse(stageinfo.chapterbossHP);
                                    hp.Value = fullHp;
                                    data.b_ATK = BigInteger.Parse(stageinfo.chapterbossATK);
                                    SetElmDmg((Define.eELMTYPE)stageinfo.elemental, BigInteger.Parse(stageinfo.chapterbossEATK));

                                    SkillSetting();

                                    break;
                                }
                        }
                    }
                    else
                        Debug.LogError(string.Format("스테이지 배틀 테이블 스테이지인덱스에 정보가 없음 : {0}", DBManager.Instance.playerData._DungeonStageData.stageInfo.curStageIdx));

                    break;
                }
            case Define.eBATTLETYPE.eUnderground_Maze:
                {
                    var stageinfo = STAGE.Underground_Maze.Underground_MazeList.Find(item => item.stage == DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurUnderGroundDGIdx);
                    if (stageinfo != null)
                    {
                        //유닛 데이터값 전체 초기화
                        data = new UnitData(this);

                        data.b_ACU = BigInteger.Parse(stageinfo.NeedAcu);
                        data.b_PEN = BigInteger.Parse(stageinfo.NeedPen);
                        
                        switch (monsterGrade)
                        {
                            case Define.eMonsterGradeType.eNormal:
                                {
                                    switch (attackType)
                                    {
                                        case AttackType.Melee:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.N_M_1_HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.N_M_1_ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.N_M_1_EATK));
                                                break;
                                            }
                                        case AttackType.Ranged:
                                        case AttackType.Magic:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.N_M_2_HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.N_M_2_ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.N_M_2_EATK));
                                                break;
                                            }
                                    }

                                    break;
                                }
                            case Define.eMonsterGradeType.eElite:
                                {
                                    switch (attackType)
                                    {
                                        case AttackType.Melee:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.E_M_1_HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.E_M_1_ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.E_M_1_EATK));
                                                break;
                                            }
                                        case AttackType.Ranged:
                                        case AttackType.Magic:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.E_M_2_HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.E_M_2_ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.E_M_2_EATK));
                                                break;
                                            }
                                    }

                                    break;
                                }
                            case Define.eMonsterGradeType.eStageBoss:
                                {
                                    fullHp = data.b_HP = BigInteger.Parse(stageinfo.B_M_HP);
                                    hp.Value = fullHp;
                                    data.b_ATK = BigInteger.Parse(stageinfo.B_M_ATK);
                                    SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.B_M_EATK));

                                    SkillSetting();

                                    break;
                                }
                            case Define.eMonsterGradeType.eChapterBoss:
                                {
                                    fullHp = data.b_HP = BigInteger.Parse(stageinfo.CB_M_HP);
                                    hp.Value = fullHp;
                                    data.b_ATK = BigInteger.Parse(stageinfo.CB_M_ATK);
                                    SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.CB_M_EATK));

                                    SkillSetting();

                                    break;
                                }
                        }
                    }
                    else
                        Debug.LogError(string.Format("미궁 테이블 스테이지인덱스에 정보가 없음 : {0}", DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurUnderGroundDGIdx));
                    break;
                }
            case Define.eBATTLETYPE.eBossChallenge:
                {
                    break;
                }
            case Define.eBATTLETYPE.eGoldMine:
                {
                    var stageinfo = STAGE.GoldMine.GoldMineList.Find(item => item.stage == DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurGoldMineDGIdx);
                    if (stageinfo != null)
                    {
                        //유닛 데이터값 전체 초기화
                        data = new UnitData(this);

                        data.b_ACU = BigInteger.Parse(stageinfo.NeedAcu);
                        data.b_PEN = BigInteger.Parse(stageinfo.NeedPen);

                        fullHp = data.b_HP = BigInteger.Parse(stageinfo.M_HP);
                        hp.Value = fullHp;
                    }
                    else
                        Debug.LogError(string.Format("GoldMine 테이블에 정보가 없음 : {0}", DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurGoldMineDGIdx));
                    break;
                }
            case Define.eBATTLETYPE.eWeaponReinforceDungeon:
                {
                    var stageinfo = STAGE.Weapon_Dungeon.Weapon_DungeonList.Find(item => item.stage == DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurWeaponReinforceDGIdx);
                    if (stageinfo != null)
                    {
                        //유닛 데이터값 전체 초기화
                        data = new UnitData(this);

                        data.b_ACU = BigInteger.Parse(stageinfo.NeedAcu);
                        data.b_PEN = BigInteger.Parse(stageinfo.NeedPen);

                        fullHp = data.b_HP = BigInteger.Parse(stageinfo.M_HP);
                        hp.Value = fullHp;

                        SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.M_EATK));
                        data.b_ATK = BigInteger.Parse(stageinfo.M_EATK);
                        SkillSetting();
                    }
                    else
                        Debug.LogError(string.Format("무기 강화석 테이블에 정보가 없음 : {0}", DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurWeaponReinforceDGIdx));
                    break;
                }
            case Define.eBATTLETYPE.eShiledReinforceDungeon:
                {
                    var stageinfo = STAGE.Armor_Dungeon.Armor_DungeonList.Find(item => item.stage == DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurShiledReinforceDGIdx);
                    if (stageinfo != null)
                    {
                        //유닛 데이터값 전체 초기화
                        data = new UnitData(this);

                        data.b_ACU = BigInteger.Parse(stageinfo.NeedAcu);
                        data.b_PEN = BigInteger.Parse(stageinfo.NeedPen);

                        fullHp = data.b_HP = BigInteger.Parse(stageinfo.M_HP);
                        hp.Value = fullHp;

                        SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.M_EATK));
                        data.b_ATK = BigInteger.Parse(stageinfo.M_EATK);
                        SkillSetting();
                    }
                    else
                        Debug.LogError(string.Format("방어구 강화석 테이블에 정보가 없음 : {0}", DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurShiledReinforceDGIdx));
                    break;
                }
            case Define.eBATTLETYPE.eAccReinforceDungeon:
                {
                    var stageinfo = STAGE.ACC_Dungeon.ACC_DungeonList.Find(item => item.stage == DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurAccReinforceDGIdx);
                    if (stageinfo != null)
                    {
                        //유닛 데이터값 전체 초기화
                        data = new UnitData(this);

                        data.b_ACU = BigInteger.Parse(stageinfo.NeedAcu);
                        data.b_PEN = BigInteger.Parse(stageinfo.NeedPen);

                        fullHp = data.b_HP = BigInteger.Parse(stageinfo.M_HP);
                        hp.Value = fullHp;

                        SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.M_EATK));
                        data.b_ATK = BigInteger.Parse(stageinfo.M_EATK);
                        SkillSetting();
                    }
                    else
                        Debug.LogError(string.Format("방어구 강화석 테이블에 정보가 없음 : {0}", DBManager.Instance.playerData._DungeonStageData.dungeonInfo.CurAccReinforceDGIdx));
                    break;
                }
            case Define.eBATTLETYPE.eWarriorGrowthDungeon:
            case Define.eBATTLETYPE.eArcherGrowthDungeon:
            case Define.eBATTLETYPE.eMageGrowthDungeon:
            case Define.eBATTLETYPE.eHealerGrowthDungeon:
                {
                    var curIdx = 0;

                    switch (BattleManager.Instance.curBattleType)
                    {
                        case Define.eBATTLETYPE.eWarriorGrowthDungeon:
                            {
                                curIdx = DBManager.Instance.playerData._DungeonStageData.GetCurWarriorGrowthDGIdx();
                                break;
                            }
                        case Define.eBATTLETYPE.eArcherGrowthDungeon:
                            {
                                curIdx = DBManager.Instance.playerData._DungeonStageData.GetCurArcherGrowthDGIdx();
                                break;
                            }
                        case Define.eBATTLETYPE.eMageGrowthDungeon:
                            {
                                curIdx = DBManager.Instance.playerData._DungeonStageData.GetCurMageGrowthDGIdx();
                                break;
                            }
                        case Define.eBATTLETYPE.eHealerGrowthDungeon:
                            {
                                curIdx = DBManager.Instance.playerData._DungeonStageData.GetCurHealerGrowthDGIdx();
                                break;
                            }
                    }

                    if (STAGE.CHR_LV_Dungeon.CHR_LV_DungeonMap.TryGetValue(curIdx, out var stageinfo))
                    {
                        //var stageinfo = STAGE.CHR_LV_Dungeon.CHR_LV_DungeonMap[curIdx];

                        //유닛 데이터값 전체 초기화
                        data = new UnitData(this);

                        data.b_ACU = BigInteger.Parse(stageinfo.NeedAcu);
                        data.b_PEN = BigInteger.Parse(stageinfo.NeedPen);

                        switch (monsterGrade)
                        {
                            case Define.eMonsterGradeType.eNormal:
                                {
                                    switch (attackType)
                                    {
                                        case AttackType.Melee:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.N_M_1HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.N_M_1ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.N_M_1EATK));
                                                //타워 공격을 위해 성장 던전에서만 공격범위를 넓힌다
                                                attackRange = 5;
                                                break;
                                            }
                                        case AttackType.Ranged:
                                        case AttackType.Magic:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.N_M_2HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.N_M_2ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.N_M_2EATK));
                                                break;
                                            }
                                    }

                                    //테스트
                                    unitSpeed = 5.5f;
                                    tempSpeed = unitSpeed;
                                    break;
                                }
                            case Define.eMonsterGradeType.eElite:
                                {
                                    switch (attackType)
                                    {
                                        case AttackType.Melee:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.E_M_1HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.E_M_1ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.E_M_1EATK));
                                                //타워 공격을 위해 성장 던전에서만 공격범위를 넓힌다
                                                attackRange = 5;
                                                break;
                                            }
                                        case AttackType.Ranged:
                                        case AttackType.Magic:
                                            {
                                                fullHp = data.b_HP = BigInteger.Parse(stageinfo.E_M_2HP);
                                                hp.Value = fullHp;
                                                data.b_ATK = BigInteger.Parse(stageinfo.E_M_2ATK);
                                                SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.E_M_2EATK));
                                                break;
                                            }
                                    }
                                    //테스트
                                    unitSpeed = 5.5f;
                                    tempSpeed = unitSpeed;
                                    break;
                                }
                            case Define.eMonsterGradeType.eStageBoss:
                            case Define.eMonsterGradeType.eChapterBoss:
                                {
                                    fullHp = data.b_HP = BigInteger.Parse(stageinfo.B_M_HP);
                                    hp.Value = fullHp;
                                    data.b_ATK = BigInteger.Parse(stageinfo.B_M_ATK);
                                    SetElmDmg((Define.eELMTYPE)stageinfo.Elemental, BigInteger.Parse(stageinfo.B_M_EATK));

                                    SkillSetting();

                                    //테스트
                                    unitSpeed = 5.5f;
                                    tempSpeed = unitSpeed;

                                    //타워 공격을 위해 성장 던전에서만 공격범위를 넓힌다
                                    if (attackType == AttackType.Melee)
                                        attackRange = 6.5f;

                                    break;
                                }
                        }
                    }

                    break;
                }
        }

        data.isAdBuffTarget = false;
 
        data.b_ASPD = monsterInfo.AttackSpeed;
    }

    /// <summary>
    /// 스테이지에 설정된 속성 대미지 값 전달
    /// </summary>
    /// <param name="type">속성 타입 값</param>
    /// <param name="elmDmg">속성 대미지 값</param>
    private void SetElmDmg(Define.eELMTYPE type, BigInteger elmDmg)
    {
        elementalType = (int)type;

        switch (type)
        {
            case Define.eELMTYPE.eFireElm: data.b_FIRE_DMG = elmDmg; break;
            case Define.eELMTYPE.eIceElm: data.b_ICE_DMG = elmDmg; break;
            case Define.eELMTYPE.eLightningElm: data.b_LIGHTNING_DMG = elmDmg; break;
        }
    }

    /// <summary>
    /// 몬스터가 스킬을 사용할 때
    /// </summary>
    public virtual void OnMonsterSkill()
    {
        var count = equipedSkillIdx.Count(x => x != 0);
        var rndIdx = Random.Range(0, count);
        AutoSkillUse(rndIdx).Forget();
    }

    /// <summary>
    /// 보스 몬스터 스킬 세팅
    /// </summary> 
    public void SkillSetting()
    {
        var monsterData = STAGE.Monster.MonsterMap[unitID];

        var skillIdList = new HashSet<int>
        {
            monsterData.SkillID01,
            monsterData.SkillID02,
            monsterData.SkillID03
        };
        var hashId = 0;
        foreach (var skillId in skillIdList)
        {
            if (skillId == 0)
                continue;
            equipedSkillIdx[hashId] = skillId;
            hashId++;
        }

        var triggerHp = 0f;
        if (monsterData.SkillTriggerHp <= 0)
        {
            triggerHp = 0.25f;
        }
        else
        {
            triggerHp = monsterData.SkillTriggerHp;
        }

        var skillCount = (int)((decimal)1f / (decimal)triggerHp);
        isSkillUse = new bool[skillCount - 1];

    }

    /// <summary>
    /// 현재 남은 체력을 계산하여 스킬을 사용할지 여부를 판단해줌.
    /// </summary>
    protected bool CalculateSkillHP()
    {
        var hpPer = (decimal)hp.Value / (decimal)fullHp;
        var triggerHpPer = STAGE.Monster.MonsterMap[unitID].SkillTriggerHp;

        if (triggerHpPer <= 0)
            triggerHpPer = 0.25f;

        var index = (int)(hpPer / (decimal)triggerHpPer) - 1;

        if (index < 0 || isSkillUse == null || index > isSkillUse.Length - 1)
            return false;

        //해당 체력 구간에서 스킬을 사용하지 않았다면
        if (!isSkillUse[index])
            return isSkillUse[index] = true;
        //해당 체력 구간에서 스킬을 사용했다면
        else
            return false;
    }

    public override async UniTask UseSkill(int equipIndex)
    {
        if (buffMgr.activeBuffEffectList.ContainsKey(1000001))
        {
            Debug.Log($"{this} : 침묵이라 스킬 못 씀");
            return;
        }
 
        await base.UseSkill(equipIndex);

        var skillData = DATA.Skill.SkillMap[equipedSkillIdx[equipIndex]];

        if (equipedSkillIdx[equipIndex] == 0) return;

        SkillManager.Instance.SkillTargetCheck(equipedSkillIdx[equipIndex], this, out var targetList);
        if (targetList.Count > 0)
        {
            var nearDistance = targetList.Min(x => (x.rigid.position - rigid.position).magnitude);
            //거리가 아직 멀면 사용 못함
            if (nearDistance > skillData.Distance)
            {
                //Debug.LogError("타겟이 멂");
                return;
            }

            var usingIndex = equipedSkillIdx[equipIndex];
            var skillInfo = DATA.Skill.SkillMap[usingIndex];


            ChangeState(State.SKILL, skillInfo);

            /*foreach (var target in targetList)
                Debug.LogError($"{monster.name}가 {skillInfo.name} 을 {target.name} 에게 스킬 시전");*/


            //스킬 이펙트
            OnSkillEffect(skillInfo, this, targetList);

            var delay = DATA.Skill.SkillMap[usingIndex].Duration * 1000f;

            await UniTask.Delay((int)delay);

            if (state.Value != State.DEAD)
            {
                ChangeState(State.IDLE);
            }
        }
    }

    #endregion
}

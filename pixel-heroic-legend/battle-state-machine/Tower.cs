using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using UnityEngine;


public class Tower : Unit
{
    #region 변수

    /// <summary>
    /// 투사체 발사 위치 
    /// </summary>
    [SerializeField] private Transform fireTr = null;
    /// <summary>
    /// 투사체 발사 이펙트 
    /// </summary>
    [SerializeField] private ParticleSystem atkEffect = null;

    #endregion

    protected override void Awake()
    {
        base.Awake();
        projectileName = "Tower_Orb";
    }

    /// <summary>
    /// 유닛 초기화 함수.
    /// </summary>
    public override void Init(int unitID = 0)
    {
        gameObject.SetActive(true);

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
            data.isAdBuffTarget = false;
            data.b_ACU = PlayerManager.Instance.currentCharacter.data.Final_ACU;
            data.b_PEN = PlayerManager.Instance.currentCharacter.data.Final_PEN;

            fullHp = data.b_HP = BigInteger.Parse(stageinfo.Tower_HP);
            hp.Value = fullHp;
            data.b_ATK = BigInteger.Parse(stageinfo.Tower_ATK);
            data.b_ASPD = (int)stageinfo.Tower_ASPD;
        }

        base.Init();

        hpBackBar?.gameObject.SetActive(true);
        mpBar?.gameObject.SetActive(false);
    }

    public override void StatInit()
    {
        base.StatInit();

        unitType = UnitType.Player;

    }

    /// <summary>
    /// 가만히 있을 때 비동기 함수
    /// </summary>
    /// <returns></returns>
    public override async UniTask IdleAsync()
    {
        cts = new CancellationTokenSource();

        targetEnemy = null;

        //스턴 혹은 슬립이 풀릴때까지 기다린다.
        await UniTask.WaitUntil(() => FindEnemy_Tower() != null, PlayerLoopTiming.FixedUpdate, cancellationToken: cts.Token);
    }

    public Unit FindEnemy_Tower()
    {
        base.FindEnemy();

        //안 죽은 적들을 가까이 있는 순서로 가져옴 (O²)
        var targetList = COMMON.Instance.GetEnemyList(this).Where(x => x.state.Value != State.DEAD).OrderBy(x => (transform.position - x.rigid.position).sqrMagnitude).ToList();

        //더 이상 잔존하는 적이 없으면 IDL
        if (targetList.Count == 0)
        {
            ChangeState(State.IDLE);
            return null;
        }

        var players = PlayerManager.Instance.GetLivePlayer();
        if (players.Count == 0) return null;

        var targetDistance = (transform.position - targetList[0].rigid.position).magnitude;
        if (targetDistance > attackRange)
            return null;

        targetEnemy = targetList[0];

        ChangeState(State.ATTACK);

        return targetEnemy;
    }

    /// <summary>
    /// 공격 비동기 함수
    /// </summary>
    /// <returns></returns>
    protected override async UniTask AttackAsync()
    {
        cts = new CancellationTokenSource();

        while (true)
        {
            //타겟이 없거나 죽었거나
            if (targetEnemy == null || targetEnemy.state.Value == State.DEAD)
            {
                ChangeState(State.IDLE);
                break;
            }

            if (PlayerManager.Instance.currentCharacter.state.Value == State.DEAD || hp.Value <= 0)
            {
                ChangeState(State.DEAD);
                break;
            }

            if (targetEnemy.hp.Value > 0)
            {
                if (attackType == AttackType.Ranged || attackType == AttackType.Magic)
                    targetPos = targetEnemy.hitFollower.transform.position;

                AttackEventCallBack(false);

                //애니메이션 재생 시간만큼 대기
                var duration = 2.0f - (float)data.Final_ASPD;

                if (duration > 0)
                    await Delay((int)(duration * 1000f));

                //if (targetEnemy == null || targetEnemy.state.Value == State.DEAD)
                //{
                //    ChangeState(State.IDLE);
                //    break;
                //}
            }

            await UniTask.Yield(PlayerLoopTiming.FixedUpdate, cts.Token);
        }
    }

    /// <summary>
    /// 스파인 애니메이션 내에서 Attack 이벤트 호출시 호출할 함수
    /// </summary>
    /// <param name="isHeavyAtk">강공격인가?</param>
    protected override void AttackEventCallBack(bool isHeavyAtk)
    {
        var projectile = ProjectileManager.Instance.Spawn(projectileName, fireTr.position);
        projectile.unit = this;

        if (targetEnemy != null)
        {
            var dam = CriticalCalculate(targetEnemy, isHeavyAtk);
            projectile.damage = dam;
            projectile.isHeavyAtk = isHeavyAtk;
            projectile.damageType = damageType;
            //projectile.Shoot(targetEnemy, 0, Define.eELMTYPE.eNormalElm);
        }
        //else
        projectile.Shoot(targetEnemy);


        atkEffect.Play();
    }

    public override async UniTask DeadAsync()
    {
        cts = new CancellationTokenSource();

        foreach (var item in buffMgr.activeAbilityEffectList)
            item.Value.RemoveEffect(this);
        foreach (var item in buffMgr.activeBuffEffectList)
            item.Value.RemoveEffect(this);
        buffMgr.StopAllBuff();

        hpBackBar.gameObject.SetActive(false);
        mpBar?.gameObject.SetActive(false);

        mpSubject?.Dispose();
        hpSubject?.Dispose();
        scaleCts?.Cancel();

        ChangeState(State.NONE);

        var battle = BattleManager.Instance.battle as GrowthDungeonBattle;
        if (battle != null)
        {
            if (name == "Tower_Top")
                battle.TowerBrokenEffectList[0].Play();
            else
                battle.TowerBrokenEffectList[1].Play();
        }
        gameObject.SetActive(false);

    }


}

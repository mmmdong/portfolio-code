using AssetKits.ParticleImage.Enumerations;
using Cysharp.Threading.Tasks;
using DATA;
using DG.Tweening;
using Spine;
using Spine.Unity;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Numerics;
using System.Threading;
using UniRx;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using static Define;
using Event = Spine.Event;
using Random = UnityEngine.Random;
using Vector3 = UnityEngine.Vector3;

/// <summary>
/// 유닛의 상태
/// </summary>
public enum State
{
    NONE,
    IDLE,
    MOVE,
    ATTACK,
    DEAD,
    SKILL,
}

/// <summary>
/// 공격 타입
/// </summary>
public enum AttackType
{
    None,
    Melee,
    Ranged,
    Magic,
    Mix,
}

public enum UnitType
{
    Player,
    Enemy,
}

/// <summary>
/// 몬스터, 플레이어 등 화면에 보이는 모든 유닛이 상속받는 Mother Class
/// </summary> 
public class Unit : MonoBehaviour
{
    /// <summary>
    /// 유닛 ID
    /// </summary>
    public int unitID;

    /// <summary>
    /// 반응형 프로그래밍을 위한 프로퍼티.
    /// State Value가 바뀔때마다 함수가 자동으로 콜백 할 수 있도록 도와준다.
    /// </summary>
    public ReactiveProperty<State> state = new(State.NONE);

    /// <summary>
    /// 타겟이 될 유닛
    /// </summary>
    public Unit targetEnemy;

    /// <summary>
    /// 도발에 걸렸을 경우 원래 타겟을 잠시 담아 놓을 유닛
    /// </summary>
    public Unit prevEnemy;

    /// <summary>
    /// 타겟이 죽었을 때 원거리 유닛이 투사체를 날릴 위치
    /// </summary>
    protected Vector3 targetPos;

    /// <summary>
    /// 공격속도
    /// </summary>
    [Range(0f, 3f)] public float attackRatio;

    /// <summary>
    /// 공격 범위
    /// </summary>
    public float attackRange;

    /// <summary>
    /// 유닛 이동속도
    /// </summary>
    public float unitSpeed;

    /// <summary>
    /// 유닛 이동속도 초기화할 변수
    /// </summary>
    [HideInInspector] public float tempSpeed;

    /// <summary>
    /// 유닛 이동속도 초기화할 변수
    /// </summary>
    [HideInInspector] public float oriSpeed;

    /// <summary>
    /// 회복불가상태
    /// </summary>
    [HideInInspector] public bool noHeal;

    /// <summary>
    /// 반응형 프로그래밍을 위한 프로퍼티.
    /// Hp Value가 바뀔때마다 그에 반응할 함수를 콜백시켜준다.
    /// </summary>
    public ReactiveProperty<BigInteger> hp;

    /// <summary>
    /// 최대체력
    /// </summary>
    public BigInteger fullHp = new(0);

    /// <summary>
    /// 동작을 관리할 리지드바디
    /// transform 보다 rigid 바디를 사용할 것.
    /// </summary>
    [HideInInspector] public Rigidbody rigid;

    /// <summary>
    /// 피격을 담당할 콜라이더
    /// </summary>
    [HideInInspector] public BoxCollider col;

    /// <summary>
    /// 공격 타입(원거리, 근거리)
    /// </summary>
    public AttackType attackType;

    /// <summary>
    /// 공격타입이 원거리일 경우 사용될 투사체
    /// </summary>
    protected string projectileName;

    /// <summary>
    /// 비동기 함수를 중단시킬 때 사용할 토큰
    /// </summary>
    protected CancellationTokenSource cts;

    /// <summary>
    /// 유닛 스케일 비동기 함수를 중단시킬 때 사용할 토큰
    /// </summary>
    protected CancellationTokenSource scaleCts;

    /// <summary>
    /// 자동 체력 회복 자동 마나 회복 토큰
    /// </summary>
    protected CancellationTokenSource autoHpMpRcvCts;

    /// <summary>
    /// 체력 게이지 움직임 토큰
    /// </summary>
    private CancellationTokenSource hpBarCts;

    /// <summary>
    /// 아이들 토큰
    /// </summary>
    private CancellationTokenSource idleCts;

    /// <summary>
    /// 캐릭터 움직임을 담당
    /// </summary>
    [HideInInspector] public SkeletonAnimation unitAnim;

    /// <summary>
    /// 체력바
    /// </summary>
    [HideInInspector] public Slider hpBar;

    /// <summary>
    /// 체력 게이지 즐어드는 효과주는 바
    /// </summary>
    [HideInInspector] public Slider hpBackBar;

    /// <summary>
    /// 마나바
    /// </summary>
    [HideInInspector] public Slider mpBar;

    /// <summary>
    /// 애니메이션 딕셔너리
    /// </summary>
    protected Dictionary<string, SkeletonAnimation> skelAnimDict = new Dictionary<string, SkeletonAnimation>();

    /// <summary>
    /// 피격 효과를 나타낼 메쉬
    /// </summary>
    [HideInInspector] public MeshRenderer meshRenderer;

    /// <summary>
    /// 체력바 달려있는 Canvas
    /// </summary>
    [HideInInspector] public BoneFollower hpUIFollower;

    /// <summary>
    /// 투사체 발사할 위치
    /// </summary>
    [HideInInspector] public BoneFollower muzzleFollower;

    /// <summary>
    /// 대미지 이펙트를 띄워줄 위치
    /// </summary>
    [HideInInspector] public BoneFollower hitFollower;

    /// <summary>
    /// 스킬 사용이 가능한 유닛인지 확인
    /// </summary>
    protected bool canSkillUse;

    /// <summary>
    /// 자동 스킬 기능
    /// </summary>
    public bool autoSkill;

    /// <summary>
    /// 현재 장착한 스킬
    /// </summary>
    public int[] equipedSkillIdx;

    /// <summary>
    /// 현재 사용할 스킬
    /// </summary>
    protected DATA.Skill curSkillInfo;

    /// <summary>
    /// 유닛이 움직일 수 있는 최대 거리
    /// </summary>
    protected float minX, maxX;

    protected float minZ, maxZ;

    /// <summary>
    /// 대미지 타입
    /// </summary>
    protected DamageType damageType;

    /// <summary>
    /// 최근 발사된 투사체
    /// </summary>
    protected Projectile projectile;

    /// <summary>
    /// 투사체 발사 갯수
    /// </summary>
    public int projectileCount = 1;

    /// <summary>
    /// 현재 사용중인 스킬
    /// </summary>
    [HideInInspector] public DATA.Skill curSkill;

    protected IDisposable hpSubject, mpSubject;
    private IDisposable stateSubject, disarraySubject;

    /// <summary>
    /// 유닛 데이터 저장
    /// </summary>
    public UnitData data = new UnitData();

    //kami테스트
    /// <summary>
    /// 버프 매니저
    /// </summary>
    public BuffManager buffMgr;

    /// <summary>
    /// 유닛 타입 (플레이어, 적군)
    /// </summary>
    public UnitType unitType = UnitType.Player;

    /// <summary>
    /// 상태이상 이펙트 리스트
    /// </summary>
    public Dictionary<eEFFECTTYPE, Effect> deBuffEffectList = new Dictionary<eEFFECTTYPE, Effect>();

    /// <summary>
    /// 버프 이펙트
    /// </summary>
    public Effect buffEffect;

    /// <summary>
    /// 디버프 이펙트
    /// </summary>
    public Effect deBuffEffect;

    /// <summary>
    /// 공격시 타겟에 터질 이펙트
    /// </summary>
    [SerializeField] protected string hitEffect;

    /// <summary>
    /// 공격시 모션에 들어갈 이펙트
    /// </summary>
    [SerializeField] protected string attackEffect;

    /// <summary>
    /// 버서커 모드
    /// </summary>
    [HideInInspector] public bool isBerserker;

    /// <summary>
    /// 죽은 상태인지
    /// </summary>
    [HideInInspector] public bool isDead;

    /// <summary>
    /// 관통 투사체 발사
    /// </summary>
    [HideInInspector] public bool isPierceShot;

    /// <summary>
    /// 조이스틱으로 움직이는지 여부
    /// </summary>
    [HideInInspector] public bool isStickMove;

    /// <summary>
    /// 혼란상태
    /// </summary>
    [HideInInspector] public BoolReactiveProperty isDisarray = new(false);

    /// <summary>
    /// 피격시 호출될 함수
    /// </summary>
    public Action<BigInteger> OnRecieveDamage;

    /// <summary>
    /// 무적인지 확인하는 변수 
    /// </summary>
    public bool IsInvincible { get; set; }

    protected virtual void Awake()
    {
        rigid = GetComponent<Rigidbody>();
        var sliders = GetComponentsInChildren<Slider>(true);
        hpBackBar = sliders[0];
        hpBackBar.value = 1f;
        hpBar = sliders[1];
        hpBar.value = 1f;

        if (sliders.Length == 3)
        {
            mpBar = sliders[2];
            mpBar.value = 1f;
        }

        buffMgr = transform.AddComponent<BuffManager>();

        oriSpeed = tempSpeed = unitSpeed;

        var boneFollowerDict = GetComponentsInChildren<BoneFollower>(true).ToDictionary(x => x.name, x => x);
        hpUIFollower = boneFollowerDict["HpUI"];
        col = GetComponentInChildren<BoxCollider>();

        //타워는 바로 아래의 설정을 하지 않고 바로 나간다
        if (this is Tower)
            return;

        muzzleFollower = boneFollowerDict["Muzzle"];
        hitFollower = boneFollowerDict["Hit"];


        skelAnimDict = GetComponentsInChildren<SkeletonAnimation>(true).ToDictionary(x => x.name, x => x);
        unitAnim = skelAnimDict["UnitSkeleton"];
        meshRenderer = GetComponentInChildren<MeshRenderer>();

        unitAnim.AnimationState.Complete += delegate
        {
            if (state.Value == State.DEAD)
                OnDeadEffect();
        };
        unitAnim.state.Event += AttackEvent;

        equipedSkillIdx = new int[4];

        foreach (var item in Enum.GetValues(typeof(eEFFECTTYPE)))
            deBuffEffectList.Add((eEFFECTTYPE)item, null);
    }

    public DamageType GetDamageType()
    {
        return damageType;
    }

    /// <summary>
    /// 스파인 애니메이션 실행 중 호출될 함수
    /// </summary>
    /// <param name="trackEntry"></param>
    /// <param name="e"></param>
    private void AttackEvent(TrackEntry trackEntry, Event e)
    {
        if (e.Data.Name == "Attack")
        {
            AttackEventCallBack(unitAnim.AnimationName == "Attack_2");
        }
    }

    /// <summary>
    /// 스파인 애니메이션 내에서 Attack 이벤트 호출시 호출할 함수
    /// </summary>
    /// <param name="isHeavyAtk">강공격인가?</param>
    protected virtual void AttackEventCallBack(bool isHeavyAtk)
    {
        if (this is Player player)
        {
            var enemy = targetEnemy as Enemy;
            switch (BattleManager.Instance.curBattleType)
            {
                case eBATTLETYPE.eStage:
                {
                    if (enemy != null)
                        enemy.squad.ChangeTarget(player);

                    break;
                }
            }
        }

        //공격 타입에 따라 공격 방식이 다름.
        switch (attackType)
        {
            case AttackType.Melee:
            {
                //타겟이 없으면 리턴
                if (targetEnemy == null)
                    return;

                var effect = EffectManager.Instance.GetEffect(hitEffect);

                if (targetEnemy.hitFollower != null)
                    effect.transform.position = targetEnemy.hitFollower.transform.position;
                else
                    effect.transform.position = targetEnemy.rigid.transform.position;

                effect.Play();

                //몬스터는 HeavyAttack이 스킬임
                if (isHeavyAtk)
                {
                    if (this is Player)
                    {
                        HeavyAttackDamage(targetEnemy.rigid.position);
                    }
                    else if (this is Monster monster)
                    {
                        monster.OnMonsterSkill();
                    }
                }
                else
                {
                    if (targetEnemy == null) break;

                    var damage = BigInteger.Zero;
                    if (this is Player)
                    {
                        var dir = unitAnim.transform.localScale.x < 0 ? Vector3.left : Vector3.right;
                        var centerPos = rigid.position + dir * (attackRange * 0.5f);
                        var targetList = COMMON.Instance.GetEnemyList(this).Where(x =>
                            (centerPos - x.rigid.position).magnitude <= attackRange * 0.5f);

                        if (!targetList.Contains(targetEnemy))
                        {
                            damage = CriticalCalculate(targetEnemy);
                            damage = (BigInteger)COMMON.Instance.SpecialBuffCheck(this, targetEnemy,
                                BUFFACTIONTYPE.DO_ATTACK, (decimal)damage);
                            targetEnemy.GetDamage(damage, this, damageType,
                                (eELMTYPE)DBManager.Instance.playerData._UserData.userInfo.SelectElmVal);
                        }

                        foreach (var enemy in targetList)
                        {
                            damage = CriticalCalculate(enemy);
                            damage = (BigInteger)COMMON.Instance.SpecialBuffCheck(this, enemy, BUFFACTIONTYPE.DO_ATTACK,
                                (decimal)damage);
                            enemy.GetDamage(damage, this, damageType,
                                (eELMTYPE)DBManager.Instance.playerData._UserData.userInfo.SelectElmVal);
                        }
                    }
                    else
                    {
                        damage = CriticalCalculate(targetEnemy, isHeavyAtk);
                        targetEnemy.GetDamage(damage, this, damageType);
                        if (unitType == UnitType.Enemy)
                        {
                            //속성 데미지 있는지 계산
                            var mon = this as Monster;

                            if (mon != null && mon.elementalType != 0)
                            {
                                if (targetEnemy == null) break;

                                var elementDmg = BigInteger.Zero;
                                var logValue = 0d;
                                switch ((eELMTYPE)mon.elementalType)
                                {
                                    case eELMTYPE.eFireElm:
                                    {
                                        //calper 계산값이 0.8 이상이면 0.8로 고정 한다 (컨셉이 이렇게 되어 있음)
                                        if (mon.data.Final_Fire_Dmg != 0)
                                        {
                                            elementDmg = mon.data.Final_Fire_Dmg;
                                            if (targetEnemy.data.Final_Fire_Reg == 0) break;
                                            logValue =
                                                BigInteger.Log(BigInteger.Pow(targetEnemy.data.Final_Fire_Reg, 2)) *
                                                (MathF.Sqrt((float)targetEnemy.data.Final_Fire_Reg * 0.73f)) * 0.00021f;
                                            if (logValue > 0.6f)
                                                logValue = 0.6f;
                                        }

                                        break;
                                    }
                                    case eELMTYPE.eIceElm:
                                    {
                                        //calper 계산값이 0.8 이상이면 0.8로 고정 한다 (컨셉이 이렇게 되어 있음)
                                        if (mon.data.Final_Ice_Dmg != 0)
                                        {
                                            elementDmg = mon.data.Final_Ice_Dmg;
                                            if (targetEnemy.data.Final_Ice_Reg == 0) break;
                                            logValue =
                                                BigInteger.Log(BigInteger.Pow(targetEnemy.data.Final_Ice_Reg, 2)) *
                                                (MathF.Sqrt((float)targetEnemy.data.Final_Ice_Reg * 0.73f)) * 0.00021f;
                                            if (logValue > 0.6f)
                                                logValue = 0.6f;
                                        }

                                        break;
                                    }
                                    case eELMTYPE.eLightningElm:
                                    {
                                        //calper 계산값이 0.8 이상이면 0.8로 고정 한다 (컨셉이 이렇게 되어 있음)
                                        if (mon.data.Final_Lightning_Dmg != 0)
                                        {
                                            elementDmg = mon.data.Final_Lightning_Dmg;
                                            if (targetEnemy.data.Final_Lightning_Reg == 0) break;
                                            logValue =
                                                BigInteger.Log(BigInteger.Pow(targetEnemy.data.Final_Lightning_Reg,
                                                    2)) * (MathF.Sqrt((float)targetEnemy.data.Final_Lightning_Reg *
                                                                      0.73f)) * 0.00021f;
                                            if (logValue > 0.6f)
                                                logValue = 0.6f;
                                        }

                                        break;
                                    }
                                }

                                elementDmg = CalculateCompatibility(targetEnemy,
                                    (decimal)elementDmg * (1 - (decimal)logValue));
                                targetEnemy.GetDamage(elementDmg, this, damageType, (eELMTYPE)mon.elementalType);
                            }
                        }
                    }
                }

                break;
            }
            case AttackType.Ranged:
            case AttackType.Magic:
            {
                if (this is Player)
                {
                    if (buffMgr)
                        Shoot(isHeavyAtk).Forget();
                }
                else if (this is Monster mon)
                {
                    if (!isHeavyAtk)
                    {
                        projectile =
                            ProjectileManager.Instance.Spawn(projectileName, muzzleFollower.transform.position);
                        projectile.unit = this;

                        BigInteger elementDmg = 0;
                        var eletype = eELMTYPE.eNormalElm;

                        //타겟이 있을 때
                        if (targetEnemy != null)
                        {
                            //몬스터가 공격시 속성에 속성타입이 있을 때
                            if (unitType == UnitType.Enemy)
                            {
                                //속성 데미지 있는지 계산
                                if (mon != null && mon.elementalType != 0)
                                {
                                    eletype = (eELMTYPE)mon.elementalType;
                                    var logValue = 0d;
                                    switch ((eELMTYPE)mon.elementalType)
                                    {
                                        case eELMTYPE.eFireElm:
                                        {
                                            //calper 계산값이 0.8 이상이면 0.8로 고정 한다 (컨셉이 이렇게 되어 있음)
                                            if (mon.data.Final_Fire_Dmg != 0)
                                            {
                                                elementDmg = mon.data.Final_Fire_Dmg;
                                                if (targetEnemy.data.Final_Fire_Reg == 0) break;
                                                logValue =
                                                    BigInteger.Log(BigInteger.Pow(targetEnemy.data.Final_Fire_Reg, 2)) *
                                                    (MathF.Sqrt((float)targetEnemy.data.Final_Fire_Reg * 0.73f)) *
                                                    0.00021f;
                                                if (logValue > 0.6f)
                                                    logValue = 0.6f;
                                            }

                                            break;
                                        }
                                        case eELMTYPE.eIceElm:
                                        {
                                            //calper 계산값이 0.8 이상이면 0.8로 고정 한다 (컨셉이 이렇게 되어 있음)
                                            if (mon.data.Final_Ice_Dmg != 0)
                                            {
                                                elementDmg = mon.data.Final_Ice_Dmg;
                                                if (targetEnemy.data.Final_Ice_Reg == 0) break;
                                                logValue =
                                                    BigInteger.Log(BigInteger.Pow(targetEnemy.data.Final_Ice_Reg, 2)) *
                                                    (MathF.Sqrt((float)targetEnemy.data.Final_Ice_Reg * 0.73f)) *
                                                    0.00021f;
                                                if (logValue > 0.6f)
                                                    logValue = 0.6f;
                                            }

                                            break;
                                        }
                                        case eELMTYPE.eLightningElm:
                                        {
                                            //calper 계산값이 0.8 이상이면 0.8로 고정 한다 (컨셉이 이렇게 되어 있음)
                                            if (mon.data.Final_Lightning_Dmg != 0)
                                            {
                                                elementDmg = mon.data.Final_Lightning_Dmg;
                                                if (targetEnemy.data.Final_Lightning_Reg == 0) break;
                                                logValue =
                                                    BigInteger.Log(BigInteger.Pow(targetEnemy.data.Final_Lightning_Reg,
                                                        2)) * (MathF.Sqrt((float)targetEnemy.data.Final_Lightning_Reg *
                                                                          0.73f)) * 0.00021f;
                                                if (logValue > 0.6f)
                                                    logValue = 0.6f;
                                            }

                                            break;
                                        }
                                    }

                                    elementDmg = CalculateCompatibility(targetEnemy,
                                        (decimal)elementDmg * (1 - (decimal)logValue));
                                    projectile.damageType = DamageType.N_ATTACK;
                                    projectile.elmType = (eELMTYPE)mon.elementalType;
                                    projectile.eleDamage = elementDmg;
                                    projectile.isHeavyAtk = isHeavyAtk;
                                }
                            }

                            if (projectile.TryGetComponent<Collider>(out var col))
                            {
                                ColliderProjectileSetting();
                                projectile.ShootWithCollider(targetEnemy);
                            }
                            else
                            {
                                projectile.Shoot(targetEnemy);
                            }
                        }
                        else //타겟이 없을 때
                        {
                            if (unitType == UnitType.Enemy)
                            {
                                //속성 데미지 있는지 계산
                                if (mon != null && mon.elementalType != 0)
                                {
                                    projectile.isHeavyAtk = default;
                                    projectile.damageType = default;
                                    projectile.elmType = eELMTYPE.eNormalElm;
                                }
                            }

                            projectile.Shoot(targetPos);
                        }
                    }
                    else
                    {
                        var monster = this as Monster;
                        monster.OnMonsterSkill();
                    }
                }

                break;
            }
        }
    }

    public void SetVisible(bool visible)
    {
        if (unitAnim != null)
            unitAnim.gameObject.SetActive(visible);

        if (hpBar != null)
            hpBar.gameObject.SetActive(visible);

        if (hpBackBar != null)
            hpBackBar.gameObject.SetActive(visible);

        if (mpBar != null)
            mpBar.gameObject.SetActive(visible);
    }

    public bool IsVisible()
    {
        return unitAnim.gameObject.activeSelf;
    }

    /// <summary>
    /// 투사체 발사
    /// </summary>
    /// <param name="isHeavyAtk"></param>
    private async UniTask Shoot(bool isHeavyAtk)
    {
        for (var i = 0; i < projectileCount; i++)
        {
            projectile = ProjectileManager.Instance.Spawn(projectileName, muzzleFollower.transform.position);
            projectile.unit = this;

            if (targetEnemy != null)
            {
                projectile.isHeavyAtk = isHeavyAtk;
                projectile.damageType = damageType;
                if (this is PVP_Player)
                    projectile.elmType =
                        (eELMTYPE)PlayFabManager.Instance.pvpPlayerData._UserData.userInfo.SelectElmVal;
                else if (this is Player)
                    projectile.elmType = (eELMTYPE)DBManager.Instance.playerData._UserData.userInfo.SelectElmVal;
                projectile.Shoot(targetEnemy);
            }
            else
                projectile.Shoot(targetPos);

            await UniTask.Delay(100);
        }
    }

    /// <summary>
    /// 유닛 초기화 함수.
    /// </summary>
    public virtual void Init(int unitID = 0)
    {
        transform.DOKill();
        rigid.DOKill();

        noHeal = false;
        isDead = false;
        mpSubject?.Dispose();
        hpSubject?.Dispose();
        stateSubject?.Dispose();
        disarraySubject?.Dispose();

        //버프 초기화
        foreach (var item in buffMgr.activeAbilityEffectList)
            item.Value.RemoveEffect(this);
        foreach (var item in buffMgr.activeBuffEffectList)
            item.Value.RemoveEffect(this, BUFFACTIONTYPE.DEAD);
        buffMgr.StopAllBuff();

        isBerserker = false;
        data.BuffStatusReset();
        data.unit = this;

        unitSpeed = oriSpeed;

        hpBackBar.gameObject.SetActive(true);

        minX = BattleManager.Instance.GetMoveRangeCollider().transform.position.x -
               BattleManager.Instance.GetMoveRangeCollider().bounds.size.x * 0.5f;
        maxX = BattleManager.Instance.GetMoveRangeCollider().transform.position.x +
               BattleManager.Instance.GetMoveRangeCollider().bounds.size.x * 0.5f;
        minZ = BattleManager.Instance.GetMoveRangeCollider().transform.position.z -
               BattleManager.Instance.GetMoveRangeCollider().bounds.size.z * 0.5f;
        maxZ = BattleManager.Instance.GetMoveRangeCollider().transform.position.z +
               BattleManager.Instance.GetMoveRangeCollider().bounds.size.z * 0.5f;

        StatInit();
        //this.state.Value = State.IDLE;
        state = new ReactiveProperty<State>(State.NONE);

        if (this is Monster_ChapterBoss || this is Monster_StageBoss || this is Monster_Reinforce || this is Player)
            canSkillUse = true;
        else
            canSkillUse = false;

        stateSubject = state.TakeUntilDestroy(this).Subscribe(ChangeStateCallBack);
        hpSubject = hp.TakeUntilDestroy(this).Subscribe(GetHPCallBack);
        disarraySubject = isDisarray.TakeUntilDestroy(this).Subscribe(OnDisarray);

        if (hitEffect == string.Empty)
        {
            if (targetEnemy is Monster)
                hitEffect = "Monster_Hit";
            else
                hitEffect = "Character_Hit";
        }

        if (this is Tower)
            return;

        hpUIFollower.skeletonRenderer = unitAnim;
        if (unitAnim.Skeleton.Data.FindBone("Hp") != null)
            hpUIFollower.boneName = "Hp";


        muzzleFollower.skeletonRenderer = unitAnim;
        if (unitAnim.Skeleton.Data.FindBone("Muzzle") != null)
            muzzleFollower.boneName = "Muzzle";

        hitFollower.skeletonRenderer = unitAnim;
        if (unitAnim.Skeleton.Data.FindBone("Hit") != null)
            hitFollower.boneName = "Hit";

        scaleCts?.Cancel();
        UnitScaleAsync().Forget();
    }

    /// <summary>
    /// 스태이터스 초기화 함수
    /// </summary>
    public virtual void StatInit()
    {
        fullHp = data.Final_Hp;
        hp.Value = fullHp;
        hpBackBar.value = 1f;
    }

    /// <summary>
    /// 공격받을 때 데미지 받으면 콜백될 함수.
    /// </summary>
    /// <param name="hp"></param>
    private void GetHPCallBack(BigInteger hp)
    {
        hpBar.value = COMMON.Instance.BigIntergerDivide(hp, data.Final_Hp);
        HpBackGaugeMove().Forget();
        //유닛 타입에 따른 기능 구분
        switch (unitType)
        {
            case UnitType.Player:
            {
                if (GameManager.Instance.SaveModeEnableCk)
                    break;

                var partyIndex = (float)PlayerManager.Instance.players.IndexOf(this as Player);
                if (partyIndex != -1)
                    GameEventSubject.SendGameEvent(GameEventType.ITEM_CHARACTER_HP_EVENT, partyIndex, hpBar.value);
                break;
            }
            case UnitType.Enemy:
            {
                if (GameManager.Instance.SaveModeEnableCk)
                    break;

                //스테이지 보스 몬스터 또는 챕터 보스 몬스터라면
                if (this is Monster_ChapterBoss || this is Monster_StageBoss || this is Monster_Reinforce)
                    GameEventSubject.SendGameEvent(GameEventType.BOSS_HP_EVENT, unitID, hpBar.value);
                //진급 전투에서 진급 던전의 pvp 캐릭터 라면
                else if (BattleManager.Instance.curBattleType == eBATTLETYPE.eUpgradeBattle && this is PVP_Player)
                    GameEventSubject.SendGameEvent(GameEventType.UPGRADE_BATTLE_HP_EVENT, hpBar.value);

                break;
            }
        }

        //PVP 상황에서 체력게이지 실시간 초기화
        if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
        {
            if (this is Player)
            {
                if (COMMON.Instance.onLoading)
                    return;
                var battle = BattleManager.Instance.battle as PVPBattle;
                var curHp = BigInteger.Zero;
                switch (unitType)
                {
                    case UnitType.Player:
                        foreach (var player in PlayerManager.Instance.players)
                        {
                            if (player.hp.Value < 0)
                            {
                                curHp += 0;
                                continue;
                            }

                            curHp += player.hp.Value;
                        }

                        battle.myHp.Value = curHp;
                        break;
                    case UnitType.Enemy:
                        foreach (var player in PlayerManager.Instance.PVP_Players)
                        {
                            if (player.hp.Value < 0)
                            {
                                curHp += 0;
                                continue;
                            }

                            curHp += player.hp.Value;
                        }

                        battle.otherHp.Value = curHp;
                        break;
                }
            }
        }

        if (hp <= 0)
        {
            COMMON.Instance.SpecialBuffCheck(this, this, BUFFACTIONTYPE.DEAD);
            isDead = true;
            if (!isBerserker)
                ChangeState(State.DEAD);
        }
    }

    /// <summary>
    /// 대미지만큼 체력이 닳음
    /// </summary>
    /// <param name="damage">대미지</param>
    /// <param name="useUnit">공격 한 유닛</param>
    public virtual void GetDamage(BigInteger damage, Unit useUnit = null, DamageType damageType = DamageType.N_ATTACK,
        eELMTYPE elmType = eELMTYPE.eNormalElm)
    {
        if (COMMON.Instance.onLoading)
            return;
        if (state.Value == State.DEAD)
            return;
        if (unitAnim != null)
        {
            if (!unitAnim.gameObject.activeSelf)
                return;
        }

        //블럭 체크 변수
        var blcCk = false;

        //무조건 피격, 무조건 관통 버프 체크
        var buffList = useUnit.buffMgr.activeBuffEffectList;
        var mustHit = false;
        var mustPierce = false;
        if (damageType != DamageType.SKILL_NORMAL && damageType != DamageType.SKILL_CRITICAL)
        {
            foreach (var item in buffList)
            {
                var specialBuffList = item.Value.SpecialStats;
                foreach (var specialBuff in specialBuffList)
                {
                    if (specialBuff.id == (int)BUFFDEBUFF.MUST_HIT)
                        mustHit = true;
                    if (specialBuff.id == (int)BUFFDEBUFF.MUST_PIERCE)
                        mustPierce = true;
                }
            }
        }

        switch (unitType)
        {
            case UnitType.Player:
            {
                if (useUnit == null)
                    break;

                #region 회피 계산

                {
                    if (!mustHit)
                    {
                        var calAvdVal = 0d;
                        if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
                        {
                            calAvdVal = ((double)data.Final_AVD / (double)useUnit.data.Final_ACU) * 0.2d;
                            if (calAvdVal >= 0.4d)
                                calAvdVal = 0.4d;
                        }
                        else
                        {
                            var val1 = Math.Log((double)BigInteger.Pow(data.Final_AVD, 2), 10);
                            var val2 = Math.Sqrt(((double)data.Final_AVD * 0.07));
                            calAvdVal = val1 * val2 * 0.00021;
                        }

                        var dicVal = Random.Range(0.0f, 1.0f);
                        //회피 했으므로 체력을 깍지 않고 바로 나간다
                        if (calAvdVal >= dicVal)
                        {
                            DisplayToValue(0, DamageType.MISS);
                            return;
                        }
                    }
                }

                #endregion

                #region 방어 계산

                if (useUnit != null)
                {
                    if (!mustPierce)
                    {
                        var increase = (decimal)1;
                        var pow = 2f;
                        if (useUnit is Monster)
                        {
                            var monsterGrade = STAGE.Monster.MonsterMap[useUnit.unitID].MonsterGrade;
                            switch (monsterGrade)
                            {
                                case 1:
                                    increase = (decimal)0.9;
                                    pow = 3f;
                                    break;
                                case 2:
                                    increase = (decimal)0.8;
                                    pow = 3.5f;
                                    break;
                                case 3:
                                    increase = (decimal)0.7;
                                    pow = 4f;
                                    break;
                                case 4:
                                    increase = (decimal)0.6;
                                    pow = 4f;
                                    break;
                            }
                        }

                        if (damage <= 0)
                        {
                            Debug.Log($"대미지로 {damage} 들어옴 체크요망");
                            damage = 1;
                        }

                        var defPer = (decimal)data.Final_DEF * increase / (decimal)damage;

                        decimal limitDefPer = 0;
                        if (useUnit is Monster)
                        {
                            var monsterGrade = STAGE.Monster.MonsterMap[useUnit.unitID].MonsterGrade;
                            switch (monsterGrade)
                            {
                                case 1:
                                    limitDefPer = (decimal)0.9;
                                    break;
                                case 2:
                                    limitDefPer = (decimal)0.75;
                                    break;
                                case 3:
                                case 4:
                                    limitDefPer = (decimal)0.6;
                                    break;
                            }
                        }
                        else if (useUnit is PVP_Player)
                        {
                            if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
                                limitDefPer = (decimal)0.9;
                            else
                                limitDefPer = (decimal)0.6;
                        }

                        if (defPer >= limitDefPer)
                            defPer = limitDefPer;

                        var def = (decimal)data.Final_DEF * defPer;

                        var damValue = damage - (BigInteger)def;

                        if (damValue <= 0)
                        {
                            damValue = 1;
                            var logDam = BigInteger.Log(damage);
                            damage = damValue + (BigInteger)Math.Pow(logDam, pow);
                        }
                        else
                        {
                            var logDam = BigInteger.Log(damage);
                            damage = damValue;
                            if (BattleManager.Instance.curBattleType != eBATTLETYPE.ePVPBattle)
                            {
                                damage += (BigInteger)Math.Pow(logDam, pow);
                            }
                        }
                    }
                }

                #endregion

                #region 블록 계산

                {
                    var calBlcVal = 0d;
                    if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
                    {
                        calBlcVal = ((double)data.Final_BLC / (double)useUnit.data.Final_PEN) * 0.5d;
                        if (calBlcVal >= 0.6d)
                            calBlcVal = 0.6d;
                    }
                    else
                    {
                        var val1 = Math.Log((double)BigInteger.Pow(data.Final_BLC, 2), 10);
                        var val2 = Math.Sqrt(((double)data.Final_BLC * 0.03));
                        calBlcVal = val1 * val2 * 0.00037;
                    }

                    var dicVal = Random.Range(0.0f, 1.0f);
                    //회피 했으므로 체력을 깍지 않고 바로 나간다
                    if (calBlcVal >= dicVal)
                    {
                        damage /= 2;
                        blcCk = true;
                    }
                }

                #endregion

                //SoundManager.Instance.GetSFX_Play(eSFX.SFX_Player_Hit);
                break;
            }
            case UnitType.Enemy:
            {
                if (useUnit == null)
                    break;

                var acuCk = false;

                if (this is Monster)
                {
                    #region 명중 계산

                    {
                        //플레이어 명중이 몬스터보다 높거나 같다면 회피 계산을 하지 않는다
                        if (!mustHit)
                        {
                            if (useUnit.data.Final_ACU < data.Final_ACU)
                            {
                                var acuRate = 20.0 * Math.Exp(Math.Log(4) *
                                                              ((double)((Decimal)useUnit.data.Final_ACU /
                                                                        (Decimal)data.Final_ACU)));
                                if (acuRate < 80.0) //계산 값이 80 미만이라면 회피를 계산하여 적용 한다
                                {
                                    var dic = Random.Range(0.0f, 1.0f);
                                    if (1.0f - (float)(acuRate * 0.01) >= dic)
                                    {
                                        damage = 0;
                                        damageType = DamageType.MISS;
                                        acuCk = true;
                                    }
                                }
                            }
                        }
                    }

                    #endregion

                    #region 방어 관통 계산

                    {
                        if (!mustPierce)
                        {
                            //플레이어 방어관통이 몬스터보다 높거나 같다면 방어관통을 계산을 하지 않는다
                            if (!acuCk && useUnit.data.Final_PEN < data.Final_PEN)
                            {
                                var penRate = 30.0 * Math.Exp(Math.Log(2) *
                                                              ((double)((Decimal)useUnit.data.Final_PEN /
                                                                        (Decimal)data.Final_PEN)));
                                if (penRate < 80.0) //계산 값이 80 미만이라면 방어관통을 계산하여 적용 한다
                                    damage = COMMON.Instance.StatusCal(damage, new List<float> { (float)(penRate) });
                                if (damage == 0)
                                {
                                    damage = 1;
                                }
                            }
                        }
                    }

                    #endregion
                }
                else if (this is PVP_Player)
                {
                    #region 회피 계산

                    {
                        if (!mustHit)
                        {
                            var calAvdVal = 0d;
                            if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
                            {
                                calAvdVal = ((double)data.Final_AVD / (double)useUnit.data.Final_ACU) * 0.2d;
                                if (calAvdVal >= 0.4d)
                                    calAvdVal = 0.4d;
                            }
                            else
                            {
                                var val1 = Math.Log((double)BigInteger.Pow(data.Final_AVD, 2), 10);
                                var val2 = Math.Sqrt(((double)data.Final_AVD * 0.07));
                                calAvdVal = val1 * val2 * 0.00021;
                            }

                            var dicVal = Random.Range(0.0f, 1.0f);
                            //회피 했으므로 체력을 깍지 않고 바로 나간다
                            if (calAvdVal >= dicVal)
                            {
                                DisplayToValue(0, DamageType.MISS);
                                return;
                            }
                        }
                    }

                    #endregion

                    #region 방어 계산

                    if (useUnit != null)
                    {
                        if (!mustPierce)
                        {
                            var increase = (decimal)1;
                            var pow = 2f;

                            if (damage <= 0)
                            {
                                Debug.Log($"대미지로 {damage} 들어옴 체크요망");
                                damage = 1;
                            }

                            var defPer = (decimal)data.Final_DEF * increase / (decimal)damage;

                            decimal limitDefPer = 0;
                            if (useUnit is Player)
                            {
                                if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
                                    limitDefPer = (decimal)0.9;
                                else
                                    limitDefPer = (decimal)0.6;
                            }

                            if (defPer >= limitDefPer)
                                defPer = limitDefPer;

                            var def = (decimal)data.Final_DEF * defPer;

                            var damValue = damage - (BigInteger)def;

                            if (damValue <= 0)
                            {
                                damValue = 1;
                                var logDam = BigInteger.Log(damage);
                                damage = damValue + (BigInteger)Math.Pow(logDam, pow);
                            }
                            else
                            {
                                var logDam = BigInteger.Log(damage);
                                damage = damValue;
                                if (BattleManager.Instance.curBattleType != eBATTLETYPE.ePVPBattle)
                                {
                                    damage += (BigInteger)Math.Pow(logDam, pow);
                                }
                            }
                        }
                    }

                    #endregion

                    #region 블록 계산

                    {
                        var calBlcVal = 0d;
                        if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
                        {
                            calBlcVal = ((double)data.Final_BLC / (double)useUnit.data.Final_PEN) * 0.5d;
                            if (calBlcVal >= 0.6d)
                                calBlcVal = 0.6d;
                        }
                        else
                        {
                            var val1 = Math.Log((double)BigInteger.Pow(data.Final_BLC, 2), 10);
                            var val2 = Math.Sqrt(((double)data.Final_BLC * 0.03));
                            calBlcVal = val1 * val2 * 0.00037;
                        }

                        var dicVal = Random.Range(0.0f, 1.0f);
                        //회피 했으므로 체력을 깍지 않고 바로 나간다
                        if (calBlcVal >= dicVal)
                        {
                            damage /= 2;
                            blcCk = true;
                        }
                    }

                    #endregion
                }

                SoundManager.Instance.GetSFX_Play(eSFX.SFX_Monster_Hit);
                break;
            }
        }

        damage = (BigInteger)COMMON.Instance.SpecialBuffCheck(this, useUnit, BUFFACTIONTYPE.BE_ATTACKED,
            (decimal)damage);

        //상성 대미지 확인
        switch (unitType)
        {
            case UnitType.Player:
            {
                if (useUnit is Monster monster)
                    damage = ElementCal(damage, (eELMTYPE)monster.elementalType,
                        (eELMTYPE)DBManager.Instance.playerData._UserData.userInfo.SelectElmVal);
                break;
            }
            case UnitType.Enemy:
            {
                if (this is Monster monster)
                    damage = ElementCal(damage, (eELMTYPE)DBManager.Instance.playerData._UserData.userInfo.SelectElmVal,
                        (eELMTYPE)monster.elementalType);
                else if (this is PVP_Player)
                    damage = ElementCal(damage, (eELMTYPE)DBManager.Instance.playerData._UserData.userInfo.SelectElmVal,
                        (eELMTYPE)PlayFabManager.Instance.pvpPlayerData._UserData.userInfo.SelectElmVal);
                break;
            }
        }

        if (BattleManager.Instance.curBattleType == eBATTLETYPE.eStage)
        {
            if (damage > 0)
            {
                //보스스탯 데미지 증가, 감소
                switch (unitType)
                {
                    case UnitType.Enemy:
                    {
                        if (useUnit is Player && this is Monster)
                        {
                            var monsterInfo = STAGE.Monster.MonsterMap[unitID];
                            if (monsterInfo.BossAtkStat == 0)
                                break;
                            var atkStat = useUnit.data.Final_Boss_Stat(monsterInfo.BossAtkStat);
                            damage = COMMON.Instance.MultiplyBigIntegerAndFloat(damage, (1 + atkStat));
                        }

                        break;
                    }
                    case UnitType.Player:
                    {
                        if (useUnit is Monster)
                        {
                            var monsterInfo = STAGE.Monster.MonsterMap[useUnit.unitID];
                            if (monsterInfo.BossDefStat == 0)
                                break;
                            var defStat = data.Final_Boss_Stat(monsterInfo.BossDefStat);
                            damage = COMMON.Instance.MultiplyBigIntegerAndFloat(damage, (1 - defStat));
                        }

                        break;
                    }
                }
            }
        }

        if (buffMgr.activeBuffEffectList.ContainsKey(1000008))
        {
            damage = 0;
            //PVP 상황에서 체력게이지 실시간 초기화
            if (BattleManager.Instance.curBattleType == eBATTLETYPE.ePVPBattle)
            {
                if (this is Player)
                {
                    var battle = BattleManager.Instance.battle as PVPBattle;
                    var curHp = BigInteger.Zero;
                    switch (unitType)
                    {
                        case UnitType.Player:
                            foreach (var player in PlayerManager.Instance.players)
                            {
                                if (player.hp.Value < 0)
                                {
                                    curHp += 0;
                                    continue;
                                }

                                curHp += player.hp.Value;
                            }

                            battle.myHp.Value = curHp;
                            break;
                        case UnitType.Enemy:
                            foreach (var player in PlayerManager.Instance.PVP_Players)
                            {
                                if (player.hp.Value < 0)
                                {
                                    curHp += 0;
                                    continue;
                                }

                                curHp += player.hp.Value;
                            }

                            battle.otherHp.Value = curHp;
                            break;
                    }
                }
            }
        }

        DisplayToValue(damage, damageType, elmType, blcCk);

        if (!IsInvincible)
            hp.Value -= damage;

        OnRecieveDamage?.Invoke(damage);

        DamageEffect().Forget();
    }

    public BigInteger ElementCal(BigInteger damage, eELMTYPE useUnitElm, eELMTYPE targetElm)
    {
        switch (useUnitElm)
        {
            case eELMTYPE.eNormalElm:
                switch (targetElm)
                {
                    case eELMTYPE.eFireElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)0.9);
                    case eELMTYPE.eIceElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)0.9);
                    case eELMTYPE.eLightningElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)0.9);
                }

                break;
            case eELMTYPE.eFireElm:
                switch (targetElm)
                {
                    case eELMTYPE.eNormalElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)1.1);
                    case eELMTYPE.eIceElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)0.8);
                    case eELMTYPE.eLightningElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)1.2);
                }

                break;
            case eELMTYPE.eIceElm:
                switch (targetElm)
                {
                    case eELMTYPE.eNormalElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)1.1);
                    case eELMTYPE.eFireElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)1.2);
                    case eELMTYPE.eLightningElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)0.8);
                }

                break;
            case eELMTYPE.eLightningElm:
                switch (targetElm)
                {
                    case eELMTYPE.eNormalElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)1.1);
                    case eELMTYPE.eFireElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)0.8);
                    case eELMTYPE.eIceElm:
                        return damage = (BigInteger)((decimal)damage * (decimal)1.2);
                }

                break;
        }

        return damage;
    }

    /// <summary>
    /// 혼란 상태에 걸릴시
    /// </summary>
    /// <param name="isDisarray"></param>
    protected virtual void OnDisarray(bool isDisarray)
    {
        var targetList = COMMON.Instance.GetEnemyList(this, isDisarray)
            .Where(x => x.state.Value != State.DEAD && x != this)
            .OrderBy(x => (rigid.position - x.rigid.position).sqrMagnitude);
        targetEnemy = targetList.FirstOrDefault();
    }

    /// <summary>
    /// 힐
    /// </summary>
    public void GetHeel(BigInteger heel)
    {
        if (state.Value == State.DEAD || noHeal)
            return;
        //힐 체력값과 현재 체력값의 합이 최종 체력보다 높다면?

        if (gameObject.activeSelf)
        {
            var effect = EffectManager.Instance.GetEffect("Healer_0_Skill", transform);
            effect.transform.localPosition = Vector3.zero;
            effect.Play();
        }

        heel = (BigInteger)COMMON.Instance.SpecialBuffCheck(this, this, BUFFACTIONTYPE.HP_HEEL, (decimal)heel);

        DisplayToValue(heel, DamageType.HEAL);

        if (heel + hp.Value > data.Final_Hp)
            hp.Value = data.Final_Hp;
        else
            hp.Value += heel;
    }

    /// <summary>
    /// 사용 유닛의 체력 비례로 배리어가 씌워짐
    /// </summary>
    /// <param name="useUnit">사용 유닛</param>
    /// <param name="barrierVal">배리어 배수</param>
    public void GetBarrier(Unit useUnit, decimal barrierVal)
    {
        var barrier = (BigInteger)((decimal)useUnit.data.Final_Hp * barrierVal);

        if (state.Value == State.DEAD) return;
    }

    /// <summary>
    /// 피격 효과
    /// </summary>
    /// <returns></returns>
    public async UniTask DamageEffect()
    {
        if (hp.Value <= 0) return;

        if (this is Tower) return;

        var mpb = new MaterialPropertyBlock();
        if (meshRenderer == null)
            meshRenderer = GetComponentInChildren<MeshRenderer>();

        mpb.SetColor("_Black", Color.white);
        meshRenderer.SetPropertyBlock(mpb);

        await UniTask.Delay(50);

        mpb.SetColor("_Black", Color.black);
        meshRenderer.SetPropertyBlock(mpb);
    }

    /// <summary>
    /// 적을 찾아주는 함수.
    /// Enemy는 Player를 찾고,
    /// Player는 Enemy를 찾아준다.
    /// </summary>
    public virtual void FindEnemy()
    {
        //pass
    }

    /// <summary>
    /// 유닛의 상태가 변할 때 마다 호출 될 함수
    /// </summary>
    /// <param name="state">변경을 원하는 유닛의 상태</param>
    private void ChangeStateCallBack(State state)
    {
        switch (state)
        {
            case State.NONE:
                scaleCts?.Cancel();
                break;
            case State.IDLE:
                IdleAsync().Forget();
                break;
            case State.MOVE:
                MoveAsync().Forget();
                break;
            case State.ATTACK:
                AttackAsync().Forget();
                break;
            case State.DEAD:
                DeadAsync().Forget();
                break;
            case State.SKILL:
                SkillAsync(curSkillInfo).Forget();
                break;
        }
    }

    /// <summary>
    /// 상태를 변화시켜주는 함수.
    /// 캔슬레이션 토큰이 취소되지 않은 상태인 경우 무시한다.
    /// </summary>
    /// <param name="state"></param>
    public void ChangeState(State state, DATA.Skill skillInfo = null)
    {
        if (unitAnim != null)
            MoveEffect(false);

        curSkillInfo = skillInfo;

        if (unitAnim != null)
            unitAnim.timeScale = 1f;

        var prevState = this.state.Value;
        if (prevState == State.DEAD)
        {
            if (state != State.IDLE)
                return;
        }

        if (this.state.Value != state)
            CancelCts();

        this.state.Value = state;
    }

    /// <summary>
    /// Unitask.Delay(); 함수
    /// 그냥 타이핑 귀찮아서 만들어놓음.
    /// </summary>
    /// <param name="milliseconds">밀리세컨드 단위로 입력하면 된다.</param>
    /// <returns></returns>
    protected async UniTask Delay(int milliseconds = 0)
    {
        await UniTask.Delay(milliseconds, cancellationToken: cts.Token);
    }

    /// <summary>
    /// 강제적으로 토큰을 취소시켜주는 함수.
    /// 토큰이 취소된 후에는 강제로 Idle 상태로 돌아간다.
    /// </summary>
    protected void CancelCts()
    {
        cts?.Cancel();
    }

    #region State Async Method

    /// <summary>
    /// 공격 비동기 함수
    /// </summary>
    /// <returns></returns>
    protected virtual async UniTask AttackAsync()
    {
        cts = new CancellationTokenSource();

        while (true)
        {
            //타겟이 없거나 죽었을 경우
            if (targetEnemy == null || targetEnemy.state.Value == State.DEAD || COMMON.Instance.CannotAttackCk(this))
            {
                ChangeState(State.IDLE);
                break;
            }

            var targetDistance = (rigid.position - targetEnemy.rigid.position).magnitude;

            if (targetDistance > attackRange)
            {
                ChangeState(State.MOVE);
                break;
            }

            if (targetEnemy.state.Value != State.DEAD)
            {
                var attackSpeed = 0f;

                attackSpeed = data.Final_ASPD;
                unitAnim.timeScale = attackSpeed;

                if (attackType == AttackType.Ranged || attackType == AttackType.Magic)
                {
                    if (targetEnemy is Tower) //공격 받는 대상이 타워라면?
                        targetPos = targetEnemy.transform.position;
                    else
                        targetPos = targetEnemy.hitFollower.transform.position;
                }

                StartAttackAnimation();

                var size = unitAnim.transform.localScale.y;
                unitAnim.transform.localScale = rigid.position.x - targetEnemy.rigid.position.x < 0
                    ? Vector3.one * size
                    : Vector3.one * size + Vector3.left * size * 2f;
                hpBackBar.transform.localScale = rigid.position.x - targetEnemy.rigid.position.x < 0
                    ? new Vector3(1, 1, 1)
                    : new Vector3(-1, 1, 1);
                if (mpBar != null)
                    mpBar.transform.localScale = rigid.position.x - targetEnemy.rigid.position.x < 0
                        ? new Vector3(1, 1, 1)
                        : new Vector3(-1, 1, 1);

                if (this is Player pc)
                {
                    var val = mpBar.transform.localScale.x;
                    pc.img.transform.localScale = new Vector3(0.01f * val, pc.img.transform.localScale.y,
                        pc.img.transform.localScale.z);
                }

                //애니메이션 재생 시간만큼 대기
                var sub = 1f;
                var duration = unitAnim.skeleton.Data.FindAnimation(unitAnim.AnimationName).Duration;

                if (attackSpeed > 0)
                {
                    sub = 1 / attackSpeed;
                }

                await Delay((int)((duration * sub) * 1000f));

                if (targetEnemy == null || targetEnemy.state.Value == State.DEAD)
                {
                    ChangeState(State.IDLE);
                    break;
                }

                if (this is Enemy)
                {
                    unitAnim.AnimationState.SetAnimation(0, "Idle", true);
                    await Delay((int)(attackRatio * 1000f / attackSpeed));
                }
            }

            await UniTask.Delay(0, cancellationToken: cts.Token);
        }
    }

    /// <summary>
    /// 가만히 있을 때 비동기 함수
    /// </summary>
    /// <returns></returns>
    public virtual async UniTask IdleAsync()
    {
        cts = new CancellationTokenSource();
        idleCts = new CancellationTokenSource();

        unitAnim.AnimationState.SetAnimation(0, "Idle", true);

        //스턴 혹은 슬립이 풀릴때까지 기다린다.
        await UniTask.WaitUntil(() =>
            !buffMgr.activeBuffEffectList.ContainsKey(1000000)
            && !buffMgr.activeBuffEffectList.ContainsKey(1000005)
            && state.Value != State.SKILL, cancellationToken: idleCts.Token);

        if (BattleManager.Instance.battle is PVPBattle battle)
        {
            await UniTask.WaitUntil(() => battle.battleReady);
        }

        if (PlayerManager.Instance.currentCharacter == this)
        {
            await UniTask.WaitUntil(() => !isStickMove && state.Value != State.SKILL, cancellationToken: idleCts.Token);
        }
    }

    /// <summary>
    /// 움직일 때 비동기 함수
    /// </summary>
    /// <returns></returns>
    protected virtual async UniTask MoveAsync()
    {
        cts = new CancellationTokenSource();

        if (buffMgr.activeBuffEffectList.ContainsKey(1000003))
            unitAnim.AnimationState.SetAnimation(0, "Idle", true);
        else
            unitAnim.AnimationState.SetAnimation(0, "Move", true);

        //본인과 타겟 간의 거리
        var distance = 0f;
        var size = unitAnim.transform.localScale.y;
        //타겟을 공격할 수 있는 최대 거리까지 이동
        while (true)
        {
            if (isStickMove)
                break;

            if (targetEnemy == null || targetEnemy.state.Value == State.DEAD || COMMON.Instance.CannotMoveCk(this))
            {
                ChangeState(State.IDLE);
                break;
            }

            MoveEffect(true);
            unitAnim.transform.localScale = rigid.position.x - targetEnemy.rigid.position.x < 0
                ? Vector3.one * size
                : Vector3.one * size + Vector3.left * size * 2f;
            hpBackBar.transform.localScale = rigid.position.x - targetEnemy.rigid.position.x < 0
                ? new Vector3(1, 1, 1)
                : new Vector3(-1, 1, 1);
            if (mpBar != null)
                mpBar.transform.localScale = rigid.position.x - targetEnemy.rigid.position.x < 0
                    ? new Vector3(1, 1, 1)
                    : new Vector3(-1, 1, 1);


            if (this is Player pc)
            {
                var val = mpBar.transform.localScale.x;
                pc.img.transform.localScale = new Vector3(0.01f * val, pc.img.transform.localScale.y,
                    pc.img.transform.localScale.z);

                if (pc.summonCreature != null)
                    pc.summonCreature.transform.localScale = unitAnim.transform.localScale;
            }


            var move = rigid.position +
                       (targetEnemy.rigid.position - rigid.position).normalized * unitSpeed * Time.deltaTime;
            //이동속도에 따른 애니메이션 속도 조절
            unitAnim.timeScale = unitSpeed / oriSpeed;

            rigid.position = LimitPos(move);

            if (state.Value == State.DEAD)
                return;

            distance = (rigid.position - targetEnemy.rigid.position).magnitude;

            //위에서 계산된 거리가 공격 사거리보다 짧으면 공격 시작
            if (distance <= attackRange)
            {
                ChangeState(State.ATTACK);
                break;
            }

            await UniTask.Delay(0, cancellationToken: cts.Token);
            //await UniTask.Yield(PlayerLoopTiming.FixedUpdate, cancellationToken: cts.Token);
        }
    }

    /// <summary>
    /// 죽을 때 비동기 함수
    /// </summary>
    /// <returns></returns>
    public virtual async UniTask DeadAsync()
    {
        cts = new CancellationTokenSource();

        unitAnim.AnimationState.SetAnimation(0, "Dead", false);

        foreach (var item in buffMgr.activeAbilityEffectList)
            item.Value.RemoveEffect(this);
        foreach (var item in buffMgr.activeBuffEffectList)
            item.Value.RemoveEffect(this, BUFFACTIONTYPE.DEAD);
        buffMgr.StopAllBuff();


        mpSubject?.Dispose();
        hpSubject?.Dispose();
        stateSubject?.Dispose();
        disarraySubject?.Dispose();

        scaleCts?.Cancel();

        mpBar?.gameObject.SetActive(false);
        //체력바 다 달고 사라지게 맹글기
        await UniTask.WaitUntil(() => hpBackBar.value <= 0f, cancellationToken: cts.Token);

        var aaa = GetComponentsInChildren<Effect>(true);
        foreach (var effect in aaa)
            effect.Destroy();

        hpBackBar.gameObject.SetActive(false);
    }

    /// <summary>
    /// 스킬을 사용할 때 비동기 함수
    /// </summary>
    /// <returns></returns>
    protected virtual async UniTask SkillAsync(DATA.Skill skillInfo)
    {
        cts = new CancellationTokenSource();

        //isOnSkill = true;

        if (skillInfo == null)
        {
            await UniTask.WaitUntil(() => skillInfo != null);
        }

        curSkill = skillInfo;
        Debug.Log($"{curSkill} 사용");

        var delay = skillInfo.Duration * 1000f;
        await UniTask.Delay((int)delay, cancellationToken: cts.Token);

        Debug.Log($"{curSkill} 사용종료");

        if (state.Value != State.DEAD)
        {
            ChangeState(State.IDLE);
        }
    }

    #endregion

    /// <summary>
    /// 카메라에 따른 스케일 조절 비동기 함수
    /// </summary>
    /// <returns></returns>
    protected virtual async UniTask UnitScaleAsync()
    {
        scaleCts = new CancellationTokenSource();

        while (true)
        {
            switch (BattleManager.Instance.curBattleType)
            {
                case eBATTLETYPE.eStage:
                case eBATTLETYPE.eGoldMine:
                case eBATTLETYPE.eWeaponReinforceDungeon:
                case eBATTLETYPE.eShiledReinforceDungeon:
                case eBATTLETYPE.eAccReinforceDungeon:
                case eBATTLETYPE.eWarriorGrowthDungeon:
                case eBATTLETYPE.eArcherGrowthDungeon:
                case eBATTLETYPE.eMageGrowthDungeon:
                case eBATTLETYPE.eHealerGrowthDungeon:
                case eBATTLETYPE.eUpgradeBattle:
                case eBATTLETYPE.ePVPBattle:
                case eBATTLETYPE.eBossChallenge:
                {
                    if (this is PVP_Player && state.Value == State.DEAD)
                        break;

                    var scaleVal =
                        ((CameraManager.Instance.virtualCameraList[(int)eCameraIdx.ePlayerGroupCam].transform.position
                            .z + 20f) - transform.position.z) * ViewManager.Instance.testVal;
                    transform.localScale = new Vector3(1 + scaleVal, 1 + scaleVal, 1 + scaleVal);
                    break;
                }
            }

            await UniTask.Delay(0, cancellationToken: scaleCts.Token);
            //await UniTask.Yield(PlayerLoopTiming.FixedUpdate, cancellationToken: scaleCts.Token);
        }
    }

    /// <summary>
    /// Dead 로 상태 변화 후 죽는 애니메이션이 종료될 때 호출될 함수
    /// </summary>
    protected virtual void OnDeadEffect()
    {
        //pass
    }

    /// <summary>
    /// 유닛들 공격 애니메이션 실행시 호출
    /// </summary>
    protected virtual void StartAttackAnimation()
    {
        var rnd = Random.Range(0.0001f, 1f);
        if (rnd <= data.Final_SAtk_p)
        {
            unitAnim.AnimationState.SetAnimation(0, "Attack_2", false);
            AttackEffect(true);
        }
        else
        {
            unitAnim.AnimationState.SetAnimation(0, "Attack_1", false);
            AttackEffect(false);
        }
    }

    /// <summary>
    /// 공격시 강/일반 공격 구분하여 발동할 이펙트
    /// </summary>
    protected virtual void AttackEffect(bool isHeavyAttack)
    {
        //pass
    }

    /// <summary>
    /// 스킬 사용시 나올 이펙트들
    /// </summary>
    /// <param name="skillInfo">스킬 정보</param>
    /// <param name="targetList">타겟 리스트</param>
    public virtual void OnSkillEffect(DATA.Skill skillInfo, Unit useUnit, List<Unit> targetList)
    {
        var type = (eTARGETTYPE)skillInfo.Target;

        switch (type)
        {
            case eTARGETTYPE.eSelf:
            case eTARGETTYPE.eParty:
            case eTARGETTYPE.eSelfAndParty:
                if (targetList.Count > 0)
                {
                    for (var i = 0; i < targetList.Count; i++)
                    {
                        var skill = SkillManager.Instance.GetSkill(skillInfo.ID, this, targetList[i].transform);
                        skill.transform.localPosition = Vector3.zero;
                        skill.transform.eulerAngles = unitAnim.transform.localScale.x < 0
                            ? new Vector3(0, -90, 0)
                            : new Vector3(0, 90, 0);
                        skill.UseSkill(targetList[i]).Forget();
                    }
                }
                else
                {
                    var skill = SkillManager.Instance.GetSkill(skillInfo.ID, this, useUnit.transform);
                    skill.transform.localPosition = Vector3.zero;
                    skill.transform.eulerAngles = unitAnim.transform.localScale.x < 0
                        ? new Vector3(0, -90, 0)
                        : new Vector3(0, 90, 0);
                    skill.UseSkill().Forget();
                }

                break;
            case eTARGETTYPE.eEnemy:
                if (targetList.Count > 0)
                {
                    for (var i = 0; i < targetList.Count; i++)
                    {
                        var skill = SkillManager.Instance.GetSkill(skillInfo.ID, this);
                        skill.transform.position = targetList[i].transform.position;
                        skill.transform.eulerAngles = unitAnim.transform.localScale.x < 0
                            ? new Vector3(0, -90, 0)
                            : new Vector3(0, 90, 0);
                        skill.UseSkill(targetList[i]).Forget();
                    }
                }
                else
                {
                    var skill = SkillManager.Instance.GetSkill(skillInfo.ID, this);
                    skill.transform.position = useUnit.rigid.position;
                    skill.transform.eulerAngles = unitAnim.transform.localScale.x < 0
                        ? new Vector3(0, -90, 0)
                        : new Vector3(0, 90, 0);
                    skill.UseSkill().Forget();
                }

                break;
        }
    }

    /// <summary>
    /// 스킬 자동 스킬 사용
    /// </summary>
    /// <param name="equipIndex"></param>
    /// <returns></returns>
    public async UniTask AutoSkillUse(int equipIndex)
    {
        //현재 유닛이 스킬 사용이 가능한 유닛이면
        if (canSkillUse /*&& !isAttack*/)
        {
            await UseSkill(equipIndex);
        }
    }

    /// <summary>
    /// 스킬 사용 함수
    /// </summary>
    /// <param name="equipIndex"></param>
    /// <returns></returns>
    public virtual async UniTask UseSkill(int equipIndex)
    {
        //pass
    }

    /// <summary>
    /// 대미지, 힐 등의 표현을 값 표현을 하기 위한 함수
    /// </summary>
    /// <returns></returns>
    public void DisplayToValue(BigInteger val, DamageType attackType = DamageType.N_ATTACK,
        eELMTYPE elmType = eELMTYPE.eNormalElm, bool blcCk = false)
    {
        if (!DBManager.Instance.playerData._UserData.settingInfo.bDmgCk || GameManager.Instance.SaveModeEnableCk)
            return;

        var effect = EffectManager.Instance.GetObjEffect(DISPLAY_TXT_PREFAB);
        effect.GetComponent<EffectDisplayToVal>().Setting(rigid == null ? transform.position : rigid.position,
            TextManager.Instance.ConvertToNumberString(val.ToString(), true), attackType, this, elmType, blcCk);
    }

    /// <summary>
    /// 유닛의 행동 범위
    /// </summary>
    public Vector3 LimitPos(Vector3 pos)
    {
        switch (BattleManager.Instance.curBattleType)
        {
            case eBATTLETYPE.eStage:
            case eBATTLETYPE.eGoldMine:
            case eBATTLETYPE.eWeaponReinforceDungeon:
            case eBATTLETYPE.eShiledReinforceDungeon:
            case eBATTLETYPE.eAccReinforceDungeon:
            case eBATTLETYPE.eWarriorGrowthDungeon:
            case eBATTLETYPE.eArcherGrowthDungeon:
            case eBATTLETYPE.eMageGrowthDungeon:
            case eBATTLETYPE.eHealerGrowthDungeon:
            case eBATTLETYPE.eUpgradeBattle:
            case eBATTLETYPE.ePVPBattle:
            case eBATTLETYPE.eBossChallenge:
            {
                var tempPos = pos;
                //if (BattleManager.Instance.curBattleType == Define.eBATTLETYPE.eGoldMine)
                //    tempPos = new UnityEngine.Vector3(pos.x, 0, pos.z);

                if (pos.x < minX)
                    tempPos = new Vector3(minX, 0, tempPos.z);
                else if (pos.x > maxX)
                    tempPos = new Vector3(maxX, 0, tempPos.z);

                if (pos.z < minZ)
                    tempPos = new Vector3(tempPos.x, 0, minZ);
                else if (pos.z > maxZ)
                    tempPos = new Vector3(tempPos.x, 0, maxZ);

                return tempPos;
            }
            case eBATTLETYPE.eUnderground_Maze:
            {
                var tempPos = pos;

                minX = -5f;
                maxX = 13f;

                if (pos.x < minX)
                    tempPos = new Vector3(minX, 0, tempPos.z);
                else if (pos.x > maxX)
                    tempPos = new Vector3(maxX, 0, tempPos.z);

                if (pos.z < minZ)
                    tempPos = new Vector3(tempPos.x, 0, minZ);
                else if (pos.z > maxZ)
                    tempPos = new Vector3(tempPos.x, 0, maxZ);

                return tempPos;
            }
        }

        return Vector3.zero;
    }

    /// <summary>
    /// 크리티컬 확률 계산
    /// </summary>
    /// <param name="target">공격받을 대상(상성 계산용)</param>
    /// <param name="isHeavyAttack">강공격인가?</param>
    /// <returns></returns>
    public BigInteger CriticalCalculate(Unit target, bool isHeavyAttack = false)
    {
        damageType = DamageType.N_ATTACK;
        var dmg = (decimal)data.Final_ATK;
        dmg = COMMON.Instance.SpecialBuffCheck(this, targetEnemy, BUFFACTIONTYPE.DMG_CALCULATE, dmg);

        if (isHeavyAttack)
        {
            dmg = (decimal)data.Final_SAtkDmg;
            damageType = DamageType.S_ATTACK;
        }


        //크리티컬 확률 계산
        var crtPer = Random.Range(0.0001f, 1f);
        var crt = data.Final_Crt_p;

        if (crt >= crtPer)
        {
            dmg = (decimal)data.Final_CrtDmg;
            damageType = DamageType.N_CRITICAL;

            //슈퍼 크리티컬 확률 계산
            crtPer = Random.Range(0.0001f, 1f);
            crt = data.Final_SCrt_p;
            if (crt >= crtPer)
            {
                dmg = (decimal)data.Final_SCrtDmg;
                damageType = DamageType.S_CRITICAL;

                //하이퍼 크리티컬 확률 계산
                crtPer = Random.Range(0.0001f, 1f);
                crt = data.Final_HCrt_p;
                if (crt >= crtPer)
                {
                    dmg = (decimal)data.Final_HCrtDmg;
                    damageType = DamageType.H_CRITICAL;
                }
            }
        }

        return CalculateCompatibility(target, dmg);
    }

    /// <summary>
    /// 상성 계산 함수
    /// </summary>
    /// <param name="target"></param>
    /// <param name="dmg"></param>
    /// <returns></returns>
    public BigInteger CalculateCompatibility(Unit target, decimal dmg)
    {
        //상성 계산
        var increase = (decimal)1.05;
        if (attackType == target.attackType)
        {
            dmg *= increase;
            return (BigInteger)dmg;
        }

        switch (attackType)
        {
            case AttackType.Melee:
                switch (target.attackType)
                {
                    case AttackType.Ranged:
                        increase = (decimal)1.1;
                        break;
                    case AttackType.Magic:
                        increase = (decimal)0.9;
                        break;
                    case AttackType.Mix:
                        increase = (decimal)0.95;
                        break;
                }

                break;
            case AttackType.Ranged:
                switch (target.attackType)
                {
                    case AttackType.Melee:
                        increase = (decimal)0.9;
                        break;
                    case AttackType.Magic:
                        increase = (decimal)0.95;
                        break;
                    case AttackType.Mix:
                        increase = (decimal)1.1;
                        break;
                }

                break;
            case AttackType.Magic:
                switch (target.attackType)
                {
                    case AttackType.Melee:
                        increase = (decimal)1.1;
                        break;
                    case AttackType.Ranged:
                        increase = (decimal)0.95;
                        break;
                    case AttackType.Mix:
                        increase = (decimal)0.9;
                        break;
                }

                break;
            case AttackType.Mix:
                switch (target.attackType)
                {
                    case AttackType.Melee:
                        increase = (decimal)0.95;
                        break;
                    case AttackType.Ranged:
                        increase = (decimal)0.9;
                        break;
                    case AttackType.Magic:
                        increase = (decimal)1.1;
                        break;
                }

                break;
        }

        dmg *= increase;
        return (BigInteger)dmg;
    }

    /// <summary>
    /// 강공격 이펙트
    /// </summary>
    public virtual void HeavyAttackDamage(Vector3 center)
    {
        CameraManager.Instance.ImPulse();
        //var centerPos = new UnityEngine.Vector3(center.x, 0f, center.y);
        var targetList = COMMON.Instance.GetEnemyList(this).Where(x => (center - x.rigid.position).magnitude <= 4f);
        foreach (var target in targetList)
        {
            var damage = CriticalCalculate(target, true);
            damage = (BigInteger)COMMON.Instance.SpecialBuffCheck(this, target, BUFFACTIONTYPE.DO_ATTACK,
                (decimal)damage);
            if (this is PVP_Player)
                target.GetDamage(damage, this, damageType,
                    (eELMTYPE)PlayFabManager.Instance.pvpPlayerData._UserData.userInfo.SelectElmVal);
            else
                target.GetDamage(damage, this, damageType,
                    (eELMTYPE)DBManager.Instance.playerData._UserData.userInfo.SelectElmVal);
        }
    }

    /// <summary>
    /// 유닛이 움직일때 호출해 줄 함수
    /// </summary>
    /// <param name="isOn">유닛이 움직이는 중인가</param>
    public virtual void MoveEffect(bool isOn)
    {
        //pass
    }

    /// <summary>
    /// 투사체가 충돌체를 포함할 시에 추가 세팅
    /// </summary>
    protected virtual void ColliderProjectileSetting()
    {
        //pass
    }

    private async UniTask HpBackGaugeMove()
    {
        if (hpBackBar.value == hpBar.value)
            return;

        await UniTask.Delay(500);

        hpBarCts?.Cancel();
        hpBarCts = new CancellationTokenSource();

        var value = (hpBackBar.value - hpBar.value) / 10f;

        while (hpBackBar.value >= hpBar.value)
        {
            hpBackBar.value -= value;
            await UniTask.Delay(0, cancellationToken: hpBarCts.Token);
        }
    }

    public virtual void OnDestroy()
    {
        cts?.Cancel();
        scaleCts?.Cancel();
        hpBarCts?.Cancel();
        idleCts?.Cancel();
    }
}
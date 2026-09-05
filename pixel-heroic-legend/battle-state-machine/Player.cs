using Cysharp.Threading.Tasks;
using Spine.Unity;
using Spine;
using System.Linq;
using UnityEngine;
using System.Numerics;
using UniRx;
using static Define;
using System.Collections.Generic;
using DG.Tweening;
using DATA;
using Unity.VisualScripting;

/// <summary>
/// 유닛의 직업 => Define.cs로 옮겨도 됨
/// </summary>
public enum ePlayerClass
{
    SwordMan = 1,
    Archer,
    Mage,
    Healer,
    Rogue,
}

/// <summary>
/// 유저가 컨트롤 가능한 유닛
/// </summary>
public class Player : Unit
{
    /// <summary>
    /// 현재 유닛의 직업
    /// </summary>
    public ePlayerClass playerClass;

    /// <summary>
    /// 무덤 애니메이션
    /// </summary>
    protected SkeletonAnimation tombAnim;
    /// <summary>
    /// 소환수 클래스
    /// </summary>
    public SummonCreature summonCreature;
    /// <summary>
    /// 반응형 프로그래밍을 위한 프로퍼티.
    /// Mp Value가 바뀔때마다 그에 반응할 함수를 콜백시켜준다.
    /// </summary>
    public ReactiveProperty<BigInteger> mp;
    /// <summary>
    /// 최대 마나
    /// </summary>
    public BigInteger fullMp = new BigInteger(0);
    /// <summary>
    /// 캐릭터 초상화 클릭시 활성화 될 이펙트
    /// </summary>
    public ParticleSystem selectEffect;
    /// <summary>
    /// 달릴때 흙먼지
    /// </summary>
    public ParticleSystem runEffect;
    /// <summary>
    /// 스킬 사용 간격
    /// </summary>
    public float skillTerm;

    #region 변수

    //kami 테스트
    [SerializeField] private TMPro.TextMeshProUGUI talk = null;
    public UnityEngine.UI.Image img = null;

    #endregion


    protected override void Awake()
    {
        base.Awake();
        tombAnim = skelAnimDict["TombSkeleton"];
        tombAnim.AnimationState.Complete += delegate
        {
            if (tombAnim.AnimationName == "Fall")
            {
                tombAnim.AnimationState.SetAnimation(0, "Idle", true);
            }
        };

        summonCreature = GetComponentInChildren<SummonCreature>(true);
        autoSkill = true;
    }

    private void OnEnable()
    {
        GameEventSubject.RegisterHandler(GameEventType.EQUIP_ITEM_EVENT, SetSkin);
        GameEventSubject.RegisterHandler(GameEventType.PLAYER_COSTUME_CHANGE_EVENT, SetSkin);
        GameEventSubject.RegisterHandler(GameEventType.PLAYER_TALK_EVENT, TestTalk);
    }

    private void OnDisable()
    {
        GameEventSubject.UnregisterHandler(GameEventType.EQUIP_ITEM_EVENT, SetSkin);
        GameEventSubject.UnregisterHandler(GameEventType.PLAYER_COSTUME_CHANGE_EVENT, SetSkin);
        GameEventSubject.UnregisterHandler(GameEventType.PLAYER_TALK_EVENT, TestTalk);
    }

    public override void Init(int unitID = 0)
    {
        base.Init();

        CancelCts();

        ChangeState(State.NONE);
        tombAnim.gameObject.SetActive(false);

        mpSubject = mp.TakeUntilDestroy(this).Subscribe(GetMPCallBack);

        //절전모드 사용 여부에 따른 스파인 셋팅
        SetPowerSaveMode(GameManager.Instance.SaveModeEnableCk);
        SetSkin(null);

        unitAnim.AnimationState.Apply(unitAnim.Skeleton);

        mpBar?.gameObject.SetActive(true);

        if (BattleManager.Instance.curBattleType == eBATTLETYPE.eUnderground_Maze)
        {
            unitAnim.transform.localRotation = UnityEngine.Quaternion.Euler(0f, 0f, 0f);
            if (summonCreature != null)
                summonCreature.transform.localRotation = UnityEngine.Quaternion.Euler(0f, 0f, 0f);
        }
        else
        {
            unitAnim.transform.localRotation = UnityEngine.Quaternion.Euler(30f, 0f, 0f);
            if (summonCreature != null)
                summonCreature.transform.localRotation = UnityEngine.Quaternion.Euler(30f, 0f, 0f);
        }

        skillTerm = GLOBAL_COOL_TIME;

        autoHpMpRcvCts?.Cancel();
        autoHpMpRcvCts = new System.Threading.CancellationTokenSource();
        AutoHpRCV().Forget();
        AutoMpRCV().Forget();
    }

    public override void StatInit()
    {
        base.StatInit();
        fullMp = data.Final_Mp;
        mp.Value = fullMp;

        hpBackBar.gameObject.SetActive(true);
    }

    public override async UniTask IdleAsync()
    {
        await base.IdleAsync();

        await UniTask.WaitUntil(() => COMMON.Instance.GetEnemyList(this)/*EnemyManager.Instance.enemies*/.Count(x => x.state.Value != State.DEAD) > 0, cancellationToken: cts.Token);

        FindEnemy();
    }

    /// <summary>
    /// PVP Player 클래스에서 Player IdleAsync 함수를 제외하고 Unit 클래스의 IdleAsync 함수를 사용하기
    /// </summary>
    /// <returns></returns>
    public async UniTask IdleAsyncCall()
    {
        await base.IdleAsync();
    }

    public override async UniTask DeadAsync()
    {
        PlayerManager.Instance.deadPlayer.Enqueue(this);

        await base.DeadAsync();

        var targetListContainThis = /*EnemyManager.Instance.enemies*/COMMON.Instance.GetEnemyList(this).Where(x => x.targetEnemy == this).ToList();
        for (var i = 0; i < targetListContainThis.Count; i++)
        {
            targetListContainThis[i].targetEnemy = null;
        }

        tombAnim.gameObject.SetActive(true);
        tombAnim.AnimationState.SetAnimation(0, "Fall", false);
        PlayerManager.Instance.ViewOneTarget(transform);
        await UniTask.Delay(1000, cancellationToken: cts.Token);

        var idx = 0;
        if (this == PlayerManager.Instance.currentCharacter)
        {
            var liveChar = PlayerManager.Instance.players.Find(x => x.state.Value != State.DEAD);
            idx = liveChar == null ? 0 : PlayerManager.Instance.players.IndexOf(liveChar);
        }
        else
            idx = PlayerManager.Instance.players.IndexOf(PlayerManager.Instance.currentCharacter);

        //성장 던전이 아니라면 다른 플레이어 클래스에게 넘겨 준다
        if (BattleManager.Instance.curBattleType != eBATTLETYPE.eWarriorGrowthDungeon && BattleManager.Instance.curBattleType != eBATTLETYPE.eArcherGrowthDungeon &&
           BattleManager.Instance.curBattleType != eBATTLETYPE.eMageGrowthDungeon && BattleManager.Instance.curBattleType != eBATTLETYPE.eHealerGrowthDungeon)
            ViewManager.Instance.characterImages.CharacterIconChange(idx, true);
        //플레이어가 죽을 당시 스킬 사용 변수가 살아 있다면 다른 플레이어들이 스킬을 사용할 수 있게
        //스킬 사용 변수를 flase 시켜 준다.


    }

    /// <summary>
    /// PVP Player 클래스에서 Player DeadAsynce 함수를 제외하고 Unit 클래스의 DeadAsynce 함수를 사용하기
    /// </summary>
    /// <returns></returns>
    public async UniTask DeadAsyncCall()
    {
        await base.DeadAsync();
    }

    public override void FindEnemy()
    {
        base.FindEnemy();

        //안 죽은 적들을 가까이 있는 순서로 가져옴 (O²)
        var targetList = COMMON.Instance.GetEnemyList(this, isDisarray.Value).Where(x => x.state.Value != State.DEAD && x != this).OrderBy(x => (rigid.position - x.rigid.position).sqrMagnitude);

        //더 이상 잔존하는 적이 없으면 IDLE
        if (/*EnemyManager.Instance.enemies.Count == 0 || */targetList.Count() == 0)
        {
            ChangeState(State.IDLE);
            return;
        }

        var players = PlayerManager.Instance.GetLivePlayer();
        if (players.Count == 0) return;

        targetEnemy = targetList.FirstOrDefault();
        ChangeState(State.MOVE);
    }

    /// <summary>
    /// 특수 이동기
    /// 스킬을 사용 할 때라던가 특수 동작을 해야할 때 호출시킬 예정
    /// </summary>
    /// <param name="targetPos"></param>
    public virtual void SpecialMove(float duration = 0f, UnityEngine.Vector3 targetPos = default)
    {
        //pass
    }

    /// <summary>
    /// 스킬 사용 시 mp가 닳으면 콜백될 함수
    /// </summary>
    /// <param name="mp"></param>
    protected virtual void GetMPCallBack(BigInteger mp)
    {
        mpBar.value = (float)mp / (float)fullMp;

        var partyIndex = (float)PlayerManager.Instance.players.IndexOf(this as Player);
        GameEventSubject.SendGameEvent(GameEventType.ITEM_CHARACTER_MP_EVENT, partyIndex, mpBar.value);
    }

    /// <summary>
    /// HP 자동 회복 함수
    /// </summary>
    /// <returns></returns>
    protected virtual async UniTask AutoHpRCV()
    {
        while (true)
        {
            //TABLE.Character.CharacterMap[unitID]
            //임시값 6초
            await UniTask.Delay(6000, cancellationToken: autoHpMpRcvCts.Token);
            if (noHeal)
                continue;
            if (state.Value == State.DEAD) return;

            var tempRcv = new BigInteger(0);

            if (data.Final_Hp - hp.Value >= data.Final_Hp_Rcv)
            {
                tempRcv = data.Final_Hp_Rcv;
                hp.Value += tempRcv;
            }
            else
            {
                tempRcv = data.Final_Hp - hp.Value;
                hp.Value += tempRcv;
            }

            if (tempRcv > 0)
                DisplayToValue(tempRcv, DamageType.HEAL);

            //Debug.LogError($"{name}이 {tempRcv}만큼의 체력회복");
        }
    }

    /// <summary>
    /// MP 자동 회복 함수
    /// </summary>
    /// <returns></returns>
    protected virtual async UniTask AutoMpRCV()
    {
        while (true)
        {
            //TABLE.Character.CharacterMap[unitID]
            //임시값 8초
            await UniTask.Delay(8000, cancellationToken: autoHpMpRcvCts.Token);
            if (state.Value == State.DEAD) return;

            var tempRcv = new BigInteger(0);

            if (data.Final_Mp - mp.Value >= data.Final_Mp_Rcv)
            {
                tempRcv = data.Final_Mp_Rcv;
                mp.Value += tempRcv;
            }
            else
            {
                tempRcv = data.Final_Mp - mp.Value;
                mp.Value += tempRcv;
            }

            if (tempRcv > 0)
                DisplayToValue(tempRcv, DamageType.MANA);

            //Debug.LogError($"{name}이 {tempRcv}만큼의 마나회복");
        }
    }

    /// <summary>
    /// 스킨 설정
    /// </summary>
    /// <param name="ge"></param>
    public void SetSkin(GameEvent ge)
    {
        var idx = DBManager.Instance.playerData._UserData.characterInfo.CharacterList.FindIndex(item => item.ChracterID == unitID);
        if (idx != -1)
        {
            var info = DBManager.Instance.playerData._UserData.characterInfo.CharacterList[idx];

            var newSkin = new Skin("PLAYER");
            var skinID = 0;
            skinID = info.ViewEquipAvataID == -1 ? info.EquipAvataID : info.ViewEquipAvataID;
            var baseCostume = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetCostumeSpineID(skinID));
            if (baseCostume != null)
                newSkin.AddSkin(baseCostume);
            skinID = info.ViewEquipMainWeaponID == -1 ? info.EquipMainWeaponID : info.ViewEquipMainWeaponID;
            var baseWeapon = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetWeaponSpineID(skinID));
            if (baseWeapon != null)
                newSkin.AddSkin(baseWeapon);
            skinID = info.ViewEquipSubWeaponID == -1 ? info.EquipSubWeaponID : info.ViewEquipSubWeaponID;
            var baseWeaponSub = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetSubWeaponSpineID(skinID));
            if (baseWeaponSub != null)
                newSkin.AddSkin(baseWeaponSub);

            unitAnim.Skeleton.SetSkin(newSkin);
            unitAnim.Skeleton.SetSlotsToSetupPose();
        }
    }

    public override async UniTask UseSkill(int equipIndex)
    {
        if (buffMgr.activeBuffEffectList.ContainsKey(1000001) || buffMgr.activeBuffEffectList.ContainsKey(1000000))
        {
            //Debug.Log($"{this} : 상태이상(스턴 또는 침묵)이라 스킬 못 씀");
            return;
        }

        await base.UseSkill(equipIndex);
        var coolTimeDict = SkillManager.Instance.GetPlayerSkillCoolTimeDict(this);

        //kami 임시 테스트
        //if (monster.playerClass == ePlayerClass.Healer || monster.playerClass == ePlayerClass.Archer ||
        //    monster.playerClass == ePlayerClass.Mage || monster.playerClass == ePlayerClass.Warrior)
        //    return;

        if (coolTimeDict == null || !coolTimeDict.ContainsKey(equipedSkillIdx[equipIndex]))
            return;

        var skillData = DATA.Skill.SkillMap[equipedSkillIdx[equipIndex]];
        var needMana = (BigInteger)COMMON.Instance.SpecialBuffCheck(this, null, BUFFACTIONTYPE.ON_SKILL, (decimal)skillData.NeedMana);
        //현재 가진 mp가 스킬 요구치보다 낮으면 사용 못 함
        if (mp.Value < needMana)
        {
            Debug.Log("마나가 업슴");
            return;
        }

        if (equipedSkillIdx[equipIndex] == 0) return;
        SkillManager.Instance.SkillTargetCheck(equipedSkillIdx[equipIndex], this, out var targetList);

        var usingIndex = equipedSkillIdx[equipIndex];
        var skillInfo = DATA.Skill.SkillMap[usingIndex];

        if (targetList.Count > 0)
        {
            if (skillInfo.Distance > 0)
            {
                var nearDistance = targetList.Min(x => (x.rigid.position - rigid.position).magnitude);
                //거리가 아직 멀면 사용 못함
                if (nearDistance > skillData.Distance)
                {
                    //Debug.LogError("타겟이 멂");
                    return;
                }
            }
        }
        else
        {
            //유닛 근처에 범위형 스킬을 쓰는게 아니면 타겟이 진짜 없는거임. 이거 ㄹㅇ임;;
            if (skillInfo.TargetSelect != 8)
                return;
        }

        if (state.Value == State.SKILL) return;
        if (state.Value == State.DEAD) return;

        ChangeState(State.SKILL, skillInfo);

        //unitAnim.AnimationState.ClearTrack(0);  //캐릭터가 스킬 사용할 때 다른상태 애니메이션 재생 방지

        //쿨타임 초기화
        var skillCoolTimeDict = SkillManager.Instance.GetPlayerSkillCoolTimeDict(this);
        skillTerm = GLOBAL_COOL_TIME;
        var coolTime = skillInfo.CoolTime - (skillInfo.CoolTime * data.Final_Skill_CoolTime_Down);
        skillCoolTimeDict[usingIndex] = coolTime;

        //시전시간 초기화
        var partyIndex = PlayerManager.Instance.players.IndexOf(this);
        SkillManager.Instance.playerSkillCastTimeDict[partyIndex] = DATA.Skill.SkillMap[usingIndex].CastingTime;

        //unitAnim.AnimationState.SetAnimation(0, DATA.Skill.SkillMap[usingIndex].PlayAniName, false);

        //스킬 이펙트
        OnSkillEffect(skillInfo, this, targetList);

        //여기에 조명을 건드려 보자
        BattleManager.Instance.SetSkillUseLight();
        ///////////////////////////////////////////////////////////////
        if (PlayerManager.Instance.currentCharacter == this)
        {
            ViewManager.Instance.SkillImageStart(playerClass);
            //잠깐 멈추기

            if (DBManager.Instance.playerData._UserData.settingInfo.bSkillEffectCk || GameManager.Instance.SaveModeEnableCk)
            {
                CameraManager.Instance.ChangeCamera(2, transform);

                await UniTask.Delay(System.TimeSpan.FromMilliseconds(250), DelayType.Realtime);

                if (ViewManager.Instance.curViewUID == (int)Define.eVIEW.MainView)
                    Time.timeScale = 0.25f;

                await UniTask.Delay(System.TimeSpan.FromMilliseconds(500), DelayType.Realtime);

                Time.timeScale = 1f;
                CameraManager.Instance.ChangeCamera(1);
                //카메라 빠지는 시점까지 기다리기 위한 딜레이
                await UniTask.Delay(System.TimeSpan.FromMilliseconds(250), DelayType.Realtime);
            }
        }
        ///////////////////////////////////////////////////////////////
        mp.Value -= needMana;
#if UNITY_EDITOR || DEVELOPMENT_BUILD   
        if (PlayerPrefs.GetInt("FullManaIncrease", 0) == 1)
        {
            mp.Value = fullMp;
        }
#endif
    }

    /// <summary>
    /// 부활
    /// </summary>
    /// <param name="heelPerVal">퍼센트로 체력 회복</param>
    public void GetRessurection(decimal heelPerVal)
    {
        Init();

        var heelVal = (BigInteger)((decimal)data.Final_Hp * heelPerVal);
        hp.Value = heelVal;
        ChangeState(State.IDLE);
    }

    public override void MoveEffect(bool isOn)
    {
        base.MoveEffect(isOn);
        if (isOn)
        {
            if (runEffect.gameObject.activeSelf)
                runEffect.transform.localEulerAngles = unitAnim.transform.localScale.x > 0 ? UnityEngine.Vector3.down * 90f : UnityEngine.Vector3.up * 90f;
            else
                runEffect.gameObject.SetActive(true);
        }
        else
        {
            runEffect.gameObject.SetActive(false);
        }
    }

    /// 절전 모드 설정 
    /// </summary>
    /// <param name="enable">활성화 여부</param>
    private void SetPowerSaveMode(bool enable)
    {
        meshRenderer.enabled = !enable;
        if (!enable)
            SetSkin(null);
    }

    /// <summary>
    /// 속성 대미지 (강화석 던전에서의 기믹을 위한 함수)
    /// </summary>
    public void GetElementDamage(BigInteger damage, eELMTYPE elmType)
    {
        switch (BattleManager.Instance.curBattleType)
        {
            case eBATTLETYPE.eWeaponReinforceDungeon:
                {
                    if (data.Final_Fire_Reg >= damage)
                        damage = COMMON.Instance.StatusCal(damage, new List<float>() { { 0.77f * 100 } });
                    else
                    {
                        var oridmg = (decimal)damage;
                        var val = 1 + (System.Math.Log((double)(1 + ((decimal)damage - (decimal)data.Final_Fire_Reg) * (decimal)0.1), 10));
                        damage = (BigInteger)(oridmg * (decimal)val);
                    }

                    DisplayToValue(damage, DamageType.N_ATTACK, elmType);
                    hp.Value -= damage;
                    DamageEffect().Forget();

                    break;
                }
            case eBATTLETYPE.eShiledReinforceDungeon:
                {
                    if (data.Final_Ice_Reg < damage)
                    {
                        var calVal = 10 * System.Math.Exp(System.Math.Log(8.0) * (double)((decimal)data.Final_Ice_Reg / (decimal)damage));

                        //이속 감소
                        var buffCom = /*new BuffComponent*/SkillBuffAbilityPool.GetBuffComponent(1000002, (decimal)(calVal * 0.01), 9999f, 1.0f, null, eBUFFTYPE.eCrowdControl);
                        buffCom.ApplyEffect(null, this);

                    }
                    break;
                }
            case eBATTLETYPE.eAccReinforceDungeon:
                {
                    if (data.Final_Lightning_Reg < damage)
                    {
                        //kami 스턴 확률 수정 20240919
                        var calVal = 10 * System.Math.Exp(System.Math.Log(/*7.5f*/8.0f) * (double)((decimal)data.Final_Lightning_Reg / (decimal)damage));

                        var dic = Random.Range(0.0f, 1.0f);
                        if (1f - (calVal * 0.01) > dic)
                        {
                            //스턴
                            var buffCom = /*new BuffComponent*/SkillBuffAbilityPool.GetBuffComponent(1000000, 0, 2.0f, 1.0f, null, eBUFFTYPE.eCrowdControl);
                            buffCom.ApplyEffect(null, this);
                        }
                    }
                    break;
                }
        }
    }



    #region 토크 기능

    public void TestTalk(GameEvent ge)
    {
        if (state.Value == State.DEAD)
        {
            TalkEnd();
            return;
        }

        var classType = ge.ReadInt;

        talk.maxVisibleCharacters = 0;

        if (playerClass != (ePlayerClass)classType)
            return;

        img.gameObject.SetActive(true);
        talk.text = TextManager.Instance.GetText(COMMON.Instance.GetRandomPlayerTalk(playerClass, data.Final_ACU, data.Final_PEN));


        DOTween.To(x => talk.maxVisibleCharacters = (int)x, 0f, talk.text.Length, 0.5f).SetUpdate(true);

        Invoke("TalkEnd", 2.0f);
    }

    public void TalkEnd()
    {
        img.gameObject.SetActive(false);
        talk.text = "";
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        autoHpMpRcvCts?.Cancel();
    }

    #endregion
}

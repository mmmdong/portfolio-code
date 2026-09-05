using Cysharp.Threading.Tasks;
using Spine;
using System.Linq;
using System.Numerics;
using UniRx;

public class PVP_Player : Player
{
    #region 변수

    #endregion

    protected override void Awake()
    {
        base.Awake();
    }

    private void OnEnable()
    {
    }
    private void OnDisable()
    {
    }

    /// <summary>
    /// 스킨 설정
    /// </summary>
    public void SetSkin(int avatarID, int weaponID, int subWeaponID)
    {
        var newSkin = new Skin("PLAYER");
        //var baseCostume = unitAnim.Skeleton.Data.FindSkin("Costume/Costume_0");
        var baseCostume = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetCostumeSpineID(avatarID));
        newSkin.AddSkin(baseCostume);
        var baseWeapon = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetWeaponSpineID(weaponID));
        newSkin.AddSkin(baseWeapon);
        var baseWeaponSub = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetSubWeaponSpineID(subWeaponID));
        newSkin.AddSkin(baseWeaponSub);

        unitAnim.Skeleton.SetSkin(newSkin);
        unitAnim.Skeleton.SetSlotsToSetupPose();
    }

    public void SetSkin_Awkane(int awakenLv)
    {
        var newSkin = new Skin("PLAYER");
        //var baseCostume = unitAnim.Skeleton.Data.FindSkin("Costume/Costume_0");
        var baseCostume = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetCostumeSpineID_Awaken(awakenLv));
        newSkin.AddSkin(baseCostume);
        var baseWeapon = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetWeaponSpineID_Awaken(awakenLv));
        newSkin.AddSkin(baseWeapon);
        var baseWeaponSub = unitAnim.Skeleton.Data.FindSkin(COMMON.Instance.GetSubWeaponSpineID_Awaken(awakenLv));
        newSkin.AddSkin(baseWeaponSub);

        unitAnim.Skeleton.SetSkin(newSkin);
        unitAnim.Skeleton.SetSlotsToSetupPose();
    }

    /// <summary>
    /// 스킬 사용 시 mp가 닳으면 콜백될 함수
    /// </summary>
    /// <param name="mp"></param>
    protected override void GetMPCallBack(BigInteger mp)
    {
        mpBar.value = (float)mp / (float)fullMp;

        //var partyIndex = (float)PlayerManager.Instance.PVP_Players.IndexOf(this as PVP_Player);
        //GameEventSubject.SendGameEvent(GameEventType.ITEM_CHARACTER_MP_EVENT, partyIndex, mpBar.value);
    }

    public override async UniTask UseSkill(int equipIndex)
    {
        if (buffMgr.activeBuffEffectList.ContainsKey(1000001))
        {
            Debug.Log($"{this} : 침묵이라 스킬 못 씀");
            return;
        }

        //await base.UseSkill(equipIndex);
        var coolTimeDict = SkillManager.Instance.GetPVP_PlayerSkillCoolTimeDict(this);

        //kami 임시 테스트
        //if (monster.playerClass == ePlayerClass.Healer || monster.playerClass == ePlayerClass.Archer ||
        //    monster.playerClass == ePlayerClass.Mage || monster.playerClass == ePlayerClass.Warrior)
        //    return;

        if (coolTimeDict == null || !coolTimeDict.ContainsKey(equipedSkillIdx[equipIndex]))
            return;

        var skillData = DATA.Skill.SkillMap[equipedSkillIdx[equipIndex]];

        ////현재 가진 mp가 스킬 요구치보다 낮으면 사용 못 함
        //if (mp.Value < skillData.NeedMana)
        //{
        //    Debug.LogError("마나가 업슴");
        //    return;
        //}

        if (equipedSkillIdx[equipIndex] == 0) return;
        SkillManager.Instance.SkillTargetCheck(equipedSkillIdx[equipIndex], this, out var targetList);

        var usingIndex = equipedSkillIdx[equipIndex];
        var skillInfo = DATA.Skill.SkillMap[usingIndex];

        if (targetList.Count > 0)
        {
            var nearDistance = targetList.Min(x => (x.rigid.position - rigid.position).magnitude);
            //거리가 아직 멀면 사용 못함
            if (nearDistance > skillData.Distance)
                return;
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

        //쿨타임 초기화
        var skillCoolTimeDict = SkillManager.Instance.GetPVP_PlayerSkillCoolTimeDict(this);
        skillTerm = Define.GLOBAL_COOL_TIME;
        var coolTime = skillInfo.CoolTime - (skillInfo.CoolTime * data.Final_Skill_CoolTime_Down);
        skillCoolTimeDict[usingIndex] = coolTime;

        //시전시간 초기화
        var partyIndex = PlayerManager.Instance.PVP_Players.IndexOf(this);
        SkillManager.Instance.pvpSkillCastTimeDict[partyIndex] = DATA.Skill.SkillMap[usingIndex].CastingTime;

        //unitAnim.AnimationState.SetAnimation(0, DATA.Skill.SkillMap[usingIndex].PlayAniName, false);

        //스킬 이펙트
        OnSkillEffect(skillInfo, this, targetList);

        //여기에 조명을 건드려 보자
        //BattleManager.Instance.SetSkillUseLight();
        #region 주석 처리 (스킬 사용시 화면 멈추는 연출 제외)
        /////////////////////////////////////////////////////////////////
        //if (PlayerManager.Instance.currentCharacter == this)
        //{
        //    ViewManager.Instance.SkillImageStart(playerClass);
        //    //잠깐 멈추기

        //    if (DBManager.Instance.playerData._UserData.settingInfo.bSkillEffectCk || GameManager.Instance.SaveModeEnableCk)
        //    {
        //        CameraManager.Instance.ChangeCamera(2, transform);

        //        await UniTask.Delay(System.TimeSpan.FromMilliseconds(250), DelayType.Realtime);

        //        if (ViewManager.Instance.curViewUID == (int)Define.eVIEW.MainView)
        //            Time.timeScale = 0.25f;

        //        await UniTask.Delay(System.TimeSpan.FromMilliseconds(500), DelayType.Realtime);

        //        Time.timeScale = 1f;
        //        CameraManager.Instance.ChangeCamera(1);
        //        //카메라 빠지는 시점까지 기다리기 위한 딜레이
        //        await UniTask.Delay(System.TimeSpan.FromMilliseconds(250), DelayType.Realtime);
        //    }
        //}
        /////////////////////////////////////////////////////////////////
        #endregion
    }

    public override async UniTask IdleAsync()
    {
        await IdleAsyncCall();

        await UniTask.WaitUntil(() => PlayerManager.Instance.players.Count(x => x.state.Value != State.DEAD) > 0, cancellationToken: cts.Token);

        FindEnemy();
    }

    public override async UniTask DeadAsync()
    {
        await DeadAsyncCall();


        var targetListContainThis = PlayerManager.Instance.players.Where(x => x.targetEnemy == this).ToList();
        for (var i = 0; i < targetListContainThis.Count; i++)
            targetListContainThis[i].targetEnemy = null;

        tombAnim.gameObject.SetActive(true);
        tombAnim.AnimationState.SetAnimation(0, "Fall", false);
        await UniTask.Delay(1000, cancellationToken: cts.Token);

    }

    public override void FindEnemy()
    {
        //안 죽은 적들을 가까이 있는 순서로 가져옴 (O²)
        var targetList = PlayerManager.Instance.players.Where(x => x.state.Value != State.DEAD && x.unitAnim.gameObject.activeSelf).OrderBy(x => (rigid.position - x.rigid.position).sqrMagnitude).ToList();

        //더 이상 잔존하는 적이 없으면 IDLE
        if (targetList.Count == 0)
        {
            ChangeState(State.IDLE);
            return;
        }

        var players = PlayerManager.Instance.GetLivePVP_Player();
        if (players.Count == 0) return;

        var trueAll = players.TrueForAll(x => x.targetEnemy == null);

        targetEnemy = targetList[0];

        ChangeState(State.MOVE);
    }
}

using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PVP_Healer : PVP_Player
{
    public override void StatInit()
    {
        base.StatInit();
        attackType = AttackType.Magic;
        projectileName = "Healer_Orb";
        playerClass = ePlayerClass.Healer;

        //임시 kami
        unitType = UnitType.Enemy;
    }

    protected override void StartAttackAnimation()
    {
        base.StartAttackAnimation();

        ////kami 테스트
        //var sss = "Skill_2";
        //unitAnim.AnimationState.SetAnimation(0, sss, false);

        unitAnim.AnimationState.SetAnimation(0, "Attack_1", false);
    }

    protected override void AttackEventCallBack(bool isHeavyAtk)
    {
        base.AttackEventCallBack(isHeavyAtk);

        var effect = EffectManager.Instance.GetEffect("Healer_NormalAtk", transform);
        effect.transform.position = muzzleFollower.transform.position;
        effect.Play();
    }

    public override void OnSkillEffect(DATA.Skill skillInfo, Unit useUnit, List<Unit> targetList)
    {
        base.OnSkillEffect(skillInfo, useUnit, targetList);

        var particle = EffectManager.Instance.GetEffect("HealerEffect", transform);
        particle.transform.localPosition = Vector3.zero;
        particle.Play();
    }

    public override async UniTask DeadAsync()
    {
        if (BattleManager.Instance.battle is PVPBattle battle)
        {
            battle.mainview.JewelLightOut(this, 3);
        }
        await base.DeadAsync();
    }
}

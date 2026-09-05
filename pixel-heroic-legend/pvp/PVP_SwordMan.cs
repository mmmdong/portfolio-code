using Cysharp.Threading.Tasks;
using DG.Tweening;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using UnityEngine;

public class PVP_SwordMan : PVP_Player
{
    public override void StatInit()
    {
        base.StatInit();
        attackType = AttackType.Melee;
        playerClass = ePlayerClass.SwordMan;

        //임시 kami
        unitType = UnitType.Enemy;
    }

    protected override void StartAttackAnimation()
    {
        base.StartAttackAnimation();

        //50% 확률로 강공격
        var rnd = Random.Range(0.0001f, 1f);
        if (rnd <= data.Final_SAtk_p)
        {
            var particle = EffectManager.Instance.GetEffect("SwordMan_StrongAtk", transform);
            ParticlePlay(particle);
            unitAnim.AnimationState.SetAnimation(0, "Attack_2", false);
        }
        else
        {
            var particle = EffectManager.Instance.GetEffect("SwordMan_NormalAtk", transform);
            ParticlePlay(particle);
            unitAnim.AnimationState.SetAnimation(0, "Attack_1", false);
        }
    }

    public override void OnSkillEffect(DATA.Skill skillInfo, Unit useUnit, List<Unit> targetList)
    {
        base.OnSkillEffect(skillInfo, useUnit, targetList);

        var particle = EffectManager.Instance.GetEffect("SwordManEffect", transform);
        particle.transform.localPosition = Vector3.zero;
        particle.Play();
    }

    private void ParticlePlay(Effect particle)
    {
        particle.transform.localPosition = Vector3.zero;
        particle.transform.localEulerAngles = unitAnim.transform.localScale.x > 0 ? Vector3.up * 180f : Vector3.zero;
        particle.Play();
    }

    public override async UniTask DeadAsync()
    {
        if (BattleManager.Instance.battle is PVPBattle battle)
        {
            battle.mainview.JewelLightOut(this, 0);
        }
        await base.DeadAsync();
    }
}

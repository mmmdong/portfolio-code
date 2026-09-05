using Cysharp.Threading.Tasks;
using DG.Tweening;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using UnityEngine;

public class SwordMan : Player
{
    private List<Unit> targetList = new List<Unit>();

    public override void StatInit()
    {
        base.StatInit();
        attackType = AttackType.Melee;
        playerClass = ePlayerClass.SwordMan;

        //임시 kami
        unitType = UnitType.Player;

    }

    protected override void AttackEffect(bool isHeavyAttack)
    {
        base.AttackEffect(isHeavyAttack);
        if (isHeavyAttack)
        {
            var particle = EffectManager.Instance.GetEffect("SwordMan_StrongAtk");
            ParticlePlay(particle);
        }
        else
        {
            var particle = EffectManager.Instance.GetEffect("SwordMan_NormalAtk");
            ParticlePlay(particle);
        }
    }

    protected override void AttackEventCallBack(bool isHeavyAtk)
    {
        //COMMON.Instance.SpecialBuffCheck(this, targetEnemy, Define.BUFFACTIONTYPE.DO_ATTACK, (decimal)data.Final_ATK);

        base.AttackEventCallBack(isHeavyAtk);

        if (isHeavyAtk)
            SoundManager.Instance.GetSFX_Play(Define.eSFX.SFX_SwordMan_Swing_Strong);
        else
            SoundManager.Instance.GetSFX_Play(Define.eSFX.SFX_SwordMan_Swing_Normal);

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
        particle.transform.position = rigid.position;
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

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using Cysharp.Threading.Tasks;

public class Archer : Player
{
    public override void StatInit()
    {
        base.StatInit();
        attackType = AttackType.Ranged;
        projectileName = "Arrow";
        playerClass = ePlayerClass.Archer;

        //임시 kami
        unitType = UnitType.Player;
 
        // SetVisible(TutorialManager.Instance.IsUnlockContents(Define.CharacterUnlockIDs[(int)playerClass - 1]));
    }

    /*protected override void AttackEffect(bool isHeavyAttack)
    {
        base.AttackEffect(isHeavyAttack);
        if (isHeavyAttack)
        {

        }
        else
        {

        }
    }*/

    protected override void AttackEventCallBack(bool isHeavyAtk)
    {
        base.AttackEventCallBack(isHeavyAtk);

        var effectName = "Archer_NormalAtk";
        var effect = EffectManager.Instance.GetEffect(effectName, transform);
        effect.transform.position = muzzleFollower.transform.position;
        effect.Play();
        SoundManager.Instance.GetSFX_Play(Define.eSFX.SFX_Archer_Shoot);
    }

    public override void OnSkillEffect(DATA.Skill skillInfo, Unit useUnit, List<Unit> targetList)
    {
        base.OnSkillEffect(skillInfo, useUnit, targetList);

        if (!gameObject.activeSelf)
            return;

        var particle = EffectManager.Instance.GetEffect("ArcherEffect", transform);
        particle.transform.localPosition = Vector3.zero;
        particle.Play();
    }

    public override async UniTask DeadAsync()
    {
        if (BattleManager.Instance.battle is PVPBattle battle)
        {
            battle.mainview.JewelLightOut(this, 1);
        }
        await base.DeadAsync();
    }
}

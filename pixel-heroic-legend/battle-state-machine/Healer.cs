using Cysharp.Threading.Tasks;
using DG.Tweening;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Healer : Player
{
    public override void StatInit()
    {
        base.StatInit();
        attackType = AttackType.Magic;
        projectileName = "Healer_Orb";
        playerClass = ePlayerClass.Healer;

        //임시 kami
        unitType = UnitType.Player;
        
        // SetVisible(TutorialManager.Instance.IsUnlockContents(Define.CharacterUnlockIDs[(int)playerClass - 1]));
    }


    protected override void AttackEventCallBack(bool isHeavyAtk)
    {
        base.AttackEventCallBack(isHeavyAtk);

        var effect = EffectManager.Instance.GetEffect("Healer_NormalAtk", transform);
        effect.transform.position = muzzleFollower.transform.position;
        effect.Play();
        SoundManager.Instance.GetSFX_Play(Define.eSFX.SFX_Mage_Shoot);
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

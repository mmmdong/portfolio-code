using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using TMPro;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

public class WorldRaidResultPopUp : UI_PopUp
{
    enum UI_TEXT
    {
        ClearDungeonText,
        ResultValueText,
        ResultGradeText,
        OKButtonText,
    }

    enum UI_BUTTON
    {
        OKButton,
    }
    private void Awake()
    {
        Bind<TextMeshProUGUI>(typeof(UI_TEXT));
        Bind<Button>(typeof(UI_BUTTON));
    }

    private Define.PLAYER_TYPE weaponType;

    /// <summary>
    /// Unboxing 미사용
    /// </summary>
    /// <param name="args"></param>
    public override void Setting(params object[] args)
    {
        base.Setting(args);

        GetText((int)UI_TEXT.ClearDungeonText).text = string.Empty;
        GetText((int)UI_TEXT.OKButtonText).text = TextManager.Instance.GetText("OK_TXT");

        weaponType = PlayManager.Instance.player.GetCurType;

        for (int i = 0; i < Enum.GetValues(typeof(UI_BUTTON)).Length; i++)
            GetButton(i).onClick.RemoveAllListeners();

        GetButton((int)UI_BUTTON.OKButton).onClick.AddListener(() => { OnOkButton(); });


        GetText((int)UI_TEXT.ResultValueText).gameObject.SetActive(true);
        GetText((int)UI_TEXT.ResultValueText).gameObject.SetActive(true);

        switch (weaponType)
        {
            case Define.PLAYER_TYPE.DAGGER:
                DBManager.Instance._StageData.WorldRaidScoreDagger += DungeonManager.Instance.dungeonScore;
                GetText((int)UI_TEXT.ResultGradeText).text = $"[{TextManager.Instance.GetText("WORLDRAID_DAGGER_CUMULATIVESCORE_TXT")}] {DBManager.Instance._StageData.WorldRaidScoreDagger}";
                break;
            case Define.PLAYER_TYPE.SWORD:
                DBManager.Instance._StageData.WorldRaidScoreSword += DungeonManager.Instance.dungeonScore;
                GetText((int)UI_TEXT.ResultGradeText).text = $"[{TextManager.Instance.GetText("WORLDRAID_SWORD_CUMULATIVESCORE_TXT")}] {DBManager.Instance._StageData.WorldRaidScoreSword}";
                break;
            case Define.PLAYER_TYPE.BOW:
                DBManager.Instance._StageData.WorldRaidScoreBow += DungeonManager.Instance.dungeonScore;
                GetText((int)UI_TEXT.ResultGradeText).text = $"[{TextManager.Instance.GetText("WORLDRAID_BOW_CUMULATIVESCORE_TXT")}] {DBManager.Instance._StageData.WorldRaidScoreBow}";
                break;
        }

        //최종 데미지 계산에는 받은 데미지까지 함께 계산해준다.
        DBManager.Instance._StageData.WorldRaidScoreTotal += DungeonManager.Instance.dungeonScore + DungeonManager.Instance.getDamageScore;

        var damage = DungeonManager.Instance.dungeonScore;
        SendDamage(DungeonManager.Instance.dungeonScore).Forget();

        GetText((int)UI_TEXT.ResultValueText).text = $"{DungeonManager.Instance.dungeonScore}";

        PlayFabManager.Instance.SetWorldRaidRankingData(weaponType).Forget();

        DungeonManager.Instance.dungeonScore = 0;
        DungeonManager.Instance.getDamageScore = 0;

        if (MapManager.Instance.GetComponentInChildren<Map_WorldRaidDungeon>().isFreeTicketUse)
            DBManager.Instance._StageData.WorldRaidChallengeCnt--;
        else
            DBManager.Instance._ConsumableItemData.ConsumItemList[8099999]--;

        PlayFabManager.Instance.DataSave(true);
    }


    private void OnOkButton()
    {
        ViewManager.Instance.OnPopUp(POPUP.LoadingPopup, null, true);

        PlayManager.Instance.TimeCheck(true, Define.STAGE_TIME);
        PlayManager.Instance.stageType = eStageType.eStage;
        gameObject.SetActive(false);
        Invoke("StageChageSend", 1.0f);
    }

    public void StageChageSend()
    {
        ViewManager.Instance.mainView.ScoreSetting(0);
        PlayManager.Instance.player.StageChange();
    }

    /// <summary>
    /// PlayFab으로 내가 입힌 누적 대미지 전송
    /// </summary>
    /// <param name="damage">내가 입힌 총 대미지</param>
    private async UniTask SendDamage(BigInteger damage)
    {
        var request = new PlayFab.ClientModels.ExecuteCloudScriptRequest
        {
            FunctionName = "WorldRaidBossHpCal", // 호출하려는 Cloud Script 함수의 이름
            FunctionParameter = new
            {
                dmg = $"{damage}",
            }
        };

        var sucCode = 0;
        // Cloud Script 함수를 호출합니다.
        PlayFab.PlayFabClientAPI.ExecuteCloudScript(request,
            result =>
            {
                result.FunctionResult.ToString();
                var resultJsonData = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(result.FunctionResult.ToString());
                if (resultJsonData.ContainsKey("newHP"))
                {
                    var remaingHp = resultJsonData["newHP"];
                }
                sucCode = 1;
            },
            error =>
            {
                Debug.LogError("에러");
                sucCode = -1;
            });

        await UniTask.WaitUntil(() => sucCode != 0);
    }
}

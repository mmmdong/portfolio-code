using System;
using System.Threading;

using ACTGames.Content.Helper;

using Cysharp.Threading.Tasks;

using GameCore.Utils;

using GameLogic;
using GameLogic.Define;

using UnityEngine;

/// <summary>
/// 드림 벌룬 페스티벌 — 쉬는중 안내 노출 조건·카운터 (구현 명세서 §7-4).
///
/// 노출 조건: 쉬어가기로 쉬는중 진입(state==1) 전제 + 아래 둘 중 하나(OR)
///   [조건1] 진입 후 지정 시간(rest_Popup_Time 분, 기본 1시간) 경과 AND 남은 에너지 임계값(그룹 39, 기본 80) 이상
///   [조건2] 누적 사용 에너지가 임계값(그룹 40, 기본 150) 이상 — 발화 시 0으로 리셋 후 재카운트
/// → 재노출 대기 시간(round_CountRestTime, 기본 30분)마다, 라운드당 최대 round_Count 회(기본 2회).
/// 다음 단계 시작 시 카운터 리셋.
///
/// - 시간 체크는 <see cref="StartAsync"/> 의 UniTask 루프가 담당한다(컨텐츠 Update 폴링 아님).
///   좌석 시뮬(<see cref="DreamBalloonSeatSimulator"/>)과 동일하게 CancellationToken 으로 안전 취소되며,
///   로비/머지 상주 중에도 구동된다.
/// - 수치 주입: 재노출 간격·횟수·아이콘·조건1 최초 노출 게이트(rest_Popup_Time, 분) = Event_DreamBalloon_Setting /
///   에너지 임계값 = MyDreamPartner_Sequence.conditionValue(그룹 39 item=80 보유). 미발행 시 기본값 폴백.
/// - 상태 저장 키(restEnterTime / restNotifyCount / restNotifyLastTime / restUsedEnergy)는 재접속 보존을 위해 로컬 저장한다.
/// - 통지는 <see cref="RestNotifyMsg"/>(팝업은 마이드림파트너 안내창 활용, 신규 UI 아님).
/// - 발화는 <b>머지판 개방 중에만</b> 이뤄진다 — 안내창(파트너 큐)이 머지판 전용이라 그 외 화면 발화는
///   조용히 버려지므로, 머지판 밖/그룹 해석 실패 시엔 카운트·재노출 잠금을 소진하지 않고 다음 주기에 재시도한다.
/// </summary>
public class DreamBalloonRestNotifier
{
    private const string KEY_ENTER_TIME_PREFIX = "DreamBalloonRestEnter_";
    private const string KEY_NOTIFY_COUNT_PREFIX = "DreamBalloonRestCount_";
    private const string KEY_NOTIFY_LAST_PREFIX = "DreamBalloonRestLast_";
    private const string KEY_USED_ENERGY_PREFIX = "DreamBalloonRestUsedEnergy_";

    private const float CHECK_INTERVAL_SEC = 10f;   // 조건 체크 주기(UniTask 루프)
    private const int SEC_PER_MINUTE = 60;

    private long eventSeq;
    private CancellationTokenSource checkCts;

    public bool IsRunning => null != checkCts;

    public void Bind(long eventSeq)
    {
        this.eventSeq = eventSeq;
    }

    /// <summary>조건 체크 루프 시작(컨텐츠 Initialize). 이미 구동 중이면 재시작한다.</summary>
    public void StartCheck()
    {
        Stop();

        checkCts = new CancellationTokenSource();
        RunCheckAsync(checkCts.Token).Forget();
    }

    /// <summary>루프 정지(컨텐츠 Release). 데이터 조작 없음 — 순수 취소.</summary>
    public void Stop()
    {
        if (null == checkCts)
        {
            return;
        }

        checkCts.Cancel();
        checkCts.Dispose();
        checkCts = null;
    }

    /// <summary>쉬어가기로 쉬는중 진입 시 호출 — 진입 시각 기록, 카운터 초기화.</summary>
    public void OnEnterRest()
    {
        long serverNow = DataManager.Instance.GetCurrentIntTimeStamp();
        PlayerPrefs.SetString(EnterTimeKey(), serverNow.ToString());
        PlayerPrefs.SetInt(NotifyCountKey(), 0);
        PlayerPrefs.SetString(NotifyLastKey(), "0");
    }

    /// <summary>다음 단계 시작 시 호출 — 카운터 리셋(§7-4).</summary>
    public void OnNextRoundStart()
    {
        PlayerPrefs.DeleteKey(EnterTimeKey());
        PlayerPrefs.SetInt(NotifyCountKey(), 0);
        PlayerPrefs.SetString(NotifyLastKey(), "0");
    }

    /// <summary>이벤트 종료 시 호출 — 누적 사용량 포함 저장 키 전량 정리(§7-4 저장 계층).</summary>
    public void ClearSavedData()
    {
        PlayerPrefs.DeleteKey(EnterTimeKey());
        PlayerPrefs.DeleteKey(NotifyCountKey());
        PlayerPrefs.DeleteKey(NotifyLastKey());
        PlayerPrefs.DeleteKey(UsedEnergyKey());
    }

    // 조건 체크 루프 — CHECK_INTERVAL_SEC 마다 노출 조건을 평가한다.
    private async UniTaskVoid RunCheckAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(CHECK_INTERVAL_SEC), DelayType.Realtime, PlayerLoopTiming.Update, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                TryNotify();
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 취소(컨텐츠 해제/이벤트 종료) — 데이터 조작 없음.
        }
    }

    // 노출 조건 평가 → 충족 시 RestNotifyMsg 발신.
    private void TryNotify()
    {
        if (!EventDreamBalloonHelper.IsResting())
        {
            return;
        }

        int notifyCount = PlayerPrefs.GetInt(NotifyCountKey(), 0);
        if (notifyCount >= EventDreamBalloonHelper.GetRestNotifyMaxCount())
        {
            return;
        }

        // 안내창은 머지판 전용(파트너 큐가 머지판 개방 중에만 활성, 비활성 Enqueue 는 no-op) —
        // 머지판 밖 발화는 소진 없이 스킵하고 재진입 후 다음 주기(10초)에 재시도한다.
        if (!MyDreamPartnerMergeMotionQueue.IsActive)
        {
            return;
        }

        // 조건1 OR 조건2 — 둘 중 하나라도 만족하면 노출(§7-4).
        bool condition2Met = IsCondition2Met();
        if (!IsCondition1Met(notifyCount) && !condition2Met)
        {
            return;
        }

        // 시퀀스 그룹 해석 실패(테이블 일시 미조회 등) 시 소진 없이 스킵 — 다음 주기 재시도.
        // 실측: 발화 순간에만 0 이 나와 토스트 폴백 + 카운트/재노출 잠금이 헛소진되는 사례가 있었다.
        int sequenceGroup = EventDreamBalloonHelper.GetRestSequenceGroup(condition2Met);
        if (sequenceGroup <= 0)
        {
            DLogger.Log($"[DreamBalloon] 쉬는중 안내 그룹 해석 실패 — 소진 없이 재시도. {EventDreamBalloonHelper.GetRestGroupDiagnostics()}");
            return;
        }

        // 조건2로 발화한 경우 누적 사용량을 0으로 리셋 후 재카운트(§2-7 리셋 타이밍).
        if (condition2Met)
        {
            ResetUsedEnergy();
        }

        long serverNow = DataManager.Instance.GetCurrentIntTimeStamp();
        PlayerPrefs.SetInt(NotifyCountKey(), notifyCount + 1);
        PlayerPrefs.SetString(NotifyLastKey(), serverNow.ToString());

        // 발화한 조건에 대응하는 시퀀스 그룹을 실어 보낸다(§2-7) — 조건2(누적 사용) = 40 / 조건1(남은 에너지) = 39.
        Message.Send(new RestNotifyMsg { sequenceGroup = sequenceGroup });
    }

    // 조건1 — 쉬는중 진입 후 1시간 경과(재노출은 round_CountRestTime 간격) AND 남은 에너지 임계값 이상.
    private bool IsCondition1Met(int notifyCount)
    {
        if (DataManager.Instance.GetEnergy() < EventDreamBalloonHelper.GetRestRequiredEnergy())
        {
            return false;
        }

        long enterTime = ParseLong(PlayerPrefs.GetString(EnterTimeKey(), "0"));
        if (enterTime <= 0)
        {
            return false;
        }

        long serverNow = DataManager.Instance.GetCurrentIntTimeStamp();
        long lastNotifyTime = ParseLong(PlayerPrefs.GetString(NotifyLastKey(), "0"));

        long repeatGapSec = EventDreamBalloonHelper.GetRestNotifyGapMinutes() * SEC_PER_MINUTE;
        long referenceTime = (notifyCount == 0) ? enterTime : lastNotifyTime;
        long requiredGap = (notifyCount == 0) ? EventDreamBalloonHelper.GetRestFirstNotifyDelayMinutes() * SEC_PER_MINUTE : repeatGapSec;

        return serverNow - referenceTime >= requiredGap;
    }

    #region 조건2 (valuecheck) — 누적 사용 에너지
    // 기획 954663047 「누적 사용량 확인 기능 추가」 역할 분담:
    //   마이드림파트너 = 전달받은 값과 conditionValue 비교만 / 이벤트(드림벌룬) = 재화 지정·집계·저장·리셋.
    // 따라서 집계·저장은 본 Helper 가 전담하고, MyDreamPartner_Sequence 행 처리 코드(MyDreamPartnerHelper.MatchesCondition)는 건드리지 않는다.
    // 저장은 디바이스 로컬(PlayerPrefs) — 이벤트 기간 중 유지(재접속·점검 포함), 기준 도달 시·이벤트 종료 시 초기화.

    /// <summary>
    /// 에너지 소모 시 호출 — 누적 사용량 가산.
    /// 발신: <c>ContentEventDreamBalloon.OnGameCondition</c> ← `ConditionDispatcher`(GeneratorEnergyUse, 생성기 탭 단일 지점).
    /// </summary>
    public void AddUsedEnergy(int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        PlayerPrefs.SetInt(UsedEnergyKey(), GetUsedEnergy() + amount);
    }

    /// <summary>누적 사용 에너지(이벤트 기간 유지).</summary>
    public int GetUsedEnergy()
    {
        return PlayerPrefs.GetInt(UsedEnergyKey(), 0);
    }

    /// <summary>기준 도달 시 0으로 리셋 후 재카운트(§2-7).</summary>
    public void ResetUsedEnergy()
    {
        PlayerPrefs.SetInt(UsedEnergyKey(), 0);
    }

    /// <summary>
    /// 조건2 — 누적 사용 에너지가 임계값(그룹 40 conditionValue, 기본 150) 이상인지.
    /// MyDreamPartner 쪽 valuecheck 판정(enum 부재)에 의존하지 않고 본 Helper 가 직접 비교한다.
    /// </summary>
    public bool IsCondition2Met()
    {
        return GetUsedEnergy() >= EventDreamBalloonHelper.GetRestUsedEnergyThreshold();
    }
    #endregion

    private static long ParseLong(string value)
    {
        return long.TryParse(value, out long result) ? result : 0;
    }

    private string EnterTimeKey()
    {
        return $"{KEY_ENTER_TIME_PREFIX}{eventSeq}";
    }

    private string NotifyCountKey()
    {
        return $"{KEY_NOTIFY_COUNT_PREFIX}{eventSeq}";
    }

    private string NotifyLastKey()
    {
        return $"{KEY_NOTIFY_LAST_PREFIX}{eventSeq}";
    }

    private string UsedEnergyKey()
    {
        return $"{KEY_USED_ENERGY_PREFIX}{eventSeq}";
    }
}

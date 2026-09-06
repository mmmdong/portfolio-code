using System;
using System.Threading;

using Cysharp.Threading.Tasks;

using GameCore.Utils;

using GameLogic;
using GameLogic.Define;

using UnityEngine;

/// <summary>
/// 드림 벌룬 페스티벌 — AI 좌석 감소(decay) 경쟁 시뮬레이터 (구현 명세서 §7-3).
///
/// - 좌석 수를 **서버 roundStartAt 이후 경과 시간의 결정론적 함수**로 관리한다. Event_DreamBalloon_AiRound 의
///   decayMinSec~decayMaxSec 간격 수열을 라운드별 시드(eventSeq·round)로 재현하므로, 언제 계산해도(재접속·백그라운드
///   복귀 포함) 같은 시점엔 같은 좌석 수가 나온다 → 앱을 닫아도 좌석은 계속 줄고, 재접속 시 위상 어긋남이 없다.
/// - UniTask 루프는 다음 감소 경계까지 대기하다 <see cref="SeatChangedMsg"/> 로 UI 에 통지만 한다(좌석 수의 소스는 시각 함수).
/// - 성공/실패 "판정"은 이 Helper 가 보유한다. 목표 코인 달성 시 <see cref="OnGoalReached"/> 로 성공 확정 →
///   이후 좌석 감소 중단(감소 정지). 좌석 0 도달 = 실패.
/// - 순위 = slotMax - 잔여좌석 + 1 (좌석 점유 순서), 모집단·상위% 분모 = 라운드별 slotMax.
/// - 연출은 순수 view 이므로, 이 시뮬은 데이터만 산출한다.
/// </summary>
public class DreamBalloonSeatSimulator
{
    public enum RoundJudge
    {
        None,       // 미시작
        Running,    // 진행중
        Success,    // 목표 달성(성공 확정)
        Fail,       // 좌석 0(실패 확정)
    }

    private const string SEAT_SAVE_KEY_PREFIX = "DreamBalloonSeat_";

    private long eventSeq;
    private int curRound;
    private int slotMax;
    private int remainSeat;
    private int decayMinSec;
    private int decayMaxSec;
    private long roundStartAt;
    private RoundJudge judge = RoundJudge.None;
    private CancellationTokenSource decayCts;
    private System.Random decayRng;   // 라운드별 시드로 감소 간격 수열을 재현(결정론적 replay)

    public int CurRound => curRound;
    public int SlotMax => slotMax;
    public int RemainSeat => remainSeat;
    public RoundJudge Judge => judge;
    public bool IsRunning => judge == RoundJudge.Running;
    public bool IsSuccess => judge == RoundJudge.Success;
    public bool IsFail => judge == RoundJudge.Fail;

    // 통과 순위: slotMax - 잔여좌석 + 1 (좌석 점유 순서, §7-3). 모집단 = 라운드별 slotMax.
    public int Rank => slotMax <= 0 ? 1 : Mathf.Clamp(slotMax - remainSeat + 1, 1, slotMax);

    // 상위 % (성공 팝업 트로피 §3-6): 순위 / slotMax(라운드별 좌석) × 100 올림, 1 ~ 100 클램프.
    public int UpperPercent => GetUpperPercent(Rank);

    /// <summary>
    /// 지정 등수의 상위 % — 공식을 여기 하나로 두고 <see cref="UpperPercent"/> 도 이걸 쓴다.
    /// [ISSUE-06] 영속화해 둔 **확정 순간의 등수**로 재산출해야 하는 호출부가 있어 등수를 인자로 받는 형태가 필요하다
    /// (현재 좌석에서 다시 뽑으면 경과 시간만큼 악화된 값이 나온다).
    /// </summary>
    public int GetUpperPercent(int rank)
    {
        return slotMax <= 0 ? 100 : Mathf.Clamp(Mathf.CeilToInt(rank * 100f / slotMax), 1, 100);
    }

    /// <summary>
    /// 라운드 시작/재개. roundStartAt = 서버 EventBalloonInfo.roundStartAt(Unix, §8-1).
    /// 잔여 좌석은 roundStartAt 이후 경과 시간으로 결정론적으로 산출한다(로컬 저장 불필요 — 어느 시점에 계산해도 동일).
    /// </summary>
    /// <summary>동일 라운드(같은 eventSeq·round·roundStartAt)를 이미 구동 중인가 — 팝업 재진입 시 불필요한 재시작 방지용.</summary>
    public bool IsRunningRound(long eventSeq, int round, long roundStartAt)
    {
        return judge == RoundJudge.Running
            && this.eventSeq == eventSeq && this.curRound == round && this.roundStartAt == roundStartAt;
    }

    /// <summary>
    /// [ISSUE-16] 동일 라운드를 **구동 중이거나 이미 성공 확정**해 붙들고 있는가 — 시뮬 재시작 판정용.
    ///
    /// <see cref="IsRunningRound"/> 는 <c>Running</c> 만 참이라, 머지판에서 목표를 채워 <c>Success</c> 로 동결한 뒤
    /// 팝업에 진입하면 거짓이 되어 <see cref="StartRound"/> 가 재호출되고 **동결해 둔 성공이 좌석 재계산으로 파괴**됐다
    /// (`judge` 가 Running/Fail 로 덮여, 커밋 판정의 1차 방어선이 도달 불가능한 죽은 조건이 됐다).
    /// 성공까지 "붙들고 있는 상태"로 보아 재시작을 막는다 — 순위도 성공 확정 순간 값으로 고정된다(§7-3).
    /// 라운드 동일성(eventSeq·round·roundStartAt)을 함께 보므로 새 라운드는 종전대로 항상 재시작한다.
    /// </summary>
    /// 실패(<c>Fail</c>)도 함께 붙든다 — **확정된 판정을 좌석 재계산으로 파괴하지 않는다**는 Success 와 같은 이유다.
    /// 실제 실패(시간 경과로 좌석 0)는 재계산해도 다시 Fail 이라 무해하지만, 치트 강제 실패(F9)는 메모리 상태뿐이라
    /// 재계산 시 좌석이 되살아나 **강제 실패가 통째로 소멸**한다(머지판 실패 테스트 불가). 확정 후에는 붙드는 편이 일관된다.
    public bool IsHoldingRound(long eventSeq, int round, long roundStartAt)
    {
        return judge != RoundJudge.None
            && this.eventSeq == eventSeq && this.curRound == round && this.roundStartAt == roundStartAt;
    }

    public void StartRound(long eventSeq, int round, int slotMax, int decayMinSec, int decayMaxSec, long roundStartAt)
    {
        // 팝업을 여닫아도 같은 라운드면 진행 중인 감소 루프를 유지한다(재시작 churn·resolvedRound 리셋 방지).
        // 좌석 수는 경과 시간 함수라 값 자체는 재계산해도 동일하지만, 루프/상태를 흔들지 않는 편이 안전하다.
        // [ISSUE-16] 성공 확정(Success)까지 포함해 붙든다 — Running 만 보면 동결해 둔 클리어가 아래 재계산으로 파괴된다.
        if (IsHoldingRound(eventSeq, round, roundStartAt))
        {
            return;
        }

        Stop();

        this.eventSeq = eventSeq;
        this.curRound = round;
        this.slotMax = Mathf.Max(0, slotMax);
        this.decayMinSec = Mathf.Max(1, decayMinSec);
        this.decayMaxSec = Mathf.Max(this.decayMinSec, decayMaxSec);
        this.roundStartAt = roundStartAt;

        // 경과 시간까지 진행된 감소 횟수를 결정론적으로 재현하고, 다음 감소까지 남은 대기(nextWaitSec)를 얻는다.
        int decayed = SimulateElapsed(GetElapsedSec(), out double nextWaitSec);
        remainSeat = Mathf.Clamp(this.slotMax - decayed, 0, this.slotMax);

        if (remainSeat <= 0)
        {
            judge = RoundJudge.Fail;
            NotifySeatChanged();
            return;
        }

        judge = RoundJudge.Running;
        NotifySeatChanged();

        decayCts = new CancellationTokenSource();
        RunDecayAsync(nextWaitSec, decayCts.Token).Forget();
    }

    /// <summary>목표 코인(미션) 달성 → 성공 확정. 이후 좌석 감소 중단(§7-3 감소 정지).</summary>
    public void OnGoalReached()
    {
        if (judge != RoundJudge.Running)
        {
            return;
        }

        judge = RoundJudge.Success;
        CancelDecay();   // 감소 중단 → 성공 확정 순간의 잔여 좌석으로 순위 고정(§7-3)
    }

    /// <summary>시뮬 정지(화면 종료/스킵/재시작). 데이터 조작 없음 — 순수 취소.</summary>
    public void Stop()
    {
        CancelDecay();
    }

    // 라운드 시작 이후 경과 초(서버 시각 기준). roundStartAt 미설정/미래면 0.
    private long GetElapsedSec()
    {
        long serverNow = DataManager.Instance.GetCurrentIntTimeStamp();
        return roundStartAt <= 0 || serverNow <= roundStartAt ? 0 : serverNow - roundStartAt;
    }

    /// <summary>
    /// 경과 초(elapsedSec)까지 이미 발생한 좌석 감소 횟수를 결정론적으로 재현한다.
    /// 라운드별 시드로 감소 간격 수열을 재생성하므로, 재접속·복귀로 언제 호출해도 같은 경과에 같은 횟수가 나온다.
    /// 반환 후 <see cref="decayRng"/> 는 "다음 감소" 직전에 위치하며, out 으로 그 감소까지의 남은 대기 초를 돌려준다.
    /// </summary>
    private int SimulateElapsed(long elapsedSec, out double nextWaitSec)
    {
        decayRng = new System.Random(ComputeSeed());

        long cumulative = 0;
        int decays = 0;
        while (decays < slotMax)
        {
            int interval = decayRng.Next(decayMinSec, decayMaxSec + 1);   // [min, max] 폐구간(상한 배타 → +1)
            if (cumulative + interval > elapsedSec)
            {
                nextWaitSec = (cumulative + interval) - elapsedSec;   // 현재 간격의 남은 부분만 대기(위상 정합)
                return decays;
            }

            cumulative += interval;
            decays++;
        }

        nextWaitSec = 0d;   // 모든 좌석 소진
        return decays;
    }

    // 라운드별 안정 시드 — eventSeq·round 로 결정(재접속 시 동일 수열 재현). 클라 시뮬이라 크로스플랫폼 일치 불요.
    private int ComputeSeed()
    {
        return unchecked((int)(eventSeq * 486187739L) + curRound);
    }

    private async UniTaskVoid RunDecayAsync(double firstWaitSec, CancellationToken token)
    {
        try
        {
            double waitSec = firstWaitSec;
            while (remainSeat > 0 && judge == RoundJudge.Running)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(waitSec), DelayType.Realtime, PlayerLoopTiming.Update, token);

                if (token.IsCancellationRequested || judge != RoundJudge.Running)
                {
                    return;
                }

                remainSeat = Mathf.Max(0, remainSeat - 1);

                if (remainSeat <= 0)
                {
                    // 좌석 0 = 실패 **확정 후 통지**(§7-3) — Message.Send 는 동기 호출이라, 통지를 먼저 하면
                    // 수신부(OnSeatChanged)가 IsFail == false 인 상태를 보고 실패 확정을 건너뛴다(재진입 전까지 연출 미재생).
                    // StartRound 의 재접속 소진 경로와 동일한 순서(확정 → NotifySeatChanged)로 맞춘다.
                    judge = RoundJudge.Fail;
                    NotifySeatChanged();
                    return;
                }

                NotifySeatChanged();
                waitSec = decayRng.Next(decayMinSec, decayMaxSec + 1);   // 다음 간격 = 재현 수열의 다음 값
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 취소 — 데이터 조작 없음(§7-3 스킵/종료 안전).
        }
    }

    private void CancelDecay()
    {
        if (null == decayCts)
        {
            return;
        }

        decayCts.Cancel();
        decayCts.Dispose();
        decayCts = null;
    }

    private void NotifySeatChanged()
    {
        Message.Send(new SeatChangedMsg { round = curRound, remainSeat = remainSeat, slotMax = slotMax });
    }

#if UNITY_EDITOR
    /// <summary>
    /// [치트] 현재 라운드를 **즉시 실패 확정**한다 — 좌석 0 도달과 **동일한 종단 상태**를 만든다.
    ///
    /// 감소 루프의 종단 분기와 같은 순서(감소 중단 → 좌석 0 → 확정 → 통지)를 그대로 따르므로,
    /// 수신부(<c>OnSeatChanged</c>)는 실제 실패와 구분 없이 처리한다 — 머지판이면 전송 보류 + 레드닷,
    /// 메인 팝업이면 즉시 전송 + 실패 연출. 즉 **어디서 눌러도 실제 실패와 같은 경로**를 검증할 수 있다.
    /// (결과만 직접 확정하는 방식은 judge 가 Running 으로 남아 좌석 기반 판정·레드닷이 전부 어긋난다.)
    /// 진행중이 아니면 no-op.
    /// </summary>
    public void Cheat_ForceFail()
    {
        if (judge != RoundJudge.Running)
        {
            return;
        }

        CancelDecay();
        remainSeat = 0;
        judge = RoundJudge.Fail;
        NotifySeatChanged();
    }
#endif

    // 좌석 수는 이제 시각 함수라 로컬 저장이 없다. 구버전이 남긴 저장 키만 정리(하위호환).
    // ⚠️ 시뮬레이터의 현재 필드(eventSeq/curRound)를 쓰면 안 된다 — 쉬는중 복귀 등으로 필드가 이전 라운드/0 이면
    //    엉뚱한 키를 지운다. 지울 대상을 호출부가 명시한다.
    public void ClearSavedSeat(long eventSeq, int round)
    {
        if (eventSeq <= 0)
        {
            return;
        }

        PlayerPrefs.DeleteKey($"{SEAT_SAVE_KEY_PREFIX}{eventSeq}_{round}");
    }
}

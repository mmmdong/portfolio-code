using System;
using System.Collections.Generic;

using ACTGames.Content.Helper;
using ACTGames.System.Data;
using Cysharp.Threading.Tasks;
using GameCore.Utility;
using GameCore.Utils;
using GameLogic;
using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.GameManagement;
using GameLogic.Management;
using GameLogic.Network;
using GameLogic.Network.FakeServer.FsDataHandler;
using GameLogic.Network.FakeServer.FsDataHandler.LiveEvent;

// 미니게임 서브컨텐츠 (2차, 상세 기획 892698729)
// 특정 오브젝트에서 진입하는 미니게임의 라운드 진행/클리어/상점 구매 제한을 다룬다.
// 보상 재화 수량은 KF_Item 지갑이 단일 소스 — 본 클래스는 진행 기록/연출만 다룬다.
//  [실서버 배선 2026-06-13] 라운드 시작/풍선 터뜨리기/클리어는 RQCharacterCafeMiniGameUpdate(patch→snapshot)로 처리한다.
//   [클라 생성 전송 2026-06-14] 라운드 시작 시 풍선 배치(레이아웃)는 클라가 생성(MiniGameModel 초기화)해 서버로 전송하고, 서버는 저장·검증한다.
//    [로컬 재화 2026-06-14] 미니게임 스냅샷 패킷엔 재화 필드가 없어(서버 echo 저장 전용) 비용 소모·히든 보상 획득은 클라가 로컬로 처리한다(ApplyPopEconomyLocal). 클라는 응답 snapshot 을 모델에 투영해 표시한다.
//   이벤트 치트(에디터) 시에는 기존 가짜서버(Fs) 흐름을 유지한다(Fs도 동일 생성 로직 공용 — 본 클래스의 BuildMiniGameBalloonContents).
//   ※ 상점 구매(TryBuyMiniGameShop)는 스냅샷에 표현되지 않는 다이아/광고 구매 흐름이라 본 패킷 범위 밖 — Fs 유지(후속 IAP 연동).
public class CharacterCafeMiniGameSubContent : ICharacterCafeSubContent
{
    // 미니게임 진행/재화 갱신을 열려 있는 미니게임 팝업(UIPopupMiniGameBalloon)에 통지(라이브 갱신).
    public class RefreshMsg : BaseMessage
    {
    }

    // 미니게임 클리어로 진입 오브젝트가 완료(Done) 전환됨을 메인 카페 팝업에 통지(맵/오브젝트 상태 재적용 트리거).
    //  미니게임 팝업(UIPopupMiniGameBalloon)도 이 통지를 받아 자신을 닫는다(이벤트 기반 분리).
    public class ObjectClearedMsg : BaseMessage
    {
        public int objectIdx;
        // 완료된 진입 오브젝트의 맵 셀 식별자(mapIdx) — 메인 팝업이 완료 위치에 도우미를 대기시키는 데 사용(§7-5).
        //  objectIdx 는 중복 가능하므로 완료 오브젝트 식별은 mapIdx 로 한다(0 이면 완료 위치 대기 생략 → 활성 오브젝트 폴백).
        public int mapIdx;
    }

    // 라운드 레이아웃 준비/전환 완료(서버 권위 송수신/적용 끝) → 미니게임 팝업이 UI 재구성(RefreshAll).
    public class RoundReadyMsg : BaseMessage
    {
        public int objectIdx;
    }

    // 풍선 터뜨리기 데이터 적용 완료(서버 권위 송수신/적용 끝) → 미니게임 팝업이 결과 연출(사운드/그리드/비행/열쇠/전체수집) 재생.
    public class BalloonPoppedMsg : BaseMessage
    {
        public int objectIdx;
        public PopBalloonResult result;
    }

    // objectIdx 별 상점 일일 구매 횟수 영속 상태(리셋 기준일 포함). 미니게임 스냅샷 패킷에 카운트가 없어
    //  GetMiniGameData 가 호출마다 재생성하는 transient MiniGameModel 로는 유지 불가 → 서브컨텐츠가 보관한다.
    private class ShopBuyState
    {
        public readonly List<int> counts = new(); // 상품 슬롯별 구매 횟수
        public int resetDateNum;                  // 일일 리셋 기준일 (yyyyMMdd, 서버 날짜)
    }

    private LiveEventData _eventData;

    // 응답 대기 중 중복 요청 차단 — 입력 잠금을 팝업이 아닌 서브컨텐츠(데이터 요청 소유자)가 가진다.
    // [단일 송신 2026-06-17] 풍선 터뜨리기(Pop)는 서버 왕복 없이 로컬 모델에만 누적하므로 _popping(네트워크 왕복 가드) 불필요 — 주석화.
    //  로컬 터뜨림은 동기 처리(읽기→변경→캐시 기록)라 연타에도 wasPopped 멱등 가드로 중복 재화/보상이 없다.
    // private bool _popping;  // 풍선 터뜨리기 왕복 중
    private bool _proceeding;  // 라운드 진행/클리어 왕복 중
    private bool _starting;    // 라운드 시작 보장(레이아웃 생성) 왕복 중 — 팝업 오픈 연타/동시호출 시 Start 패킷 중복 송신 차단

    // [단일 송신 2026-06-17] 로컬 터뜨림(Pop)으로 서버에 아직 반영되지 않은 진행 상태(objectIdx)를 보관한다.
    //  라운드 진행/클리어(마지막 흐름)에서 단일 스냅샷으로 함께 전송되거나, 팝업을 미진행 상태로 닫을 때 1회 flush 된다(재접속 시 보드 상태 유실/재팝 방지).
    private readonly HashSet<int> _pendingFlush = new();

    // 상점 일일 구매 횟수(클라 영속, objectIdx 별) — 광고/다이아 구매 일일 한도 유지(세션·자정 경계). 세션 간 영속은 서버 패킷 확장 시(후속).
    // [ISSUE-65] 키 = (objectIdx, round) 복합(MakeShopStateKey) — 라운드별 상점 설정/하루 한도가 다르므로 일일 구매 카운트도 라운드별로 분리한다.
    private readonly Dictionary<long, ShopBuyState> _shopBuyStates = new();

    public void Initialize(LiveEventData eventData)
    {
        _eventData = eventData;
    }

    public void OnObjectInteractComplete(int objectIdx)
    {
        // 미니게임은 오브젝트 상호작용 완료타이밍때 처리할 로직 없음
    }

    public void Refresh()
    {
        CharacterCafeEventData data = _eventData?.GetEventInfo<CharacterCafeEventData>();
        if (null == data)
            return;

        // 미니게임 진행 UI 갱신 — 열려 있는 미니게임 팝업에 통지(재화/라운드/보상 슬롯 등 데이터 기반 갱신).
        Message.Send(new RefreshMsg());
    }

    #region 요청 진입점 (이벤트 기반 분리 2026-06-14 — 시작점=서브컨텐츠, 송수신/적용/시퀀스/가드 소유 → 팝업은 연출만 재생)

    // 라운드 시작 보장 — 진행 기록이 없으면(새 미니게임) 서버 권위로 레이아웃 생성 후 RoundReadyMsg 통지.
    //  팝업 오픈(InitRoundAsync) 시 호출. 시작 불필요(복원)면 데이터 변경 없이 RoundReadyMsg 만 통지(팝업 RefreshAll).
    public void RequestEnsureRoundStarted(int objectIdx)
        => RequestEnsureRoundStartedAsync(objectIdx).Forget();

    private async UniTaskVoid RequestEnsureRoundStartedAsync(int objectIdx)
    {
        // [2026-06-16 중복 가드] 팝업 오픈/재초기화가 응답 전 두 번 호출되면 needStart 가 둘 다 true 로 읽혀
        //  Start 패킷이 중복 송신되고 풍선 레이아웃이 2번 생성된다(Pop/Proceed 의 _popping/_proceeding 과 동일 취지).
        if (_starting)
            return;

        _starting = true;
        try
        {
            MiniGameModel data = FindMiniGameData(objectIdx);
            bool needStart = (null == data) || data.balloonContents.IsNullOrEmpty();
            if (needStart)
                // [isReset 의미 변경 2026-06-14] 첫 시작은 진행 초기화(isReset=true)가 아니라 신규 진입(isReset=false)으로 보낸다.
                //  진행 초기화(isReset=true)는 미니게임 "전체 완료" 시점에만 콜백으로 송신한다(RequestProceedMiniGameAsync 클리어 분기).
                // await TryStartMiniGameRoundAsync(objectIdx, isReset: true);
                await TryStartMiniGameRoundAsync(objectIdx, isReset: false);

            Message.Send(new RoundReadyMsg { objectIdx = objectIdx });
        }
        finally
        {
            _starting = false;
        }
    }

    // 풍선 터뜨리기 — [케이스별 송신 2026-06-16] 진행 모델(캐시 스냅샷)에 터뜨림/열쇠를 반영 + 비용/히든 보상 로컬 처리 후, 풍선 1개당 RQCharacterCafeMiniGameUpdate 를 1회 송신한다.
    //  · 입력만 되는 케이스(꽝/열쇠) / 보상을 얻는 케이스 모두 동일하게 진행 스냅샷을 1회 동기화한다(태그로 구분).
    //  · 결과 연출(BalloonPoppedMsg)은 클라 권위라 서버 응답을 기다리지 않고 즉시 통지한다.
    //  재화 부족 선검사/구매 팝업은 UI 가드라 팝업에 둔다(여기선 데이터만).
    public void RequestPopBalloon(int objectIdx, int balloonIndex)
        => RequestPopBalloonAsync(objectIdx, balloonIndex).Forget();

    private async UniTaskVoid RequestPopBalloonAsync(int objectIdx, int balloonIndex)
    {
        PopBalloonResult result = ApplyPopBalloonLocal(objectIdx, balloonIndex);
        if (null == result)
            return;

        // 결과 연출 즉시 통지(클라 권위 — 서버 응답 대기 불필요).
        Message.Send(new BalloonPoppedMsg { objectIdx = objectIdx, result = result });

        // 케이스별 송신 — 터뜨린 풍선 내용으로 입력만(꽝/열쇠) vs 보상 획득을 구분해 진행 스냅샷을 1회 서버 동기화한다.
        MiniGameModel data = FindMiniGameData(objectIdx);
        if (null != data)
        {
            int miniGameId = EventCharacterCafeHelper.GetObjectMiniGameId(objectIdx);
            CharacterCafeMiniGameSnapshot patch = ToSnapshot(data);
            string tag = (result.content > 0) ? "PopReward" : "PopInput";
            await SendMiniGameUpdateAsync(miniGameId, patch, objectIdx, tag);
        }
    }

    // 풍선 터뜨리기(로컬 적용) — 진행 모델(캐시 스냅샷)에 터뜨림/열쇠를 반영하고, 비용(popCost)·히든 보상을 로컬 지갑에 처리한 뒤 결과를 구성한다.
    //  서버 동기화는 호출부(RequestPopBalloonAsync)가 풍선 1개당 1회 RQCharacterCafeMiniGameUpdate 로 처리한다(케이스별 송신).
    private PopBalloonResult ApplyPopBalloonLocal(int objectIdx, int balloonIndex)
    {
        MiniGameModel data = FindMiniGameData(objectIdx);
        if (null == data || data.balloonContents.IsNullOrEmpty())
            return null;
        if (balloonIndex < 0 || balloonIndex >= data.balloonContents.Count)
            return null;

        // 이번 슬롯이 신규 터뜨림인지(이전 미터뜨림) 캡처 — 로컬 비용/보상 1회 적용 가드(연타/재오픈 중복 방지).
        bool wasPopped = balloonIndex < data.balloonPopped.Count && data.balloonPopped[balloonIndex];
        int content = data.balloonContents[balloonIndex];

        // [ISSUE-66] 확률 추첨 모드 — 미터뜨림 슬롯의 내용은 미정(0)이며, 터뜨리는 이 순간 추첨해 슬롯에 확정 기록한다.
        //  이미 터뜨린 슬롯(wasPopped, 재오픈/연타)은 기록값을 그대로 쓴다. 추첨은 popped 세팅 전에 해야 팝 순번(1-base)이 맞는다.
        if (!wasPopped && UsesRateRoll(objectIdx, data.curRound))
        {
            content = RollBalloonContent(objectIdx, data.curRound, data.balloonContents, data.balloonPopped);
            data.balloonContents[balloonIndex] = content;
        }

        // 진행 모델 변경(터뜨림 + 열쇠 발견) 후 로컬 캐시(DataManagement miniGameList)에 기록 — GetMiniGameData 가 다음 조회에 반영한다.
        while (data.balloonPopped.Count <= balloonIndex)
            data.balloonPopped.Add(false);
        data.balloonPopped[balloonIndex] = true;
        if (content == -1)
            data.keyFound = true;

        WriteLocalSnapshot(objectIdx, data);
        // [케이스별 송신 2026-06-16] 풍선 1개당 송신으로 전환 — 로컬 누적 후 일괄 flush(_pendingFlush) 불필요. 원본 보존:
        // _pendingFlush.Add(objectIdx);

        // 비용 소모/히든 보상 — 신규 터뜨림 1회만 로컬 지갑에 반영(서버 echo 저장형과 동일, 미니게임 패킷에 재화 필드 없음).
        if (!wasPopped)
            ApplyPopEconomyLocal(objectIdx, data.curRound, content);

        PopBalloonResult result = new()
        {
            balloonIndex   = balloonIndex,
            content        = content,
            isKey          = (content == -1),
            isAllCollected = IsAllBalloonRewardsCollected(ToSnapshot(data)),
        };
        // 히든 보상(content>0) 표시 전용 — 실제 획득은 위 ApplyPopEconomyLocal 에서 로컬 지급(여기선 비행 연출용 보상만 구성).
        if (content > 0)
            result.rewards = CharacterCafeRewardFlyHelper.ResolveRewards(new[] { content });

        return result;
    }

    // 로컬 진행 스냅샷 기록 — 서버 응답을 기다리지 않고 진행 모델을 DataManagement(miniGameList) 캐시에 즉시 반영한다(팝업 라이브 읽기/오브젝트 Done 파생 소스).
    //  서버 영속은 터뜨리기/진행/클리어 각 케이스의 RQCharacterCafeMiniGameUpdate 송신이 담당한다(케이스별 송신).
    private static void WriteLocalSnapshot(int objectIdx, MiniGameModel model)
    {
        if (null == model)
            return;
        long cafeId = EventCharacterCafeHelper.GetCafeId();
        CharacterCafeMiniGameSnapshot snap = ToSnapshot(model);
        DataManagement.Instance.EventData.SetCharacterCafeMiniGame(cafeId, snap.miniGameId, snap);
    }

    // 미진행 상태로 닫을 때 호출 — 로컬에만 누적된 터뜨림 진행을 단일 스냅샷으로 1회 영속한다(재접속 시 보드 상태 유실/재팝 방지). dirty 없으면 무동작.
    public void FlushPendingPops(int objectIdx)
    {
        // 진행/클리어 단일 송신이 왕복 중이면 생략 — 그 송신이 진행 상태를 영속한다.
        //  ⚠️클리어는 TryClearMiniGameAsync 가 ObjectClearedMsg 를 동기 발송 → 팝업이 즉시 닫혀(OnDisable) 본 메서드가 호출되는데,
        //   이때 proceed 가 아직 _pendingFlush.Remove 전이라 가드가 없으면 Clear + FlushPops 로 이중 송신된다(중복 방지 핵심).
        if (_proceeding)
            return;
        if (!_pendingFlush.Contains(objectIdx))
            return;
        FlushPendingPopsAsync(objectIdx).Forget();
    }

    private async UniTaskVoid FlushPendingPopsAsync(int objectIdx)
    {
        _pendingFlush.Remove(objectIdx);

        MiniGameModel data = FindMiniGameData(objectIdx);
        if (null == data)
            return;

        int miniGameId = EventCharacterCafeHelper.GetObjectMiniGameId(objectIdx);
        CharacterCafeMiniGameSnapshot patch = ToSnapshot(data);
        await SendMiniGameUpdateAsync(miniGameId, patch, objectIdx, "FlushPops");
    }

    // 라운드 진행/클리어 — 열쇠 발견 시에만 진행(§2). 클리어면 ObjectClearedMsg(메인 맵 재구성 + 팝업 닫기), 아니면 다음 라운드 생성 후 RoundReadyMsg.
    //  클리어/다음 라운드 시퀀스를 서브컨텐츠가 소유한다(팝업은 결과 통지만 수신).
    public void RequestProceedMiniGame(int objectIdx)
        => RequestProceedMiniGameAsync(objectIdx).Forget();

    private async UniTaskVoid RequestProceedMiniGameAsync(int objectIdx)
    {
        if (_proceeding)
            return;

        MiniGameModel data = FindMiniGameData(objectIdx);
        if (null == data || !data.keyFound)
            return; // 열쇠 미발견 — 진행 불가

        _proceeding = true;
        try
        {
            // [단일 송신 2026-06-17] 라운드 진행/클리어는 "마지막 흐름"에서 RQCharacterCafeMiniGameUpdate 를 단 한 번만 보낸다.
            //  (기존엔 Pop + Clear + Start 가 각각 송신돼 한 행위에 3회 왕복했음 — Pop 은 로컬 누적으로, Clear/Start 는 단일 스냅샷으로 통합.)
            int curRound = (data.curRound > 0) ? data.curRound : 1;
            int totalRounds = EventCharacterCafeHelper.GetMiniGameRounds(objectIdx)?.Count ?? 0;
            bool isClear = totalRounds > 0 && curRound >= totalRounds; // 마지막 라운드 열쇠 → 클리어

            // [메타베이스 로그] 미니게임 라운드 클리어(라운드 보상 획득 시) — 열쇠 발견 후 라운드 확정(클리어/다음 라운드 진행) 시점 1회.
            //  Action 은 이번 라운드에서 유저가 실제 터뜨려 획득한 히든 보상 idx 만(콤마 배열). Label=미니게임 고유 idx / Value=그룹 idx.
            SendMiniGameRoundClearLog(objectIdx, curRound, data);

            if (isClear)
            {
                // 미니게임 "전체 완료" — 단일 송신(Clear, isClear=true). TryClearMiniGameAsync 내부에서 ObjectClearedMsg 를 통지해
                //  메인 맵 재구성 + 진입 오브젝트 Done(snapshot.isClear 파생 즉시 + StepUpdate 영속) + 팝업 닫기를 일반 흐름과 동일하게 수행한다.
                //  ⚠️ 진입 오브젝트는 Done(재진입 불가)이라 기존의 추가 reset(isReset=true) 송신은 불필요 → 제거(단일 송신). 원본 보존:
                //   await TryStartMiniGameRoundAsync(objectIdx, isReset: true);
                ClearMiniGameResult result = await TryClearMiniGameAsync(objectIdx);
                if (null != result && result.isClear)
                    _pendingFlush.Remove(objectIdx); // 단일 클리어 송신이 진행 상태를 영속 — 닫기 flush 불필요
                return;
            }

            // 다음 라운드 — 단일 송신(Start). 다음 라운드(curRound+1) 레이아웃을 생성해 curRound 전진과 함께 한 번에 전송한다.
            //  기존의 Clear 선행 송신(라운드 전진 통지)은 제거 — 이어지는 Start 스냅샷이 curRound/레이아웃을 모두 덮어쓰므로 불필요(전체 스냅샷 전송 구조).
            int nextRound = curRound + 1;
            StartMiniGameRoundResult startResult = await SendRoundStartAsync(objectIdx, nextRound, isReset: false, "Proceed");
            if (null != startResult)
            {
                _pendingFlush.Remove(objectIdx); // 단일 진행 송신이 진행 상태를 영속(다음 라운드 fresh) — 닫기 flush 불필요
                Message.Send(new RoundReadyMsg { objectIdx = objectIdx });
            }
        }
        finally
        {
            _proceeding = false;
        }
    }

    // [메타베이스 로그] 라운드 클리어 시 획득 보상 집계 → SendMMPCafeMiniGameClear.
    //  이번 라운드(curRound)에서 터뜨린(balloonPopped) 히든 보상(balloonContents>0)만 모아 콤마 배열로 Action 에 기록한다.
    //  (기획: 유저가 어떤 보상을 획득한 상태에서 라운드 클리어를 결정했는지가 핵심 — 테이블 roundReward 전체가 아닌 실제 획득분)
    private void SendMiniGameRoundClearLog(int objectIdx, int curRound, MiniGameModel data)
    {
        EventCharacterCafeMiniGameTableData roundTable = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, curRound);
        if (null == roundTable)
            return;

        List<int> acquired = new();
        if (null != data && null != data.balloonContents && null != data.balloonPopped)
        {
            int count = data.balloonContents.Count;
            for (int i = 0; i < count; i++)
            {
                if (i < data.balloonPopped.Count && data.balloonPopped[i] && data.balloonContents[i] > 0)
                    acquired.Add(data.balloonContents[i]);
            }
        }

        AnalyticsManager.SendMMPCafeMiniGameClear(roundTable.index, string.Join(",", acquired), roundTable.evnetGIdx);
    }

    #endregion

    #region 데이터 변경 (서버 권위 — 실서버 패킷 / 치트 시 Fs)

    // 서브(2차) — 미니게임 클리어/라운드 진행 (마지막 라운드 클리어 시 진입 오브젝트 Done + 다음 활성).
    //  [2026-06-14 Fs 디커미션] 치트 경로 폐기 — 실서버 패킷(RQCharacterCafeMiniGameUpdate) 전용.
    public async UniTask<ClearMiniGameResult> TryClearMiniGameAsync(int objectIdx)
    {
        ClearMiniGameResult result = await RequestMiniGameClearAsync(objectIdx);

        // 최종 클리어 시 진입 오브젝트가 Done 전환 + 다음 오브젝트 활성화됨 → 메인 카페 팝업에 맵 갱신 통지.
        //  mapIdx(진입 오브젝트 셀)도 함께 통지한다 — Content 가 step 전환/포인트 처리에, 메인 팝업이 완료 위치 도우미 대기(§7-5)에 사용한다.
        if (null != result && result.isClear)
            Message.Send(new ObjectClearedMsg { objectIdx = result.objectIdx, mapIdx = result.mapIdx });

        return result;
    }

    // 서브(2차) — 미니게임 라운드 시작 (풍선 레이아웃 클라 생성 → 서버 저장).
    //  isReset = true 면 새 미니게임 시작(라운드 1로 초기화). false(기본)면 기존 미니게임의 라운드 진행.
    //  [2026-06-14 Fs 디커미션] 치트 경로 폐기 — 실서버 패킷 전용.
    public async UniTask<StartMiniGameRoundResult> TryStartMiniGameRoundAsync(int objectIdx, bool isReset = false)
    {
        return await RequestMiniGameStartAsync(objectIdx, isReset);
    }

    // 서브(2차) — 미니게임 풍선 터뜨리기 (popCost 차감 → 내용 공개/지급).
    //  [단일 송신 2026-06-17] 풍선 터뜨림은 서버 왕복(RequestMiniGamePopAsync) 대신 로컬 누적(ApplyPopBalloonLocal)으로 전환 — 본 서버 송신형은 미사용. 원본 보존 — 주석화.
    // public async UniTask<PopBalloonResult> TryPopBalloonAsync(int objectIdx, int balloonIndex)
    // {
    //     return await RequestMiniGamePopAsync(objectIdx, balloonIndex);
    // }

    // 서브(2차) — 미니게임 상점 구매 (이벤트 재화 구매). ※스냅샷 미표현 = 본 패킷 범위 밖.
    //  [2026-06-14 Fs 디커미션] Fs 가짜서버 구매 경로 폐기 — 실서버 구매 패킷(IAP/광고) 연동 전까지 무동작(false). 원본 보존 — 주석화.
    // 미니게임 상점 구매(§3-6) — 일일 구매제한 체크 → 비용 처리(다이아 차감/광고는 호출 전 시청) → 이벤트 재화 지급.
    //  재화는 로컬 처리(미니게임 패킷에 상점/재화 필드 없음, FakeServer TryBuyMiniGameShop 과 동치). 다이아 사용 확인/광고 시청은 호출측(팝업)이 선처리.
    //  ※ shopBuyCounts(일일 횟수)는 스냅샷 패킷에 없어 런타임 증가만 — 일일 리셋(EnsureShopDailyReset, 서버 날짜 경계)로 한도 적용, 세션 간 영속은 패킷 확장 시(후속).
    public bool TryBuyMiniGameShop(int objectIdx, int shopIndex, out BuyMiniGameShopResult result)
    {
        result = null;

        MiniGameModel data = FindMiniGameData(objectIdx);
        if (null == data)
            return false;

        // [ISSUE-65] 상점 구성은 라운드별 — 현재 진행 라운드(curRound, 폴백 1)의 미니게임 테이블 행에서 읽는다(팝업/풍선 popCost 조회와 동일 규약).
        //  (구 rounds[0] 고정 = 모든 라운드가 1라운드 상점 설정·하루 한도로 처리되던 버그)
        int round = (data.curRound > 0) ? data.curRound : 1;
        EventCharacterCafeMiniGameTableData shopTable = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, round);
        if (null == shopTable)
            return false;

        if (shopTable.shopEventGoodsCount.IsNullOrEmpty() || !shopTable.shopEventGoodsCount.IsInOfRange(shopIndex))
            return false;

        // 구매 횟수는 서브컨텐츠(세션 영속)가 소유 — 스냅샷 기반 transient 모델(data)은 호출마다 재생성돼 카운트가 유지되지 않는다(광고/구매 한도 미동작 원인).
        int shopCount = shopTable.shopEventGoodsCount.Length;
        ShopBuyState shopState = GetShopBuyState(objectIdx, round, shopCount);

        // 일일 구매제한 체크 (0=무제한). [세션 간 영속] 서버 스냅샷 만료시각(expirePurchaseTime) 미래면 한도 소진(재접속 유지) OR 세션 내 카운트 초과.
        int buyLimit = shopTable.shopBuyLimit.IsInOfRange(shopIndex) ? shopTable.shopBuyLimit[shopIndex] : 0;
        if (buyLimit > 0 && (IsShopPurchaseBlocked(objectIdx, round, shopIndex) || shopState.counts[shopIndex] >= buyLimit))
            return false; // 일일 한도 초과

        // 비용 처리 (buyType 0=다이아 / 1=광고) + 이벤트 재화 지급 — 모두 로컬 지갑.
        List<RewardPacketData> changes = new();
        int buyType = shopTable.shopBuyType.IsInOfRange(shopIndex) ? shopTable.shopBuyType[shopIndex] : 0;
        int spentDia = 0;
        if (buyType == 0)
        {
            int diaCost = shopTable.shopBuyValue.IsInOfRange(shopIndex) ? shopTable.shopBuyValue[shopIndex] : 0;
            if (diaCost > 0)
            {
                if (DataManager.Instance.GetCurrencyCount((int)CurrencyType.Diamond) < diaCost)
                    return false; // 다이아 부족
                changes.Add(new RewardPacketData(ItemType.Currency, (int)CurrencyType.Diamond, -diaCost));
                spentDia = diaCost;
            }
        }
        // 광고형(buyType==1)은 호출 전(팝업) 광고 시청 완료 → 다이아 미차감, 이벤트 재화만 지급.

        int grantCount = shopTable.shopEventGoodsCount[shopIndex];
        int currencyIdx = ResolveEventCurrencyIndex();
        if (grantCount > 0 && currencyIdx > 0)
            changes.Add(new RewardPacketData(ItemType.Currency, currencyIdx, grantCount));

        ApplyCurrencyChangesLocal(changes, LogItemTriggerType.Spend_cafe_minigame,  (int)EventCharacterCafeHelper.GetCafeId());

        // 운영툴 로그 — 미니게임(풍선) 재화 구매 다이아 소모(지갑 반영 후 lastValue 정확). 광고형(buyType==1)은 SendCafeAdLog(팝업)에서 별도 기록.
        //if (spentDia > 0)
        //{
            //EventCharacterCafeHelper.SendCafeCurrencyLog(LogItemTriggerType.Spend_cafe_minigame, (int)CurrencyType.Diamond, spentDia);
            // [메타베이스 로그] 미니게임 다이아 소모(추가) — Use_dia / 라벨 Spend_Cafeminigame / Value=소모값.
            //  재화 변경은 SetRewardItems(logType=None) 로 처리돼 MMP 가 자동 송신되지 않으므로 여기서 직접 SendDiaLog 를 호출한다.
            //AnalyticsManager.SendDiaLog(LogItemTriggerType.Spend_cafe_minigame, spentDia, 0);
        //}

        shopState.counts[shopIndex]++;

        // [세션 간 영속] 한도 도달 시 만료시각(다음 일일 리셋)을 서버 스냅샷(expirePurchaseTime)에 영속 — 재접속해도 한도 유지.
        if (buyLimit > 0 && shopState.counts[shopIndex] >= buyLimit)
            PersistShopPurchaseExpire(objectIdx, round, shopIndex);

        result = new BuyMiniGameShopResult { shopIndex = shopIndex, grantedCurrency = grantCount };
        return true;
    }

    // objectIdx 의 상점 구매 영속 상태 확보 — (1) 카운트 배열을 상품 수만큼 보정하고,
    //  (2) 서버 시각 날짜(UTC, yyyyMMdd)가 기준일과 다르면 모든 카운트를 0 으로 리셋한다.
    //  리셋 경계는 상점 아이템 리셋 타이머(UIMiniGameBalloonPurchaseItem, 다음 UTC 자정)와 동일. 구매(TryBuyMiniGameShop)·표시(GetShopRemainCount) 공용.
    //  ※ 카운트는 미니게임 스냅샷 패킷 미포함 → GetMiniGameData 가 호출마다 재생성하는 transient 모델에 두면 즉시 소실된다.
    //    이벤트 수명 동안 살아있는 본 서브컨텐츠가 objectIdx 별로 보관해 세션·자정 경계 내에서 한도를 정확히 유지한다(세션 간 영속은 서버 패킷 확장 시 후속).
    private ShopBuyState GetShopBuyState(int objectIdx, int round, int shopCount)
    {
        long key = MakeShopStateKey(objectIdx, round);
        if (!_shopBuyStates.TryGetValue(key, out ShopBuyState state))
        {
            state = new ShopBuyState();
            _shopBuyStates[key] = state;
        }

        while (state.counts.Count < shopCount)
            state.counts.Add(0);

        int todayNum = DateTimeUtils.DateConvertIntTime(DataManager.Instance.GetCurrentTime());
        if (state.resetDateNum != todayNum)
        {
            int count = state.counts.Count;
            for (int i = 0; i < count; i++)
                state.counts[i] = 0;
            state.resetDateNum = todayNum;
        }
        return state;
    }

    // 상점 슬롯 남은 일일 구매 가능 횟수(무제한이면 -1) — 팝업 표시/버튼 비활성 판단용(클라 영속 카운트 기준). [ISSUE-65] 라운드별 분리.
    public int GetShopRemainCount(int objectIdx, int round, int shopIndex, int buyLimit)
    {
        if (buyLimit <= 0)
            return -1;
        if (shopIndex < 0)
            return 0;

        // [세션 간 영속] 서버 스냅샷 만료시각(expirePurchaseTime)이 미래면 한도 소진(재접속 유지).
        if (IsShopPurchaseBlocked(objectIdx, round, shopIndex))
            return 0;

        ShopBuyState state = GetShopBuyState(objectIdx, round, shopIndex + 1);
        int remain = buyLimit - state.counts[shopIndex];
        return remain > 0 ? remain : 0;
    }

    // 상점 슬롯 구매 차단 여부(세션 간 영속) — 서버 스냅샷에 영속된 만료시각(expirePurchaseTime)이 현재 서버시각보다 미래면 한도 소진 상태.
    //  미니게임 스냅샷은 서버 권위·영속이라 재접속해도 유지된다(메모리 _shopBuyStates 와 달리 초기화되지 않음 — HE 재화구매 한도 재접속 초기화 버그 대응).
    //  [ISSUE-65] 라운드별 한도 분리 — expirePurchaseTime(단일 리스트)을 (라운드, 슬롯)으로 평탄화한 인덱스로 읽는다.
    private bool IsShopPurchaseBlocked(int objectIdx, int round, int shopIndex)
    {
        MiniGameModel model = FindMiniGameData(objectIdx);
        if (null == model)
            return false;

        int slot = GetExpireSlotIndex(objectIdx, round, shopIndex);
        if (model.expirePurchaseTime.IsNullOrEmpty() || !model.expirePurchaseTime.IsInOfRange(slot))
            return false;

        long expire = model.expirePurchaseTime[slot];
        if (expire <= 0)
            return false;

        long now = DateTimeUtils.GetUnixTimestamp(DataManager.Instance.GetCurrentTime());
        return expire > now;
    }

    // 상점 슬롯 한도 도달 → 만료시각(다음 일일 리셋 = 다음 자정, 상점 리셋 타이머와 동일 경계)을 서버 스냅샷에 영속한다.
    //  로컬 캐시 즉시 반영(표시/재진입 읽기) + MiniGameUpdate 송신(서버 영속, 재접속 유지). 송신은 결과 불필요라 fire-and-forget.
    //  [ISSUE-65] 라운드별 한도 분리 — (라운드, 슬롯) 평탄화 인덱스에 만료시각을 영속한다.
    private void PersistShopPurchaseExpire(int objectIdx, int round, int shopIndex)
    {
        MiniGameModel model = FindMiniGameData(objectIdx);
        if (null == model)
            return;

        int slot = GetExpireSlotIndex(objectIdx, round, shopIndex);
        while (model.expirePurchaseTime.Count < slot + 1)
            model.expirePurchaseTime.Add(0);

        DateTime nextReset = DataManager.Instance.GetCurrentTime().Date.AddDays(1); // 다음 자정(상점 리셋 타이머와 동일 경계)
        model.expirePurchaseTime[slot] = DateTimeUtils.GetUnixTimestamp(nextReset);

        WriteLocalSnapshot(objectIdx, model); // DataManagement 캐시 즉시 반영(표시/재진입 읽기)
        int miniGameId = EventCharacterCafeHelper.GetObjectMiniGameId(objectIdx);
        SendMiniGameUpdateAsync(miniGameId, ToSnapshot(model), objectIdx, "ShopBuyExpire").Forget();
    }

    // [ISSUE-65] 라운드별 일일 구매 카운트(_shopBuyStates) 복합 키 — (objectIdx, round). round 는 1-base(폴백 1).
    private static long MakeShopStateKey(int objectIdx, int round)
        => ((long)objectIdx << 16) | (uint)(round > 0 ? round : 1);

    // [ISSUE-65] expirePurchaseTime(스냅샷의 단일 long 리스트)을 (라운드, 슬롯)으로 평탄화한 인덱스.
    //  index = (round-1) * shopCount + shopIndex. 라운드별 상점 슬롯 수는 동일(테이블 규약 = 3)하다는 전제이며, stride 는 해당 라운드 테이블에서 산출한다.
    private int GetExpireSlotIndex(int objectIdx, int round, int shopIndex)
    {
        int r = round > 0 ? round : 1;
        int stride = GetShopSlotCount(objectIdx, r);
        if (stride <= 0)
            stride = 1;
        return (r - 1) * stride + shopIndex;
    }

    // [ISSUE-65] 해당 라운드의 상점 슬롯 수(shopEventGoodsCount 길이). 미존재 시 0.
    private int GetShopSlotCount(int objectIdx, int round)
    {
        EventCharacterCafeMiniGameTableData table = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, round > 0 ? round : 1);
        return (table?.shopEventGoodsCount != null) ? table.shopEventGoodsCount.Length : 0;
    }

    #endregion

    #region 실서버 경로 (RQCharacterCafeMiniGameUpdate patch→snapshot)

    // 라운드 시작 — 클라가 라운드 풍선 레이아웃을 생성한 초기화 모델(MiniGameModel)을 만들어 snapshot patch 로 서버에 전송한다.
    //  [클라 생성 전송 2026-06-14] 서버 RNG 권위 → 클라 생성·서버 저장으로 전환. 시작 시 데이터를 초기화(curRound/isClear/keyFound/풍선 배치)해서 보낸다.
    //   isReset = true 면 새 미니게임 시작(라운드 1로 초기화), false 면 다음 라운드 진입(현재 라운드 = 서버가 전진시킨 curRound).
    //   서버는 전달받은 레이아웃을 저장·검증하고 최신 snapshot 을 응답한다. 응답이 비면(레거시 서버) 클라 생성 레이아웃으로 폴백.
    private async UniTask<StartMiniGameRoundResult> RequestMiniGameStartAsync(int objectIdx, bool isReset)
    {
        MiniGameModel data = FindMiniGameData(objectIdx);

        // 시작 라운드 — 새 미니게임(isReset)은 라운드 1, 이어하기는 현재 진행 라운드(서버가 클리어로 전진시킨 값).
        int curRound = isReset ? 1 : ((null != data && data.curRound > 0) ? data.curRound : 1);

        return await SendRoundStartAsync(objectIdx, curRound, isReset, "Start");
    }

    // 라운드 시작(명시 라운드) — targetRound 의 풍선 레이아웃(클라 생성)을 초기화 모델로 만들어 단일 snapshot patch 로 전송한다.
    //  [단일 송신 2026-06-17] 라운드 진행 시 Clear 선행 송신 없이 다음 라운드(curRound+1)를 직접 지정해 한 번에 보내기 위한 공용 송신부.
    //   서버는 전달받은 레이아웃을 저장·검증하고 최신 snapshot 을 응답한다. 응답이 비면(레거시 서버) 클라 생성 레이아웃으로 폴백.
    private async UniTask<StartMiniGameRoundResult> SendRoundStartAsync(int objectIdx, int targetRound, bool isReset, string tag)
    {
        int miniGameId = EventCharacterCafeHelper.GetObjectMiniGameId(objectIdx);

        // 초기화 모델 — 시작 시점 데이터를 초기화(진행/열쇠 리셋 + 라운드 풍선 배치 클라 생성)해 서버로 보낼 형태로 구성.
        MiniGameModel startModel = BuildStartModel(objectIdx, targetRound);

        CharacterCafeMiniGameSnapshot patch = ToSnapshot(startModel);

        RSCharacterCafeMiniGameUpdate response = await SendMiniGameUpdateAsync(miniGameId, patch, objectIdx, tag, isReset);
        if (null == response)
            return null;

        // [Stage3 2026-06-14] 미러 투영 제거 — 미니게임 스냅샷은 실서버 권위(DataManagement miniGameList, 응답 핸들러 OnResponseCharacterCafeMiniGameUpdate 가 갱신, 읽기=GetMiniGameData).
        // 응답 snapshot 우선, 비었으면 클라가 보낸 초기화 레이아웃으로 폴백(클라 권위 생성이므로 정합).
        int[] balloons = response.snapshot?.balloonContents
                         ?? (startModel.balloonContents != null ? startModel.balloonContents.ToArray() : System.Array.Empty<int>());
        return new StartMiniGameRoundResult
        {
            round           = response.snapshot?.curRound ?? targetRound,
            balloonContents = balloons,
        };
    }

    // 미니게임 시작 시점의 초기화 모델 — 진행/열쇠 리셋 + 해당 라운드 풍선 배치(클라 생성). 서버 전송/폴백 공용.
    private static MiniGameModel BuildStartModel(int objectIdx, int curRound)
    {
        List<int> contents = BuildMiniGameBalloonContents(objectIdx, curRound) ?? new List<int>();

        // [세션 간 영속] 상점 일일 구매 한도 만료시각(expirePurchaseTime)은 라운드 진행과 무관하게 유지돼야 한다.
        //  라운드 시작은 "전체 스냅샷 전송"(서버가 그대로 저장)이라, 보존하지 않으면 라운드 시작/재접속 시 만료시각이 초기화돼 구매 한도가 풀린다.
        MiniGameModel existing = EventCharacterCafeHelper.GetMiniGameData(objectIdx);
        List<long> expirePurchaseTime = (null != existing && existing.expirePurchaseTime != null)
            ? new List<long>(existing.expirePurchaseTime)
            : new List<long>();

        return new MiniGameModel
        {
            objectIdx          = objectIdx,
            curRound           = curRound,
            isClear            = false,
            keyFound           = false,
            balloonContents    = contents,
            balloonPopped      = new List<bool>(new bool[contents.Count]),
            expirePurchaseTime = expirePurchaseTime,
        };
    }

    // [전체 스냅샷 전송 2026-06-14] 부분 patch(변경 필드만) 대신 보유 중인 "전체 스냅샷"을 만들어 전송하기 위한 빌더.
    //  서버 patch 병합 정책(null=유지 vs 덮어쓰기)에 의존하지 않도록 balloonContents 등 모든 필드를 함께 보낸다.
    //  data 가 없으면(예외 케이스) 최소 식별 정보만 채운 스냅샷으로 폴백한다(호출부에서 변경 필드를 덮어씀).
    private static CharacterCafeMiniGameSnapshot BuildFullSnapshot(int objectIdx, int miniGameId, MiniGameModel data)
    {
        if (null != data)
            return ToSnapshot(data);

        return new CharacterCafeMiniGameSnapshot
        {
            objectIdx  = objectIdx,
            miniGameId = miniGameId,
            curRound   = 1,
        };
    }

    // MiniGameModel → 서버 전송용 snapshot patch 변환.
    private static CharacterCafeMiniGameSnapshot ToSnapshot(MiniGameModel model)
    {
        return new CharacterCafeMiniGameSnapshot
        {
            objectIdx       = model.objectIdx,
            miniGameId      = EventCharacterCafeHelper.GetObjectMiniGameId(model.objectIdx),
            curRound        = model.curRound,
            isClear         = model.isClear,
            keyFound        = model.keyFound,
            balloonContents = (model.balloonContents != null) ? model.balloonContents.ToArray() : System.Array.Empty<int>(),
            balloonPopped   = (model.balloonPopped != null) ? model.balloonPopped.ToArray() : System.Array.Empty<bool>(),
            expirePurchaseTime = (model.expirePurchaseTime != null) ? model.expirePurchaseTime.ToArray() : System.Array.Empty<long>(),
        };
    }

    // [이동 2026-06-14] 미니게임 라운드 풍선 레이아웃 생성(클라 권위) — 열쇠1 + 히든 보상(roundReward) + 나머지 꽝, 그리드 크기에 맞춰 Fisher-Yates 셔플.
    //  미니게임 데이터 소유(서브컨텐츠)에 생성 로직을 둔다. 시작/다음 라운드 진입 시 클라가 생성해 서버로 전송(서버는 저장·검증).
    //  치트(Fs 핸들러)도 본 메서드를 공용 호출. 테이블/그리드가 없으면 null(슬롯 0).
    public static List<int> BuildMiniGameBalloonContents(int objectIdx, int round)
    {
        var roundTable = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, round);
        if (null == roundTable)
            return null;

        int totalBalloon = 0;
        if (roundTable.roundColsRows != null && roundTable.roundColsRows.Length >= 2)
            totalBalloon = roundTable.roundColsRows[0] * roundTable.roundColsRows[1];
        if (totalBalloon <= 0)
            return null;

        // [ISSUE-66] 확률 추첨 모드 — 내용을 사전 배치하지 않는다. 슬롯 값은 "터뜨린 슬롯만 확정"이며
        //  미터뜨림 슬롯의 0 은 꽝이 아니라 미정이다(스냅샷 포맷 무변경 호환 — 터뜨리는 순간 RollBalloonContent 로 확정·기록).
        if (UsesRateRoll(objectIdx, round))
            return new List<int>(new int[totalBalloon]);

        // 내용물 구성: 열쇠 1 + 히든 보상(roundReward) + 나머지 꽝, 그리드 크기에 맞춰 셔플.
        List<int> contents = new(totalBalloon);
        contents.Add(-1); // 열쇠
        if (!roundTable.roundReward.IsNullOrEmpty())
        {
            foreach (int rewardIndex in roundTable.roundReward)
            {
                if (contents.Count >= totalBalloon) break;
                if (rewardIndex <= 0) continue;
                contents.Add(rewardIndex); // 히든 보상
            }
        }
        while (contents.Count < totalBalloon)
            contents.Add(0); // 꽝

        ShuffleInPlace(contents);
        return contents;
    }

    #region [ISSUE-66] 풍선 내용 확률 추첨 (팝 시점 lazy roll)

    // 후보 항목 표기(로그용) — -1 열쇠 / 0 꽝 / 1~ 보상 idx.
    private const int CONTENT_KEY = -1;
    private const int CONTENT_BLANK = 0;

    /// <summary>
    /// [ISSUE-66] 확률 추첨 모드 여부 — `rewardRate` 가 발행된(양수 항목이 하나라도 있는) 라운드만 팝 시점 추첨을 쓴다.
    /// 미발행 라운드는 기존 사전 배치(열쇠1+보상+꽝 셔플)를 그대로 유지한다(하위 호환 — "미작성 시 0 취급").
    /// </summary>
    public static bool UsesRateRoll(int objectIdx, int round)
    {
        var roundTable = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, round);
        if (null == roundTable || roundTable.rewardRate.IsNullOrEmpty())
            return false;

        foreach (int rate in roundTable.rewardRate)
        {
            if (rate > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// [ISSUE-66] 팝 시점 내용 추첨 — "재고 + 가중치" 모델.
    ///
    /// 재고: 꽝 = 총슬롯 − 열쇠1 − 유효 보상 수(티켓 서두) / 열쇠·각 보상 = 1. 터뜨려 나온 만큼 차감.
    /// 후보 제외(분모에서 제거):
    ///  · 재고 소진 항목 — "당첨된 아이템의 확률은 제외하고 나머지 내에서 계산"(티켓 코멘트①)
    ///  · 이번 팝 순번(1-base) &lt; rewardHoldCount[k] 인 항목 — "대기 카운트 동안 비율을 빼고 계산"(코멘트②)
    /// 폴백(소프트락 방지): 1차(재고+hold+비율) → 2차(hold 무시) → 3차(재고만, 균등). 재고가 유한하므로 열쇠/보상 미등장 소프트락이 없다.
    ///
    /// 미터뜨림 슬롯의 contents 값은 미정(무시)이며, popped 슬롯의 확정 내용만 재고 집계에 쓴다.
    /// 반환: -1 열쇠 / 0 꽝 / 1~ 보상 idx. 호출부가 터뜨린 슬롯에 기록해 확정한다.
    /// </summary>
    public static int RollBalloonContent(int objectIdx, int round, IReadOnlyList<int> balloonContents, IReadOnlyList<bool> balloonPopped)
    {
        var roundTable = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, round);
        int totalBalloon = balloonContents?.Count ?? 0;
        if (null == roundTable || totalBalloon <= 0)
            return CONTENT_BLANK;

        // 터뜨려 확정된 내용 집계 — 재고 차감 소스. popIndex = 이번이 몇 번째 터뜨림인지(1-base).
        CollectPoppedContents(balloonContents, balloonPopped, out int poppedCount, out bool keyOut, out int blankOut, out Dictionary<int, int> rewardOut);
        int popIndex = poppedCount + 1;

        int[] rewards = roundTable.roundReward;
        int validRewardCount = CountValidRewards(rewards);

        // 후보 구성 — rate/hold 인덱스: 0=꽝, 1=열쇠, 2+i=roundReward[i] (티켓 명세).
        List<(int content, int weight, int hold)> stocked = new();

        int blankStock = totalBalloon - 1 - validRewardCount - blankOut;
        if (blankStock > 0)
            stocked.Add((CONTENT_BLANK, GetAt(roundTable.rewardRate, 0), GetAt(roundTable.rewardHoldCount, 0)));

        if (!keyOut)
            stocked.Add((CONTENT_KEY, GetAt(roundTable.rewardRate, 1), GetAt(roundTable.rewardHoldCount, 1)));

        if (!rewards.IsNullOrEmpty())
        {
            Dictionary<int, int> remainOut = new(rewardOut);   // 동일 보상 idx 중복 발행 대응 — 나온 횟수만큼 앞 항목부터 소진 처리
            for (int i = 0; i < rewards.Length; i++)
            {
                int rewardIndex = rewards[i];
                if (rewardIndex <= 0)
                    continue;

                if (remainOut.TryGetValue(rewardIndex, out int outCount) && outCount > 0)
                {
                    remainOut[rewardIndex] = outCount - 1;   // 이미 당첨 — 재고 소진(후보 제외)
                    continue;
                }

                stocked.Add((rewardIndex, GetAt(roundTable.rewardRate, 2 + i), GetAt(roundTable.rewardHoldCount, 2 + i)));
            }
        }

        if (stocked.Count == 0)
        {
            DLogger.Error($"[CharacterCafe][풍선추첨] 재고 없음 — obj:{objectIdx} R{round} pop:{popIndex} (테이블 확인 필요)");
            return CONTENT_BLANK;
        }

        // 1차: hold 통과 + 비율 양수 → 2차: hold 무시 + 비율 양수 → 3차: 재고만(균등). 폴백 단계는 로그에 표기.
        List<(int content, int weight)> candidates = new();
        string fallbackTag = string.Empty;

        foreach ((int content, int weight, int hold) entry in stocked)
        {
            if (entry.weight > 0 && (entry.hold <= 0 || popIndex >= entry.hold))
                candidates.Add((entry.content, entry.weight));
        }

        if (candidates.Count == 0)
        {
            fallbackTag = " [폴백:hold무시]";
            foreach ((int content, int weight, int hold) entry in stocked)
            {
                if (entry.weight > 0)
                    candidates.Add((entry.content, entry.weight));
            }
        }

        if (candidates.Count == 0)
        {
            fallbackTag = " [폴백:재고균등]";
            foreach ((int content, int weight, int hold) entry in stocked)
                candidates.Add((entry.content, 1));
        }

        int totalWeight = 0;
        foreach ((int content, int weight) candidate in candidates)
            totalWeight += candidate.weight;

        // 가중치 룰렛 — 각 후보가 누적 가중치 구간 [시작~끝) 을 차지하고, roll 값이 속한 구간이 당첨된다.
        int picked = candidates[candidates.Count - 1].content;
        int pickedWeight = candidates[candidates.Count - 1].weight;
        int pickedStart = totalWeight - pickedWeight;
        int rollValue = UnityEngine.Random.Range(0, totalWeight);

        // [ISSUE-66] 결과 로그 — 아이템명(로컬라이징)·당첨 확률·룰렛 당첨 지점(roll 값이 어느 구간에 들어갔는지)을 기록한다(QA 검증용).
        List<string> candidateLogs = new(candidates.Count);
        int accumulated = 0;
        bool found = false;
        foreach ((int content, int weight) candidate in candidates)
        {
            int rangeStart = accumulated;
            accumulated += candidate.weight;

            bool isHit = !found && rollValue < accumulated;
            if (isHit)
            {
                found = true;
                picked = candidate.content;
                pickedWeight = candidate.weight;
                pickedStart = rangeStart;
            }

            candidateLogs.Add($"{ContentLogName(candidate.content)} [{rangeStart}~{accumulated})({100f * candidate.weight / totalWeight:F1}%){(isHit ? " ◀당첨" : string.Empty)}");
        }

        DLogger.Log($"[CharacterCafe][풍선추첨] obj:{objectIdx} R{round} {popIndex}번째 → 결과:{ContentLogName(picked)} 확률:{pickedWeight}/{totalWeight}({100f * pickedWeight / totalWeight:F1}%) roll:{rollValue} ∈ [{pickedStart}~{pickedStart + pickedWeight}){fallbackTag} | 후보: {string.Join(", ", candidateLogs)}");

        return picked;
    }

    /// <summary>
    /// [ISSUE-66] 열쇠 + 모든 유효 보상 수집 완료 판정 — 추첨/사전 배치 양 모드 공용.
    /// 추첨 모드는 미터뜨림 슬롯이 미정(0)이라 슬롯 검사가 성립하지 않으므로, "터뜨려 나온 내용" 집계로 판정한다.
    /// </summary>
    public static bool AreAllRewardsCollected(int objectIdx, int round, IReadOnlyList<int> balloonContents, IReadOnlyList<bool> balloonPopped)
    {
        if (null == balloonContents || null == balloonPopped)
            return false;

        if (!UsesRateRoll(objectIdx, round))
        {
            // 사전 배치 모드(기존) — 꽝 외 슬롯이 전부 터뜨려졌는지.
            int count = balloonContents.Count;
            for (int i = 0; i < count; i++)
            {
                if (balloonContents[i] == CONTENT_BLANK) continue;
                if (i >= balloonPopped.Count || !balloonPopped[i]) return false;
            }
            return true;
        }

        CollectPoppedContents(balloonContents, balloonPopped, out _, out bool keyOut, out _, out Dictionary<int, int> rewardOut);
        if (!keyOut)
            return false;

        return CountRemainingRewardStock(objectIdx, round, rewardOut) == 0;
    }

    /// <summary>
    /// [ISSUE-66] 아직 획득하지 못한 히든 보상 수 — 추첨/사전 배치 양 모드 공용(UI 잔여 카운트·진행 게이트용).
    /// </summary>
    public static int CountRemainingHiddenRewards(int objectIdx, int round, IReadOnlyList<int> balloonContents, IReadOnlyList<bool> balloonPopped)
    {
        if (null == balloonContents)
            return 0;

        if (!UsesRateRoll(objectIdx, round))
        {
            // 사전 배치 모드(기존) — 미터뜨림 히든(>0) 슬롯 수.
            int count = 0;
            int total = balloonContents.Count;
            for (int i = 0; i < total; i++)
            {
                bool isPopped = (null != balloonPopped) && i < balloonPopped.Count && balloonPopped[i];
                if (balloonContents[i] > 0 && !isPopped)
                    count++;
            }
            return count;
        }

        CollectPoppedContents(balloonContents, balloonPopped, out _, out _, out _, out Dictionary<int, int> rewardOut);
        return CountRemainingRewardStock(objectIdx, round, rewardOut);
    }

    /// <summary>
    /// [ISSUE-67] 아직 획득하지 못한 히든 보상의 content(Event_Reward.index) 목록 — 추첨/사전 배치 양 모드 공용(UI 잔여 보상 아이콘용).
    /// 추첨 모드는 미터뜨림 슬롯이 미정(0)이라 슬롯 스캔이 항상 0건이 되므로, 재고(roundReward − 이미 나온 분)를 테이블 순서대로 돌려준다.
    /// CountRemainingHiddenRewards 와 같은 판정을 쓰되 개수 대신 idx 를 반환한다(둘의 Count 는 항상 일치).
    /// </summary>
    public static List<int> GetRemainingHiddenRewardContents(int objectIdx, int round, IReadOnlyList<int> balloonContents, IReadOnlyList<bool> balloonPopped)
    {
        List<int> remaining = new();
        if (null == balloonContents)
            return remaining;

        if (!UsesRateRoll(objectIdx, round))
        {
            // 사전 배치 모드(기존) — 미터뜨림 히든(>0) 슬롯의 내용을 그대로.
            int total = balloonContents.Count;
            for (int i = 0; i < total; i++)
            {
                bool isPopped = (null != balloonPopped) && i < balloonPopped.Count && balloonPopped[i];
                if (balloonContents[i] > 0 && !isPopped)
                    remaining.Add(balloonContents[i]);
            }
            return remaining;
        }

        CollectPoppedContents(balloonContents, balloonPopped, out _, out _, out _, out Dictionary<int, int> rewardOut);
        CollectRemainingRewardStock(objectIdx, round, rewardOut, remaining);
        return remaining;
    }

    // 터뜨려 확정된 내용 집계 — 추첨 모드의 재고/판정 공용 소스(미터뜨림 슬롯의 값은 미정이라 무시).
    private static void CollectPoppedContents(IReadOnlyList<int> balloonContents, IReadOnlyList<bool> balloonPopped,
        out int poppedCount, out bool keyOut, out int blankOut, out Dictionary<int, int> rewardOut)
    {
        poppedCount = 0;
        keyOut = false;
        blankOut = 0;
        rewardOut = new Dictionary<int, int>();

        if (null == balloonContents || null == balloonPopped)
            return;

        int total = balloonContents.Count;
        for (int i = 0; i < total; i++)
        {
            if (i >= balloonPopped.Count || !balloonPopped[i])
                continue;

            poppedCount++;
            int content = balloonContents[i];
            if (content == CONTENT_KEY)
                keyOut = true;
            else if (content == CONTENT_BLANK)
                blankOut++;
            else
                rewardOut[content] = rewardOut.TryGetValue(content, out int outCount) ? outCount + 1 : 1;
        }
    }

    // 유효 보상(roundReward > 0) 중 아직 나오지 않은 재고 수 — 동일 idx 중복 발행 대응(나온 횟수만큼 소진).
    //  [ISSUE-67] 수집 로직은 CollectRemainingRewardStock 단일 소스로 이관(개수/목록 판정이 갈리지 않도록).
    private static int CountRemainingRewardStock(int objectIdx, int round, Dictionary<int, int> rewardOut)
    {
        List<int> remaining = new();
        CollectRemainingRewardStock(objectIdx, round, rewardOut, remaining);
        return remaining.Count;
    }

    // [ISSUE-67] 아직 나오지 않은 재고 보상 idx 수집(테이블 순서) — 개수(CountRemainingRewardStock)/목록(GetRemainingHiddenRewardContents) 공용.
    private static void CollectRemainingRewardStock(int objectIdx, int round, Dictionary<int, int> rewardOut, List<int> results)
    {
        var roundTable = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, round);
        int[] rewards = roundTable?.roundReward;
        if (rewards.IsNullOrEmpty())
            return;

        Dictionary<int, int> remainOut = new(rewardOut);
        foreach (int rewardIndex in rewards)
        {
            if (rewardIndex <= 0)
                continue;

            if (remainOut.TryGetValue(rewardIndex, out int outCount) && outCount > 0)
            {
                remainOut[rewardIndex] = outCount - 1;
                continue;
            }

            results.Add(rewardIndex);
        }
    }

    // 유효 보상 수(roundReward > 0).
    private static int CountValidRewards(int[] rewards)
    {
        if (rewards.IsNullOrEmpty())
            return 0;

        int count = 0;
        foreach (int rewardIndex in rewards)
        {
            if (rewardIndex > 0)
                count++;
        }

        return count;
    }

    // 배열 안전 접근 — 범위 밖/미작성(null)은 0 취급(티켓 명세).
    private static int GetAt(int[] values, int index)
    {
        return (null != values && index >= 0 && index < values.Length) ? values[index] : 0;
    }

    // 로그 표기 — -1 열쇠 / 0 꽝 / 1~ 보상은 로컬라이징된 아이템명 포함(Event_Reward → itemType/itemIdx → 아이템명).
    private static string ContentLogName(int content)
    {
        if (content == CONTENT_KEY)
            return "열쇠(-1)";
        if (content == CONTENT_BLANK)
            return "꽝(0)";

        if (TableManager.GetData(content, out EventRewardTableData rewardRow))
            return $"{ResourceUtils.GetItemName((ItemType)rewardRow.itemType, rewardRow.itemIdx)}({content})";

        return $"보상({content})";
    }

    #endregion

    // Fisher-Yates 셔플 (UnityEngine.Random). BuildMiniGameBalloonContents 전용.
    private static void ShuffleInPlace(List<int> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // 풍선 터뜨리기 — patch(터뜨린 슬롯 표시)로 요청 → 응답 snapshot(공개/지급 반영) 투영 + 결과(내용/열쇠/보상) 구성.
    //  [단일 송신 2026-06-17] 풍선 1개마다의 서버 왕복을 제거(로컬 누적 ApplyPopBalloonLocal 로 대체) — 본 송신형은 미사용. 원본 보존(#if false).
#if false
    private async UniTask<PopBalloonResult> RequestMiniGamePopAsync(int objectIdx, int balloonIndex)
    {
        int miniGameId = EventCharacterCafeHelper.GetObjectMiniGameId(objectIdx);
        MiniGameModel data = FindMiniGameData(objectIdx);

        // [로컬 재화 처리 2026-06-14] 이번 슬롯이 신규 터뜨림인지(이전 미터뜨림) 캡처 — 로컬 비용/보상 1회 적용 가드(재오픈/중복 방지).
        bool wasPopped = (null != data) && balloonIndex >= 0 && balloonIndex < data.balloonPopped.Count && data.balloonPopped[balloonIndex];

        // 현재 터뜨림 상태에 이번 슬롯을 더해 전달(서버는 스냅샷 저장·echo, 비용/보상은 로컬 처리 — 미니게임 패킷에 재화 필드 없음).
        bool[] popped = BuildPoppedPatch(data, balloonIndex);

        // [전체 스냅샷 전송 2026-06-14] 보유 중인 전체 스냅샷(balloonContents 포함)을 보내고 이번 터뜨림 슬롯만 덮어쓴다.
        CharacterCafeMiniGameSnapshot patch = BuildFullSnapshot(objectIdx, miniGameId, data);
        patch.balloonPopped = popped;

        // [열쇠 발견 반영 2026-06-14] 클라가 레이아웃 권위(전체 스냅샷 전송)이므로, 터뜨린 슬롯이 열쇠(-1)면 keyFound 도 올려 보낸다.
        //  서버는 keyFound 를 balloonPopped×balloonContents 로 파생하지 않고 저장값을 echo 하므로, 클라가 명시해야
        //  진행 게이트(data.keyFound)와 보물상자 버튼 활성이 열린다. keyFound 는 sticky(한 번 true 면 BuildFullSnapshot 이 유지).
        int poppedContent = (null != data?.balloonContents && balloonIndex >= 0 && balloonIndex < data.balloonContents.Count)
            ? data.balloonContents[balloonIndex] : 0;
        if (poppedContent == -1)
            patch.keyFound = true;

        // [HACK] 기존: 변경된 필드(balloonPopped)만 담는 부분 patch — balloonContents 가 null 로 전송되어
        //        서버가 null 을 "유지(변경 없음)"로 병합한다는 가정에 의존했음. 서버가 null 을 덮어쓰면 레이아웃이 소실되므로
        //        전체 스냅샷 전송으로 대체. 원본 보존.
        // CharacterCafeMiniGameSnapshot patch = new()
        // {
        //     objectIdx     = objectIdx,
        //     miniGameId    = miniGameId,
        //     curRound      = (null != data) ? data.curRound : 1,
        //     balloonPopped = popped,
        // };

        RSCharacterCafeMiniGameUpdate response = await SendMiniGameUpdateAsync(miniGameId, patch, objectIdx, "Pop");
        if (null == response)
            return null;

        // [Stage3 2026-06-14] 미러 투영 제거 — 미니게임 스냅샷은 실서버 권위(DataManagement miniGameList, 응답 핸들러 OnResponseCharacterCafeMiniGameUpdate 가 갱신, 읽기=GetMiniGameData).
        // ApplyServerMiniGameSnapshot(objectIdx, response.snapshot);

        CharacterCafeMiniGameSnapshot snapshot = response.snapshot;
        int content = (null != snapshot?.balloonContents && balloonIndex >= 0 && balloonIndex < snapshot.balloonContents.Length)
            ? snapshot.balloonContents[balloonIndex] : 0;

        // [로컬 재화 처리 2026-06-14] 미니게임 재화는 스냅샷 패킷(CharacterCafeMiniGame)에 필드가 없어 서버가 비용/보상을 처리하지 않는다(echo 저장 전용).
        //  기획/명세(§1 "풍선 1개 = 꽃 1송이" 소모 + 히든 즉시 획득)대로 비용·히든 보상을 로컬 지갑에 반영한다(신규 터뜨림 1회).
        if (!wasPopped)
            ApplyPopEconomyLocal(objectIdx, (null != data) ? data.curRound : 1, content);

        PopBalloonResult result = new()
        {
            balloonIndex   = balloonIndex,
            content        = content,
            isKey          = (content == -1),
            isAllCollected = IsAllBalloonRewardsCollected(snapshot),
        };
        // 히든 보상(content>0) 표시 전용 — 실제 획득은 위 ApplyPopEconomyLocal 에서 로컬 지급(여기선 비행 연출용 보상만 구성).
        if (content > 0)
            result.rewards = CharacterCafeRewardFlyHelper.ResolveRewards(new[] { content });

        return result;
    }
#endif

    // 풍선 터뜨리기 로컬 재화 처리 — 비용(popCost) 소모 + 히든 보상(content>0) 즉시 획득을 로컬 지갑(이벤트 재화)에 반영하고 서버 백업한다.
    //  미니게임은 클라 권위 스냅샷(서버 echo 저장 전용, 패킷에 재화 필드 없음)이라 비용/보상은 클라가 로컬로 처리한다(§1, 기존 로컬 소모/획득 기능 재사용).
    private void ApplyPopEconomyLocal(int objectIdx, int curRound, int content)
    {
        List<RewardPacketData> changes = new();

        // 비용 소모 — 풍선 1개 = popCost (이벤트 재화 음수 반영)
        EventCharacterCafeMiniGameTableData roundTable = EventCharacterCafeHelper.GetMiniGameRound(objectIdx, curRound);
        int popCost = (null != roundTable) ? roundTable.popCost : 0;
        int currencyIdx = ResolveEventCurrencyIndex();
        if (popCost > 0 && currencyIdx > 0)
            changes.Add(new RewardPacketData(ItemType.Currency, currencyIdx, -popCost));

        // 히든 보상 즉시 획득 — content = Event_Reward.index → 아이템 패킷(지급용, 표시 변환과 동일 규칙)
        RewardPacketData[] hiddenRewards = null;
        if (content > 0)
        {
            hiddenRewards = CharacterCafeRewardFlyHelper.ResolveRewards(new[] { content });
            if (!hiddenRewards.IsNullOrEmpty())
                changes.AddRange(hiddenRewards);
        }

        if (changes.Count == 0)
            return;

        ApplyCurrencyChangesLocal(changes, LogItemTriggerType.Gain_cafe_minigame, (int)EventCharacterCafeHelper.GetCafeId());

        // 운영툴 로그 — 미니게임(풍선) 달성 보상(히든 보상, 지갑 반영 후 lastValue 정확). 비용(popCost) 소모는 기획상 별도 로그 대상 아님.
        //EventCharacterCafeHelper.SendCafeRewardLog(LogItemTriggerType.Gain_cafe_minigame, hiddenRewards);
    }

    // 카페 재화(로컬) 소모/획득을 영속 저장소(Fs 지갑)에 반영하고, 표시 지갑(DataManager)을 동기화한 뒤 로컬 저장한다.
    //  카페 이벤트 재화/퍼즐 조각은 PlayInfo 가 아닌 Fs 데이터로 영속되므로 Fs 지갑(권위 소스)을 갱신해야 다음 진입에도 유지된다.
    //  표시 지갑은 SynchronizeCurrency 로 Fs 권위값을 절대값 set(이중 차감 없음) + UI 갱신. 저장은 카페 핸들러 OnSaveData 와 동일한 SaveChanges(Local).
    private void ApplyCurrencyChangesLocal(List<RewardPacketData> changes, LogItemTriggerType logItemTrigger, int logValue)
    {
        if (changes.IsNullOrEmpty())
            return;

        // 영속 권위 저장소(Fs 지갑) 반영
        FsWebManager.GetProcess<FsProcessCommon>().SetRewardItems(changes.ToArray(), logItemTrigger, logValue);

        // 표시 지갑(DataManager) ← Fs 권위값 동기화 (+ UI 갱신)
        int count = changes.Count;
        for (int i = 0; i < count; i++)
        {
            RewardPacketData change = changes[i];
            if (change.type == ItemType.Currency)
                DataManager.Instance.SynchronizeCurrency((CurrencyType)change.id);
        }

        // 영속 — 카페 핸들러 OnSaveData 와 동일.
        // [재시작 시 0 출력 수정 2026-06-18] Local 만 저장하면 다음 로그인에서 서버 syncId 우세 시 UpdateFsData 로 로컬 지갑이
        //  서버 지갑(카페 재화 미적립)으로 덮어써져 리셋된다. All(SaveChanges + 서버 업로드)로 지갑을 서버에도 반영해 변동이 생존하게 한다(GrantRewards/퍼즐과 대칭).
        FsWebManager.Instance.SaveDataStorage_PlayInfo(DataSaveType.Local);
        // FsWebManager.Instance.SaveDataStorage_PlayInfo(DataSaveType.All);
    }

    // 카페 이벤트 재화 인덱스(Event_Setting.itemIdx, 예: 218 CharacterCafeEventCurrency) — 미정의/비활성 시 0.
    private int ResolveEventCurrencyIndex()
    {
        LiveEventData liveData = EventDataHelper.GetLiveEventData(LiveEventType.CHARACTERCAFE);
        return (null != liveData) ? liveData.GetEventCurrencyIndex() : 0;
    }

    // 라운드 클리어/진행 — patch(클리어 요청)로 요청 → 응답 snapshot 투영 + 클리어 시 진입 오브젝트 Done/다음 활성.
    private async UniTask<ClearMiniGameResult> RequestMiniGameClearAsync(int objectIdx)
    {
        int miniGameId = EventCharacterCafeHelper.GetObjectMiniGameId(objectIdx);
        MiniGameModel data = FindMiniGameData(objectIdx);

        // [클라 권위 라운드 전진/클리어 2026-06-14] 전체 스냅샷 전송(서버 echo) 구조라, 라운드 전진/클리어 "판정"도 클라가 한다.
        //  이 계산이 없으면 patch.curRound 가 현재 라운드 그대로 echo 돼 "라운드 전환"이 일어나지 않는다(라운드 1에 고정).
        int curRound = (null != data && data.curRound > 0) ? data.curRound : 1;
        int totalRounds = EventCharacterCafeHelper.GetMiniGameRounds(objectIdx)?.Count ?? 0;
        bool isClear = totalRounds > 0 && curRound >= totalRounds; // 마지막 라운드 열쇠 → 클리어
        int nextRound = isClear ? curRound : curRound + 1;         // 아니면 다음 라운드로 전진

        // [2026-06-14] 미니게임 완료 시 진입 오브젝트의 step 전환을 서버에도 통지(StepUpdate)하기 위해, 응답 전(아직 미완료)인 진입 오브젝트 mapIdx 를 캡처한다.
        //  (응답 수신 후엔 miniGameList.isClear 파생으로 Done 전환됨. objectIdx 는 중복 가능 → 같은 objectIdx 셀 중 진행 중(비-Done)인 셀이 진입 오브젝트.)
        //  ⚠️파생 상태가 항상 Active 라는 보장이 없어(프론티어/사이드 파생 순서 등) Active 단독 필터로는 누락될 수 있다 → Active 우선, 없으면 비-Done 폴백.
        int entryMapIdx = 0;
        if (isClear)
        {
            List<CafeObjectData> objs = EventCharacterCafeHelper.GetCafeObjectDatas();
            CafeObjectData entry = objs?.Find(o => null != o && o.objectIdx == objectIdx
                && (o.state == CharacterCafeObjectState.Active || o.state == CharacterCafeObjectState.EasterEgg));
            entry ??= objs?.Find(o => null != o && o.objectIdx == objectIdx && o.state != CharacterCafeObjectState.Done);
            if (null != entry) entryMapIdx = entry.mapIdx;

            if (entryMapIdx <= 0)
                DLogger.Error($"[CharacterCafe] 미니게임 완료 StepUpdate 생략 — 진입 오브젝트 mapIdx 미해석 : objectIdx={objectIdx} (objs={(objs?.Count ?? 0)})");
        }

        // [전체 스냅샷 전송 2026-06-14] 보유 중인 전체 스냅샷을 보내되 열쇠 발견 + 라운드 전진/클리어를 덮어쓴다.
        CharacterCafeMiniGameSnapshot patch = BuildFullSnapshot(objectIdx, miniGameId, data);
        patch.keyFound = true;      // 열쇠 발견 → 라운드 진행/클리어 요청
        patch.isClear  = isClear;   // 마지막 라운드면 클리어
        patch.curRound = nextRound; // 클리어가 아니면 다음 라운드로 전진(이후 Start 가 해당 라운드 레이아웃 재생성)

        // [HACK] 기존: keyFound 만 담는 부분 patch — balloonContents/balloonPopped 가 null 로 전송되어
        //        서버 null=유지 병합 가정에 의존했음. 전체 스냅샷 전송으로 대체. 원본 보존.
        // CharacterCafeMiniGameSnapshot patch = new()
        // {
        //     objectIdx  = objectIdx,
        //     miniGameId = miniGameId,
        //     curRound   = (null != data) ? data.curRound : 1,
        //     keyFound   = true,
        // };

        RSCharacterCafeMiniGameUpdate response = await SendMiniGameUpdateAsync(miniGameId, patch, objectIdx, "Clear");
        if (null == response)
            return null;

        // [2026-06-15] 미니게임 완료 시 진입 오브젝트 step 전환 통지(StepUpdate)를 Content 로 이관.
        //  기존: 여기서 fire-and-forget StepUpdate 를 보냈으나 응답을 버려 ① 누적 포인트 지갑 미반영(SetCharacterCafePoint 누락)
        //   ② 포인트/미션 fan-out 미발생(게이지 달성/보상 연출/단계 수령 없음) 이었다.
        //  이제 진입 오브젝트 완료를 ObjectClearedMsg(mapIdx 포함)로 통지하면, Content(OnMiniGameEntryObjectCleared)가
        //   일반 상호작용과 동일하게 TryInteractObjectAsync(→ApplyServerStepResult 가 지갑 포인트 반영) + fan-out 을 수행한다.
        //   원본(fire-and-forget StepUpdate) 보존 — 주석화:
        // bool isClearConfirmed = (null != response.snapshot) ? response.snapshot.isClear : isClear;
        // if (isClearConfirmed && entryMapIdx > 0)
        // {
        //     long stepCafeId = EventCharacterCafeHelper.GetCafeId();
        //     WrapWebManager.Instance.RequestCharacterCafeStepUpdate(stepCafeId, entryMapIdx, objectIdx, cb =>
        //     {
        //         if (cb.code != 0)
        //             DLogger.Error($"[CharacterCafe] 미니게임 완료 StepUpdate 실패 [{(ResponseError)cb.code}] : cafeId={stepCafeId}, step={entryMapIdx}, objectIdx={objectIdx}");
        //     });
        // }

        // [Stage3 2026-06-14] 미러 변이 제거 — 진입 오브젝트 Done/다음활성은 실서버 권위에서 파생한다:
        //  EventCharacterCafeHelper.BuildObjectDatasFromServerCache 가 miniGameList.snapshot.isClear 로 진입 오브젝트를 Done 보정하고,
        //  ActivateFrontierOnList 가 다음 오브젝트를 활성화한다. (맵 재구성은 ObjectClearedMsg → 메인 팝업 OnChangeObject 가 트리거)

        // 클라 권위 — 응답 echo 가 있으면 우선하되, 비어도 클라 계산값(isClear/nextRound)을 신뢰한다.
        return new ClearMiniGameResult
        {
            objectIdx = objectIdx,
            curRound  = response.snapshot?.curRound ?? nextRound,
            isClear   = (null != response.snapshot) ? response.snapshot.isClear : isClear,
            // 완료 위치 도우미 대기용 mapIdx — 응답 전 캡처한 진입 오브젝트(Active) 셀(미클리어면 0). 메인 팝업이 OnChangeObject 에 전달.
            mapIdx    = entryMapIdx,
        };
    }

    // RQCharacterCafeMiniGameUpdate 송신 → 응답 await(공통). isReset = 진행 초기화 송신 여부(기본 false).
    private async UniTask<RSCharacterCafeMiniGameUpdate> SendMiniGameUpdateAsync(int miniGameId, CharacterCafeMiniGameSnapshot patch, int objectIdx, string tag, bool isReset = false)
    {
        // [레벨 가드] 오픈 레벨 미달이면 MiniGameUpdate 를 송신하지 않는다(실패=null 로 종료해 호출 측이 연출 없이 처리).
        if (EventCharacterCafeHelper.IsBelowOpenLevel())
            return null;

        long cafeId = EventCharacterCafeHelper.GetCafeId();

        UniTaskCompletionSource<RSCharacterCafeMiniGameUpdate> tcs = new();
        WrapWebManager.Instance.RequestCharacterCafeMiniGameUpdate(cafeId, miniGameId, isReset, patch, cb =>
        {
            if (cb.code != 0)
            {
                DLogger.Error($"[CharacterCafe] MiniGameUpdate({tag}) 실패 [{(ResponseError)cb.code}] : cafeId={cafeId}, miniGameId={miniGameId}, objectIdx={objectIdx}");
                tcs.TrySetResult(null);
                return;
            }
            tcs.TrySetResult(cb.GetBody<RSCharacterCafeMiniGameUpdate>());
        });

        return await tcs.Task;
    }

    // [Stage3 2026-06-14] 미러 투영 제거 — 미니게임 진행 스냅샷은 실서버 권위(DataManagement miniGameList)에서 읽는다(GetMiniGameData).
    //  응답 핸들러(OnResponseCharacterCafeMiniGameUpdate → SetCharacterCafeMiniGame)가 DataManagement 를 갱신하므로 미러 투영은 불필요. 원본 보존 — 주석화.
    // private void ApplyServerMiniGameSnapshot(int objectIdx, CharacterCafeMiniGameSnapshot snapshot)
    // {
    //     if (null == snapshot) return;
    //
    //     CharacterCafeEventData model = EventCharacterCafeHelper.GetClientModel();
    //     if (null == model)
    //     {
    //         DLogger.Error($"[CharacterCafe] MiniGameUpdate 모델 없음 — 투영 생략 : objectIdx={objectIdx}");
    //         return;
    //     }
    //
    //     MiniGameModel data = model.miniGameDatas.Find(d => d.objectIdx == objectIdx);
    //     if (null == data)
    //     {
    //         data = new MiniGameModel { objectIdx = objectIdx };
    //         model.miniGameDatas.Add(data);
    //     }
    //
    //     data.curRound        = snapshot.curRound;
    //     data.isClear         = snapshot.isClear;
    //     data.keyFound        = snapshot.keyFound;
    //     data.balloonContents = (null != snapshot.balloonContents) ? new List<int>(snapshot.balloonContents) : new List<int>();
    //     data.balloonPopped   = (null != snapshot.balloonPopped) ? new List<bool>(snapshot.balloonPopped) : new List<bool>();
    // }

    // 현재 터뜨림 상태(balloonPopped)에 이번 슬롯을 더한 patch 배열을 만든다.
    //  [단일 송신 2026-06-17] 유일 호출처(RequestMiniGamePopAsync)가 #if false 로 비활성화돼 미사용 — 동일하게 비활성화. 원본 보존.
#if false
    private static bool[] BuildPoppedPatch(MiniGameModel data, int balloonIndex)
    {
        int count = (null != data && !data.balloonPopped.IsNullOrEmpty()) ? data.balloonPopped.Count : 0;
        // 슬롯 수를 모를 때(데이터 없음)는 인덱스+1 길이로 최소 구성.
        if (count <= balloonIndex)
            count = balloonIndex + 1;

        bool[] popped = new bool[count];
        if (null != data && !data.balloonPopped.IsNullOrEmpty())
        {
            for (int i = 0; i < data.balloonPopped.Count && i < count; i++)
                popped[i] = data.balloonPopped[i];
        }
        if (balloonIndex >= 0 && balloonIndex < count)
            popped[balloonIndex] = true;
        return popped;
    }
#endif

    // 열쇠 + 모든 히든 보상 공개 완료 여부 (snapshot 기준).
    // [ISSUE-66] 추첨/사전 배치 양 모드 공용 판정(AreAllRewardsCollected)에 위임 — 추첨 모드는 슬롯 검사가 성립하지 않는다(미터뜨림 = 미정).
    private static bool IsAllBalloonRewardsCollected(CharacterCafeMiniGameSnapshot snapshot)
    {
        if (null == snapshot?.balloonContents || null == snapshot.balloonPopped) return false;
        return AreAllRewardsCollected(snapshot.objectIdx, snapshot.curRound, snapshot.balloonContents, snapshot.balloonPopped);
    }

    // [소스 이원화 2026-06-14] 미니게임 진행 데이터는 접근자(실서버=DataManagement miniGameList, 치트=미러)에서 조회.
    private MiniGameModel FindMiniGameData(int objectIdx)
        => EventCharacterCafeHelper.GetMiniGameData(objectIdx);

    // [2026-06-14 공용화] objectIdx → objectEventIdx 해석은 EventCharacterCafeHelper.GetObjectMiniGameId 로 이전(서브/Fs 핸들러 공용). 원본 보존 — 주석화.
    // private static int ResolveObjectEventIdx(int objectIdx)
    // {
    //     if (TableManager.GetData(objectIdx, out EventCharacterCafeObjectTableData objectTable))
    //         return objectTable.objectEventIdx;
    //     return 0;
    // }

    #endregion

#if UNITY_EDITOR
    #region 치트 (에디터 전용) — 미니게임 강제 완료

    // [치트] 미니게임을 끝까지 강제 클리어한다(상호작용 진행). 최종 라운드 클리어 시 TryClearMiniGameAsync 가
    //  ObjectClearedMsg 를 통지 → 메인 팝업이 미니게임 팝업을 닫고 맵을 재구성하며, Content(OnMiniGameEntryObjectCleared)가
    //  진입 오브젝트 step 전환(StepUpdate)+포인트/미션 fan-out 까지 수행한다(일반 버튼 완료와 동일 경로).
    //  열쇠 발견(keyFound) 게이트는 거치지 않는다 — RequestMiniGameClearAsync 가 patch.keyFound=true 로 보내 서버가 수락한다.
    public async UniTask<bool> Cheat_ButtonCompleteAsync(int objectIdx)
    {
        int totalRounds = EventCharacterCafeHelper.GetMiniGameRounds(objectIdx)?.Count ?? 0;
        if (totalRounds <= 0)
        {
            DLogger.Error($"[CharacterCafe][치트] 미니게임 라운드 테이블 없음 — 완료 불가 : objectIdx={objectIdx}");
            return false;
        }

        // 라운드 수만큼(+여유) 반복하며 라운드를 강제 클리어/전진시킨다. 최종 라운드에서 isClear→ObjectClearedMsg 통지.
        for (int guard = 0; guard <= totalRounds + 2; guard++)
        {
            // 진행 데이터(레이아웃)가 없으면 먼저 라운드를 시작시킨다(신규 진입=isReset).
            MiniGameModel data = FindMiniGameData(objectIdx);
            if (null == data || data.balloonContents.IsNullOrEmpty())
                await TryStartMiniGameRoundAsync(objectIdx, isReset: (null == data));

            ClearMiniGameResult result = await TryClearMiniGameAsync(objectIdx);
            if (null == result)
            {
                DLogger.Error($"[CharacterCafe][치트] 미니게임 클리어 요청 실패 — 중단 : objectIdx={objectIdx}");
                return false;
            }

            if (result.isClear)
            {
                // 전체 완료 — 일반 흐름과 동일하게 다음 사이클 대비 진행 초기화(isReset)를 송신한다.
                await TryStartMiniGameRoundAsync(objectIdx, isReset: true);
                return true;
            }

            // 다음 라운드 레이아웃 생성 후 반복.
            await TryStartMiniGameRoundAsync(objectIdx);
        }

        DLogger.Error($"[CharacterCafe][치트] 미니게임 완료 루프 한도 초과 — 중단 : objectIdx={objectIdx}, totalRounds={totalRounds}");
        return false;
    }

    // [치트] 미니게임 라운드를 끝까지 강제 클리어하되 ObjectClearedMsg 통지/완료 후 진행 초기화(isReset)를 하지 않는다(데이터 전용).
    //  "마지막 직전까지 완료" 치트(Content)가 진입 오브젝트 step 전환(StepUpdate)을 직접 await 하기 위한 변형 —
    //  메시지 기반 fire-and-forget StepUpdate 에 의존하지 않아 루프가 결정적으로 다음 오브젝트로 진행한다.
    //  클리어 후 miniGameList.isClear=true 가 유지되므로 진입 오브젝트는 Done 으로 파생된다(완료 영속).
    public async UniTask<bool> Cheat_ForceClearRoundsAsync(int objectIdx)
    {
        int totalRounds = EventCharacterCafeHelper.GetMiniGameRounds(objectIdx)?.Count ?? 0;
        if (totalRounds <= 0)
        {
            DLogger.Error($"[CharacterCafe][치트] 미니게임 라운드 테이블 없음 — 완료 불가 : objectIdx={objectIdx}");
            return false;
        }

        for (int guard = 0; guard <= totalRounds + 2; guard++)
        {
            MiniGameModel data = FindMiniGameData(objectIdx);
            if (null == data || data.balloonContents.IsNullOrEmpty())
                await RequestMiniGameStartAsync(objectIdx, isReset: (null == data));

            ClearMiniGameResult result = await RequestMiniGameClearAsync(objectIdx); // ObjectClearedMsg 미통지
            if (null == result)
            {
                DLogger.Error($"[CharacterCafe][치트] 미니게임 클리어 요청 실패 — 중단 : objectIdx={objectIdx}");
                return false;
            }
            if (result.isClear)
                return true; // 진행 초기화(isReset) 생략 — isClear 유지로 진입 오브젝트 Done 파생.

            await RequestMiniGameStartAsync(objectIdx, false); // 다음 라운드 레이아웃 생성.
        }

        DLogger.Error($"[CharacterCafe][치트] 미니게임 완료 루프 한도 초과 — 중단 : objectIdx={objectIdx}, totalRounds={totalRounds}");
        return false;
    }

    #endregion
#endif

    public void Release()
    {
        _eventData = null;
    }
}

// 미니게임 서브컨텐츠 진행 모델 (구 MiniGameModel) — 오브젝트별 라운드/풍선/상점 진행 기록.
//  [이동·개명 2026-06-14] 서브컨텐츠가 소유하는 모델로 이관(MiniGameModel → MiniGameModel).
//   Fs 저장소(치트)·미러(CharacterCafeEventData)·서버 스냅샷 투영(GetMiniGameData)이 공용하는 데이터 구조.
//  ※ 단일 어셈블리라 전역 네임스페이스 top-level 로 두어 기존 참조(GameLogic Fs/미러, GameContents UI)를 그대로 유지한다.
[Serializable]
public class MiniGameModel
{
    public int objectIdx;                        // 연결된 오브젝트 (objectEventIdx)
    public int curRound;                         // 진행 라운드 (클리어한 라운드 수)
    public bool isClear;                         // 미니게임 클리어 여부
    public List<int> shopBuyCounts = new();      // 상품별 구매 횟수 (shopBuyLimit 체크, 배열 3) — 세션 내 카운트(메모리)
    public int shopBuyResetDateNum;              // 일일 구매제한 리셋 기준일 (yyyyMMdd, EnsureShopDailyReset 가 서버 날짜와 비교)
    public List<long> expirePurchaseTime = new(); // [세션 간 영속] 상품 슬롯별 구매 한도 만료 시각(Unix초). 스냅샷 포함 → 서버 영속. 현재시각 < 만료시각이면 한도 소진(재접속 유지)

    // 현재 라운드 풍선 레이아웃 (클라 생성 → 서버 저장·영속). 슬롯별 내용물:
    //   -1 = 열쇠 / 0 = 꽝 / 1~ = 히든 보상 (Event_Reward index)
    public List<int> balloonContents = new();
    public List<bool> balloonPopped = new();     // 슬롯별 터뜨림 여부
    public bool keyFound;                        // 현재 라운드 열쇠 발견 여부

    public MiniGameModel Clone()
    {
        MiniGameModel newData = new();
        newData.objectIdx           = this.objectIdx;
        newData.curRound            = this.curRound;
        newData.isClear             = this.isClear;
        newData.shopBuyCounts       = new(this.shopBuyCounts);
        newData.shopBuyResetDateNum = this.shopBuyResetDateNum;
        newData.expirePurchaseTime  = new(this.expirePurchaseTime);
        newData.balloonContents     = new(this.balloonContents);
        newData.balloonPopped       = new(this.balloonPopped);
        newData.keyFound            = this.keyFound;
        return newData;
    }
}

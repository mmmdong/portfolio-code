using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using Cysharp.Threading.Tasks;

using GameLogic;
using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.GameManagement;
using GameLogic.Management;
using GameLogic.Network;

using UnityEngine;

namespace ACTGames.Content.Helper
{
    /// <summary>
    /// 드림 벌룬 페스티벌 데이터 접근자 (구현 명세서 §7-1·§7-1a).
    /// 진행 상태(state 포함)의 단일 소스는 서버 캐시 <see cref="DataManager.EventBalloonInfo"/> 이며,
    /// 테이블(Setting/AiRound)·재화는 각 매니저에서 조회한다. (좌석/순위는 클라 시뮬 §7-3)
    /// </summary>
    public sealed class EventDreamBalloonHelper
    {
        public static LiveEventData GetLiveEventData()
        {
            return EventDataHelper.GetLiveEventData(LiveEventType.DREAMBALLOON);
        }

        public static long GetEventSeq()
        {
            return GetLiveEventData()?.eventID ?? 0;
        }

        public static int GetLoadTableIdx()
        {
            return GetLiveEventData()?.GetLoadTableIndex() ?? 0;
        }

        // ── 서버 진행 정보(EventBalloonInfo) — 상태의 단일 소스 ────────────────────────────
        /// <summary>
        /// 서버 진행 정보(state·난이도·라운드·roundStartAt) 캐시 조회.
        ///
        /// 🔴 [ISSUE-28] **시퀀스 이벤트(같은 타입 연속 편성) stale 방지 — eventSeq 스코핑.**
        /// <see cref="DataManager.EventBalloonInfo"/> 는 eventSeq 로 구분되지 않는 **단일 전역 캐시**라(<c>DataManager.LiveEvent.cs</c>),
        /// 이전 시퀀스(seq1)의 상태가 남아 있으면 현재 활성 시퀀스(seq2)의 상태로 오독된다.
        /// 특히 seq1 종료로 전역 <c>state==4</c> 가 찍히면 <see cref="EventDataHelper.IsActiveEvent"/>(= <c>!IsEnded()</c>, 서버 state 기준)가
        /// seq2 에 대해서도 거짓이 되어 **새 이벤트 아이콘까지 사라진다**(오늘의 안내/월드맵/HUD — ISSUE-28 재발 현상).
        /// 캐시된 <c>info.eventSeq</c> 가 현재 리졸브된 이벤트 ID 와 다르면 **다른 시퀀스의 stale 정보**이므로 신뢰하지 않고
        /// '미로드(=state 0=활성)'로 취급한다 — seq2 의 <c>RQEventBalloonInfo</c> 응답이 캐시를 갱신하면 정상 복구된다(RequestInfoAsync).
        /// 두 ID 가 모두 확보된 경우에만 판정한다 — 서버가 eventSeq 를 안 실어주거나(0)·이벤트 미확보(0)면 기존 동작 유지(회귀 방지).
        /// </summary>
        public static EventBalloonInfoPacketData GetServerInfo()
        {
            EventBalloonInfoPacketData info = DataManager.Instance.EventBalloonInfo;
            if (null == info)
            {
                return null;
            }

            long curEventId = GetLiveEventData()?.eventID ?? 0;
            if (curEventId > 0 && info.eventSeq > 0 && info.eventSeq != curEventId)
            {
                return null;
            }

            return info;
        }

        /// <summary>
        /// 🔴 [ISSUE-28] **시퀀스 이벤트 전환 시 stale 전역 캐시 정리 — 서버 독립 방어.**
        /// <see cref="EventDataHelper.RemoveLiveEventCaches"/>(로그인 종료 시퀀스·타이머 종료가 모두 거치는 공통 choke)에서
        /// 제거되는 이벤트가 DREAMBALLOON 일 때 호출된다. 전역 <see cref="DataManager.EventBalloonInfo"/> 는 eventSeq 로 구분되지 않는
        /// 단일 캐시라, 이전 시퀀스(seq1)의 종료 상태(<c>state 4</c>, <c>ApplyEventEndLocally</c> 로 클라가 직접 기록)가 남아 다음
        /// 시퀀스(seq2)의 아이콘/진입 판정(<see cref="EventDataHelper.IsActiveEvent"/> = <c>!IsEnded()</c>)을 오염시킨다.
        /// 제거되는 이벤트(seq1)의 것이거나 소유 불명(eventSeq&lt;=0, 서버 미채움)인 캐시면 비운다 —
        /// seq2 의 <c>RQEventBalloonInfo</c> 응답이 새로 채우기 전까지 '미로드=활성' 으로 취급된다.
        /// eventSeq 가 다른(이미 로드된 seq2) 캐시는 보존한다(중첩 편성 대비). <see cref="GetServerInfo"/> 의 eventSeq 스코핑과 이중 방어.
        /// </summary>
        public static void ClearStaleServerInfoOnRemove(long removedEventId)
        {
            EventBalloonInfoPacketData info = DataManager.Instance.EventBalloonInfo;
            if (null != info && (info.eventSeq == removedEventId || info.eventSeq <= 0))
            {
                DataManager.Instance.ResetEventBalloonCaching();
            }
        }

        // 선조회 응답 대기 상한(초) — 아래 RequestInfoAsync 참조.
        private const float REQUEST_INFO_TIMEOUT_SEC = 5f;

        /// <summary>
        /// 서버 진행상태(`RQEventBalloonInfo`) 선조회 — **로비 진입(로그인) 시점**용(<c>LobbySceneSequencer.RequestInfoPackets</c>).
        ///
        /// 🔴 **왜 필요한가**: 진입 버튼(HUD·머지)과 「오늘의 안내」 노출은 <see cref="EventDataHelper.IsActiveEvent"/> → 서버 `state` 로
        /// 판정하는데, 컨텐츠 자체 조회(<c>ContentEventDreamBalloon.Initialize</c> → <c>RequestInfo</c>)는 **로비 빌드 이후**에 일어난다.
        /// 그 사이 캐시(<c>DataManager.EventBalloonInfo</c>)가 비어 있어 <see cref="GetState"/> 가 0(미시작)을 반환하고,
        /// **이미 끝낸 이벤트가 로그인 직후 버튼·안내에 그대로 노출**된다(실측: 로그인 13:42:00 → 최초 Info 13:43:38, 98초 격차).
        /// 카페·인형뽑기가 같은 이유로 이 타이밍에 선조회한다(<c>EventCharacterCafeHelper</c> / <c>EventClawHelper.RequestInfoAsync</c>).
        ///
        /// 응답 캐싱은 전역 등록 핸들러(<c>OnResponseEventBalloonInfo</c> → <c>DataManager.SetEventBalloonInfo</c>)가 담당하므로
        /// 본 메서드는 **송신·대기만** 한다(인형뽑기와 동일 분담). 컨텐츠가 이미 생성돼 있으면 그 경로로 보내
        /// <c>SyncFromServer</c>(모델 재구성 · 아이콘/재화 메시지)까지 이어지게 한다.
        ///
        /// ⚠️ **로비 시퀀스를 막지 않는다** — 선조회는 표시 정확도를 위한 것이지 진입 관문이 아니다. 무응답 시
        /// <see cref="REQUEST_INFO_TIMEOUT_SEC"/> 후 그대로 진행한다(응답이 늦게 와도 캐시는 정상 갱신된다).
        /// eventSeq 미확보(이벤트 미운영) 시 송신하지 않는다(graceful no-op).
        /// </summary>
        public static async UniTask RequestInfoAsync(CancellationToken token = default)
        {
            // 이벤트가 없거나 기간이 아니면 **송신하지 않는다**(인형뽑기 EventClawHelper·카페 EventCharacterCafeHelper 와 동일 가드).
            //   완료(IsCompleteEvent)는 예외로 통과시킨다 — 미수령 보상·종료 팝업 판정에 최신 state 가 필요하다.
            //   ⚠️ eventSeq 만으로는 부족하다: 이벤트 데이터가 남아 있으면 기간이 끝났거나 미운영이어도 seq 는 양수라 그대로 나갔다.
            LiveEventData liveData = GetLiveEventData();
            if (!EventDataHelper.IsValidData(liveData) && !EventDataHelper.IsCompleteEvent(liveData))
            {
                return;
            }

            long eventSeq = GetEventSeq();
            if (eventSeq <= 0)
            {
                return;
            }

            UniTaskCompletionSource tcs = new();

            ContentEventDreamBalloon content = GetContent();
            if (null != content)
            {
                content.RequestInfo(() => tcs.TrySetResult());
            }
            else
            {
                // 컨텐츠 미생성(활성 게이트 등) — 패킷만 보내 캐시를 채운다. 등록 핸들러가 DataManager 에 반영한다.
                WrapWebManager.Instance.RequestEventBalloonInfo(eventSeq, _ => tcs.TrySetResult());
            }

            await UniTask.WhenAny(
                tcs.Task,
                UniTask.Delay(TimeSpan.FromSeconds(REQUEST_INFO_TIMEOUT_SEC), DelayType.Realtime, PlayerLoopTiming.Update, token));
        }

        // EventBalloonInfo.state (§8-1) — 0 미시작 / 1 쉬어가기(쉬는중) / 2 진행중 / 3 클리어 / 4 종료
        public const int REST_STATE = 1;
        public const int PROGRESS_STATE = 2;
        public const int CLEAR_STATE = 3;
        public const int END_STATE = 4;     // 최종 보상 수령 후 종료

        /// <summary>
        /// 라운드 클리어(3) = **한 라운드를 통과해 다음 라운드를 기다리는 상태**. 매 라운드 성공마다 서버가 내려준다.
        ///
        /// ⚠️ **'이벤트 완주'가 아니다.** 클라는 오랫동안 3 을 "최종 라운드까지 클리어"로 해석했는데, 실측 결과
        ///   1라운드 성공에도 `state:3 / currentRound:2` 가 내려왔다(서버 스펙 확인 2026-07-19).
        ///   그 오해 때문에 파트너가 숨겨지고(ShowPartnerAtStage 조기 return), 재진입 시 최종 보상 팝업으로 새는 문제가 있었다.
        ///   완주 판정은 <see cref="IsCleared"/>(= 라운드 진행도까지 함께 본다)를 쓸 것.
        /// </summary>
        public static bool IsRoundCleared()
        {
            return GetState() == CLEAR_STATE;
        }

        /// <summary>
        /// **이벤트 완주** = 최종 라운드까지 클리어해 최종 연출·보상 팝업 진입을 기다리는 상태(§5-1 #5).
        ///
        /// 서버 `state` 만으로는 알 수 없다(3 은 매 라운드 뜬다) → 진행 라운드로 판정한다.
        /// 라운드 N 클리어 시 서버가 `currentRound = N+1` 로 올려주므로(실측), 최종 라운드까지 깼다면 `currentRound > totalRound` 다.
        /// 테이블 미로드(totalRound 0)면 false — 완주로 오판해 최종 보상이 새는 것을 막는다.
        /// </summary>
        public static bool IsCleared()
        {
            if (!IsRoundCleared())
            {
                return false;
            }

            int totalRound = GetTotalRound();
            return totalRound > 0 && GetCurrentRound() > totalRound;
        }

        // 종료(4) = 최종 보상까지 끝난 상태. 진입 버튼을 내린다(§3-8).
        // ⚠️ **최종 보상 획득 직후 클라도 이 상태를 만든다**(2026-07-19, ContentEventDreamBalloon.ApplyEventEndLocally) —
        //    서버가 state 4 를 안 내려줘도 화면이 완료로 정리되게 하기 위함. 재접속 시 RQEventBalloonInfo 값이 최종 기준이므로
        //    데이터 권위는 여전히 서버에 있다(로컬 전이는 응답 전까지의 화면 정합용).
        public static bool IsEnded()
        {
            return GetState() == END_STATE;
        }

        /// <summary>
        /// 이벤트 완주(최종 보상 획득 + 종료 팝업 확인) 시 — 표준 완료 게이트를 통과시키기 위해 **전 보상행을 '표시만' Receive 로 보정**한다(ISSUE-23).
        ///
        /// ⚠️ **전제 갱신(2026-07-19)**: 아래 서술은 "보상 아이템이 5·10 라운드에만 있다"를 전제로 쓰였으나, **2026-07-16 CSV 재발행으로
        /// 10개 라운드 전부 보상이 발행**되어 <c>GrantRoundReward</c> 가 전 라운드를 수령 마킹한다 → 완주 시 공용 완료 판정이 **스스로 참이 된다**.
        /// 그로 인해 진입이 막히던 문제는 <see cref="EventDataHelper.IsActiveEvent"/> 의 DREAMBALLOON 분기(완료 = 서버 <c>state 4</c>)로 해소했고,
        /// **진입 게이트는 더 이상 이 보정에 의존하지 않는다.** 이 메서드는 표준 보상 데이터 정합(표시용) 보정으로만 남는다.
        ///
        /// 드림 벌룬은 보상 아이템이 5·10 라운드에만 있어(§2-2), 그 두 행만 <c>GrantRoundReward</c>(HasStageReward 게이트, §3-4 ⑨)로 수령 마킹된다.
        /// 그런데 공용 완료 판정 <see cref="LiveEventData.IsCompleteEvent"/> → <c>IsRewardAllRecievedCondition</c> 은 **그룹 전 행(10개) 수령**을 요구하므로
        /// (드림 벌룬 보상행의 conditionType 은 None — <c>EventTypeHelper.GetRoundChangeTargetCondition</c> 에 DREAMBALLOON case 부재),
        /// 8개 관문 행이 미수령으로 남아 완료가 성립하지 않는다 → <c>IsActiveEvent</c> 가 계속 true → **최종 보상 후에도 아이콘이 안 사라지고 재진입이 열린다.**
        ///
        /// 표준 컨텐츠(<c>EventClawHelper.MarkAllRoundRewardsReceived</c> · Carrot 완주 시 전 티어 표시)와 동일하게 **완주 시점**에 전 행을 Receive 로 보정한다.
        /// **재지급 없음** — 실제 지급은 이미 <c>GrantRoundReward</c>(공용 <c>CompleteEventRewards</c>, §8-5)가 수행했다. 여기서는 게이트용 상태 표시만.
        /// 완주 시점(종료 팝업 확인)에만 호출해야 한다 — 진행 중 호출하면 완료로 찍혀 진입이 막힌다(구 #35 회귀).
        /// [재접속 durability] 런타임 LiveEventData 뿐 아니라 Fs 백업(FsLiveEventData.RewardData)까지 보정해 재접속 후에도 유지한다.
        /// </summary>
        public static void MarkAllRewardsReceived()
        {
            LiveEventData liveData = GetLiveEventData();
            if (!EventDataHelper.IsExistEventData(liveData) || null == liveData.rewardData)
            {
                return;
            }

            List<EventRewardInfo> infoes = liveData.rewardData.GetAllRewards();
            if (infoes.IsNullOrEmpty())
            {
                return;
            }

            // 1) 런타임 LiveEventData 표시(현재 세션 완료 게이트) — 대상 rewardIndex 도 함께 수집.
            List<int> targetRewardIndices = new();
            foreach (EventRewardInfo info in infoes)
            {
                if (null == info)
                {
                    continue;
                }
                info.recieveState = EventRewardState.Receive;
                targetRewardIndices.Add(info.rewardIndex);
            }

            // 2) Fs 백업 표시(재접속 durability) — 없으면 추가, 있으면 상태만 갱신(재지급/재화 지급 없음).
            FsLiveEventData fsLiveEventData = EventCommonHelper.GetFsLiveEventData(LiveEventType.DREAMBALLOON);
            if (null == fsLiveEventData || null == fsLiveEventData.RewardData)
            {
                return;
            }

            foreach (int rewardIndex in targetRewardIndices)
            {
                FsEventRewardInfo fsInfo = fsLiveEventData.RewardData.EventRewardInfoes.Find(x => x.RewardIndex == rewardIndex);
                if (null == fsInfo)
                {
                    fsInfo = new FsEventRewardInfo { RewardIndex = rewardIndex };
                    fsLiveEventData.RewardData.EventRewardInfoes.Add(fsInfo);
                }
                fsInfo.RecieveState = EventRewardState.Receive;
            }

            EventCommonHelper.SetFsLiveEventData(fsLiveEventData);
        }

        // ── 보상 수령 기록 (§8-2 `RQEventBalloonRewardClaim`) ─────────────────────────────
        // 패킷 정의: rewardType **1 = 단계 보상**(round N) / **2 = 최종 보상**(round **0**).
        public const int REWARD_TYPE_STAGE = 1;
        public const int REWARD_TYPE_FINAL = 2;

        /// <summary>
        /// 해당 보상을 이미 수령했는지 — **서버 `claimedRewardList` 가 단일 소스**다(§8-2).
        /// `RQEventBalloonInfo` / `RQEventBalloonRewardClaim` 응답이 이 목록을 갱신한다(`DataManager.EventBalloonClaimedRewards`).
        /// </summary>
        public static bool IsRewardClaimed(int rewardType, int round)
        {
            EventBalloonRewardPacketData[] claimed = DataManager.Instance.EventBalloonClaimedRewards;
            if (claimed.IsNullOrEmpty())
            {
                return false;
            }

            int count = claimed.Length;
            for (int i = 0; i < count; i++)
            {
                EventBalloonRewardPacketData reward = claimed[i];
                if (null != reward && reward.rewardType == rewardType && reward.round == round)
                {
                    return true;
                }
            }

            return false;
        }

        public static int GetState()
        {
            return GetServerInfo()?.state ?? 0;
        }

        // 쉬는중 = state == 1 (§7-1a)
        public static bool IsResting()
        {
            return GetState() == REST_STATE;
        }

        /// <summary>
        /// 재화 획득 가능 상태 — 이벤트 활성(미완료) + 라운드 진행중(state == 2).
        /// 쉬는중(state 1)·미시작(0)·클리어(3)·종료(4)에는 코인을 획득하지 않는다(기획 §5-3 "휴식 중 미션 달성 미획득").
        /// 드림 벌룬은 라운드를 자체 패킷(EventBalloonInfo)으로 관리해 제네릭 roundStartTime 을 세팅하지 않으므로,
        /// 표준 <see cref="EventDataHelper.IsStartRound"/> 대신 이 판정을 사용한다.
        /// </summary>
        public static bool IsRoundProgressing()
        {
            // 🔴 [ISSUE-17] 벌룬 완료 판정은 **서버 state** 가 단일 소스다(§3-8) — 공용 보상기반 IsCompleteEvent 를 쓰지 않는다.
            //   공용 IsCompleteEvent(→ LiveEventData.IsCompleteEvent = 그룹 전 보상행 수령)는 2026-07-16 CSV 재발행으로 10라운드
            //   전부 보상이 발행된 뒤 GrantRoundReward/MarkAllRewardsReceived 가 매 라운드 수령 마킹을 남겨 **완주 시 스스로 참**이 되고,
            //   그 상태가 Fs 백업으로 영속돼 **종료>재시작(같은 eventSeq) 시 갓 시작한 이벤트에도 남는다** → state 가 서버상 진행중(2)이어도
            //   IsCompleteEvent 참 → IsRoundProgressing 거짓 → 재화 게이지가 미노출됐다(showGauge 게이트, DreamBalloonStageItem).
            //   IsActiveEvent 가 같은 이유로 벌룬만 !IsEnded()(서버 state 4) 로 특수처리(EventDataHelper.cs:579)된 것과 동일 원칙.
            //   여기서는 IsActiveEvent(종료 배제) + GetState()==2(진행중) 로 충분하다 — 보상 수령 마킹은 완료 권위가 아니다.
            if (!EventDataHelper.IsActiveEvent(LiveEventType.DREAMBALLOON))
            {
                return false;
            }

            return GetState() == PROGRESS_STATE;
        }

        /// <summary>
        /// 현재 라운드의 클리어 조건 도달 — 진행중(state 2) + 현재 구름의 목표 코인 달성(= 진급 가능).
        /// HUD 레드닷(기획 §3-2 「2) 버튼」 "플레이 가능할 때 레드닷 출력")과 머지 버튼 글로우가 공유하는 단일 판정.
        /// </summary>
        public static bool IsRoundGoalReached()
        {
            if (!IsRoundProgressing())
            {
                return false;
            }

            int goalCoin = GetGoalCoin(GetCurrentRound());
            return goalCoin > 0 && GetCurrentCoin() >= goalCoin;
        }

        // 라운드 클리어(성공) 확정 후 아직 팝업 진입·확인 전 — 아이콘/레드닷을 진행중 상태로 유지하기 위한 판정(ISSUE-24).
        // 자동 성공확정 직후 서버 state 가 쉬는중(1)으로 내려와도 '진급 대기'이므로 아직 쉬는 상태가 아니다.
        public static bool IsRoundClearPending()
        {
            return GetContent()?.HasPendingRoundClear ?? false;
        }

        // 라운드 실패(좌석 소진) 확정 후 아직 팝업 진입·확인 전 — 머지 버튼 레드닷(MergeCheck) 노출 판정.
        // 실패는 머지판(화면 밖)에서 확정되므로 유저에게 재진입(재시작/쉬어가기 선택)을 알려야 한다.
        // 성공 계열(목표 도달·클리어)은 완료 버튼(Btn_Ok)이 담당하므로 레드닷과 배타적이다(ISSUE-21).
        public static bool IsRoundFailPending()
        {
            return GetContent()?.HasPendingRoundFail ?? false;
        }

        // 난이도 인코딩 — 패킷·테이블·프리팹이 <b>같은 값</b>을 쓴다(서버·클라 통일 확정 2026-09-04).
        //  변환 지점이 하나도 없는 것이 계약이다 — 프리팹 인스펙터에 박힌 int 가 RQEventBalloonStart 까지 그대로 간다.
        //  ⚠️ enum 이 아니라 const int 인 데는 이유가 있다. 이 값은 ①UIDreamBalloonDifficultyButton 의
        //     [SerializeField] int 로 <b>프리팹에 직렬화</b>돼 있고, ②AiRound·RewardGroup 조회의 딕셔너리 키이며,
        //     ③패킷 필드가 int 다. enum 으로 바꾸면 세 경계 모두에 캐스팅이 생겨 "변환 0회" 계약이 흐려지고
        //     프리팹 재저작까지 필요해진다. 같은 파일의 상태 상수(REST_STATE 계열)도 같은 성격이라 const int 다.
        public const int DIFFICULTY_EASY = 1;
        public const int DIFFICULTY_NORMAL = 0;
        public const int DIFFICULTY_HARD = 2;
        // 미지정 — 난이도가 아직 정해지지 않은 상태. 0 이 보통이라 0 을 센티널로 쓸 수 없어 음수를 쓴다.
        //  ⚠️ UI 로컬 표현이 아니라 <b>서버와 공유하는 계약값</b>이다 — 서버도 이 값을 내려줄 수 있다.
        //
        //  <b>미지정으로는 난이도 테이블을 조회하지 않는다.</b> 아래 조회 함수들이 이 값을 만나면 시도조차 하지 않는데,
        //  막는 것이 둘이다:
        //   ① TableManager 의 난이도 그룹 조회는 미매칭을 <c>DLogger.Error</c> 로 남긴다. "아직 난이도를 모른다" 는
        //      정상 상태라 그 로그는 거짓 경보이고, 로그인 직후 창에서 반복해서 뜬다.
        //   ② <see cref="GetRewardRow"/> 의 레거시 폴백은 <b>난이도를 보지 않는다</b>. 미지정이 거기까지 흘러가면
        //      엉뚱한 난이도의 보상 행이 실제 값처럼 돌아온다 — 종전 0 폴백이 늘 "보통" 행을 돌려주던 것과 같은 종류의 거짓말이다.
        //  미지정에서 옳은 답은 <b>"값 없음"</b> 이지 다른 난이도의 값이 아니다.
        public const int DIFFICULTY_UNSPECIFIED = -1;

        /// <summary>
        /// 서버가 보관 중인 선택 난이도. 진행 정보가 아직 없으면 <see cref="DIFFICULTY_UNSPECIFIED"/>.
        ///
        /// ⚠️ 폴백이 <b>보통(0)이 아니다.</b> 종전에는 정보 부재 시 0 을 돌려줬는데, 0 은 "값 없음"이 아니라
        /// <b>보통</b>이라 미지정이 보통으로 오독됐다 — 로그인 직후처럼 진행 정보를 아직 못 받은 창에서
        /// 좌석·목표코인·최종보상 조회가 <b>보통 난이도 행을 실제 값처럼</b> 돌려줬다.
        /// 미지정으로 돌려주면 그 조회들이 <see cref="DIFFICULTY_UNSPECIFIED"/> 가드에서 끊겨 null/0 이 되고,
        /// 호출부가 "아직 모른다" 를 구분할 수 있다.
        /// </summary>
        public static int GetDifficulty()
        {
            return GetServerInfo()?.difficulty ?? DIFFICULTY_UNSPECIFIED;
        }

        /// <summary>
        /// 현재 난이도 — **패킷·테이블이 같은 인코딩(1쉬움 / 0보통 / 2어려움 / -1미지정)** 이다
        /// (2026-07-20 통일 → 2026-09-04 서버·클라 합의로 미지정 추가).
        ///
        /// 구 구조는 패킷(1쉬움/2보통/3어려움)과 테이블(`Event_DreamBalloon_AiRound.difficulty` = 1/0/2)이 달라
        /// 경계마다 변환(`ToTableDifficulty`)이 필요했고, 로그의 숫자와 CSV 의 숫자가 달라 오독을 유발했다.
        /// 서버는 난이도를 해석하지 않고 **클라가 등록한 값을 그대로 보관**하므로(스토리지), 테이블 인코딩으로 통일했다.
        ///
        /// ⚠️ **`0` 은 보통이며 "미선택"이 아니다.** 미선택 판정은 `state == 0`(미시작, §7-1a) 으로 한다.
        ///    난이도 자체의 미지정은 <see cref="DIFFICULTY_UNSPECIFIED"/> 로 표현한다 — 서버와 공유하는 계약값이다.
        /// ⚠️ 드림 벌룬은 `Event_Setting.difficultSet` 을 **쓰지 않는다**(2026-07-20 기획 확정) — 이벤트 인덱스가 13101 단일 행이 되면서
        ///    그 컬럼으로는 난이도를 구분할 수 없다. 난이도 뱃지도 활성 행이 아니라 선택 난이도로 구동한다(UILiveEventLevel.SetDifficult).
        ///
        /// <b>이 래퍼를 남겨 두는 이유</b> — 지금은 <see cref="GetDifficulty"/> 를 그대로 돌려주므로 동작상 차이가 없다.
        /// 그래도 이름을 유지하는 것은 <b>단 하나의 호출부</b>(메인 팝업의 난이도 뱃지) 때문이다. 바로 위 ⚠️ 가 말하는
        /// 혼동 — 활성 <c>Event_Setting</c> 행의 난이도인가, 유저가 <b>선택한</b> 난이도인가 — 을 이름으로 못 박는다.
        /// </summary>
        public static int GetTableDifficulty()
        {
            return GetDifficulty();
        }

        /// <summary>
        /// [ISSUE-06] 현재 난이도를 **트로피 챌린지 조건값 스케일**(Condition_Setting.conditionValue)로 변환한 값.
        ///
        /// 내부 인코딩(1쉬움 / 0보통 / 2어려움)은 난이도 순서와 무관한 배치라 **대소 비교가 성립하지 않는다**
        /// (0보통 &lt; 1쉬움). 기획 정의는 1=쉬움 / 2=보통 / 3=어려움 의 **오름차순** 스케일이고
        /// TrophyChallengeManager.IsValidMissionGroup 이 "이 난이도 이상" 으로 범위 비교하므로 여기서 변환해 넘긴다.
        /// (0 = 난이도 무관 — 미션 쪽 조건값 전용이라 여기서는 "판정 불가" 폴백으로만 쓰인다.)
        /// </summary>
        public static int GetMissionDifficultyValue()
        {
            return ToMissionDifficultyValue(GetDifficulty());
        }

        // 난이도 <b>인코딩</b>(1쉬움 / 0보통 / 2어려움) → 트로피 조건값 <b>스케일</b>(1쉬움 / 2보통 / 3어려움).
        //
        // <b>두 체계가 따로 있는 이유</b> — 인코딩은 식별용이라 순서가 없다(0보통 &lt; 1쉬움). 반면 트로피는
        //  TrophyChallengeManager.IsValidMissionGroup 이 "달성난이도 >= conditionValue" 로 <b>범위 비교</b>를 하므로
        //  오름차순 스케일이 필요하다. 이 함수가 그 사이의 어댑터다.
        //  ⚠️ 인코딩 통일(2026-09-04)로 없어질 함수가 아니다 — 인코딩을 1/0/2 로 <b>확정</b>했다는 것은
        //     순서 없는 배치를 영구히 유지하겠다는 뜻이라, 비교가 필요한 쪽에는 앞으로도 변환이 있어야 한다.
        //  ⚠️ 출력 1/2/3 이 2026-07-20 이전의 구 패킷 인코딩(1쉬움/2보통/3어려움)과 <b>숫자가 같다.</b>
        //     그래도 이 스케일의 출처는 구 패킷이 아니라 기획의 <c>Condition_Setting.conditionValue</c> 다 —
        //     TrophyChallengeManager.IsValidMissionGroup 이 그 값과 비교한다(MissionHelper 주석도 같은 테이블을 지목).
        //     숫자가 겹친 경위까지는 코드로 확인할 수 없으나, <b>구 인코딩 잔재로 오해해 지우면 트로피 판정이 무너진다.</b>
        public static int ToMissionDifficultyValue(int difficulty)
        {
            switch (difficulty)
            {
                case DIFFICULTY_EASY:   return 1;
                case DIFFICULTY_NORMAL: return 2;
                case DIFFICULTY_HARD:   return 3;

                // 미지정 — <b>정의된 계약값</b>이라 default 에 맡기지 않고 명시한다. 반환값은 아래와 같지만
                //  이유가 다르다: 이쪽은 "난이도를 아직 모른다", 아래는 "정의되지 않은 값이 들어왔다".
                case DIFFICULTY_UNSPECIFIED: return 0;

                // 0 = "난이도 무관(cv=0)" 미션만 충족시키고 난이도 지정 미션은 통과시키지 않는 보수적 폴백.
                //  난이도를 모르는 채로 지정 미션을 부당 충족시키는 것보다 안전하다.
                //  발행부(ContentEventDreamBalloon)도 이 값이 0 이면 진척을 아예 발행하지 않는다.
                default: return 0;
            }
        }

        public static int GetCurrentRound()
        {
            return GetServerInfo()?.currentRound ?? 0;
        }

        public static long GetRoundStartAt()
        {
            return GetServerInfo()?.roundStartAt ?? 0;
        }

        /// <summary>
        /// 라운드 시작 시, 캐시된 roundStartAt 을 현재 서버 시각으로 로컬 갱신한다(ISSUE-18).
        /// 좌석 시뮬(§7-3)은 roundStartAt 기준 경과시간으로 잔여 좌석을 결정론적으로 재현하는데,
        /// 서버/FakeServer 가 RoundStart 에서 roundStartAt 을 갱신하지 않으면(FakeServer 미처리) stale(과거) 값이 남아
        /// 시작·재시작 시 좌석이 즉시 0 으로 소진돼 실패 연출이 재생된다. state 로컬 오버라이드(ApplyLocalState)와 같은 철학.
        /// ※ 재접속 결정성은 서버가 roundStartAt 을 저장/반환해야 보장된다(서버 확인 대상).
        /// </summary>
        public static void MarkRoundStartNow()
        {
            EventBalloonInfoPacketData info = GetServerInfo();
            if (null == info)
            {
                return;
            }

            info.roundStartAt = DataManager.Instance.GetCurrentIntTimeStamp();
        }

        public static int GetLastRoundResult()
        {
            return GetServerInfo()?.lastRoundResult ?? 0;
        }

        // ── 테이블(Setting / AiRound) ─────────────────────────────────────────────────────
        // 쉬는중 안내(§7-4) 파라미터 미발행 시 폴백 기본값 — 기획 §5-5 예시값 기준.
        private const int DEFAULT_REST_NOTIFY_GAP_MIN = 30;
        private const int DEFAULT_REST_NOTIFY_MAX_COUNT = 2;
        private const int DEFAULT_FIRST_NOTIFY_DELAY_MIN = 60;   // 조건1 최초 노출 게이트(분) — 쉬어가기 진입 후 1시간(테이블 rest_Popup_Time 미발행 시 폴백)
        private const string DEFAULT_REST_ICON_PATH = "MergeEventDreamBalloon_Rest";
        private static readonly int[] EMPTY_SEQUENCE_GROUPS = new int[0];

        // 쉬는중 안내 조건 임계값 — MyDreamPartner_Sequence.conditionValue 에서 주입(§2-7·§7-4). 미발행 시 폴백.
        private const int DEFAULT_REST_REQUIRED_ENERGY = 80;    // 조건1: 남은 에너지
        private const int DEFAULT_REST_USED_ENERGY = 150;       // 조건2: 누적 사용 에너지
        private const string CONDITION_TYPE_ITEM = "item";                 // 그룹 39 — 보유 재화 비교
        private const string CONDITION_TYPE_VALUECHECK = "valuecheck";     // 그룹 40 — 이벤트가 전달한 값 비교 (qa 발행 명칭)
        private const string CONDITION_TYPE_CONTEXTVALUE = "ContextValue"; // 그룹 40 동일 의미의 신 명칭 — 파트너 콘텐츠 enum 정비로 develop CSV 가 이 표기로 발행됨. 양쪽 수용(불일치 시 조건2 발화가 토스트 폴백으로 새는 실측 이슈)

        // Event_DreamBalloon_Setting 1건(loadTableIdx 그룹). 미발행 시 null → 각 접근자가 기본값 폴백.
        public static EventDreamBalloon_SettingData GetSetting()
        {
            return TableManager.Instance.GetDreamBalloonSetting(GetLoadTableIdx());
        }

        // 안내 팝업 재노출 대기 시간(분) — round_CountRestTime.
        public static int GetRestNotifyGapMinutes()
        {
            EventDreamBalloon_SettingData setting = GetSetting();
            if (null == setting || setting.round_CountRestTime <= 0)
            {
                return DEFAULT_REST_NOTIFY_GAP_MIN;
            }

            return setting.round_CountRestTime;
        }

        // 안내 팝업 조건1 최초 노출 게이트(분) — rest_Popup_Time. 쉬어가기 진입 후 이 시간이 지나야 조건1 최초 노출(재노출은 round_CountRestTime 간격). 미발행/0 이면 폴백(1시간).
        public static int GetRestFirstNotifyDelayMinutes()
        {
            EventDreamBalloon_SettingData setting = GetSetting();
            if (null == setting || setting.rest_Popup_Time <= 0)
            {
                return DEFAULT_FIRST_NOTIFY_DELAY_MIN;
            }

            return setting.rest_Popup_Time;
        }

        // 라운드당 최대 노출 횟수 — round_Count.
        public static int GetRestNotifyMaxCount()
        {
            EventDreamBalloon_SettingData setting = GetSetting();
            if (null == setting || setting.round_Count <= 0)
            {
                return DEFAULT_REST_NOTIFY_MAX_COUNT;
            }

            return setting.round_Count;
        }

        /// <summary>
        /// 이벤트 종료까지 남은 시간 타이머 배선(기획 §3-2 시작·난이도선택·진행·성공·실패 화면 공통).
        /// 이후 갱신은 <see cref="UILiveEventTimer"/> 가 LiveEventManager.OnRefreshTimerHandle 로 자동 수행하므로,
        /// 대상 이벤트만 지정하고 1회 즉시 표기한다(오픈 직후 다음 틱 전까지 빈칸 방지).
        /// </summary>
        public static void BindEventTimer(UILiveEventTimer timer)
        {
            timer.SetEventType(LiveEventType.DREAMBALLOON);
            timer.RefreshTimer();
        }

        // 진행중 버튼 아이콘(§3-8) — HUD·머지 공용. 쉬는중 아이콘은 테이블(icon_rest) 발행값을 쓴다(GetRestIconPath).
        public const string PROGRESS_ICON_PATH = "MergeEventDreamBalloon_Progress";

        /// <summary>
        /// 지금 진입 버튼에 보여야 할 아이콘 경로 — **아이콘 판정의 단일 소스**.
        ///
        /// `_Rest` 는 '쉬는 상태'일 때만이며, **결과 확정 후 팝업으로 확인하기 전(HasPendingRoundResult)** 에는
        /// 서버 state 가 쉬는중(1)이어도 `_Progress` 를 유지한다(성공·실패 공통 — 상세는 <c>ContentEventDreamBalloon.SendIconRefresh</c>).
        ///
        /// push(상태 전이 시 <c>OnRefreshEventIconMsg</c>)와 pull(버튼 생성·갱신 시 <c>UILiveEventItem.RefreshUIItem</c>) 양쪽이
        /// 이 함수를 공유한다. 메시지는 **이미 존재하는 버튼**에만 닿으므로, 나중에 만들어진 버튼은 pull 로 같은 값을 얻어야
        /// 상태와 어긋나지 않는다(쉬는중에 머지판을 새로 열면 authored 스프라이트가 그대로 남는 문제).
        /// </summary>
        public static string GetCurrentIconPath()
        {
            bool hasPendingResult = GetContent()?.HasPendingRoundResult ?? false;
            // 라운드 사이 '대기' = 쉬어가기(state 1) 또는 정착된 라운드 클리어(state 3, 완주 아님).
            //  서버는 라운드 성공마다 state 3 을 내려주므로(스펙 2026-07-19) IsResting()(state 1) 만 보면
            //  재접속 시 아이콘이 _Progress 로 남는다(ISSUE-29). 팝업 RefreshRestView(IsResting || IsRoundCleared)와 동일 관용구.
            //  완주(IsCleared)는 완료 버튼(Btn_Ok) 노출 상태이므로 rest 아이콘 대상에서 제외한다.
            bool waitingNextRound = IsResting() || (IsRoundCleared() && !IsCleared());
            return waitingNextRound && !hasPendingResult ? GetRestIconPath() : PROGRESS_ICON_PATH;
        }

        // 쉬는중 안내/버튼 아이콘 — icon_rest.
        public static string GetRestIconPath()
        {
            EventDreamBalloon_SettingData setting = GetSetting();
            if (null == setting || string.IsNullOrEmpty(setting.icon_rest))
            {
                return DEFAULT_REST_ICON_PATH;
            }

            return setting.icon_rest;
        }

        // ── 마이드림파트너 (§3-2·§4-3·§7-5) ────────────────────────────────────────
        // 파트너 미설정 시 기본 캐릭터 — 1101(헬로키티). Character_Main(ActorTableData) index.
        public const int DEFAULT_PARTNER_ID = 1101;

        // 4-3 경쟁자 모집 연출 표시 친구 수(유저 파트너 제외)의 상한 — 기획 확정 2026-07-19로 **9**(구 기획서 표기 10에서 하향).
        // 이 값은 구름 프리팹(EventDreamBalloonItem)의 포트레이트 자리 수(portraitVecList·PortraitPanel 자식 = 9)와 같아야 한다.
        // 자리보다 크면 DreamBalloonStageItem.ShownPortraitCount 가 조용히 잘라내 테이블 의도와 화면이 어긋난다.
        public const int MAX_RECRUIT_PROFILE_COUNT = 9;

        // 구 계산식 계수(8) — profileCount 미발행 시 폴백 전용. 정상 경로는 테이블이 권위다(GetRecruitFriendCount).
        private const int RECRUIT_BASE_FRIEND_COUNT = 8;

        // 파트너 모션 그룹(MyDreamPartner_Motion.motionGroup) — 실제 스파인 클립명은 테이블이 보유한다.
        // 클립명을 코드에 적지 않는 이유: 실제 클립은 "Raise/Motion_Cheer_Up" 처럼 경로형이라 하드코딩 시 Animation not found 로 끊긴다.
        public const int MOTION_GROUP_SUCCESS = 304;   // Raise/Motion_Happy_3 — ⚠️ 기획 미표기, 임시(§3-6)
        public const int MOTION_GROUP_FAIL = 306;      // Raise/Motion_Cheer_Up — 기획 명시(챕터 실패 팝업 ⑤)
        public const int MOTION_GROUP_FINAL = 326;     // Raise/Motion_Hello_Happy_1 — "오늘의 안내"(안내_좋아함, §3-7)

        // §4 트랙 연출(메인 팝업 진행 파트너) 전용 모션 그룹 — 기획서 920223827 §4-4~4-7. 클립명은 테이블이 보유(MyDreamPartner_Motion).
        public const int MOTION_GROUP_STAGE_SUCCESS = 304;   // Raise/Motion_Happy_3 — §4-4 단계 성공(팝업 성공과 동일 클립)
        public const int MOTION_GROUP_STAGE_FAIL = 310;      // Raise/Motion_Hurry_Up — §4-5 단계 실패(기획 정정 2026-07-20, 구 313 Motion_Sullen)
        public const int MOTION_GROUP_REST = 105;            // Emotion_Sleep — §4-6 쉬어가기(자는 연출)
        public const int MOTION_GROUP_RESTART = 314;         // Raise/Motion_Surprise_Happy — §4-7 쉬어가기 후 재시작(놀람)

        /// <summary>
        /// 이벤트 화면에 그릴 파트너 캐릭터 idx. **파트너 미설정(0)이면 기본 캐릭터(1101)로 대체**한다.
        /// 파트너를 캡처하지 않고 표시 시점에 실시간 조회한다(§7-5).
        /// </summary>
        public static int GetPartnerId()
        {
            int partnerId = MyDreamPartnerHelper.CurrentPartnerId;
            return partnerId > 0 ? partnerId : DEFAULT_PARTNER_ID;
        }

        /// <summary>
        /// 모션 그룹 → 현재 파트너의 (스킨, 스파인 클립명). 미발행 시 (null, null) → 호출부는
        /// <see cref="CharacterAnimationController.SetupAndPlayAsync"/> 의 startingAnimation·Front 폴백에 위임한다.
        /// (참조 구현: MyDreamPartnerHelper.ResolvePartnerPopupMotion)
        /// </summary>
        public static (string skin, string motion) GetPartnerMotion(int motionGroup)
        {
            MyDreamPartnerMotionTableData motion = TableManager.Instance.GetMyDreamPartnerMotion(motionGroup, GetPartnerId());
            return null == motion ? (null, null) : (motion.charSkin, motion.motionName);
        }

        /// <summary>
        /// 4-3 경쟁자 모집 연출에서 열기구에서 내리는 친구 수(유저 파트너 제외 — 내 파트너는 이 계산과 무관하게 항상 출력).
        ///
        /// **테이블 권위**(기획서 920223827 §4-3, 2026-07-19 발행): <c>Event_DreamBalloon_AiRound.profileCount</c>.
        /// 발행값은 라운드별 10·10·9·9·8·8·7·7·6·5(난이도 3종 동일).
        /// 상한은 <see cref="MAX_RECRUIT_PROFILE_COUNT"/>(9, 기획 확정 2026-07-19) — 재발행 전 1~2라운드의 10 은 여기서 9 로 걸린다.
        ///
        /// 미발행(행 없음/0)일 때만 구 계산식으로 폴백한다 — max(1, ceil(8 × (총 단계 − 현재 단계) / (총 단계 − 1))).
        /// 폴백을 남기는 이유: 컬럼 누락·헤더 오타로 테이블이 통째로 안 읽히면(구 <c>loadTableldx</c> 사례) 연출에서
        /// 경쟁자가 전부 사라져 조용히 망가진다. 그 경우 최소한 예전 동작으로 버틴다.
        /// </summary>
        public static int GetRecruitFriendCount(int curRound, int totalRound)
        {
            EventDreamBalloon_AiRoundData aiRound = GetAiRound(curRound);
            if (null != aiRound && aiRound.profileCount > 0)
            {
                return Mathf.Min(aiRound.profileCount, MAX_RECRUIT_PROFILE_COUNT);
            }

            if (totalRound <= 1)
            {
                return 0;   // 단계가 1개뿐이면 0 나눗셈 방어 — 친구 없음
            }

            int remainRound = Mathf.Clamp(totalRound - curRound, 0, totalRound - 1);
            return Mathf.Max(1, Mathf.CeilToInt((float)RECRUIT_BASE_FRIEND_COUNT * remainRound / (totalRound - 1)));
        }


        // 쉬는중 안내 시퀀스 그룹 — dreamballoon_Sequence (MyDreamPartner_Sequence.sequenceGroup). 미발행 시 빈 배열.
        public static int[] GetRestSequenceGroups()
        {
            EventDreamBalloon_SettingData setting = GetSetting();
            return setting?.dreamballoon_Sequence ?? EMPTY_SEQUENCE_GROUPS;
        }

        // 쉬는중 안내 조건1 — 남은 에너지 임계값. MyDreamPartner_Sequence 그룹 39(conditionType=item, conditionValue=80).
        public static int GetRestRequiredEnergy()
        {
            return GetRestConditionValue(byUsedEnergy: false, DEFAULT_REST_REQUIRED_ENERGY);
        }

        // 쉬는중 안내 조건2 — 누적 사용 에너지 임계값. MyDreamPartner_Sequence 그룹 40(conditionType=valuecheck/ContextValue, conditionValue=150).
        public static int GetRestUsedEnergyThreshold()
        {
            return GetRestConditionValue(byUsedEnergy: true, DEFAULT_REST_USED_ENERGY);
        }

        /// <summary>
        /// 쉬는중 안내창에 사용할 <b>시퀀스 그룹</b>(§2-4 `dreamballoon_Sequence` = 39,40 → §2-7).
        /// 조건1(남은 에너지, conditionType=item) = 39 / 조건2(누적 사용 에너지, valuecheck/ContextValue) = 40 이며,
        /// 어떤 조건으로 발화했는지에 따라 해당 그룹을 돌려준다(그룹 내 대사·모션은 마이드림파트너가 랜덤 선택).
        /// 미발행 시 0 — 호출부는 안내창을 띄우지 않는다.
        /// </summary>
        public static int GetRestSequenceGroup(bool byUsedEnergy)
        {
            return GetRestConditionGroup(byUsedEnergy);
        }

        /// <summary>
        /// 쉬는중 안내창에 실을 시퀀스 행 — 그룹 내 랜덤 1개(현재 발행은 그룹당 1행). 미발행 시 null(호출부 토스트 폴백).
        /// 큐에 **사전 선택(preset)으로 전달**해 조건 재평가를 건너뛴다 — 그룹 40 의 `ContextValue` 조건은
        /// 큐 경로로 판정값을 실어보낼 수 없어 재평가 시 항상 탈락한다(발화 판정은 노티파이어가 §7-4 에서 이미 완료).
        /// </summary>
        public static MyDreamPartnerSequenceTableData GetRestSequenceRow(int sequenceGroup)
        {
            IReadOnlyList<MyDreamPartnerSequenceTableData> rows = TableManager.Instance.GetMyDreamPartnerSequencesByGroup(sequenceGroup);
            if (null == rows || rows.Count == 0)
            {
                return null;
            }

            return rows[UnityEngine.Random.Range(0, rows.Count)];
        }

        /// <summary>
        /// 쉬는중 안내 그룹 해석 실패(0) 시 원인 추적용 상태 덤프 — Setting 조회·그룹 목록·그룹별 conditionType.
        /// 실측: 세팅/시퀀스 조회가 발화 순간에만 일시 실패해 그룹 0 이 나오는 사례가 있어(원인 미확정) 진단 로그로 남긴다.
        /// </summary>
        public static string GetRestGroupDiagnostics()
        {
            EventDreamBalloon_SettingData setting = GetSetting();
            if (null == setting)
            {
                return $"setting:NULL loadTableIdx:{GetLoadTableIdx()}";
            }

            TableManager tableManager = TableManager.Instance;
            int[] groups = GetRestSequenceGroups();
            StringBuilder sb = new StringBuilder($"loadTableIdx:{GetLoadTableIdx()} groups:[{string.Join(",", groups)}]");

            int groupCount = groups.Length;
            for (int i = 0; i < groupCount; i++)
            {
                IReadOnlyList<MyDreamPartnerSequenceTableData> rows = tableManager.GetMyDreamPartnerSequencesByGroup(groups[i]);
                if (null == rows)
                {
                    sb.Append($" {groups[i]}:rowsNULL");
                    continue;
                }

                sb.Append($" {groups[i]}:");
                int rowCount = rows.Count;
                for (int j = 0; j < rowCount; j++)
                {
                    sb.Append($"{(j > 0 ? "|" : string.Empty)}{rows[j]?.conditionType ?? "null"}");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 조건 타입 문자열 매칭 — 조건1 = `item` / 조건2 = `valuecheck`(qa 발행 명칭)·`ContextValue`(develop 신 명칭) **양쪽 수용**.
        /// 파트너 콘텐츠 enum 정비로 환경마다 표기가 달라, 한쪽만 보면 조건2 발화가 시퀀스 그룹 0 → 토스트 폴백으로 샌다(실측).
        /// </summary>
        private static bool MatchesRestConditionType(string conditionType, bool byUsedEnergy)
        {
            if (!byUsedEnergy)
            {
                return string.Equals(conditionType, CONDITION_TYPE_ITEM, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(conditionType, CONDITION_TYPE_VALUECHECK, StringComparison.OrdinalIgnoreCase)
                || string.Equals(conditionType, CONDITION_TYPE_CONTEXTVALUE, StringComparison.OrdinalIgnoreCase);
        }

        // dreamballoon_Sequence 그룹들 중 해당 조건 타입의 행을 가진 그룹 번호. 미일치 시 0.
        private static int GetRestConditionGroup(bool byUsedEnergy)
        {
            int[] groups = GetRestSequenceGroups();
            int groupCount = groups.Length;
            TableManager tableManager = TableManager.Instance;

            for (int i = 0; i < groupCount; i++)
            {
                IReadOnlyList<MyDreamPartnerSequenceTableData> rows = tableManager.GetMyDreamPartnerSequencesByGroup(groups[i]);
                if (null == rows)
                {
                    continue;
                }

                int rowCount = rows.Count;
                for (int j = 0; j < rowCount; j++)
                {
                    MyDreamPartnerSequenceTableData row = rows[j];
                    if (null != row && MatchesRestConditionType(row.conditionType, byUsedEnergy))
                    {
                        return groups[i];
                    }
                }
            }

            return 0;
        }

        // dreamballoon_Sequence 가 가리키는 그룹들을 순회해 해당 조건 타입 첫 행의 conditionValue 를 반환.
        // 미발행·미일치 시 기본값 폴백(§7-0 방어 철학).
        private static int GetRestConditionValue(bool byUsedEnergy, int defaultValue)
        {
            int[] groups = GetRestSequenceGroups();
            int groupCount = groups.Length;
            TableManager tableManager = TableManager.Instance;

            for (int i = 0; i < groupCount; i++)
            {
                IReadOnlyList<MyDreamPartnerSequenceTableData> rows = tableManager.GetMyDreamPartnerSequencesByGroup(groups[i]);
                if (null == rows)
                {
                    continue;
                }

                int rowCount = rows.Count;
                for (int j = 0; j < rowCount; j++)
                {
                    MyDreamPartnerSequenceTableData row = rows[j];
                    if (null == row || row.conditionValue <= 0)
                    {
                        continue;
                    }

                    if (MatchesRestConditionType(row.conditionType, byUsedEnergy))
                    {
                        return row.conditionValue;
                    }
                }
            }

            return defaultValue;
        }

        // 총 라운드 = AiRound 난이도 그룹의 행 수 (Setting 구 스키마의 totalRound 폐기 → AiRound 에서 파생).
        public static int GetTotalRound()
        {
            return GetTotalRound(GetDifficulty());
        }

        // 지정 난이도의 총 라운드 — 난이도 선택 팝업처럼 **아직 고르지 않은 난이도**의 최종 보상을 미리 볼 때 쓴다.
        public static int GetTotalRound(int difficulty)
        {
            var aiRounds = GetAiRounds(difficulty);
            return aiRounds?.Count ?? 0;
        }

        // AiRound 는 loadTableIdx(이벤트 회차) > 난이도(테이블 기준) 2단 그룹(§5-5). 현재 난이도로 조회.
        //  loadTableIdx 는 Setting 과 동일하게 활성 이벤트(Event_Setting.loadTableIdx)에서 가져오므로 호출부는 몰라도 된다.
        public static List<EventDreamBalloon_AiRoundData> GetAiRounds()
        {
            return GetAiRounds(GetDifficulty());
        }

        // 지정 난이도(1쉬움 / 0보통 / 2어려움 — 패킷·테이블 공통 인코딩)로 조회.
        public static List<EventDreamBalloon_AiRoundData> GetAiRounds(int difficulty)
        {
            // 미지정 — 조회하지 않는다(근거는 DIFFICULTY_UNSPECIFIED 선언부).
            if (DIFFICULTY_UNSPECIFIED == difficulty)
            {
                return null;
            }

            return TableManager.Instance.GetDreamBalloonAiRounds(GetLoadTableIdx(), difficulty);
        }

        public static EventDreamBalloon_AiRoundData GetAiRound(int round)
        {
            return GetAiRound(round, GetDifficulty());
        }

        public static EventDreamBalloon_AiRoundData GetAiRound(int round, int difficulty)
        {
            // 미지정 — 조회하지 않는다(근거는 DIFFICULTY_UNSPECIFIED 선언부).
            if (DIFFICULTY_UNSPECIFIED == difficulty)
            {
                return null;
            }

            return TableManager.Instance.GetDreamBalloonAiRound(GetLoadTableIdx(), difficulty, round);
        }

        // ── 좌석(§7-3 클라 권위) ──────────────────────────────────────────────────────────

        /// <summary>
        /// 현재 라운드 좌석 정원(§2-5 `slotMax`). 좌석 시뮬이 돌고 있으면 시뮬 값,
        /// 미구동(미시작·쉬는중)이면 테이블 값으로 폴백한다. 미발행 시 0(표시부 no-op).
        /// </summary>
        public static int GetSlotMax()
        {
            ContentEventDreamBalloon content = GetContent();
            int simSlotMax = null != content ? content.SlotMax : 0;
            if (simSlotMax > 0)
            {
                return simSlotMax;
            }

            // 미시작(state 0)이면 currentRound 가 0 으로 내려온다 → 1단계 기준으로 보정.
            EventDreamBalloon_AiRoundData aiRound = GetAiRound(Mathf.Max(1, GetCurrentRound()));
            return null != aiRound ? aiRound.slotMax : 0;
        }

        /// <summary>
        /// 현재 라운드 잔여 좌석(§7-3). 시뮬 미구동이면 아직 아무도 앉지 않은 상태 = 정원.
        /// </summary>
        public static int GetRemainSeat()
        {
            ContentEventDreamBalloon content = GetContent();
            return null != content && content.SlotMax > 0 ? content.RemainSeat : GetSlotMax();
        }

        // ── 재화 / 목표 ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// 이 라운드에 획득한 코인. 지갑(itemIdx 224)이 곧 "현재 라운드 획득량"이다 —
        /// 라운드 시작·실패·통과·이벤트 종료 시 <see cref="ResetRoundCoin"/> 으로 지갑을 0 으로 비우기 때문이다(기획 §2 정책).
        /// </summary>
        public static int GetCurrentCoin()
        {
            return EventCurrencyHelper.GetEventCurrency(LiveEventType.DREAMBALLOON);
        }

        /// <summary>
        /// 열기구 코인(itemIdx 224)을 0 으로 소각한다 — 기획 §2 「실패 시 현재 층 누적 코인 초기화」·「이벤트 종료 시 보유 재화 초기화」.
        /// 재화 권위는 클라(FakeServer = 내장 원장)이므로 음수 SetRewardItem 으로 차감한다(Carrot 입장 코인 차감과 동일 패턴).
        /// 지갑 원장 → 세이브 → UI 미러 순으로 갱신해야 Q키 검증(VerificationItems)에서 서버/클라가 어긋나지 않는다. 멱등(0 이면 no-op).
        /// </summary>
        public static void ResetRoundCoin()
        {
            AddCoin(-GetCurrentCoin());
        }

        /// <summary>열기구 코인 가감(양수 지급 / 음수 차감). 0 이면 no-op.</summary>
        public static void AddCoin(int value)
        {
            if (0 == value)
            {
                return;
            }

            LiveEventData liveData = GetLiveEventData();
            if (!EventDataHelper.IsExistEventData(liveData))
            {
                return;
            }

            int currencyIndex = liveData.GetEventCurrencyIndex();   // Event_Setting.itemIdx(= CurrencyType 224)
            if (currencyIndex <= 0)
            {
                return;
            }

            FsWebManager.GetProcess<FsProcessCommon>().SetRewardItem(ItemType.Currency, currencyIndex, value, LogItemTriggerType.None, 0);
            FsWebManager.Instance.SaveDataStorage_PlayInfo(DataSaveType.Server);

            // UI 미러 동기화. EventCurrencyHelper.RefreshEventCurrency 는 만료·완료 이벤트에서 조기 반환하므로 쓰지 않는다 —
            // 이벤트 종료 시 리셋(§2 정책)에서 미러만 갱신되지 않아 지갑 원장과 어긋나기 때문이다(Q키 VerificationItems 불일치).
            DataManager.Instance.SynchronizeCurrency((CurrencyType)currencyIndex, false);
        }

        // 해당 구름(단계)의 목표 코인 = Event_RewardGroup.goalValue2 (goalValue1 == round). 미발행 시 0.
        // 현재 난이도 기준 해당 구름(단계)의 목표 코인.
        public static int GetGoalCoin(int round)
        {
            return GetGoalCoin(round, GetDifficulty());
        }

        // 지정 난이도의 해당 구름(단계) 목표 코인 = 그 라운드 보상 행의 goalValue2. 미발행 시 0.
        public static int GetGoalCoin(int round, int difficulty)
        {
            return GetRewardRow(round, difficulty)?.goalValue2 ?? 0;
        }

        /// <summary>
        /// 난이도별 보상/목표 그룹(rewardGroup). difficulty = 1쉬움 / 0보통 / 2어려움(패킷·테이블 공통).
        ///
        /// **난이도 구분의 축 = `Event_DreamBalloon_AiRound.difficulty`** 이므로, 그룹 번호도 그 난이도의 보상 행에서 읽는다.
        /// (`Event_Setting` 은 더 이상 난이도를 구분하지 않는다 — 아래 <see cref="GetRewardRow"/> 주석 참조.)
        /// 그룹 번호 자체가 필요한 곳은 없고(행 직접 조회로 대체), 구 경로 폴백을 위해 남겨 둔다.
        /// </summary>
        public static int GetRewardGroup(int difficulty)
        {
            // 미지정 — 조회하지 않는다. 아래 폴백도 난이도를 보지 않는다(근거는 DIFFICULTY_UNSPECIFIED 선언부 ②).
            if (DIFFICULTY_UNSPECIFIED == difficulty)
            {
                return 0;
            }

            EventRewardGroupTableData row = FindRewardRowByAiRound(1, difficulty);
            if (null != row && row.rewardGroup > 0)
            {
                return row.rewardGroup;
            }

            // 폴백: AiRound 미발행(치트 주입 등) → 활성 이벤트 행의 rewardGroup.
            var liveEventData = GetLiveEventData();
            if (null == liveEventData || !TableManager.GetData(liveEventData.typeIndex, out EventSettingTableData activeSetting))
            {
                return 0;
            }

            return activeSetting.rewardGroup;
        }

        // 최종 보상 행 = 지정 난이도의 마지막 라운드(totalRound) 보상 행.
        public static EventRewardGroupTableData GetFinalRewardRow(int difficulty)
        {
            int totalRound = GetTotalRound(difficulty);
            if (totalRound <= 0)
            {
                return null;
            }

            return GetRewardRow(totalRound, difficulty);
        }

        /// <summary>
        /// 지정 라운드·난이도의 보상 행.
        ///
        /// **[2026-07-20 리팩토링] 난이도 구분의 단일 축 = `Event_DreamBalloon_AiRound.difficulty`.**
        /// 이벤트 인덱스가 난이도별 3행(13101/13102/13103)에서 **13101 하나**로 바뀌었고,
        /// **`Event_Setting.difficultSet` 은 이 컨텐츠에서 쓰지 않기로 확정**됐다(그 행의 값은 0 이며 난이도와 무관하다).
        /// 즉 `Event_Setting` 만으로는 난이도를 구분할 수 없다(구 구현은 전 난이도가 쉬움 그룹 33101 을 조회하게 된다).
        /// → AiRound 의 난이도 그룹에서 해당 라운드 행을 찾고, 그 행의 **`roundReward`(= `Event_RewardGroup.index` 직결 FK)**
        ///   로 보상 행을 **직접** 조회한다. 인덱스 인코딩 규칙(`rewardGroup*100+round`)에도 의존하지 않는다.
        ///
        /// 폴백: `roundReward` 미발행(구 CSV·치트 주입)이면 기존 그룹 스캔 경로를 그대로 탄다.
        /// </summary>
        public static EventRewardGroupTableData GetRewardRow(int round, int difficulty)
        {
            // 미지정 — 조회하지 않는다. <b>아래 폴백이 난이도를 보지 않으므로 특히 여기서 끊어야 한다</b>
            //  (근거는 DIFFICULTY_UNSPECIFIED 선언부 ②).
            if (DIFFICULTY_UNSPECIFIED == difficulty)
            {
                return null;
            }

            EventRewardGroupTableData row = FindRewardRowByAiRound(round, difficulty);
            if (null != row)
            {
                return row;
            }

            // 폴백: 구 경로(난이도 그룹 스캔) — roundReward 미발행 CSV·에디터 치트 주입 대비.
            var liveEventData = GetLiveEventData();
            if (null == liveEventData || !TableManager.GetData(liveEventData.typeIndex, out EventSettingTableData activeSetting)
                || activeSetting.rewardGroup <= 0)
            {
                return null;
            }

            var rows = TableManager.Instance.GetEventRewardData(activeSetting.rewardGroup);
            if (rows.IsNullOrEmpty())
            {
                return null;
            }

            return rows.Find(r => r.goalValue1 == round);
        }

        // AiRound(난이도, 라운드) → roundReward FK → Event_RewardGroup 행 1건. 미발행·미해석 시 null(호출측이 폴백).
        private static EventRewardGroupTableData FindRewardRowByAiRound(int round, int difficulty)
        {
            EventDreamBalloon_AiRoundData aiRound = GetAiRound(round, difficulty);
            if (null == aiRound || aiRound.roundReward <= 0)
            {
                return null;
            }

            return TableManager.GetData(aiRound.roundReward, out EventRewardGroupTableData row) ? row : null;
        }

        /// <summary>
        /// 메타베이스 라운드 결과 로그(ISSUE-25 · Confluence 965935140) — 라운드 클리어/실패를 **난이도별 이벤트명**으로 발신한다.
        ///
        /// 매핑은 표준 관례 그대로다(Carrot `Define.Constans.cs` 리전 주석 · Doughnut/CookingBook/MakeCake 구현 일치):
        ///   · <b>Label</b> = `Event_RewardGroup.rewardGroup`  · <b>Action</b> = `Event_RewardGroup.index`
        ///   · <b>Value</b> = 문서 공란이라 **미사용**(타 이벤트는 goalValue1 을 싣는다)
        ///   · <b>Level</b> = 넣지 않는다 — <see cref="AnalyticsManager.CustomSendEvent"/> 가 모든 로그에 유저 레벨을 자동 부착한다.
        ///
        /// 보상 행이 없으면(테이블 미발행) 발신하지 않는다 — Label/Action 이 비면 집계가 무의미하다.
        /// </summary>
        public static void SendRoundResultLog(int round, int difficulty, bool success)
        {
            EventRewardGroupTableData rewardRow = GetRewardRow(round, difficulty);
            if (null == rewardRow)
            {
                return;
            }

            string eventName = ResolveRoundResultLogName(difficulty, success);
            if (string.IsNullOrEmpty(eventName))
            {
                return;
            }

            AnalyticsManager.AnalyticsEventData data = new();
            data.AddLabel(rewardRow.rewardGroup.ToString());
            data.AddAction(rewardRow.index.ToString());
            AnalyticsManager.CustomSendEvent(eventName, data);
        }

        // 난이도(1쉬움 / 0보통 / 2어려움 — 패킷·테이블 공통) × 결과 → 메타베이스 이벤트명. 미정의 난이도는 빈 문자열(발신 안 함).
        private static string ResolveRoundResultLogName(int difficulty, bool success)
        {
            switch (difficulty)
            {
                case 1:
                    return success ? AnalyticsEventName.EVENT_DREAMBALLOON_CLEAR_EASY : AnalyticsEventName.EVENT_DREAMBALLOON_FAIL_EASY;
                case 0:
                    return success ? AnalyticsEventName.EVENT_DREAMBALLOON_CLEAR_NORMAL : AnalyticsEventName.EVENT_DREAMBALLOON_FAIL_NORMAL;
                case 2:
                    return success ? AnalyticsEventName.EVENT_DREAMBALLOON_CLEAR_HARD : AnalyticsEventName.EVENT_DREAMBALLOON_FAIL_HARD;
                default:
                    return string.Empty;
            }
        }

        // 지정 라운드에 즉시 지급할 단계 보상이 있는지(itemIdx 중 0 아닌 항목 존재, §3-4 ⑨).
        public static bool HasStageReward(int round, int difficulty)
        {
            var row = GetRewardRow(round, difficulty);
            if (null == row || row.itemIdx.IsNullOrEmpty())
            {
                return false;
            }

            foreach (var idx in row.itemIdx)
            {
                if (0 != idx)
                {
                    return true;
                }
            }

            return false;
        }

        // 보상 목록 공통 바인딩(§7-5) — 보상 행의 itemType/itemIdx/itemValue 배열을 CommonRewardCheckItem 슬롯에 채운다.
        // itemIdx==0 항목은 건너뛰고, 채우지 못한 나머지 슬롯은 숨긴다. items·row 미바인딩 시 안전.
        public static void BindRewards(CommonRewardItem[] items, EventRewardGroupTableData row)
        {
            if (items.IsNullOrEmpty())
            {
                return;
            }

            int slot = 0;
            if (null != row && null != row.itemType && null != row.itemIdx && null != row.itemValue)
            {
                int count = row.itemType.Length;
                for (int i = 0; i < count && slot < items.Length; i++)
                {
                    if (0 == row.itemIdx[i])
                    {
                        continue;
                    }

                    var item = items[slot];
                    slot++;
                    if (null == item)
                    {
                        continue;
                    }

                    item.gameObject.SetActive(true);
                    // 뱃지(BadgePack)/블록/시설 보상은 인포 버튼 자동 활성 + 터치 시 정보 말풍선(OnClickShowInfo) — 다른 컨텐츠 보상 슬롯 표준 패턴.
                    //   clickAction 을 넘기지 않으면 BadgePack 은 UpdateButtonState 에서 핸들러가 배선되지 않아 뱃지 말풍선이 뜨지 않는다.
                    item.SetInfoAutoHideInfo(row.itemType[i], row.itemIdx[i], row.itemValue[i],
                        clicked => CommonRewardItem.OnClickShowInfo(clicked, null));
                }
            }

            for (int i = slot; i < items.Length; i++)
            {
                if (null != items[i])
                {
                    items[i].gameObject.SetActive(false);
                }
            }
        }

        // 최종 보상 목록 바인딩(현재/지정 난이도) — GetFinalRewardRow + BindRewards 편의 래퍼.
        public static void BindFinalRewards(CommonRewardItem[] items, int difficulty)
        {
            BindRewards(items, GetFinalRewardRow(difficulty));
        }

        // ── 컨텐츠 접근자 · 팝업 오픈 (구현 명세서 §5-1 팝업 활성화 시퀀스) ──────────────────
        // 컨텐츠 로직 인스턴스(요청/판정 위임). 팝업 버튼 → Request* 호출 경로.
        public static ContentEventDreamBalloon GetContent()
        {
            return EventContentBaseHelper.GetEventContent(LiveEventType.DREAMBALLOON) as ContentEventDreamBalloon;
        }

        private static ContentEventDreamBalloon.DreamBalloonModel GetModel()
        {
            return GetContent()?.Model;
        }

        // [표준] 주소=클래스명으로 정렬된 팝업은 타입-오픈(OpenUIMsgAsync<T>) 사용 — 기존 LiveEvent(Carrot 등) 동일 관례.
        // 나머지(_Info/_Select/_Success/_Fail/_Final/_End)는 주소에 언더스코어가 남아 아직 이름-오픈(PREFAB_PATH).
        public static void OpenStartPopup()
        {
            var info = new UIPopupEventDreamBalloonStart.Info { model = GetModel() };
            UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonStart.PREFAB_PATH, typeof(UIPopupEventDreamBalloonStart), info).Forget();
        }

        public static void OpenInfoPopup()
        {
            var info = new UIPopupEventDreamBalloonInfo.Info { model = GetModel() };
            UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonInfo.PREFAB_PATH, typeof(UIPopupEventDreamBalloonInfo), info).Forget();
        }

        public static void OpenSelectPopup()
        {
            var info = new UIPopupEventDreamBalloonSelect.Info { model = GetModel() };
            UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonSelect.PREFAB_PATH, typeof(UIPopupEventDreamBalloonSelect), info).Forget();
        }

        public static void OpenMainPopup()
        {
            OpenMainPopupAsync().Forget();
        }

        // 메인 팝업 오픈(인스턴스 반환) — 오픈 직후 형제 순서 조정 등 후처리가 필요한 호출부용(§3-3 커튼 전환).
        public static UniTask<UIBase> OpenMainPopupAsync()
        {
            var info = new UIPopupEventDreamBalloon.Info { model = GetModel() };
            return UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloon.PREFAB_PATH, typeof(UIPopupEventDreamBalloon), info);
        }

        public static void OpenSuccessPopup(StageResultMsg result)
        {
            var info = new UIPopupEventDreamBalloonSuccess.Info
            {
                model = GetModel(),
                round = result?.round ?? 0,
                rank = result?.rank ?? 0,
                upperPercent = result?.upperPercent ?? 0,
            };
            UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonSuccess.PREFAB_PATH, typeof(UIPopupEventDreamBalloonSuccess), info).Forget();
        }

        /// <summary>
        /// 라운드 성공 결과 제시(2026-07-16 규칙): **성공 연출(다음 구름 N+1 도착) 후 → N 라운드 보상 공용 획득 팝업 → 닫힘 후 성공 팝업.**
        /// 클리어한 라운드 N 의 보상(goalValue1==N)을 공용 획득 팝업(<see cref="UIPopupRewardResult"/>, "보상 획득")으로 노출한다.
        /// 이 팝업은 성공 연출로 **다음 구름(N+1)에 도착한 직후** 뜨므로, 기획 "N 보상은 N+1 구름에서 노출" 을 충족한다.
        /// 보상이 없으면(미발행 등) 공용 팝업 없이 곧바로 성공 팝업(<see cref="OpenSuccessPopup"/>)으로 이어진다.
        /// 최종(마지막 라운드) 성공은 이 경로를 타지 않고 _Final 이 최종 보상(goalValue1 마지막)을 담당한다.
        /// 보상 **데이터 지급**은 이미 클리어 시점에 처리된다(<c>ContentEventDreamBalloon.GrantRoundReward</c>). 여기서는 **표시**만 담당한다.
        /// </summary>
        public static void OpenRoundRewardThenSuccess(StageResultMsg result)
        {
            int round = result?.round ?? 0;

            RewardInfoData rewardInfoData = new();

            // 클리어한 라운드 N 의 보상(goalValue1==N)을 노출한다(지급된 것과 동일 행).
            EventRewardGroupTableData rewardRow = GetRewardRow(round, GetDifficulty());
            if (null != rewardRow && !rewardRow.itemType.IsNullOrEmpty())
            {
                int slotCount = rewardRow.itemType.Length;
                for (int i = 0; i < slotCount; ++i)
                {
                    if (0 == rewardRow.itemType[i] || 0 == rewardRow.itemIdx[i])
                    {
                        continue;   // 빈 슬롯(트레일링 0) 스킵
                    }

                    rewardInfoData.AddRewardInfo(new RewardInfo((ItemType)rewardRow.itemType[i], rewardRow.itemIdx[i], rewardRow.itemValue[i]));
                }
            }

            // 보상이 있으면 공용 획득 팝업 → 닫힘 후 성공 팝업. 없으면 OpenCommonRewardPopup 이 곧바로 콜백을 실행한다.
            RewardHelper.OpenCommonRewardPopup(rewardInfoData, () => OpenSuccessPopup(result));
        }

        public static void OpenFailPopup(StageResultMsg result)
        {
            var info = new UIPopupEventDreamBalloonFail.Info
            {
                model = GetModel(),
                round = result?.round ?? 0,
            };
            UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonFail.PREFAB_PATH, typeof(UIPopupEventDreamBalloonFail), info).Forget();
        }

        public static void OpenFinalPopup()
        {
            var info = new UIPopupEventDreamBalloonFinal.Info { model = GetModel() };
            UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonFinal.PREFAB_PATH, typeof(UIPopupEventDreamBalloonFinal), info).Forget();
        }

        public static void OpenEndPopup()
        {
            var info = new UIPopupEventDreamBalloonEnd.Info { model = GetModel() };
            UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonEnd.PREFAB_PATH, typeof(UIPopupEventDreamBalloonEnd), info).Forget();
        }

        // 종료 팝업을 열고 닫힐 때까지 대기 — 다음 시퀀스 이벤트 시작 팝업보다 먼저 노출·완료시키기 위함(ISSUE-28).
        // 팝업 오픈 실패 시 즉시 반환. 대기는 팝업 종료(SetCloseCallBack) 또는 외부 취소로 해제.
        public static async UniTask OpenEndPopupAndWaitAsync(CancellationToken ct = default)
        {
            var info = new UIPopupEventDreamBalloonEnd.Info { model = GetModel() };
            var popup = await UIManager.OpenUIMsgWitNameAsync(UIPopupEventDreamBalloonEnd.PREFAB_PATH, typeof(UIPopupEventDreamBalloonEnd), info, ct);
            if (popup == null)
                return;

            var tcs = new UniTaskCompletionSource();
            popup.SetCloseCallBack(() => tcs.TrySetResult());
            await tcs.Task.AttachExternalCancellation(ct);
        }
    }
}

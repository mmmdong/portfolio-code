using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ACTGames.Content.Helper;
using ACTGames.Table;
using Cysharp.Threading.Tasks;

using GameCore.Pattern;
using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.GameManagement;
using GameLogic.Management;
using GameLogic.Network;
using GameLogic.PlayGuide;

using UnityEngine;

namespace GameLogic.TrophyChallenge
{
    // 트로피 챌린지 시스템 진입점 — LiveEventManager 와 유사한 위치(MonoSingleton)로 두되,
    // 비즈니스 로직은 외부에서 주입한 정책 객체에 위임한다(DIP).
    //
    // 책임 분리:
    //   - 본 매니저 : 라이프사이클 / 메시지 발행 / 네트워크 호출 / 정책 조합
    //   - Repository : 캐시 R/W
    //   - StateEvaluator : 챌린지 상태 분기
    //   - SortPolicy : 챌린지 정렬 전략
    //
    // 즉, 정책 교체(시즌별 정렬 변화 등)는 인터페이스 의존 + Initialize 의 기본 구현체 교체로 처리한다(DIP).
    public partial class TrophyChallengeManager : MonoSingleton<TrophyChallengeManager>
    {
        private ITrophyChallengeRepository repository;
        private ITrophyChallengeStateEvaluator stateEvaluator;
        private ITrophyChallengeSortPolicy sortPolicy;

        // 명세서.md §6.2 (1) — 시작 팝업 자동 호출 1회 노출 방지용 (세션 단위 추적).
        // 같은 세션 내에서 마킹 전에 중복 자동 호출되지 않도록 막는 보조 가드. 영속 가드는 PlayerPrefs 의
        // PREFS_KEY_LAST_SEEN_START_POPUP 키로 처리 (시즌 단위, trophyId 단조 증가 기반 단일 키 비교).
        private readonly HashSet<long> autoOpenedStartPopupIds = new();

        // ISSUE-01 — 진행 중인 (trophyId, challengeId) 완료 요청 추적. 보상 받기 버튼(StatefulUI 공유 버튼)의
        // interactable 가드가 응답 대기 중 패널 갱신으로 풀려 같은 챌린지의 RQTrophyChallengeComplete 가
        // 중복 송신되는 것을 막는다. 요청 시작 시 추가하고 응답 콜백(성공/실패 모두)에서 제거한다.
        private readonly HashSet<(long trophyId, int challengeId)> pendingChallengeCompletes = new();

        // 시작 팝업을 본 마지막 trophyId 를 영속 저장하기 위한 PlayerPrefs 키.
        // trophyId 가 시퀀스 고유값(명세서 §4.1, 단조 증가) 이므로 단일 키 + `>=` 비교만으로 시즌별 1회 노출이 보장된다.
        // 계정 초기화 치트 등 외부에서도 동일 키를 참조하기 위해 public 으로 노출.
        public const string PREFS_KEY_LAST_SEEN_START_POPUP = "TC_LastSeenTrophyId";

        // 최종 보상 클리어 연출을 본 마지막 trophyId 영속 키 (재접속 강제 연출 가드 — 명세서 §10).
        public const string PREFS_KEY_LAST_SEEN_CLEAR_POPUP = "TC_LastSeenClearTrophyId";

        // ISSUE-02 (2026-05-26) — 챌린지 보상에 포함되는 "트로피" 통화의 itemIdx.
        // TrophyChallenge UI(슬롯/진행도 게이지)에서 카운터 용도로만 쓰이고 인벤토리 보관·소비처가 없어
        // GrantRewards 단계에서 제외한다(제외하지 않으면 DataManager.AddItem 의 CurrencyType 스위치
        // default 분기에서 매 보상마다 DLogger.Error("217") 가 발생).
        private const int TROPHY_CURRENCY_ITEM_IDX = 217;

        public event Action OnDataRefreshed;

        public void Initialize()
        {
            // 기본 정책 주입 — 교체가 필요하면 본 메서드의 구현체만 바꾼다(필드가 인터페이스 의존이라 호출부 영향 없음).
            repository ??= new TrophyChallengeRepository();
            stateEvaluator ??= new TrophyChallengeStateEvaluator();
            sortPolicy ??= new TrophyChallengeSortPolicy();
        }

        public void Release()
        {
            OnDataRefreshed = null;
        }

        // ---- 외부 조회 API ----
        // 정책(repository 등) 주입/Initialize 완료 여부 — 외부에서 GetMasters 등 호출 전 가드용.
        public bool IsInitialized => repository != null;

        public IReadOnlyDictionary<long, TrophyMasterPacketData> GetMasters() => repository.GetMasters();
        public TrophyMasterPacketData GetMaster(long trophyId) => repository.GetMaster(trophyId);
        public IReadOnlyCollection<long> GetUserTrophyIds() => repository.GetUserTrophyIds();
        public TrophyInfoPacketData GetUserInfo(long trophyId) => repository.GetUserInfo(trophyId);
        public IReadOnlyDictionary<int, TrophyChallengePacketData> GetChallenges(long trophyId) => repository.GetChallenges(trophyId);

        // 명세서.md §4.4 — 활성 판정은 TrophyInfo.startAt / endAt (유저 기준 기간) 으로 일원화한다.
        // Master.begin / end (운영 기간) 와 다를 수 있어(§10) 유저 입장에서 정확한 기간 판정 기준은 Info 패킷.
        public bool IsActiveSeason(long trophyId)
        {
            var info = repository.GetUserInfo(trophyId);
            if (info == null) return false;

            var now = DataManager.Instance.GetCurrentIntTimeStamp();
            if (info.startAt > 0 && info.startAt > now) return false;       // 시작 전
            if (info.endAt > 0 && info.endAt <= now) return false;          // 종료됨

            // 오픈 레벨은 마스터에서 가져옴 — 시즌 발급이 레벨 미달 유저에게 안 내려오는 게 정상이지만 방어적 체크.
            var master = repository.GetMaster(trophyId);
            if (master != null && DataManager.Instance.GetCurrentLevel() < master.openLevel) return false;

            return true;
        }

        // 명세서.md §7.5 / §10 — 시즌 종료(만료) 판정. 마스터 운영 기간 또는 유저 기준 기간이 지났으면 true.
        // IsActiveSeason 이 "시작 전" 도 false 로 처리하는 것과 달리, 본 메서드는 "종료됨" 만 true 다.
        public bool IsSeasonExpired(long trophyId)
        {
            var master = repository.GetMaster(trophyId);
            var info = repository.GetUserInfo(trophyId);
            var now = DataManager.Instance.GetCurrentIntTimeStamp();
            return TrophyChallengeStateEvaluator.IsExpired(master, info, now);
        }

        // 어느 한 시즌이라도 활성(startAt/endAt 기준) 이면 true — HUD 버튼 스폰 판정에 사용.
        public bool HasAnyActiveSeason()
        {
            var entries = DataManager.Instance.TrophyChallenge.UserEntries;
            foreach (var pair in entries)
            {
                if (IsActiveSeason(pair.Key)) return true;
            }
            return false;
        }

        // §14 장식 이동 기능 — World_Facility.getTypeIdx(트로피 챌린지 미션 그룹 번호) 에 대응하는
        // 트로피 챌린지 시즌이 활성(IsActiveSeason)이면 true. 월드 편집 UI 의 장식 노출 필터에서 사용.
        public bool IsActiveSeasonByMissionGroup(int missionGroupIdx)
        {
            if (!IsInitialized)
            {
                DLogger.Warning($"[장식이동] IsActiveSeasonByMissionGroup({missionGroupIdx}): 트로피 매니저 미초기화");
                return false;
            }

            foreach (var pair in repository.GetMasters())
            {
                if (pair.Value != null && pair.Value.missionGroupIdx == missionGroupIdx && IsActiveSeason(pair.Key))
                    return true;
            }

            return false;
        }

        // 명세서.md §6.2 (1) — 트로피 챌린지 시작 팝업 자동 호출 시퀀스.
        // 다른 컨텐츠(뱃지 컬렉션 등)와 동일하게 LobbySceneSequencer 의 EnterLobby / LevelUp 시퀀스에서
        // 호출된다. 최초 게임 온보딩 튜토리얼(CommonTutorialManager) / 플레이가이드가 끝난 뒤 시작 팝업을 노출한다.
        // isLevelUp: 레벨업 경로(LevelUpSequence) 면 true — 레벨 도달 즉시 노출을 위해 데이터를 재요청한다.
        public async UniTask AutoOpenPopupSequenceAsync(bool isLevelUp, CancellationToken token)
        {
            await UniTask.WaitUntil(() => !CommonTutorialManager.Instance.IsRunningTutorial(), cancellationToken: token);
            await UniTask.WaitUntil(() => !PlayGuideManager.Instance.IsPlaying, cancellationToken: token);

            if (!IsInitialized) return;

            // [임시 / 클라] 레벨업 직후엔 LobbyScene 진입 때 받은 데이터가 오픈 레벨 미달 기준이라
            // 시즌 데이터가 없을 수 있다(서버는 레벨 미달 유저에게 시즌 미발급이 정상). 레벨 도달 즉시
            // 노출을 위해 클라가 Master/Info 를 재요청한다 — 명세서 §5 비고 참조(서버 정식 흐름 확정 시 제거).
            //if (isLevelUp)
            {
                var refreshed = false;
                RequestMaster(() => RequestInfo(() => refreshed = true));
                await UniTask.WaitUntil(() => refreshed, cancellationToken: token);
            }

            // 미작업 명세서 G-3 — _Clear 우선. 시즌 만료/최종 보상 후 미열람 시즌이 있으면 _Clear 진입.
            // 그게 없을 때만 미참여 시즌의 _Start 자동 노출 시도.
            if (TryAutoOpenClearPopup()) return;
            TryAutoOpenStartPopup();
        }

        // 명세서.md §6.2 (1) — 자동 해금 + 미참여 시즌의 시작 팝업 1회 자동 호출.
        // 참여 여부 판정 = 시즌 내 챌린지 중 진행도 누적 / 완료가 하나라도 있는지.
        // 영속 가드: 이미 본 시즌(trophyId 이하) 은 PlayerPrefs 기반 단일 키 비교로 스킵.
        private void TryAutoOpenStartPopup()
        {
            foreach (var pair in repository.GetMasters())
            {
                var trophyId = pair.Key;
                if (autoOpenedStartPopupIds.Contains(trophyId)) continue;
                if (IsAutoStartPopupShown(trophyId)) continue;
                if (!IsActiveSeason(trophyId)) continue;
                if (HasParticipated(trophyId)) continue;

                TrophyChallengeUIBridge.OpenStartPopup(trophyId);
                autoOpenedStartPopupIds.Add(trophyId);
                break;
            }
        }

        // 미작업 명세서 G-3 — 로비 재진입 시 만료 시즌 _Clear 자동 노출.
        // ShouldForceShowClearPopup 가드(① 최종 보상 수령 / ② 만료+참여+미열람)에 매칭되는 시즌을 찾으면
        // 메인 팝업 진입(OpenMainPopup) — 진입 시 SetInfo 가 ShouldForceShowClearPopup 통과 케이스에
        // _Clear 패널로 자동 분기한다. _Start 자동 노출보다 우선 처리.
        private bool TryAutoOpenClearPopup()
        {
            foreach (var pair in repository.GetMasters())
            {
                var trophyId = pair.Key;
                if (!ShouldForceShowClearPopup(trophyId)) continue;

                TrophyChallengeUIBridge.OpenMainPopup(trophyId);
                return true;
            }
            return false;
        }

        // 시작 팝업 영속 가드 — 저장된 시즌넘버가 현재 trophyId 와 동일할 때만 "본 시즌" 으로 간주.
        // 다른 시즌으로 바뀌면(시즌 갱신) 다시 안내 노출되도록 같다/다르다 비교 채택.
        public bool IsAutoStartPopupShown(long trophyId)
        {
            var lastSeen = PlayerPrefs.GetInt(PREFS_KEY_LAST_SEEN_START_POPUP, 0);
            return lastSeen == trophyId;
        }

        // _Start 패널에서 유저가 명시적 인터랙션(닫기 / 도전 버튼) 을 한 시점에 호출.
        // 동일 값이면 갱신 생략 (PlayerPrefs.Save 호출 절약).
        public void MarkAutoStartPopupShown(long trophyId)
        {
            var current = PlayerPrefs.GetInt(PREFS_KEY_LAST_SEEN_START_POPUP, 0);
            if (current == trophyId) return;
            PlayerPrefs.SetInt(PREFS_KEY_LAST_SEEN_START_POPUP, (int)trophyId);
            PlayerPrefs.Save();
        }

        // 시즌 참여 여부 — 시즌 내 챌린지 중 진행도 또는 완료 항목이 하나라도 있으면 참여로 간주.
        public bool HasParticipated(long trophyId)
        {
            var challenges = repository.GetChallenges(trophyId);
            foreach (var challenge in challenges.Values)
            {
                if (challenge == null) continue;
                if (challenge.progress > 0 || challenge.isComplete) return true;
            }
            return false;
        }

        // 팝업 진입 시 _Start 패널부터 보여줄지 결정 — 시작 팝업 미열람 + 미참여 시즌만 _Start 부터.
        // 그 외(이미 본 시즌 / 참여 중인 시즌) 는 _Main 으로 바로 진입.
        public bool ShouldShowStartPanel(long trophyId)
            => !IsAutoStartPopupShown(trophyId) && !HasParticipated(trophyId);

        public bool IsFinalRewardClaimed(long trophyId)
        {
            var info = repository.GetUserInfo(trophyId);
            return info != null && info.finalRewardClaim;
        }

        // 명세서 §10 — 클리어 연출 영속 가드. 저장된 시즌넘버가 현재 trophyId 와 같을 때만 "본 시즌" 으로 간주.
        public bool IsClearPopupShown(long trophyId)
        {
            var lastSeen = PlayerPrefs.GetInt(PREFS_KEY_LAST_SEEN_CLEAR_POPUP, 0);
            return lastSeen == trophyId;
        }

        // _Clear 패널을 유저가 명시적으로 닫은 시점에 호출. 동일 값이면 갱신 생략(PlayerPrefs.Save 절약).
        public void MarkClearPopupShown(long trophyId)
        {
            var current = PlayerPrefs.GetInt(PREFS_KEY_LAST_SEEN_CLEAR_POPUP, 0);
            if (current == trophyId) return;
            PlayerPrefs.SetInt(PREFS_KEY_LAST_SEEN_CLEAR_POPUP, (int)trophyId);
            PlayerPrefs.Save();
        }

        // 명세서 §10 — 최종 보상 수령 완료(finalRewardClaim) 했으나 클리어 연출을 아직 못 본 시즌이면 true.
        // 팝업 진입 시 _Start/_Main 대신 _Clear 패널로 강제 분기(미연출분 재노출)하는 판정.
        // 미작업 명세서 G-1/G-2/G-3 — _Clear 강제 진입 조건 확장.
        //   ① 최종 보상 수령 완료인데 _Clear 미열람 (기존)
        //   ② 시즌 만료 + 참여 이력 보유 + _Clear 미열람 (신규: 부분/0개 클리어 케이스 footer 31415/31416)
        // 둘 다 IsClearPopupShown 영속 가드로 1회만 노출 보장.
        // 미작업 명세서 G-1/G-2/G-3 — _Clear 강제 진입 조건 확장.
        //   ① 최종 보상 수령 완료인데 _Clear 미열람 (기존)
        //   ② 시즌 만료 + 참여 이력 보유 + _Clear 미열람 (신규: 부분/0개 클리어 케이스 footer 31415/31416)
        // 둘 다 IsClearPopupShown 영속 가드로 1회만 노출 보장.
        // 미작업 명세서 G-1/G-2/G-3 — _Clear 강제 진입 조건.
        //   ① 최종 보상 수령 완료인데 _Clear 미열람 (기존)
        //   ② 시즌 만료 + _Clear 미열람 (신규)
        // 트로피 챌린지는 레벨 도달 시 '자동 참여' 컨텐츠(명세서 §6.1)다. 진행도가 0이어도 시즌 데이터를 받은
        // 유저는 종료 연출 대상이며(0개 클리어 footer 31416 포함), 진행도 기준 HasParticipated 로 거르면 안 된다.
        // IsSeasonExpired 는 master/info 존재 + 만료를 함께 판정하므로(시즌 데이터 없으면 false) 그 자체로
        // '자동 참여 시즌 + 만료'를 함의한다. IsClearPopupShown 영속 가드로 1회만 노출 보장.
        // 미작업 명세서 G-1/G-2/G-3 — _Clear 강제 진입 조건.
        //   ① 최종 보상 수령 완료인데 _Clear 미열람 (기존)
        //   ② 시즌 만료 + _Clear 미열람 (신규)
        // 트로피 챌린지는 레벨 도달 시 '자동 참여' 컨텐츠(명세서 §6.1)다. 진행도가 0이어도 시즌 데이터를 받은
        // 유저는 종료 연출 대상이며(0개 클리어 footer 31416 포함), 진행도 기준 HasParticipated 로 거르면 안 된다.
        // IsSeasonExpired 는 master/info 존재 + 만료를 함께 판정하므로(시즌 데이터 없으면 false) 그 자체로
        // '자동 참여 시즌 + 만료'를 함의한다. IsClearPopupShown 영속 가드로 1회만 노출 보장.
        public bool ShouldForceShowClearPopup(long trophyId)
            => !IsClearPopupShown(trophyId) && (IsFinalRewardClaimed(trophyId) || IsSeasonExpired(trophyId));

        // 미작업 명세서 G-2/G-3 — 팝업 진입 동선의 가드용 헬퍼.
        // 활성 시즌이거나, 만료됐지만 종료 연출(_Clear) 강제 노출 대상인 시즌이면 true.
        // 이 가드를 통과해야 TrophyChallengeUIBridge.OpenMainPopup / UIPopupTrophyChallenge.SetInfo
        // 에서 해당 시즌을 currentTrophyId 로 잡고 _Clear 패널로 분기할 수 있다.
        public bool IsActiveSeasonOrPendingClear(long trophyId)
            => IsActiveSeason(trophyId) || ShouldForceShowClearPopup(trophyId);

        // 명세서 §6.2 5번 — 보상 수령 완료(isCompleted == true, Info RS 규약상 "보상 수령까지 완료") 챌린지 수.
        // 최종 보상 조건(clearCount) 판정 입력값으로 쓰인다.
        public int CountCompletedChallenges(long trophyId)
        {
            var challenges = repository.GetChallenges(trophyId);
            var count = 0;
            foreach (var pair in challenges)
            {
                if (pair.Value != null && pair.Value.isComplete) ++count;
            }
            return count;
        }

        public bool IsFinalRewardClaimable(long trophyId)
        {
            var info = repository.GetUserInfo(trophyId);
            if (info == null || info.finalRewardClaim) return false;
            return CountCompletedChallenges(trophyId) >= info.clearCount;
        }

        // 명세서 §7.2 + §7.3 — 한 트로피 시즌의 챌린지 리스트를 정렬된 뷰모델 리스트로 반환.
        public List<TrophyChallengeViewModel> BuildViewModels(long trophyId)
        {
            var result = new List<TrophyChallengeViewModel>();
            var master = repository.GetMaster(trophyId);
            if (master == null || master.missionGroup == null) return result;

            var info = repository.GetUserInfo(trophyId);
            var challenges = repository.GetChallenges(trophyId);
            var isFinalRewardClaimed = IsFinalRewardClaimed(trophyId);

            var currentLevel = DataManager.Instance.GetCurrentLevel();
            var currentEpoch = DataManager.Instance.GetCurrentIntTimeStamp();

            var missionGroups = master.missionGroup;
            var groupCount = missionGroups.Length;

            // ISSUE-03 — groupRow.tableIdx 는 TrophyChallenge_Mission.csv 의 gIdx 컬럼과 매칭된다(시즌 미션 그룹 단위로 동일).
            // gIdx == tableIdx 인 미션 테이블 행 리스트를 루프 앞에서 한 번만 가져와 그룹 순번(i)대로 매핑한다.
            var tableIdx = missionGroups.FirstOrDefault(group => group != null)?.tableIdx ?? 0;
            var missionRows = TableManager.Instance.FindTable<TrophyChallengeMissionTableData>().Values
                .Where(x => x.gIdx == tableIdx)
                .ToArray();
            for (var i = 0; i < groupCount; ++i)
            {
                var groupRow = missionGroups[i];
                if (groupRow == null) continue;

                challenges.TryGetValue(groupRow.groupSeq, out var userChallenge);

                var missionTable = i < missionRows.Length ? missionRows[i] : null;
                if (missionTable == null)
                    DLogger.Error(
                        $"[트로피 챌린지] 운영툴 미션 데이터와 테이블 데이터 불일치 — trophyId:{trophyId}, gIdx(tableIdx):{tableIdx}, 서버 미션 그룹 수:{groupCount}, 테이블 행 수:{missionRows.Length}, groupSeq:{groupRow.groupSeq}");

                var state = stateEvaluator.Evaluate(
                    master,
                    info,
                    userChallenge,
                    groupRow,
                    groupRow.conditionCount,
                    currentLevel,
                    currentEpoch);

                result.Add(new TrophyChallengeViewModel
                {
                    TrophyId = trophyId,
                    ChallengeId = groupRow.groupSeq,
                    MissionIndex = missionTable?.index ?? groupRow.groupSeq,
                    State = state,
                    Progress = userChallenge?.progress ?? 0,
                    RequiredCount = groupRow.conditionCount,
                    IsRewardClaimed = isFinalRewardClaimed,
                    MissionGroupRow = groupRow,
                    MissionTableRow = missionTable,
                    UserChallenge = userChallenge,
                });
            }

            sortPolicy.Sort(result);
            return result;
        }

        // ISSUE-04 — challengeId(=groupSeq) 에 대응하는 뷰모델 조회.
        // 토스트/연출이 팝업(PanelMissionTab)과 동일한 미션 테이블 매핑(BuildViewModels 의 그룹 순번 매핑)을
        // 쓰도록 한다. challengeId(groupSeq) 를 미션 테이블 index 로 직접 조회하면 다른 행이 잡히므로 금지.
        public TrophyChallengeViewModel GetViewModel(long trophyId, int challengeId)
        {
            var viewModels = BuildViewModels(trophyId);
            foreach (var viewModel in viewModels)
            {
                if (viewModel.ChallengeId == challengeId) return viewModel;
            }
            return null;
        }

        // ---- 네트워크 흐름 (명세서 §1.2) ----

        public void RequestMaster(Action onComplete = null)
        {
            WrapWebManager.Instance.RequestTrophyChallengeMaster(_ =>
            {
                Message.Send(new OnTrophyChallengeMasterRefreshedMsg());
                OnDataRefreshed?.Invoke();
                onComplete?.Invoke();
            });
        }

        public void RequestInfo(Action onComplete = null)
        {
            WrapWebManager.Instance.RequestTrophyChallengeInfo(_ =>
            {
                Message.Send(new OnTrophyChallengeInfoRefreshedMsg());
                OnDataRefreshed?.Invoke();

                // 팝업 자동 호출(시작/종료)은 LobbySceneSequencer 가 EnterLobby / LevelUp 시퀀스에서
                // AutoOpenPopupSequenceAsync 로 구동한다 — 다른 컨텐츠와 동일 패턴, 최초 튜토리얼 종료 후 노출.
                onComplete?.Invoke();
            });
        }

        // 챌린지 진행도 누적 — 서버 응답 본문 없음(명세서 §3.3).
        public void NotifyProgress(long trophyId, int challengeId, int progressCount, Action<bool> onComplete = null)
        {
            if (progressCount == 0)
            {
                onComplete?.Invoke(false);
                return;
            }

            WrapWebManager.Instance.RequestTrophyChallengeUpdate(trophyId, challengeId, progressCount, callback =>
            {
                var success = callback.code == 0;
                if (success)
                {
                    // 명세서.md §3.3 — Update 응답이 갱신 후 누적값(amount)을 내려주므로
                    // 로컬 누적이 아닌 서버 권위값으로 동기화한다.
                    var response = callback.GetBody<RSTrophyChallengeUpdate>();
                    if (response != null)
                    {
                        repository.SetProgress(trophyId, challengeId, response.amount);
                        Message.Send(new OnTrophyChallengeProgressUpdatedMsg
                        {
                            TrophyId = trophyId,
                            ChallengeId = challengeId,
                            ProgressCount = progressCount,
                        });
                        if(TrophyChallengeHelper.IsCompleteChallenge(trophyId, challengeId, response.amount))
                            TrophyChallengeHelper.OnShowCompleteToast(trophyId, challengeId, response.amount);
                    }
                    else
                    {
                        DLogger.Error("[TrophyChallenge] Update 응답 본문 null — 진행도 동기화 스킵");
                    }
                }
                onComplete?.Invoke(success);
            });
        }

        // 챌린지 완료 처리 — 별도 호출(명세서 §5 비고).
        // 챌린지 완료 처리 — 별도 호출(명세서 §5 비고).
        // ISSUE-02 (2026-05-26) — 최종 보상 자동 청구가 발동되는 케이스에선 최종 보상까지 서버 응답 +
        // 보상 지급(인벤토리 반영)을 모두 끝낸 뒤에 UI 메시지를 일괄 발행한다. 보상 RS 본문이 비어
        // 클라가 직접 인벤토리에 반영하는 구조(§15.5)라, UI 팝업 노출 도중 강제 종료가 일어나면
        // 보상이 유실될 수 있어 위험 면적을 최소화한다.
        // 메시지 발행 순서: OnTrophyChallengeCompletedMsg → OnTrophyChallengeFinalRewardClaimedMsg(있을 때만)
        // → OnTrophyChallengeStateChangedMsg. UI 측 PanelTrophyChallenge_Main.OnChallengeRewardClaimed 가
        // 먼저 시퀀스(claimSequenceActive=true)를 시작하고, 직후 도착한 FinalRewardClaimedMsg 가
        // pendingFinalRewardClaim 플래그를 세팅 → 단일 → 최종 → _Clear 직렬화로 이어진다.
        public void NotifyChallengeComplete(long trophyId, int challengeId, Action<bool> onComplete = null)
        {
            // ISSUE-01 — 이미 완료(보상 수령)된 챌린지면 중복 완료 요청을 보내지 않는다.
            var cachedChallenge = repository?.GetChallenge(trophyId, challengeId);
            if (cachedChallenge != null && cachedChallenge.isComplete)
            {
                DLogger.Log($"[TrophyChallenge] 완료 요청 스킵 — 이미 완료된 챌린지 trophy={trophyId} challenge={challengeId}");
                onComplete?.Invoke(false);
                return;
            }

            // ISSUE-01 — 같은 챌린지의 완료 요청이 이미 응답 대기 중이면 중복 송신을 막는다.
            // 응답 전에 두 요청이 모두 나가는 케이스라 상태(State == Completed) 가드로는 걸러지지 않는다.
            // 콜백을 호출하면 진행 중인 첫 요청의 버튼 가드를 풀어버리므로 여기선 호출하지 않고 조용히 스킵한다.
            var completeKey = (trophyId, challengeId);
            if (!pendingChallengeCompletes.Add(completeKey))
            {
                DLogger.Log($"[TrophyChallenge] 완료 요청 스킵 — 응답 대기 중 중복 요청 trophy={trophyId} challenge={challengeId}");
                return;
            }

            WrapWebManager.Instance.RequestTrophyChallengeComplete(trophyId, challengeId, callback =>
            {
                pendingChallengeCompletes.Remove(completeKey);

                var success = callback.code == 0;
                if (!success)
                {
                    onComplete?.Invoke(false);
                    return;
                }

                repository.MarkCompleted(trophyId, challengeId);

                // 명세서.md §7.4.1 — RQTrophyChallengeComplete 성공 응답 직후, 챌린지 클리어 보상을
                // 기존 아이템 획득 로직(SetRewardItems → SaveDataStorage_PlayInfo)으로 지급.
                // ISSUE-05 — 미션 클리어 시 운영툴 재화 로그(Gain_Trophymission) 송신, eventId = challengeId.
                GrantRewards(GetChallengeRewardIndices(trophyId, challengeId), LogItemTriggerType.Gain_Trophymission, challengeId);

                // 명세서.md §11.2 — 챌린지 개별 달성 보상 획득 메타베이스 로그(trophy_challenge_reward)는
                // 보상받기 버튼 클릭 시점에 송신하도록 PanelMissionTab.ClaimReward 콜백으로 이동.

                // 명세서.md §7.4.2 — 최종 보상은 별도 수령 버튼 없이 즉시 지급. clearCount 도달 시 자동 청구.
                // ISSUE-02 — 최종 보상이 같이 청구되는 케이스는 최종 보상까지 처리한 뒤에 UI 메시지 일괄 발행.
                if (IsFinalRewardClaimable(trophyId))
                {
                    ClaimFinalRewardInternal(trophyId, dispatchMessages: false, finalSuccess =>
                    {
                        DispatchChallengeCompleteMessages(trophyId, challengeId);
                        if (finalSuccess)
                            Message.Send(new OnTrophyChallengeFinalRewardClaimedMsg { TrophyId = trophyId });
                        Message.Send(new OnTrophyChallengeStateChangedMsg { TrophyId = trophyId });
                        onComplete?.Invoke(true);
                    });
                    return;
                }

                DispatchChallengeCompleteMessages(trophyId, challengeId);
                Message.Send(new OnTrophyChallengeStateChangedMsg { TrophyId = trophyId });
                onComplete?.Invoke(true);
            });
        }

        // ISSUE-02 — 챌린지 개별 완료 UI 이벤트 발행.
        private void DispatchChallengeCompleteMessages(long trophyId, int challengeId)
        {
            Message.Send(new OnTrophyChallengeCompletedMsg
            {
                TrophyId = trophyId,
                ChallengeId = challengeId,
            });
        }

        // 최종 보상 수령 — clearCount 충족 시에만 호출 가능 (명세서 §5).
        // 최종 보상 수령 — clearCount 충족 시에만 호출 가능 (명세서 §5).
        // 단독 호출 진입점(치트 등). ISSUE-02 (2026-05-26) — 내부 구현을 ClaimFinalRewardInternal 로 분리.
        public void ClaimFinalReward(long trophyId, Action<bool> onComplete = null)
        {
            ClaimFinalRewardInternal(trophyId, dispatchMessages: true, onComplete);
        }

        // ISSUE-02 (2026-05-26) — 최종 보상 수령 내부 구현.
        // `dispatchMessages` 가 false 이면 OnTrophyChallengeFinalRewardClaimedMsg / StateChangedMsg 발행을
        // 호출자에게 위임한다. NotifyChallengeComplete 가 개별 + 최종 보상 지급을 모두 끝낸 뒤에
        // UI 메시지를 한꺼번에 발행하기 위해 사용.
        private void ClaimFinalRewardInternal(long trophyId, bool dispatchMessages, Action<bool> onComplete)
        {
            if (!IsFinalRewardClaimable(trophyId))
            {
                DLogger.Error($"ClaimFinalReward: 조건 미충족 trophyId={trophyId}");
                onComplete?.Invoke(false);
                return;
            }

            WrapWebManager.Instance.RequestTrophyChallengeFinalRewardClaim(trophyId, callback =>
            {
                var success = callback.code == 0;
                if (success)
                {
                    repository.MarkFinalRewardClaimed(trophyId);

                    var claimedMaster = repository.GetMaster(trophyId);

                    // 명세서.md §7.4.2 — RQTrophyChallengeFinalRewardClaim 성공 응답 직후, 최종 보상을
                    // 기존 아이템 획득 로직(SetRewardItems → SaveDataStorage_PlayInfo)으로 지급.
                    // ISSUE-05 — 최종 미션 클리어 시 운영툴 재화 로그(Gain_TrophyLastmission) 송신, eventId = missionGroupIdx.
                    GrantRewards(claimedMaster?.group?.completeReward, LogItemTriggerType.Gain_TrophyLastmission, claimedMaster?.missionGroupIdx ?? 0);

                    // 명세서.md §11.2 / Confluence 893518164 — 챌린지 전체 달성 보상 획득 메타베이스 로그
                    // (최종 보상 수령 = RQTrophyChallengeFinalRewardClaim 성공 응답 직후).
                    // Label = 그룹 번호(TrophyChallenge_Master.missionGIdx).
                    if (claimedMaster != null)
                    {
                        AnalyticsManager.AnalyticsEventData logData = new();
                        AnalyticsManager.CustomSendEvent(AnalyticsEventName.TROPHY_CHALLENGE_ALL_REWARD,
                            logData.AddLabel(claimedMaster.missionGroupIdx.ToString()));
                    }

                    if (dispatchMessages)
                    {
                        Message.Send(new OnTrophyChallengeFinalRewardClaimedMsg { TrophyId = trophyId });
                        Message.Send(new OnTrophyChallengeStateChangedMsg { TrophyId = trophyId });
                    }
                }
                onComplete?.Invoke(success);
            });
        }


        // 명세서.md §7.4 — 챌린지/최종 보상 지급. 보상 인덱스 배열(Event_Reward index)을
        // RewardPacketData 로 변환해 기존 아이템 획득 로직으로 넘긴다.
        // 경로: LiveEventManager.RefreshRewardDatas → DataManager.SetRewardData
        //       (재화/아이템/장식 캐시 반영 + 머지 인벤토리 UI 갱신) — 라이브 이벤트 보상 수령과 동일.
        // 명세서.md §7.4 — 챌린지/최종 보상 지급. 보상 인덱스 배열(Event_Reward index)을
        // RewardPacketData 로 변환해 기존 아이템 획득 로직으로 넘긴다.
        // 경로: LiveEventManager.RefreshRewardDatas → DataManager.SetRewardData
        //       (재화/아이템/장식 캐시 반영 + 머지 인벤토리 UI 갱신) — 라이브 이벤트 보상 수령과 동일.
        //
        // 명세서.md §10 — 재접속 시 보상이 사라지지 않도록 클라 캐시 갱신 직후 서버 영속화 트리거.
        // 메일 보상 수령(WrapWebManager.Mail.OnResponseMailOpen) 등 다른 보상 수령 흐름과 동일 패턴.
        private void GrantRewards(int[] rewardIndices, LogItemTriggerType logType, int eventId)
        {
            if (rewardIndices.IsNullOrEmpty()) return;

            var indexCount = rewardIndices.Length;
            var rewards = new List<RewardPacketData>(indexCount);
            for (var i = 0; i < indexCount; ++i)
            {
                var rewardIndex = rewardIndices[i];
                if (rewardIndex <= 0) continue;
                if (!TableManager.GetData(rewardIndex, out EventRewardTableData rewardRow)) continue;
                // ISSUE-02 — 트로피 통화는 챌린지 UI 카운터 용도라 실제 인벤토리 지급에서 제외.
                if ((ItemType)rewardRow.itemType == ItemType.Currency && rewardRow.itemIdx == TROPHY_CURRENCY_ITEM_IDX) continue;
                rewards.Add(new RewardPacketData((ItemType)rewardRow.itemType, rewardRow.itemIdx, rewardRow.itemValue));
            }
            if (rewards.Count == 0) return;
            // ISSUE-05 — 운영툴 재화 로그(RQLog)는 SetRewardItems 내부 SetRewardItem 의
            // Currency 분기에서 logType != None 이면 자동 송신된다(FsProcessCommon.cs:517).
            FsWebManager.GetProcess<FsProcessCommon>().SetRewardItems(rewards.ToArray(), logType, eventId);
            //LiveEventManager.Instance.RefreshRewardDatas(rewards.ToArray());
            //저장 데이터 서버 백업처리
            FsWebManager.Instance.SaveDataStorage_PlayInfo(DataSaveType.Server);
            //런타임 보상 데이터 적용 처리(실제 데이터 저장 아님)
            RewardHelper.RefreshRewardData(rewards.ToArray());
        }

        // 챌린지(groupSeq) 의 클리어 보상 인덱스 배열(TrophyMissionGroup.rewards) — master.missionGroup 에서 조회.
        private int[] GetChallengeRewardIndices(long trophyId, int challengeId)
        {
            var groups = repository.GetMaster(trophyId)?.missionGroup;
            if (groups == null) return null;

            var groupCount = groups.Length;
            for (var i = 0; i < groupCount; ++i)
            {
                var group = groups[i];
                if (group != null && group.groupSeq == challengeId) return group.rewards;
            }
            return null;
        }

        // ---- 레드닷 (명세서 §7.6) ----
        public bool HasAnyRedDot()
        {
            var entries = DataManager.Instance.TrophyChallenge.UserEntries;
            foreach (var pair in entries)
            {
                var trophyId = pair.Key;
                if (!IsActiveSeason(trophyId)) continue;

                if (IsFinalRewardClaimable(trophyId)) return true;

                // 명세서 §7.6 — 보상 미수령(클리어O & isCompleted == false) 챌린지가 하나라도 있으면 레드닷.
                var missionGroup = repository.GetMaster(trophyId)?.missionGroup;
                if (missionGroup == null) continue;

                var challenges = pair.Value.Challenges;
                foreach (var groupRow in missionGroup)
                {
                    if (groupRow == null) continue;

                    challenges.TryGetValue(groupRow.groupSeq, out var challenge);
                    if (IsChallengeRewardClaimable(challenge, groupRow.conditionCount))
                        return true;
                }
            }
            return false;
        }

        // 챌린지 보상 수령 가능 여부 — 로컬 캐싱 진행도가 conditionCount 도달 + 아직 미수령.
        // Info RS 의 isCompleted 는 "보상 수령 완료" 의미이므로, 미수령 = isCompleted == false.
        private static bool IsChallengeRewardClaimable(TrophyChallengePacketData challenge, int requiredCount)
        {
            return challenge != null
                && !challenge.isComplete
                && requiredCount > 0
                && challenge.progress >= requiredCount;
        }

        // 이미 진행이 끝난 챌린지인지 — 조건 충족(progress >= conditionCount) 또는 보상 수령 완료(isComplete).
        // OnProgressTrophyChallenge 에서 이미 끝난 미션에 불필요한 진행도 Update 패킷을 보내지 않도록 제외 판정에 사용.
        private bool IsChallengeConditionMetOrRewarded(long trophyId, TrophyMissionGroupPacketData group)
        {
            if (group == null) return false;
            var challenge = repository?.GetChallenge(trophyId, group.groupSeq);
            if (challenge == null) return false;
            return challenge.isComplete || (group.conditionCount > 0 && challenge.progress >= group.conditionCount);
        }

        #region Helper

        // 컨텐츠 발생부 → 트로피 챌린지 진행도 동기화.
        //   commonConditionType : 공통 조건 타입(MissionCommonConditionType). 활성 시즌 미션 그룹 중
        //                         conditionIdx→MissionCondition 테이블로 이 타입에 매핑되는 챌린지를 찾아 진행한다.
        //   conditionValue : 발생부의 실제 값(itemIndex/단계 등). 활성 시즌 해당 챌린지의 conditionValue
        //                    와 일치할 때만 진행. 필터가 필요 없으면 테이블 conditionValue 를 그대로 넘긴다.
        //   conditionCount : 증가량
        public static void OnProgressTrophyChallenge(MissionCommonConditionType commonConditionType, int conditionValue, int conditionCount)
        {
            if (!IsInstance() || !Instance.IsInitialized) return;
            if (conditionCount == 0) return;

            long trophyId = Instance.GetActiveTrophyId();
            if (trophyId == 0) return;

            var packets = Instance.GetMisssionGroupPacketByCondition(trophyId, commonConditionType);
            if (packets.IsNullOrEmpty()) return;

            foreach (TrophyMissionGroupPacketData trophyMissionGroupPacketData in packets)
            {
                // 이미 조건을 충족했거나 보상을 받은 미션은 진행도 갱신 대상에서 제외.
                if (Instance.IsChallengeConditionMetOrRewarded(trophyId, trophyMissionGroupPacketData)) continue;

                // 활성 시즌 해당 챌린지의 conditionValue 와 일치할 때만 동기화.
                if (!Instance.IsValidMissionGroup(trophyMissionGroupPacketData.conditionIdx, conditionValue, conditionCount)) continue;

                Instance.NotifyProgress(trophyId, trophyMissionGroupPacketData.groupSeq, conditionCount, (success) =>
                {
                    if (success)
                        DLogger.Log($"[성공] trophy={trophyId} challenge={trophyMissionGroupPacketData.groupSeq} progressCount={conditionCount} 전송 완료.");
                    else
                        DLogger.Log($"[실패] trophy={trophyId} challenge={trophyMissionGroupPacketData.groupSeq} progressCount={conditionCount} — 서버 응답 코드 비정상 (콘솔 확인).");
                });
            }
        }

        // 지정 조건 타입의 활성 시즌 챌린지 중, 아직 진행 가능한(완료/수령 전 = PROGRESS) 항목이 하나라도 있는지.
        // 매 로그인마다 복구 진행도를 불필요하게 보내지 않도록, 호출부에서 사전 확인하는 용도.
        public static bool HasProgressableChallenge(MissionCommonConditionType commonConditionType)
        {
            if (!IsInstance() || !Instance.IsInitialized) return false;

            long trophyId = Instance.GetActiveTrophyId();
            if (trophyId == 0) return false;

            var packets = Instance.GetMisssionGroupPacketByCondition(trophyId, commonConditionType);
            if (packets.IsNullOrEmpty()) return false;

            foreach (TrophyMissionGroupPacketData group in packets)
            {
                // 하나라도 완료/수령 전이면 진행 가능.
                if (!Instance.IsChallengeConditionMetOrRewarded(trophyId, group)) return true;
            }
            return false;
        }

        // 활성 시즌(startAt/endAt 기준) 의 trophyId. 없으면 0.
        public long GetActiveTrophyId()
        {
            if (repository == null) return 0;
            foreach (var pair in repository.GetMasters())
            {
                if (IsActiveSeason(pair.Key)) return pair.Key;
            }
            return 0;
        }

        // 활성 시즌 master.missionGroup 중, conditionIdx 가 가리키는 MissionCondition 테이블의
        // conditionType 이 인자와 일치하는 미션 그룹들을 반환. 컨텐츠 발생부가 challengeId 상수 대신
        // 공통 조건 타입만으로 진행 대상 챌린지를 찾도록 conditionType↔groupSeq 매핑을 데이터(테이블)로 해소한다.
        // (한 조건 타입이 여러 챌린지에 매핑될 수 있으므로 리스트 반환.) 활성 시즌/일치 항목이 없으면 빈 리스트.
        public List<TrophyMissionGroupPacketData> GetMisssionGroupPacketByCondition(long trophyId, MissionCommonConditionType commonConditionType)
        {
            var result = new List<TrophyMissionGroupPacketData>();
            if (commonConditionType == MissionCommonConditionType.None) return result;

            var master = repository?.GetMaster(trophyId);
            if (master?.missionGroup == null) return result;

            foreach (var group in master.missionGroup)
            {
                if (group == null) continue;

                var table = TableHelper.GetTable<MissionConditionTableData>(group.conditionIdx);
                if (table == null) continue;
                if (table.missionType != ConditionUseType.TrophyChallenge) continue;
                if (table.conditionType != commonConditionType) continue;

                result.Add(group);
            }
            return result;
        }

        public TrophyMissionGroupPacketData GetMisssionGroupPacket(long trophyId, long challengeId)
        {
            TrophyMissionGroupPacketData result = null;

            var master = repository?.GetMaster(trophyId);
            if (master?.missionGroup == null) return result;

            foreach (var group in master.missionGroup)
            {
                if (group == null) continue;

                var table = TableHelper.GetTable<MissionConditionTableData>(group.conditionIdx);
                if (table == null) continue;

                if (group.groupSeq == challengeId)
                {
                    result = group;
                    break;
                }
            }
            return result;
        }

        public bool IsValidMissionGroup(int conditionIndex, int value, int count)
        {
            var table = TableHelper.GetTable<MissionConditionTableData>(conditionIndex);
            if(null == table) return false;
            if (table.missionType != ConditionUseType.TrophyChallenge) return false;

            MissionCommonConditionType conditionType = table.conditionType;
            int conditionValue = table.conditionValue;

            // [ISSUE-06] 난이도형 조건 — conditionValue 는 "정확히 이 난이도" 가 아니라 **"이 난이도 이상"** 이다.
            //  조건값은 1쉬움 / 2보통 / 3어려움 의 오름차순이므로 달성 난이도(value)가 기준치 **이상**이면 충족
            //  (cv=2 → 보통·어려움 인정, 쉬움 미인정). cv = 0 은 "난이도 무관" 이라 값 비교를 생략한다.
            //  ⚠️ value 는 발생부가 조건값 스케일로 변환해 넘긴 값이다(EventDreamBalloonHelper.GetMissionDifficultyValue).
            //     내부 인코딩(1쉬움/0보통/2어려움)을 그대로 넘기면 0(보통)이 최하위로 뒤집혀 판정이 무너진다.
            if (MissionHelper.IsDifficultyConditionValue(conditionType))
            {
                if (conditionValue != 0
                    && value < conditionValue) return false;
            }
            else if (MissionHelper.IsIgnoreConditionValue(conditionType))
            {
                // conditionValue = 0 무시 관련 예외처리 0이 아닐때만 conditionValue를 비교하도록
                if(conditionValue != 0
                   && value != conditionValue) return false;
            }
            else
            {
                if (value != conditionValue) return false;
            }

            return true;
        }
        #endregion
    }
}

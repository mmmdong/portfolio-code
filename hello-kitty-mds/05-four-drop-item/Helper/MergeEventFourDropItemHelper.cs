using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using Cysharp.Threading.Tasks;

using GameCore.Utils;

using GameLogic.Define;
using GameLogic.Extension;
using GameLogic.Management;
//MergeEventBoard(열쇠 비행 도착점 조회)를 쓰기 위한 참조. 형제 MergeEventHelper 도 같은 이유로 이 네임스페이스를 연다.
using GameLogic.MergeEvent;
//MergeEventFourDropItemFieldPanel(화단 UI — 튜토리얼 포커싱 대상) 참조.
using GameLogic.MergeEvent.SubObject;
using GameLogic.Network;

//열쇠 비행 프리팹(FourDropItemKey)은 Image 가 아니라 SkeletonGraphic 이다 — 스킨(Normal/Special)을 갈아끼우기 위한 참조.
using Spine.Unity;

using UnityEngine;

namespace ACTGames.Content.Helper
{
    /// <summary>
    /// FourDropItem 전용 보조 헬퍼(활성화/방출 메커니즘 + 성장 블록 재화 배수). 테마01 표기: 바구니 = 활성화 블록, 도시락 = 방출 블록, 화단 = 성장 블록.
    /// 공용 헬퍼(MergeEventHelper)를 건드리지 않으려고 분리한 파일이라 FourDropItem 외 경로에서는 호출되지 않는다(additive).
    ///
    /// 단계 수치를 상수로 박지 않는 이유: 기획서가 "N단계"의 N을 두 절에서 서로 다르게 적어 모순 상태다.
    /// 그래서 "더 이상 업그레이드가 없는 최종 단계인가"로만 판정한다(수치가 바뀌어도 CSV 행 수만 바뀐다).
    /// 최종 단계 규약은 CSV 실측으로 확인했다 — MergeEvent_Block_Main.csv 의 upBlockIdx 는 자기참조가 아니라
    /// '없음 = 0' 규약이고(dangling 참조 0건), 그룹 내 최고 blockLevel 행이 0을 갖는다(머지 불가 생성기 그룹은 전 행이 0).
    /// 이 규약을 이미 코드로 표현한 것이 FsEventBlockData.IsMaxLevel() 이라 그대로 재사용한다.
    /// </summary>
    public static class MergeEventFourDropItemHelper
    {
        //MergeEvent_FourDropItemCycle.cycleChk 규약(기획서 §7-3(6) 2026-08-31 정정본).
        //  1 = 주기 사용     → 레어가 나오면 **그 자리에서** 리셋. 안 나오면 rareCycle 회차에서 리셋
        //  0 = 주기 사용 안함 → 레어가 나와도 주기 횟수를 다 채운 뒤 리셋(rareCycle 을 안 보므로 확률표 길이가 곧 주기다)
        //🔴 의미가 두 번 뒤집힌 값이다(v16 은 0=사용/1=사용 안함, v17 이 라벨을 지금처럼 되돌렸다).
        //  원시값을 그대로 비교하지 말고 반드시 이 상수를 경유한다.
        //✅ `D10` 최종 종결(2026-08-31) — §7-3(6) 은 **불릿(2)와 바로 아래 예시 문단이 정반대**인 채로 오래 열려 있었다.
        //  2026-08-12 에는 불릿을 정본으로 채택했으나 아래 근거로 **반증**됐고, 기획이 불릿(2)를 예시에 맞춰 고쳤다
        //  → **정본은 예시 문단**이고 위 표가 그 결론이다. 리드 확답(2026-08-12)은 `cycleChk` 가
        //  *"rareCycle 컬럼을 쓸 것인가"* 라는 축만 답한 것이라 이 정정과 충돌하지 않는다.
        //🔴 반증 근거 — `HL-1277` 댓글(2026-08-31) *"나무 1개 기준 테이블 값은 **최대 6번째**에서 블록이 등장되어야함"*.
        //  발행 4행이 전부 `cycleChk = 1` 인데 레어↔레어 간격이 `rareCycle` 이하여야 한다는 뜻이라
        //  **1 = 즉시 리셋** 외에는 성립하는 조합이 없다. 옛 해석(주기를 다 채움)에서는 주기 경계가 고정이라
        //  간격이 `(rareCycle - 이전위치) + 다음위치` 가 되어 50101 기준 최대 8탭까지 벌어졌다(7탭이 21.8%).
        public const int CYCLE_CHK_USE = 1;
        public const int CYCLE_CHK_UNUSE = 0;

        //rateArr 은 만분율이다(기획서 §7-3(6) "각 등장 확률 관리(만분율, 최대 10개)").
        public const int RARE_RATE_DENOMINATOR = 10000;

        //[무한모드 순위 표기] 기획서 v18 §5-1 "1% 미만일 경우 소수점 2째 자리까지 표시".
        //경계값 자체(1%)는 정수 표기다 — 미만일 때만 소수를 쓴다.
        private const float RANK_PERCENT_DECIMAL_THRESHOLD = 1f;
        private const string RANK_PERCENT_DECIMAL_FORMAT = "0.00";

        //[무한모드 순위 표기] '순위' 라벨 LIdx. 기획서 §5-1 진행도 무한 모드 *"「순위」(LIdx 120611) + 「{0}%」 형식으로 체크 / 「순위」 & 「\n{0}%」"*.
        //즉 최종 표기는 **라벨 한 줄 + 퍼센트 한 줄**이다(text_cn 발행 완료 — 「排名」).
        //🔴 rankIdx(120506 = "상위 {0}%")와 다른 축이다. 그쪽은 BossRaid 와 공유하는 한 줄짜리 문구이고,
        //  이 이벤트의 HUD 는 라벨과 수치가 세로로 쌓인다 — 라벨이 미발행인 언어에서만 그 한 줄짜리로 물러선다.
        private const int TXT_RANK_LABEL = 120611;

        //퍼센트 기호는 스트링 테이블이 아니라 여기서 붙인다 — 120611 은 라벨("排名")뿐이고 수치 자리가 없다.
        private const string RANK_PERCENT_SUFFIX = "%";

        /// <summary>
        /// 보상 묶음(MergeEvent_RewardGroup) 조회. groupIndex 로 묶인 MergeEvent_Reward 행들을 순서대로 돌려준다.
        /// 발행 전이거나 잘못된 idx 가 섞여 있으면 그 항목만 건너뛴다(빈 목록 = 표시할 보상 없음).
        /// 축하 팝업과 보상 지급이 같은 해석을 쓰도록 조회를 이 한 곳으로 모은다.
        /// </summary>
        public static List<MergeEventRewardTableData> GetRewardGroup(int groupIndex)
        {
            List<MergeEventRewardTableData> rewards = new();
            if (groupIndex <= 0) return rewards;
            if (TableManager.GetData(groupIndex, out MergeEventRewardGroupTableData groupTable) == false) return rewards;
            if (groupTable.rewardIdx.IsNullOrEmpty()) return rewards;

            foreach (int rewardIndex in groupTable.rewardIdx)
            {
                if (rewardIndex <= 0) continue;
                if (TableManager.GetData(rewardIndex, out MergeEventRewardTableData rewardTable) == false) continue;

                rewards.Add(rewardTable);
            }

            return rewards;
        }

        /// <summary>
        /// 보상 묶음(<see cref="GetRewardGroup"/> 결과)을 지급 패킷으로 변환한다.
        /// 지급 경로가 표시 경로와 같은 목록을 쓰도록 변환만 담당한다 — 조회를 여기서 다시 하지 않는다(호출부가 한 번만 조회).
        /// 수량이 0 이하인 행은 지급 대상이 아니라 건너뛴다(표시 임계와는 무관 — 표시는 수량 2 이상, 지급은 1 이상).
        /// </summary>
        public static List<RewardPacketData> ToRewardPackets(List<MergeEventRewardTableData> rewards)
        {
            List<RewardPacketData> packets = new();
            if (rewards.IsNullOrEmpty()) return packets;

            foreach (MergeEventRewardTableData rewardTable in rewards)
            {
                if (rewardTable.itemValue <= 0) continue;

                packets.Add(new RewardPacketData(rewardTable.itemType, rewardTable.itemIdx, rewardTable.itemValue));
            }

            return packets;
        }

        /// <summary>
        /// 무한 페이즈 병합 점수(MergeEvent_FourDropItemSet.mergePoint). blockLevel 은 1부터.
        /// 기획서 v17 §5-1 "병합한 아이템의 레벨에 따라 점수 차등(레벨이 높을수록 점수가 높음)" 의 데이터 소스다.
        /// 배열보다 높은 레벨은 0 을 돌려준다 — 기획이 상위 레벨을 아직 안 채운 상태에서 점수가 배열 끝값으로 새는 것보다,
        /// 안 오르는 쪽이 눈에 띄어 발행 누락을 빨리 찾는다.
        /// </summary>
        public static int GetMergePoint(int mergeEventId, int blockLevel)
        {
            if (blockLevel <= 0) return 0;
            if (TableManager.GetData(mergeEventId, out MergeEventFourDropItemSetTableData setTable) == false) return 0;
            if (setTable.mergePoint.IsNullOrEmpty()) return 0;
            if (blockLevel > setTable.mergePoint.Length) return 0;

            return setTable.mergePoint[blockLevel - 1];
        }

        #region 무한 페이즈 반복 보상 (기획 확정 2026-08-14)
        /// <summary>
        /// [무한모드 반복 보상] 목표 점수 — <c>MergeEvent_FourDropItemSet.repeatRewardPoint</c>.
        ///
        /// 🔴 <b>무한 페이즈의 목표 점수는 <c>MergeEvent_Round.value1</c> 이 아니다.</b> 기획 확정(2026-08-14)으로
        ///   조건과 보상의 축이 갈렸다 — <b>조건 = 이 컬럼 / 보상 = 그 Round 행의 <c>rewardIdx</c></b>.
        ///   501 의 Round 행(50101)은 <c>roundClearType=None</c> · <c>value1=0</c> 으로 발행돼 있어 종전 축으로는
        ///   판정이 아예 서지 않았고(게이지가 <c>n/0</c>), 그것이 이 함수를 만든 이유다.
        ///
        /// 무한 페이즈가 아니면 <b>항상 0</b> 이다 — 수집 페이즈나 타 이벤트에서 이 값을 목표로 쓰면 안 된다.
        /// 0(미발행 포함)은 '판정하지 않는다'는 뜻이다. 0 을 목표로 삼으면 <c>eventPoint >= 0</c> 이 늘 참이라
        /// 병합마다 보상이 무한 연쇄한다 — 그래서 소비처는 전부 <see cref="TryGetInfiniteRepeatRewardPoint"/> 를 거친다.
        /// </summary>
        public static int GetInfiniteRepeatRewardPoint(FsMergeEventState mergeEventState)
        {
            if (MergeEventHelper.IsFourDropItemInfinitePhase(mergeEventState) == false) return 0;
            if (TableManager.GetData(mergeEventState.id, out MergeEventFourDropItemSetTableData setTable) == false) return 0;

            return setTable.repeatRewardPoint;
        }

        /// <summary>
        /// [무한모드 반복 보상] 목표 점수가 <b>유효할 때만</b> true. 판정·차감을 하는 쪽은 반드시 이것을 쓴다.
        /// 미발행(0)이면 false 라 반복 보상 경로가 통째로 잠긴다 — 데이터가 없을 때 조용히 오작동하는 대신 아무 일도 안 한다.
        /// </summary>
        public static bool TryGetInfiniteRepeatRewardPoint(FsMergeEventState mergeEventState, out int repeatRewardPoint)
        {
            repeatRewardPoint = GetInfiniteRepeatRewardPoint(mergeEventState);
            return repeatRewardPoint > 0;
        }

        /// <summary>
        /// [무한모드 반복 보상] 지금 보상을 줄 때인가 — <c>eventPoint >= repeatRewardPoint</c>.
        ///
        /// 🔴 공용 <c>MergeEventBoardUpdater.CheckRoundChangeReady</c> 를 쓰지 않고 별도 술어를 두는 이유:
        ///   그 함수는 <c>roundClearType</c> 축이라 <c>None</c> 인 501 행에서는 즉시 false 이고,
        ///   거기에 무한 분기를 넣으면 <b>같은 함수를 부르는 무인자 오버로드까지 함께 참으로 바뀐다</b>.
        ///   그 호출부(<c>MergeEvent</c> 의 블록 획득 팝업 닫힘 콜백)는 참일 때 <c>LockIdleGuide(false)</c> 와
        ///   <c>TryOpenPopupClearNormal</c> 을 **건너뛰므로**, 무한 페이즈에서 유휴 가이드 잠금이 풀리지 않는 회귀가 난다.
        ///   → 판정은 이 술어로 국소화하고 공용 파일은 건드리지 않는다.
        /// </summary>
        public static bool IsInfiniteRepeatRewardReady(FsMergeEventState mergeEventState)
        {
            if (TryGetInfiniteRepeatRewardPoint(mergeEventState, out int repeatRewardPoint) == false) return false;

            return mergeEventState.eventPoint >= repeatRewardPoint;
        }
        #endregion

        /// <summary>
        /// 무한 페이즈일 때 병합 결과 블록의 레벨만큼 이벤트 포인트를 가산한다. 실제로 가산했으면 true + addedPoint.
        /// <b>데이터만 바꾸는 단계다</b> — 연출과 후처리 액션은 호출부가 잇는다.
        /// BossRaid 의 <c>EventBlockControllerSpecial.BlockClickProcess</c>(AttackType)와 같은 3단 구조를 따른다:
        /// ①데이터 동기 처리(DirectApi_AddEventPoint) → ②투사체·숫자 연출 → ③후처리 액션(ActionMergeEvent) → 핸들러가 상태 재조회·UI 갱신.
        /// addedPoint 를 돌려주는 이유: 연출(PlayGainMergeEventPoint)이 증가량을 인자로 받기 때문이다.
        /// FourDropItem 이 아니거나 아직 수집 페이즈면 아무 것도 하지 않는다(타 타입 무영향, additive).
        /// </summary>
        public static bool TryAddInfinitePhaseMergePoint(FsMergeEventState mergeEventState, FsEventBlockData mergedBlock, out int addedPoint)
        {
            addedPoint = 0;
            if (mergedBlock == null) return false;
            if (MergeEventHelper.IsFourDropItemInfinitePhase(mergeEventState) == false) return false;

            //업그레이드가 끝난 '결과 블록'의 레벨이다 — 기획서가 "병합한 아이템의 레벨"이라 적은 대상.
            int point = GetMergePoint(mergeEventState.id, mergedBlock.GetBlockLevel());
            if (point <= 0) return false;

            FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_AddEventPoint(mergeEventState.id, point);
            addedPoint = point;
            return true;
        }

        /// <summary>
        /// 최종 단계(= upBlockIdx 가 0 이라 더 이상 업그레이드 대상이 없음) 판정.
        /// </summary>
        public static bool IsFinalStage(FsEventBlockData blockData)
        {
            if (blockData == null) return false;
            if (blockData.MainTable == null) return false;

            return blockData.IsMaxLevel();
        }

        /// <summary>
        /// Making 테이블이 규정한 총 생성 수량("지정 수만큼 생성"의 수량). 미발행 CSV 에서는 0.
        /// MergeEventHelper.GetTotalMakingCount 는 maxMakeBlock 이 비면 DLogger.Error 를 남기므로 여기서 먼저 걸러낸다.
        /// </summary>
        public static int GetMakeCount(MergeEventBlockMakingTableData makingData)
        {
            if (makingData == null) return 0;
            if (makingData.maxMakeBlock.IsNullOrEmpty()) return 0;

            return MergeEventHelper.GetTotalMakingCount(makingData);
        }

        /// <summary>
        /// 이번 순번(offset)에 생성할 블록 id. 0 = 생성 대상 없음(makeBlockIdx 미발행 등).
        /// makeBlockIdx 를 순환 사용한다(offset 은 항상 0 이상이라 음수 인덱스가 불가능).
        /// 성장(화단)·방출(도시락) 컨트롤러가 **공유**한다 — 예전엔 성장 쪽에 사본이 있었으나 반쪽 CSV 가드를 한쪽만 고치는 사고가 나서 여기로 합쳤다.
        /// EventBlockControllerCreate.GetTargetBlockId 를 재사용하지 않는 이유: 그쪽 FixedCountByRule 분기
        /// (maxMakeCount - calculateProductCount + offset)는 수량이 테이블 밖에서 정해질 때 음수 인덱스가 되어 예외가 난다
        /// (성장 블록의 생성 수량은 정적 테이블이 아니라 growCount 에서 나와 maxMakeCount 를 넘을 수 있다).
        /// </summary>
        public static int GetMakeBlockId(MergeEventBlockMakingTableData makingData, int offset)
        {
            if (makingData == null) return 0;
            //makeBlockIdx 는 두 분기가 모두 쓴다 → 분기보다 먼저 막는다(GetRandomMakingIndex 도 널 체크 없이 순회한다).
            if (makingData.makeBlockIdx.IsNullOrEmpty()) return 0;

            if (makingData.makeType == EventBlockMakeType.Probabilistic)
            {
                //makeBlockRate 는 확률 분기에서만 쓰이므로 여기서 막는다(순환 분기는 확률 컬럼이 비어 있어도 정상 동작이라 위로 올리면 안 된다).
                //GetRandomMakingIndex 는 makeBlockRate 를 널 체크 없이 순회하고 그 길이로 makeBlockIdx 를 인덱싱한다 —
                //대상만 발행되고 확률 컬럼이 비었거나(널 역참조) 확률 컬럼이 더 긴(인덱스 초과) 반쪽 CSV 에서
                //클릭 핸들러 한복판에 예외가 나 블록이 영구 무반응이 되고 매 터치마다 같은 예외가 반복된다.
                if (makingData.makeBlockRate.IsNullOrEmpty()) return 0;
                if (makingData.makeBlockRate.Length > makingData.makeBlockIdx.Length) return 0;

                return MergeEventHelper.GetRandomMakingIndex(makingData);
            }

            List<int> candidates = makingData.makeBlockIdx.Where(id => id != 0).ToList();
            if (candidates.IsNullOrEmpty()) return 0;

            return candidates[offset % candidates.Count];
        }

        /// <summary>
        /// 생성 대상 id 가 하나라도 유효한가(= makeBlockIdx 에 0 아닌 값이 있는가).
        /// 수량(maxMakeBlock)만 채워지고 대상(makeBlockIdx)이 비어 있는 반쪽 CSV 에서 '소모했는데 아무것도 안 나오는' 상태를 막기 위한 사전 검사다.
        /// makeType 과 무관하게 성립해야 하므로 GetMakeBlockId 의 Probabilistic 분기보다 앞서 쓰는 것을 전제로 한다.
        /// </summary>
        public static bool HasAnyMakeBlockId(MergeEventBlockMakingTableData makingData)
        {
            if (makingData == null) return false;
            if (makingData.makeBlockIdx.IsNullOrEmpty()) return false;

            return makingData.makeBlockIdx.Any(id => id != 0);
        }

        /// <summary>
        /// 블록 인덱스가 Block_Main 에 실재하는가. 오타 등으로 발행되지 않은 idx 가 makeBlockIdx 에 섞이면
        /// 생성 단계에서 테이블 조회가 실패해 '수량만 차감되고 블록은 안 나오는' 상태가 되므로 소모 전에 확인한다.
        /// TableManager.GetData 는 실패해도 에러 로그를 남기지 않는다(TableHelper.GetTable 과 다름).
        /// </summary>
        public static bool IsValidBlockIndex(int blockIndex)
        {
            if (blockIndex == 0) return false;

            return TableManager.GetData(blockIndex, out MergeEventBlockMainTableData _);
        }

        /// <summary>
        /// 생성 대상 id 가 <b>전부</b> Block_Main 에 실재하는가.
        /// '소모하는 쪽'과 '생성하는 쪽'이 분리된 메커닉(활성화 블록 → 상호작용 오브젝트)의 소모 전 가드다.
        /// <see cref="HasAnyMakeBlockId"/>(하나라도 있으면 통과)로는 부족하다 —
        /// 생성측이 <see cref="IsValidBlockIndex"/> 로 차감 전에 return 하는데 소모측이 더 느슨하면,
        /// 오타 id 하나에 블록만 소모되고 잔여 수량이 영영 0 이 되지 않아 메커닉 전체가 고착된다.
        /// 순환 분기는 0 아닌 후보를 순서대로 전부 쓰고 확률 분기는 무엇이든 뽑을 수 있으므로 '전부'를 요구한다.
        /// </summary>
        public static bool AreAllMakeBlockIdsValid(MergeEventBlockMakingTableData makingData)
        {
            if (HasAnyMakeBlockId(makingData) == false) return false;

            int[] makeBlockIdx = makingData.makeBlockIdx;
            for (int i = 0; i < makeBlockIdx.Length; ++i)
            {
                //0 은 '이 자리엔 대상 없음'이라 GetMakeBlockId 의 순환 후보에서 이미 빠진다(확률 분기에서 뽑혀도 0 을 돌려줘 호출측이 막는다).
                if (makeBlockIdx[i] == 0) continue;
                if (IsValidBlockIndex(makeBlockIdx[i]) == false) return false;
            }

            return true;
        }

        /// <summary>
        /// [FourDropItem] 활성화된 상호작용 오브젝트(테마01: 피크닉 바구니)가 드롭할 아이템과 총 개수.
        /// 기획서 970293249 §5-1 *"activeDropItem 에 셋팅된 아이템을 드롭 / activeDropItemCount 에 작성된 수량 만큼 생성 후 비활성"*.
        ///
        /// 🔴 <b>Block_Making 이 아니라 MergeEvent_FourDropItemSet 이 정본이다.</b> 종전 코드는 활성화에 쓰인 블록의
        /// Making(maxMakeBlock·makeBlockIdx)에서 읽었는데 그건 v31 이전 설계이고, 501 발행분의 활성화 아이템(261307)에는
        /// <b>Making 행 자체가 없어</b> 사용이 조용히 막혔다(계획서 `Y19` 와 같은 개정 축).
        ///
        /// false = 쓸 수 없는 데이터(Set 행 미발행 / 대상 미발행 / 개수 0 이하 / 대상 id 가 Block_Main 에 없음).
        /// 소모 전에 이 검사를 통과시켜야 '블록만 사라지고 아무것도 안 나오는' 손해를 막는다.
        /// </summary>
        public static bool TryGetActiveDropInfo(int mergeEventId, out int dropItemId, out int dropCount)
        {
            dropItemId = 0;
            dropCount  = 0;

            if (TableManager.GetData(mergeEventId, out MergeEventFourDropItemSetTableData setTable) == false) return false;
            if (setTable.activeDropItemCount <= 0) return false;
            if (IsValidBlockIndex(setTable.activeDropItem) == false) return false;

            dropItemId = setTable.activeDropItem;
            dropCount  = setTable.activeDropItemCount;
            return true;
        }

        /// <summary>
        /// [FourDropItem] 이 이벤트에서 **상호작용 오브젝트를 활성화하는 아이템**(테마01: 바구니 아이템)의 블록 idx.
        /// 기획서 970293249 v32 §7-3(11) *"바구니를 구분하기 위한 타입 추가: **7**"* — `blockType` 이 정본이다.
        /// (v31 의 `FourDropItemSet.activeObjIdx` 컬럼 지정 방식은 v32 에서 제거됐다 — 계획서 `Y19`.)
        ///
        /// 비활성 말풍선이 *"무엇을 가져와야 하는가"* 를 그리는 데 쓴다. 501 실측 대상은 `261307`.
        /// 같은 이벤트에 `blockType 7` 이 여러 개면 **가장 작은 idx** 를 쓴다 — 순회 순서가 사전마다 다를 수 있어
        /// 값을 고정하지 않으면 실행마다 다른 아이콘이 뜬다.
        /// </summary>
        public static bool TryGetActivateItemIndex(int mergeEventId, out int blockIndex)
        {
            blockIndex = 0;

            Dictionary<int, MergeEventBlockMainTableData> table = TableManager.Instance.FindTable<MergeEventBlockMainTableData>();
            if (table == null) return false;

            foreach (KeyValuePair<int, MergeEventBlockMainTableData> pair in table)
            {
                MergeEventBlockMainTableData data = pair.Value;
                if (data.mergeEventIdx != mergeEventId) continue;
                if (data.blockType != EventBlockType.Activate) continue;

                if (blockIndex == 0 || data.index < blockIndex)
                    blockIndex = data.index;
            }

            return blockIndex != 0;
        }

        /// <summary>
        /// 블록 인덱스로 Making 테이블을 직접 조회한다(보드 밖 오브젝트는 FsEventBlockData 를 들고 있지 않다).
        /// TableHelper.GetTable 은 미발행 인덱스마다 DLogger.Error 를 남기므로 쓰지 않는다.
        /// </summary>
        public static bool TryGetMakingTable(int blockIndex, out MergeEventBlockMakingTableData makingData)
        {
            makingData = null;
            if (blockIndex == 0) return false;

            return TableManager.GetData(blockIndex, out makingData);
        }

        //[v17 §7-3(5)] boosterChk 배열의 각 칸이 뜻하는 배수. {a,b,c,d} = 1배/2배/4배/8배 사용 여부.
        //배열이 이보다 길면 뒤쪽은 무시한다(기획이 칸을 늘리면 이 표를 늘릴 것).
        private static readonly int[] BOOSTER_MULTIPLIERS = { 1, 2, 4, 8 };

        /// <summary>
        /// 이 이벤트에서 쓸 수 있는 재화 배수 목록(오름차순).
        /// 미발행이거나 전부 0 이면 기본 1배만 돌려준다 — 토글이 사라질 뿐 진행은 막지 않는다.
        ///
        /// 🔴 [HL-1176 · 기획 2026-08-25] 한 배수가 표에 들어오려면 <b>두 컬럼을 모두 통과</b>해야 한다:
        ///   <c>boosterChk[i] != 0</c>(사용 여부의 정본) <b>그리고</b> <c>boosterOpenKey[i] &gt; 0</c>(여는 열쇠가 지정돼 있을 것).
        ///   열쇠 칸이 0 이거나 배열에 아예 없으면 <c>boosterChk</c> 가 0 인 것과 똑같이 뺀다 —
        ///   0 을 '열쇠 없이 기본 오픈' 으로 읽으면 발행 실수 한 칸에 x8 이 공짜로 풀린다.
        ///   덕분에 중간 0(<c>0,A,0,B</c>)·짧은 배열(<c>0,A</c>)·구 단일값이 전부 이 한 규칙으로 흡수되고,
        ///   진단 로그도 보정 코드도 필요 없다(전 조합이 결정론적이다).
        ///
        /// 🔴 첫 칸(x1)만 예외로 열쇠를 보지 않는다. 1배는 어떤 발행에서도 남아야 한다 —
        ///   목록이 비면 유저가 아무 배수도 고를 수 없어 소환 자체가 막힌다.
        ///
        /// TODO(기획 확인 — 계획서 §1-2 D11): §7-3(5) 테이블은 8배까지 열어 뒀는데 §5-1 메인 팝업 설명은
        ///   "최대 2배 … 터치 시 1배, 2배 전환"이다. 코드는 테이블을 정본으로 삼아 일반화해 뒀으므로,
        ///   UI 를 2배로 묶기로 확정되면 boosterChk 를 1,1,0,0 으로 발행하면 되고 코드 수정은 필요 없다.
        /// </summary>
        /// <remarks>
        /// 호출마다 List 를 새로 만들고 Sort 하므로 슬롯 루프에서 직접 부르면 안 된다.
        /// 보드 세션당 한 번만 부르고(MergeEventBoard 가 캐시한다) 그 결과를 아래 오버로드들에 넘길 것.
        /// 반환형이 IReadOnlyList 인 이유도 그 캐시를 호출자가 변형하지 못하게 하기 위해서다.
        /// </remarks>
        public static IReadOnlyList<int> GetAllowedGrowMultipliers(int mergeEventId)
        {
            List<int> multipliers = new() { MergeEventHelper.GROW_MULTIPLIER_DEFAULT };
            if (TableManager.GetData(mergeEventId, out MergeEventFourDropItemSetTableData setTable) == false) return multipliers;
            if (setTable.boosterChk.IsNullOrEmpty()) return multipliers;

            int count = setTable.boosterChk.Length < BOOSTER_MULTIPLIERS.Length
                            ? setTable.boosterChk.Length
                            : BOOSTER_MULTIPLIERS.Length;
            for (int i = 0; i < count; ++i)
            {
                if (setTable.boosterChk[i] == 0) continue;
                //[HL-1176] i == 0(x1)은 위에서 이미 넣었고 열쇠도 보지 않는다 — 아래 검사는 부스터로 여는 배수에만 건다.
                if (i > 0 && HasBoosterOpenKey(setTable, i) == false) continue;

                int multiplier = BOOSTER_MULTIPLIERS[i];
                if (multipliers.Contains(multiplier)) continue;

                multipliers.Add(multiplier);
            }

            multipliers.Sort();
            return multipliers;
        }

        /// <summary>[HL-1176] <paramref name="slotIndex"/> 칸에 열쇠가 지정돼 있는가. 칸이 없거나 0 이면 false.</summary>
        private static bool HasBoosterOpenKey(MergeEventFourDropItemSetTableData setTable, int slotIndex)
        {
            if (setTable.boosterOpenKey.IsNullOrEmpty()) return false;
            if (slotIndex >= setTable.boosterOpenKey.Length) return false;

            return setTable.boosterOpenKey[slotIndex] > 0;
        }

        /// <summary>
        /// [HL-1176 · 기획 2026-08-25] 획득한 블록이 배수 열쇠면, 그 열쇠가 <b>도달시키는 해금 단계</b>를 돌려준다.
        /// 열쇠가 아니거나 그 배수가 표에 없으면 false — 호출측은 아무 일도 하지 않는다.
        ///
        /// 🔴 <b>같은 idx 가 여러 칸이면 가장 뒤칸이 정본</b>이다(루프가 덮어쓰며 끝까지 훑는 이유).
        ///   기획 원문: *"boosterOpenKey 가 0,260109,260109,260109 일 경우 8배수까지 한번에 오픈"*.
        ///
        /// 🔴 돌려주는 값은 <b>유효 배수 표 안의 인덱스</b>이지 원시 배열 인덱스가 아니다.
        ///   중간 0 이나 <c>boosterChk</c> 0 으로 배수가 빠지면 두 인덱스가 어긋나는데,
        ///   <c>boosterOpenCount</c> 를 자르는 쪽(<see cref="GetUnlockedGrowMultipliers(IReadOnlyList{int}, int)"/>)이
        ///   표 기준으로 세므로 여기서도 표 기준이어야 한다.
        ///   예) key = 0,260109,0,260114 · chk = 1,1,1,1 → 표 {1,2,8} → 260114 는 원시 3번 칸이지만 단계는 <b>2</b>.
        /// </summary>
        public static bool TryGetBoosterOpenStep(int mergeEventId, int acquiredBlockIndex, out int targetStep)
        {
            targetStep = 0;
            //0 을 걸러 두지 않으면 미발행 칸(0)이 '획득한 블록' 과 맞아떨어져 엉뚱하게 열린다.
            if (acquiredBlockIndex <= 0) return false;
            if (TableManager.GetData(mergeEventId, out MergeEventFourDropItemSetTableData setTable) == false) return false;
            if (setTable.boosterOpenKey.IsNullOrEmpty()) return false;

            int count = setTable.boosterOpenKey.Length < BOOSTER_MULTIPLIERS.Length
                            ? setTable.boosterOpenKey.Length
                            : BOOSTER_MULTIPLIERS.Length;
            int keyMultiplier = 0;
            for (int i = 0; i < count; ++i)
            {
                if (setTable.boosterOpenKey[i] != acquiredBlockIndex) continue;

                keyMultiplier = BOOSTER_MULTIPLIERS[i];
            }

            if (keyMultiplier <= 0) return false;

            IReadOnlyList<int> allowed = GetAllowedGrowMultipliers(mergeEventId);
            for (int i = 0; i < allowed.Count; ++i)
            {
                if (allowed[i] != keyMultiplier) continue;

                targetStep = i;
                return true;
            }

            //표에 없는 배수의 열쇠다(boosterChk 가 뺐다) → 열쇠가 아닌 물건과 같이 취급한다.
            return false;
        }

        /// <summary>
        /// 표 전체(<paramref name="multiplierTable"/>)에서 <b>해금한 단계까지만</b> 잘라낸 실제 사용 가능 배수 목록.
        ///
        /// 표가 오름차순이므로 앞에서부터 <c>1 + boosterOpenCount</c> 개를 취한다.
        /// 표가 꽉 찬 발행이면 0단계 → {1}(잠김) / 1단계 → {1,2} / 2단계 → {1,2,4} / 3단계 → {1,2,4,8}.
        ///
        /// 🔴 [HL-1176] 단계는 <b>표의 인덱스</b>이지 배수 그 자체가 아니다. 표가 좁혀진 발행에서는 대응이 앞당겨진다
        /// (예 표 {1,2,8} → 1단계 = {1,2} / 2단계 = {1,2,8}). 몇 단계가 열리는지는 호출측이 정하고
        /// (<see cref="TryGetBoosterOpenStep"/>), 이 함수는 <b>단계 → 목록</b> 변환만 담당한다.
        ///
        /// 1배는 언제나 남는다 — 잘라 낸 결과가 비면 유저가 아무 배수도 고를 수 없어 소환 자체가 막힌다.
        /// </summary>
        public static IReadOnlyList<int> GetUnlockedGrowMultipliers(IReadOnlyList<int> multiplierTable, int boosterOpenCount)
        {
            List<int> unlocked = new() { MergeEventHelper.GROW_MULTIPLIER_DEFAULT };
            if (multiplierTable == null) return unlocked;

            //음수 저장값(오염)도 잠김으로 본다 — 배수를 여는 쪽이 아니라 막는 쪽으로 물러선다.
            int openCount = boosterOpenCount < 0 ? 0 : boosterOpenCount;

            int count = multiplierTable.Count < openCount + 1 ? multiplierTable.Count : openCount + 1;
            for (int i = 0; i < count; ++i)
            {
                if (unlocked.Contains(multiplierTable[i])) continue;

                unlocked.Add(multiplierTable[i]);
            }

            return unlocked;
        }

        /// <summary>
        /// 지금 이 이벤트에서 실제로 쓸 수 있는 배수 목록(권위 상태의 해금 횟수 반영).
        /// 보드처럼 목록을 캐시하는 쪽은 <see cref="GetUnlockedGrowMultipliers(IReadOnlyList{int}, int)"/> 를 쓸 것 —
        /// 이 오버로드는 호출마다 표를 다시 만든다.
        /// </summary>
        public static IReadOnlyList<int> GetUnlockedGrowMultipliers(int mergeEventId)
        {
            FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_MergeEvent_GetBoosterOpenCount(out int boosterOpenCount);

            return GetUnlockedGrowMultipliers(GetAllowedGrowMultipliers(mergeEventId), boosterOpenCount);
        }

        /// <summary>
        /// 배수가 요구하는 레벨 상승 단계 수. x1 = 0단계, x2 = 1단계, x4 = 2단계, x8 = 3단계.
        /// 배수가 2의 거듭제곱이라는 전제(BOOSTER_MULTIPLIERS)에서 나온 값이다.
        /// </summary>
        public static int GetUpgradeStepCount(int multiplier)
        {
            int steps = 0;
            for (int value = multiplier; value > MergeEventHelper.GROW_MULTIPLIER_DEFAULT; value /= 2)
                ++steps;

            return steps;
        }

        /// <summary>
        /// 한 단계 위(레벨 +1) 블록 id. 배수 x2 가 만들어야 할 '레벨 2' 는 makeBlockIdx 가 주는 레벨 1 블록의 upBlockIdx 다.
        /// 상수/하드코딩 없이 Block_Main 의 upBlockIdx 규약("없음 = 0")만 따른다.
        /// 다음 단계가 없거나(upBlockIdx == 0) 미발행 인덱스면 false — 호출부는 이때 x1 로 폴백해 재화 손해를 막아야 한다.
        /// </summary>
        public static bool TryGetUpgradedBlockId(int blockIndex, out int upgradedBlockIndex)
        {
            upgradedBlockIndex = 0;
            if (blockIndex == 0) return false;
            if (TableManager.GetData(blockIndex, out MergeEventBlockMainTableData mainTable) == false) return false;
            if (IsValidBlockIndex(mainTable.upBlockIdx) == false) return false;

            upgradedBlockIndex = mainTable.upBlockIdx;
            return true;
        }

        /// <summary>
        /// blockIndex 에서 stepCount 단계 위의 블록 id. stepCount 0 이면 자기 자신.
        /// 중간 단계가 하나라도 끊기면 false — 호출부는 이때 배수를 낮춰 재화 손해를 막아야 한다.
        /// </summary>
        public static bool TryGetUpgradedBlockId(int blockIndex, int stepCount, out int upgradedBlockIndex)
        {
            upgradedBlockIndex = blockIndex;
            if (blockIndex == 0) return false;

            for (int i = 0; i < stepCount; ++i)
            {
                if (TryGetUpgradedBlockId(upgradedBlockIndex, out int next) == false) return false;

                upgradedBlockIndex = next;
            }

            return true;
        }

        /// <summary>
        /// 이 생성기의 후보 블록이 전부 stepCount 단계 위를 갖는가. 하나라도 없으면 그 배수를 쓸 수 없다.
        /// 후보 하나만 확인하지 않는 이유: 확률 생성(Probabilistic)은 터치 순간에 후보가 정해져
        /// 어떤 후보가 뽑히느냐에 따라 소모량이 달라지면 손가락 가이드와 실제 소모가 어긋난다.
        /// 전 후보를 요구하면 판정이 뽑기 결과와 무관해져 가이드·소모가 항상 같은 값을 본다.
        /// </summary>
        public static bool CanUpgradeAllMakeBlocks(MergeEventBlockMakingTableData makingData, int stepCount)
        {
            if (makingData == null) return false;
            if (makingData.makeBlockIdx.IsNullOrEmpty()) return false;
            if (stepCount <= 0) return true;

            bool hasCandidate = false;
            for (int i = 0; i < makingData.makeBlockIdx.Length; ++i)
            {
                int blockIndex = makingData.makeBlockIdx[i];
                if (blockIndex == 0) continue;

                hasCandidate = true;
                if (TryGetUpgradedBlockId(blockIndex, stepCount, out int _) == false) return false;
            }

            return hasCandidate;
        }

        /// <summary>
        /// 이번 터치에 '실제로' 적용될 배수. 재화 소모량·생성 블록 레벨·손가락 가이드가 모두 이 한 함수만 본다.
        /// 요청 배수(토글)가 x2 이상이라도 <b>생성 후보 중 그 단계 수만큼 위가 없는 것이 있으면</b> 한 칸씩 낮춘다.
        /// 소모 전에 걸러 내는 것이 핵심이다 — 걸러 내지 않으면 '재화 2 소모 + 레벨 1 생성'이 되어 유저 손해다.
        ///
        /// 🔴 [2026-08-13] 종전에는 여기에 '성장 수(꽃 개수) 미달' 조건이 하나 더 있었는데,
        ///   개방 조건이 열쇠 획득으로 바뀐 뒤 그것만 남아 배수가 통째로 먹지 않았다(사유는 ClampGrowMultiplier 주석).
        /// </summary>
        public static int GetAppliedGrowMultiplier(MergeEventBlockMakingTableData makingData, int requestedMultiplier, int mergeEventId)
        {
            //표 전체가 아니라 **해금한 단계까지**로 자른 목록을 본다 — 표만 보면 아직 안 연 배수까지 통과한다.
            return GetAppliedGrowMultiplier(makingData, requestedMultiplier, GetUnlockedGrowMultipliers(mergeEventId));
        }

        /// <summary>
        /// 허용 배수 목록을 이미 들고 있을 때 쓰는 오버로드. 슬롯 루프처럼 반복 호출되는 경로는 반드시 이쪽을 쓴다
        /// (mergeEventId 를 받는 위 오버로드는 호출마다 목록을 새로 만든다).
        /// </summary>
        public static int GetAppliedGrowMultiplier(MergeEventBlockMakingTableData makingData, int requestedMultiplier, IReadOnlyList<int> allowed)
        {
            int clampedMultiplier = MergeEventHelper.ClampGrowMultiplier(requestedMultiplier, allowed);

            //[v17] 요청 배수가 요구하는 단계 수를 못 채우면 허용 목록에서 한 칸씩 낮춰 본다.
            //(v16 은 배수가 2뿐이라 되거나 1배거나 였지만, 4·8배가 생기면서 8은 안 되고 4는 되는 중간 상태가 실재한다.)
            for (int multiplier = clampedMultiplier; multiplier > MergeEventHelper.GROW_MULTIPLIER_DEFAULT; )
            {
                if (CanUpgradeAllMakeBlocks(makingData, GetUpgradeStepCount(multiplier))) return multiplier;

                multiplier = GetLowerMultiplier(allowed, multiplier);
            }

            return MergeEventHelper.GROW_MULTIPLIER_DEFAULT;
        }

        /// <summary>허용 목록에서 <paramref name="multiplier"/> 바로 아래 배수. 없으면 기본(1배).</summary>
        private static int GetLowerMultiplier(IReadOnlyList<int> allowed, int multiplier)
        {
            int lower = MergeEventHelper.GROW_MULTIPLIER_DEFAULT;
            if (allowed == null) return lower;

            for (int i = 0; i < allowed.Count; ++i)
            {
                if (allowed[i] >= multiplier) continue;
                if (allowed[i] > lower) lower = allowed[i];
            }

            return lower;
        }

        /// <summary>
        /// [FourDropItem · 화단 UI] 소환 대상 아이템 하나가 요구 단계 수를 채우는 최대 배수.
        /// <see cref="GetAppliedGrowMultiplier"/> 와 같은 규칙이지만 후보가 <b>Making 테이블이 아니라 아이템 id 하나</b>다 —
        /// 화단이 보드 블록에서 UI 패널(<c>MergeEventFourDropItemFieldPanel</c>)로 옮겨지면서 생성 대상이
        /// <c>MergeEvent_FourDropItemSet.itemIdx1</c> 하나로 확정됐기 때문이다.
        /// </summary>
        private static int GetAppliedGrowMultiplierForItem(int blockIndex, int requestedMultiplier, IReadOnlyList<int> allowed)
        {
            int clampedMultiplier = MergeEventHelper.ClampGrowMultiplier(requestedMultiplier, allowed);

            for (int multiplier = clampedMultiplier; multiplier > MergeEventHelper.GROW_MULTIPLIER_DEFAULT; )
            {
                if (TryGetUpgradedBlockId(blockIndex, GetUpgradeStepCount(multiplier), out int _)) return multiplier;

                multiplier = GetLowerMultiplier(allowed, multiplier);
            }

            return MergeEventHelper.GROW_MULTIPLIER_DEFAULT;
        }

        /// <summary>
        /// [FourDropItem] 이 이벤트·이 꽃 수(<paramref name="itemDropCount"/>)에 해당하는 레어 주기 행(<c>MergeEvent_FourDropItemCycle</c>).
        /// 행은 <c>eventIdx</c> + <c>itemDropCount</c> 조합으로 유일하다(501 실측 4행 = 꽃 1~4개).
        /// 꽃 수가 표 범위를 벗어나면 <b>가장 가까운 아래 행</b>으로 물러선다 — 표가 1~4 인데 화단 상한이 늘어도 조용히 멈추지 않게 한다.
        /// </summary>
        public static bool TryGetRareCycleTable(int mergeEventId, int itemDropCount, out MergeEventFourDropItemCycleTableData cycleTable)
        {
            cycleTable = null;
            if (itemDropCount <= 0) return false;

            Dictionary<int, MergeEventFourDropItemCycleTableData> table = TableManager.Instance.FindTable<MergeEventFourDropItemCycleTableData>();
            if (table == null) return false;

            foreach (MergeEventFourDropItemCycleTableData data in table.Values)
            {
                if (data.eventIdx != mergeEventId) continue;
                if (data.itemDropCount > itemDropCount) continue;
                //같은 이벤트에서 조건을 만족하는 행 중 itemDropCount 가 가장 큰 것(= 가장 가까운 아래 행)을 고른다.
                if (cycleTable != null && data.itemDropCount <= cycleTable.itemDropCount) continue;

                cycleTable = data;
            }

            return cycleTable != null;
        }

        /// <summary>
        /// 주기 <paramref name="step"/> 회차(0-based)의 레어 등장 확률(만분율). 배열 밖이면 0(= 등장 없음).
        /// </summary>
        public static int GetRareCycleRate(MergeEventFourDropItemCycleTableData cycleTable, int step)
        {
            if (cycleTable == null) return 0;
            if (cycleTable.rateArr.IsNullOrEmpty()) return 0;
            if (step < 0 || step >= cycleTable.rateArr.Length) return 0;

            return cycleTable.rateArr[step];
        }

        /// <summary>
        /// [FourDropItem] 이 이벤트의 <b>레어1 아이템</b>(테마01: 바구니) 블록 id — <c>MergeEvent_FourDropItemSet.itemIdx2</c>.
        /// 기획서 §7-3(5) *"레어로 등장할 아이템 idx — 예) 바구니"*. 501 실측 <c>260201</c>.
        /// 미발행이거나 Block_Main 에 없으면 false — 호출부는 그때 일반 아이템만 생성해 '아무것도 안 나오는' 상태를 피한다.
        /// </summary>
        public static bool TryGetRareItemId(int mergeEventId, out int rareBlockId)
        {
            rareBlockId = 0;
            if (TableManager.GetData(mergeEventId, out MergeEventFourDropItemSetTableData setTable) == false) return false;
            if (IsValidBlockIndex(setTable.itemIdx2) == false) return false;

            rareBlockId = setTable.itemIdx2;
            return true;
        }

        /// <summary>
        /// [FourDropItem · 화단 UI] 이번 터치의 <b>소환 계획</b> — 생성할 블록 id 와 소모할 재화량을 한 번에 확정한다.
        ///
        /// 🔴 <b>소환(<c>MergeEventFourDropItemFieldPanel.OnClickField</c>)과 손가락 가이드①이 반드시 이 함수 하나만 본다.</b>
        /// 둘이 각자 계산하면 '재화가 모자란데 손가락이 뜨거나(눌러도 부족 토스트)' '충분한데 안 뜨는' 어긋남이 생긴다 —
        /// 종전 보드 블록 화단(<c>EventBlockControllerGrow</c>)에서도 같은 이유로 술어를 한 곳에 모았었다.
        ///
        /// 기획서 970293249 §5-1 *"한 번에 사용되는 재화량 표시 … 최대 2배 … **2배 진행 시 레벨 1 상승하여 배출**"* —
        /// 배수는 <b>개수가 아니라 레벨</b>을 올린다. 생성 개수는 활성 화단 수가 그대로 정하고 그 판정은 호출부에 있다
        /// (<c>MergeEventHelper.GetGrowProduceCount</c>) — 이 함수는 '무엇을 몇 배로' 만 정한다.
        /// 승격 단계를 못 채우면 <see cref="GetAppliedGrowMultiplierForItem"/> 이 배수를 낮추므로
        /// <b>'재화 2 소모 + 레벨 1 산출' 같은 유저 손해가 구조적으로 불가능</b>하다.
        ///
        /// <paramref name="appliedMultiplier"/> 는 <b>실제로 적용된</b> 배수다(요청 배수가 낮춰졌으면 낮춰진 값).
        /// 레어(<c>itemIdx2</c>)에도 <b>같은 단계 수</b>를 태우려고 밖으로 낸다 —
        /// 검수 시트 row 27 *"배수 적용 시 레어 아이템의 레벨도 오를 수 있도록 처리"*.
        ///
        /// false = 소환 불가(Set 행 미발행 / <c>itemIdx1</c> 이 Block_Main 에 없음).
        /// </summary>
        public static bool TryGetSummonPlan(int mergeEventId, int requestedMultiplier, IReadOnlyList<int> allowed,
                                            out int summonBlockId, out int currencyConsume, out int appliedMultiplier)
        {
            summonBlockId     = 0;
            appliedMultiplier = MergeEventHelper.GROW_MULTIPLIER_DEFAULT;
            currencyConsume   = MergeEventHelper.GetGrowCurrencyConsume(MergeEventHelper.GROW_MULTIPLIER_DEFAULT);

            if (TableManager.GetData(mergeEventId, out MergeEventFourDropItemSetTableData setTable) == false) return false;

            //0(미발행)뿐 아니라 Block_Main 에 없는 id(오타 등)도 걸러낸다.
            //통과시키면 테이블 조회가 실패하는 쓰레기 블록이 보드에 놓여 머지·클릭·보관 어느 것도 안 되는 칸이 된다.
            int baseBlockId = setTable.itemIdx1;
            if (IsValidBlockIndex(baseBlockId) == false) return false;

            appliedMultiplier = GetAppliedGrowMultiplierForItem(baseBlockId, requestedMultiplier, allowed);
            //GetAppliedGrowMultiplierForItem 이 '그 단계 수를 채운다'를 이미 보장하지만, 승격이 실패하면 1배로 물러선다
            //(배수만 올라가고 레벨은 그대로인 상태를 만들지 않는다).
            if (TryGetUpgradedBlockId(baseBlockId, GetUpgradeStepCount(appliedMultiplier), out summonBlockId) == false)
            {
                appliedMultiplier = MergeEventHelper.GROW_MULTIPLIER_DEFAULT;
                summonBlockId     = baseBlockId;
            }

            currencyConsume = MergeEventHelper.GetGrowCurrencyConsume(appliedMultiplier);
            return true;
        }

        /// <summary>
        /// [손가락 가이드 ①] <b>화단(UI)</b>을 지금 터치하면 실제로 아이템이 나오는가.
        /// <c>MergeEventFourDropItemFieldPanel.OnClickField</c> 의 선행 조건(소환 계획 성립 / 재화 잔량 ≥ 소모량)과
        /// <b>같은 함수</b>(<see cref="TryGetSummonPlan"/>)를 본다 — 어긋나면 터치해도 재화 부족·무반응으로 끝나 안내가 데드락이 된다.
        ///
        /// 🔴 종전에는 <c>blockType Grow</c> 인 <b>보드 블록</b>을 스캔했는데, 기획서 v32 §7-3(11)로 화단이 UI 로 확정되면서
        /// 그 값이 CSV 에서 사라져(501 실측 `bT 10` 0행) 이 안내가 <b>영구 실패</b>하고 있었다(계획서 `Q28`).
        /// 빈 슬롯 조건은 호출부가 본다 — 이 함수는 테이블·재화만 판정한다(보드 탐색기는 팝업 쪽에 있다).
        /// </summary>
        public static bool IsGuidableFieldPanel(int mergeEventId, int activeFieldCount, int currencyPoint, int growMultiplier, IReadOnlyList<int> allowed)
        {
            if (MergeEventHelper.GetGrowProduceCount(activeFieldCount) <= 0) return false;
            if (TryGetSummonPlan(mergeEventId, growMultiplier, allowed, out int _, out int currencyConsume, out int _) == false) return false;

            return currencyPoint >= currencyConsume;
        }

        /// <summary>
        /// [손가락 가이드 ②] 활성화 블록을 지금 터치하면 상호작용 오브젝트가 켜지는가.
        /// <c>EventBlockControllerActivate.BlockClickProcess</c> 의 블록측 선행 조건(최종 단계 + 드롭 계획 유효)과 <b>같은 값</b>을 본다.
        /// 상호작용 오브젝트 자체의 상태(프리팹 바인딩 여부·이미 활성 여부)는 블록 데이터로 알 수 없어 호출부가 따로 확인한다.
        ///
        /// 🔴 종전에는 이 블록의 <c>Block_Making</c>(수량·대상)을 봤는데, 생성 대상·수량의 정본이
        /// <see cref="TryGetActiveDropInfo"/>(= <c>MergeEvent_FourDropItemSet</c>)로 옮겨진 뒤에도 그대로 남아 있었다.
        /// 501 의 활성화 아이템(<c>261307</c>)에는 <b>Making 행 자체가 없어</b> 이 안내가 영구 false 였다(계획서 `Q30`).
        /// </summary>
        public static bool IsGuidableActivateBlock(FsEventBlockData blockData, int mergeEventId)
        {
            if (IsFinalStage(blockData) == false) return false;

            //컨트롤러(EventBlockControllerActivate:48)와 **같은 술어**여야 한다 —
            //느슨하면 '손가락은 떴는데 탭해도 무반응', 빡빡하면 '쓸 수 있는데 안내가 없는' 공백이 된다.
            return TryGetActiveDropInfo(mergeEventId, out int _, out int _);
        }

        /// <summary>
        /// 이 특별 아이템을 획득하면 해금되는 구름 그룹 id. false = 해금 대상 아님(groupId 0).
        /// 조건(MergeEvent_Special): sBlockType 이 FirstAcquireType(5)/SpecialFirstAcquireType(6) + mergeType 이 LockedCloudOpen + value1[0] > 0.
        /// 컬럼 계약(기획서 v17 §7-3(3)): index = 획득 아이템(팝업 가운데 노출) / **value1 = 해금할 구름 그룹 번호** / value2 = MergeEvent_RewardGroup 번호.
        /// ※ v16 까지 표·본문이 어긋나 있었으나(D5) v17 에서 위 배치로 정리됐다.
        /// 최초 획득 트리거(MergeEvent.TryTriggerFourDropItemAcquire)와 보드 로드 재동기화(SyncUnlockedCloudGroups)가
        /// 서로 다른 판정을 쓰면 한쪽만 해금되는 상태가 생기므로 조건을 여기 한 곳에 모은다.
        /// </summary>
        public static bool TryGetCloudUnlockGroupId(MergeEventBlockSpecialTableData specialTable, out int groupId)
        {
            groupId = 0;
            if (IsSpecialRowForFourDropItem(specialTable) == false) return false;
            if (specialTable.sBlockType != EventBlockSpecialType.FirstAcquireType
                && specialTable.sBlockType != EventBlockSpecialType.SpecialFirstAcquireType) return false;
            if (specialTable.mergeType != EventBlockMergeType.LockedCloudOpen) return false;
            if (specialTable.value1.IsNullOrEmpty()) return false;

            groupId = specialTable.value1[0];
            return groupId > 0;
        }

        /// <summary>
        /// [HL-915] 지금 열려 있는 머지 이벤트가 FourDropItem 인가.
        ///
        /// <b>공용 팝업이 4드롭에서만 다른 사운드를 낼 때 쓰는 게이트</b>다. 팝업 자신에게 물어볼 수 없어서 존재한다 —
        /// <c>UIPopupMergeEventNewItemInfo</c> 는 <c>eventID</c> 를 필드로 갖지만 그 값은 <c>SetInfo</c> 에서 들어오는데,
        /// 오픈음을 정하는 <c>OnEnable</c> 은 그보다 <b>먼저</b> 돈다(신규 생성이면 Instantiate 시점, 풀 재사용이면 SetActive 시점).
        /// 그 시점의 <c>eventID</c> 는 아직 0 이거나 직전 이벤트 값이라 판정 근거로 쓸 수 없다.
        ///
        /// 🔴 <c>MergeEventHelper.GetMergeEvent()</c> 를 쓰지 않는다 — 그쪽은 못 찾으면 <c>DLogger.Error</c> 를 찍는데
        ///   (게다가 무관한 함수명으로 오귀속돼 있다) 여기는 '아니면 조용히 false' 가 정상 동작인 자리다.
        /// </summary>
        public static bool IsFourDropItemActive()
        {
            UIPopupMergeEvent popup = UIManager.Instance.FindUIWindow<UIPopupMergeEvent>();
            if (popup == null) return false;

            MergeEvent mergeEvent = popup.MergeEvent;
            if (mergeEvent == null || mergeEvent.MergeEventData == null) return false;

            return MergeEventHelper.GetMergeEventType(mergeEvent.MergeEventData.id) == MergeEventEvType.FourDropItem;
        }

        /// <summary>
        /// [FourDropItem] 이 구름 그룹이 <b>특별 아이템 구간</b>인가.
        /// 기획서 §3-2 10번 — *"**특별한 아이템 구간**에는 구름과 열쇠의 **색상이 다르도록** 구성"*.
        ///
        /// 판정은 그 그룹을 <b>여는</b> MergeEvent_Special 행의 <c>sBlockType</c> 이다 —
        /// <c>5</c>(FirstAcquireType) = 일반 / <c>6</c>(SpecialFirstAcquireType) = 특별.
        /// 501 발행분은 그룹 1~12 가 <b>홀수=5 / 짝수=6</b> 으로 교대한다(CSV 실측). 다만 그 교대는 발행값일 뿐이므로
        /// 번호 홀짝으로 판정하지 않는다 — 테이블이 바뀌면 조용히 틀린다.
        /// 같은 그룹을 여는 행이 여럿이면 하나라도 6 이면 특별로 본다(특별 연출을 놓치는 쪽보다 안전하다).
        ///
        /// <see cref="TryGetCloudUnlockGroupId"/> 는 5·6 을 <b>같게</b> 취급해 groupId 만 돌려주므로(해금 여부만 보면 되는 자리),
        /// 색 축이 필요한 곳은 이 함수를 쓴다.
        /// </summary>
        public static bool IsSpecialCloudGroup(int groupId)
        {
            if (groupId <= 0) return false;

            Dictionary<int, MergeEventBlockSpecialTableData> dicSpecialTable = TableManager.Instance.FindTable<MergeEventBlockSpecialTableData>();
            if (dicSpecialTable == null) return false;

            foreach (MergeEventBlockSpecialTableData specialTable in dicSpecialTable.Values)
            {
                if (TryGetCloudUnlockGroupId(specialTable, out int unlockGroupId) == false) continue;
                if (unlockGroupId != groupId) continue;
                if (specialTable.sBlockType != EventBlockSpecialType.SpecialFirstAcquireType) continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// 이 MergeEvent_Special 행을 FourDropItem 이 해석해도 되는가.
        /// MergeEvent_Special 은 전 이벤트 타입이 공유하는 테이블이라, blockIdx 대역이 겹치는 순간
        /// 다른 이벤트의 행이 sBlockType 5/6 + LockedCloudOpen 조합을 우연히 만족할 수 있다.
        /// 그때 이 필터가 없으면 BossRaid/GetPoint 보드에서 구름 해금 API 가 돌고 축하 팝업이 뜬 뒤 그 상태가 저장된다.
        /// [v17 §7-3(3)] mergeEventType 은 이 행이 적용되는 이벤트 타입이고 None(0) 이 '공용' 규약이다.
        /// ※ 이것은 '행'에 대한 필터일 뿐이다. 공용 행은 여기서 통과하므로,
        ///   '지금 이 이벤트가 FourDropItem 인가'는 호출자 쪽 컨텍스트 게이트가 따로 책임진다
        ///   (<see cref="SyncUnlockedCloudGroups"/> 는 자체 게이트를 갖고 있고,
        ///    획득 트리거는 MergeEvent.TryTriggerFourDropItemAcquire 상단에서 막는다).
        /// </summary>
        public static bool IsSpecialRowForFourDropItem(MergeEventBlockSpecialTableData specialTable)
        {
            if (specialTable == null) return false;

            return specialTable.mergeEventType == MergeEventEvType.None
                   || specialTable.mergeEventType == MergeEventEvType.FourDropItem;
        }

        /// <summary>
        /// [검수 시트 7번 · 2026-08-13] 이 블록이 <b>특별 구간(보라) 축하 팝업 대상</b>인가 —
        /// <c>MergeEvent_Special.sBlockType == 6(SpecialFirstAcquireType)</c>.
        ///
        /// 기획 지적: *"보라색 잠금 해제했을 때 스페셜 팝업이 아닌 **일반 팝업**이 나오고 있음"*.
        /// 원인은 두 팝업이 <b>같은 획득에서 함께 열리는</b> 것이다 —
        /// 축하 팝업(<c>UIPopupFourDropItemSpecialResult</c>)과 일반 발견 팝업(<c>blockFindPopupPrefabPath</c>)이
        /// 각각 <see cref="MergeEvent.TryTriggerFourDropItemAcquire"/> 와 <c>OnOpenPopupWitDelayed</c> 에서 독립적으로 뜬다.
        /// → 특별 구간 아이템은 <b>축하 팝업만</b> 남기고 일반 발견 팝업을 건너뛴다.
        ///
        /// 🔴 컬럼을 그대로 갈아끼우지 않은 이유 — <c>specialRewardPopupPath</c>(=<c>UIPopupFourDropItemSpecialResult</c>) 프리팹은
        /// <b>독립 팝업(UIBasePopup)</b> 이라 <c>MergeEventNewItemInfo</c> 컴포넌트가 없다. 발견 팝업 자리에 넣으면
        /// <c>UIPopupMergeEventNewItemInfo</c> 가 컴포넌트를 못 찾아 <b>열자마자 닫힌다</b>.
        /// 그 컬럼은 축하 팝업 오픈 지점이 <b>어드레스로</b> 쓰고 있다(<c>MergeEvent.GetSpecialResultPopupPath</c>).
        /// </summary>
        public static bool IsSpecialRewardBlock(int mergeEventId, int blockIndex)
        {
            if (MergeEventHelper.GetMergeEventType(mergeEventId) != MergeEventEvType.FourDropItem) return false;
            if (TableManager.GetData(blockIndex, out MergeEventBlockSpecialTableData specialTable) == false) return false;
            if (IsSpecialRowForFourDropItem(specialTable) == false) return false;

            return specialTable.mergeType == EventBlockMergeType.LockedCloudOpen
                   && specialTable.sBlockType == EventBlockSpecialType.SpecialFirstAcquireType;
        }

        /// <summary>
        /// [FourDropItem] 구름 그룹이 열렸는가. 컨베이어 트리거(CloudGroupOpen)의 판정 진입점이다.
        /// 판정을 FakeServer 권위 상태 한 곳(FsMergeEventState.IsCloudGroupUnlocked)에 모아 둔다 —
        /// 보드를 훑어 답하면 '그 그룹 구름이 원래 없었음'과 '해금됨'이 구분되지 않고, 화면 밖 대기열·보관함도 못 본다.
        /// [2026-08-07] 해제 경로는 **획득 하나뿐**이다(라운드 비교 제거).
        /// </summary>
        public static bool IsCloudGroupUnlocked(int groupId)
        {
            FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_MergeEvent_IsCloudGroupUnlocked(groupId, out bool unlocked);

            return unlocked;
        }

        /// <summary>
        /// [FourDropItem] 보드에 덮여 있는 구름 그룹 중 다음에 열릴 그룹 번호(= 가장 작은 번호). 없으면 0.
        /// 기획서 §4-2 '잠금 블록 1' — "다음에 해금될 잠금 영역 그룹을 밝은 색상으로 표시" 판정에 쓴다.
        /// 판정을 FakeServer 한 곳에 두는 이유는 <see cref="IsCloudGroupUnlocked"/> 와 같다 —
        /// 뷰가 각자 훑으면 블록마다 다른 답을 내 같은 그룹인데 밝기가 섞이는 화면이 나온다.
        /// </summary>
        public static int GetNextCloudGroup()
        {
            FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_MergeEvent_GetNextCloudGroup(out int groupId);

            return groupId;
        }

        /// <summary>
        /// [FourDropItem] 진행도 게이지에서 <b>열쇠 말풍선이 붙을 단계</b>의 블록 idx. 없으면 -1.
        /// 기획서 §5-1 진행도 영역 상세 — *"특정 단계에 열쇠 말풍선 표시 / **다음에 열리는 단계 1개에만** 표시"*.
        ///
        /// 🔴 열쇠 찾기와 <b>축이 다르다</b>. 그쪽은 이 대상을 MergeEvent_Round 의 roundClearType(FindKeyBlock)+value1 에서 얻는데,
        ///   FourDropItem 501 발행분은 라운드가 <b>1행뿐이고 roundClearType 이 None</b> 이라 그 축이 통째로 비어 있다
        ///   → 공용 경로(MergeEvent.UpdateRoundKeyBubble)가 조기 반환하고 말풍선이 저작 좌표에 방치된다.
        ///   이 콘텐츠에서 '다음 목표'는 라운드가 아니라 <b>다음에 열릴 구름 그룹</b>이므로 그쪽에서 답을 얻는다.
        ///
        /// 그 그룹을 여는 MergeEvent_Special 행의 <c>index</c>(= 획득 대상 머지 아이템 idx)가 곧 게이지의 그 단계다
        /// (501 발행분: 260105~260116 이 그룹 1~12 와 1:1, 게이지 항목 260101~260116 안에 모두 들어 있다).
        /// 위치 계산은 공용 <see cref="MergeEventCollectScroll.SetRoundKeyIndex"/> 가 그대로 하고,
        /// 여기서 바뀌는 것은 '어느 단계인가' 와 '그 단계가 무슨 색인가' 둘뿐이다.
        ///
        /// <paramref name="isSpecialSection"/> 은 기획서 §3-2 10번 *"**특별한 아이템 구간**에는 구름과 **열쇠의** 색상이 다르도록 구성"*
        /// 의 열쇠 축이다(말풍선 안 Key01 = 일반 / Key02 = 특별).
        /// </summary>
        public static bool TryGetKeyBubbleBlock(out int blockIndex, out bool isSpecialSection)
        {
            blockIndex       = -1;
            isSpecialSection = false;

            int nextCloudGroup = GetNextCloudGroup();
            //남은 그룹이 없다(전부 해금)= 더 가리킬 단계가 없다 → 호출측이 말풍선을 끈다.
            if (nextCloudGroup <= 0) return false;

            Dictionary<int, MergeEventBlockSpecialTableData> dicSpecialTable = TableManager.Instance.FindTable<MergeEventBlockSpecialTableData>();
            if (dicSpecialTable == null) return false;

            foreach (MergeEventBlockSpecialTableData specialTable in dicSpecialTable.Values)
            {
                //해금 판정은 트리거·재동기화와 같은 술어를 쓴다(행 필터·타입·컬럼 계약이 한 곳에 모여 있다).
                if (TryGetCloudUnlockGroupId(specialTable, out int unlockGroupId) == false) continue;
                if (unlockGroupId != nextCloudGroup) continue;

                blockIndex = specialTable.index;
                break;
            }

            if (blockIndex < 0) return false;

            //🔴 색은 이 행의 sBlockType 을 직접 읽지 않고 **자물쇠와 같은 술어**(IsSpecialCloudGroup)로 정한다.
            //  기획서가 구름·자물쇠·열쇠를 함께 특별색으로 묶었으므로 판정이 갈리면 한쪽만 보라가 되는 어긋남이 생긴다.
            //  (그 술어는 '같은 그룹을 여는 행이 여럿이면 하나라도 6 이면 특별' 규칙까지 포함한다 — 여기서 재구현하지 않는다.)
            //  테이블 스캔이 한 번 더 돌지만 이 경로는 팝업 오픈·보드 갱신·구름 해금 때만 지나므로 비용이 문제되지 않는다.
            isSpecialSection = IsSpecialCloudGroup(nextCloudGroup);
            return true;
        }

        /// <summary>
        /// 보드 로드 시점 구름 재동기화: 이미 도감에 등록된 특별 아이템의 구름 그룹을 일괄 해금한다.
        /// 획득 트리거만으로는 구멍이 남는다 — 트리거 아이템을 이전 라운드에서 이미 수집·소모한 뒤
        /// 그 그룹의 구름이 깔린 맵으로 진입하면 다시 획득할 일이 없어 해금 기회가 영영 오지 않는다.
        /// [2026-08-07] 라운드 해제가 사라진 뒤로 이 보정은 **더 중요해졌다** — 이제 그 그룹은 영구 잠금이 된다.
        /// UnlockCloudGroup 은 멱등이라 이미 해금된 그룹은 0건을 돌려주고 저장도 하지 않는다.
        /// 슬롯 뷰가 만들어지기 전에 호출하는 것을 전제로 한다 — 그래야 부분 갱신(RefreshSlotViews)도,
        /// 보드 전환 재진입도 없이 처음부터 해금된 상태로 그려진다.
        /// ⚠️ 복구 기준이 **도감 등록**이라, 구 빌드에서 라운드로만 열려 있던 그룹은 여기서 복구되지 않는다
        ///    (필드 주석 `unlockedCloudGroups` 참조 — 로컬 구 세이브는 이벤트 리셋 후 검증할 것).
        /// </summary>
        public static void SyncUnlockedCloudGroups(FsMergeEventState mergeEventState)
        {
            if (mergeEventState == null) return;
            if (MergeEventHelper.GetMergeEventType(mergeEventState.id) != MergeEventEvType.FourDropItem) return;

            Dictionary<int, MergeEventBlockSpecialTableData> dicSpecialTable = TableManager.Instance.FindTable<MergeEventBlockSpecialTableData>();
            if (dicSpecialTable == null) return;

            FsProcessMergeEvent process = FsWebManager.GetProcess<FsProcessMergeEvent>();
            foreach (MergeEventBlockSpecialTableData specialTable in dicSpecialTable.Values)
            {
                if (TryGetCloudUnlockGroupId(specialTable, out int groupId) == false) continue;
                //Special.index = 획득한 머지 아이템 idx. 도감에 이미 등록돼 있으면 해금 조건을 이미 충족한 것이다.
                if (mergeEventState.ExistBlockIndexInCollection(specialTable.index) == false) continue;

                process.DirectApi_MergeEvent_UnlockCloudGroup(groupId, out _);
            }
        }

        /// <summary>
        /// 맵 CSV(MergeEvent_FourDropItemMap)의 구름 기입 오류를 보드 로드 시점에 크게 남긴다.
        /// 두 오류 모두 '조용히 잘못 동작'해서 플레이로는 원인을 못 찾는 종류라 로그가 유일한 단서다.
        ///  (1) cloudState = 1 인데 cloudGroup 이 없다 → 그룹 0 은 규약상 '구름 없음' 이라 구름이 아예 안 보인다.
        ///      (판정은 MergeEventFourDropItemMapTableData.IsCloudCovered 가 읽는 지점에서 끊는다.)
        ///  (2) cloudGroup 은 있는데 cloudState = 0 이다 → 그룹 지정이 조용히 버려지고 구름 퍼즐이 통째로 스킵된다.
        ///  (3) 그 그룹을 여는 Special 행이 없다 → 해제 경로가 아예 없다.
        ///      구름 밑 블록이 영구 접근 불가가 되고, 그 구름이 0행에 닿으면 컨베이어까지 라운드 내내 멈춘다(계획서 §0-2g V3).
        /// [2026-08-07] 라운드 해제가 제거되면서 <b>(1-b) '해제 라운드가 이미 지났다' 검사는 삭제했다</b> —
        /// cloudGroup 은 더 이상 라운드와 비교되지 않으므로 그 경고는 전부 오탐이 된다.
        /// 대신 (3)에서 라운드 조건을 빼 <b>모든 그룹</b>이 Special 행을 갖는지 보게 했다(해제 경로가 획득뿐이라 이제 필수 조건이다).
        /// [v17] 두 값이 blockCondition 에서 분리돼 전용 컬럼이 됐으므로, 더 이상 blockCondition 값(14/15/16 오기입)을
        /// 볼 필요가 없다 — 피킹 계열 blockConditionValue 와도 축이 완전히 갈려 오탐 여지가 사라졌다.
        /// 검사를 FsEventBlockData 생성자(공용)에 두지 않은 이유: 그 경로는 전 타입이 공유하고,
        /// 이 검사는 FourDropItem 맵에만 의미가 있다.
        /// </summary>
        public static void ValidateCloudMapRows(FsMergeEventState mergeEventState)
        {
            if (mergeEventState == null) return;
            if (MergeEventHelper.GetMergeEventType(mergeEventState.id) != MergeEventEvType.FourDropItem) return;

            Dictionary<int, MergeEventFourDropItemMapTableData> dicMapTable = TableManager.Instance.FindTable<MergeEventFourDropItemMapTableData>();
            if (dicMapTable == null) return;

            //획득으로 열 수 있는 구름 그룹 집합(MergeEvent_Special 기준). 아래 (3) 검사 전용이다.
            //[2026-08-07] 해제 경로가 획득 하나뿐이 되면서 이 집합에 없는 그룹은 **열 방법이 아예 없다**
            //= 구름 밑 블록 영구 접근 불가 + 그 구름이 0행에 닿으면 컨베이어 정지. 라운드 조건은 더 이상 보지 않는다.
            HashSet<int> acquirableGroups = new();
            Dictionary<int, MergeEventBlockSpecialTableData> dicSpecialTable = TableManager.Instance.FindTable<MergeEventBlockSpecialTableData>();
            if (dicSpecialTable != null)
            {
                foreach (MergeEventBlockSpecialTableData specialTable in dicSpecialTable.Values)
                {
                    if (TryGetCloudUnlockGroupId(specialTable, out int unlockGroupId) == false) continue;

                    acquirableGroups.Add(unlockGroupId);
                }
            }

            int eventId = mergeEventState.id;
            int round = mergeEventState.round;
            foreach (MergeEventFourDropItemMapTableData mapData in dicMapTable.Values)
            {
                if (mapData.mergeEventIdx != eventId || mapData.eventRound != round) continue;
                if (mapData.blockIdx == 0) continue;

                //cloudGroup 0 + cloudState 1 은 기획 규약상 '구름 없음'으로 정의된 조합이라 오류가 아니다.
                //다만 구름을 의도했는데 그룹만 빠뜨린 경우와 표기가 같아 구분이 불가능하므로 경고로 남긴다.
                if (mapData.cloudState != 0 && mapData.cloudGroup <= 0)
                {
                    DLogger.Warning($"[FourDropItem] cloudState 가 무시된다(cloudGroup 0 = 구름 없음). 구름을 의도했다면 그룹을 1 이상으로 기입할 것. row[{mapData.index}] round[{round}] cloudState[{mapData.cloudState}] cloudGroup[{mapData.cloudGroup}]");
                    continue;
                }

                bool isCovered = mapData.IsCloudCovered;
                if (isCovered == false && mapData.cloudGroup > 0)
                    DLogger.Error($"[FourDropItem] cloudGroup 이 무시된다 — 구름을 의도했다면 cloudState 를 1 로 기입할 것. row[{mapData.index}] round[{round}] cloudState[{mapData.cloudState}] cloudGroup[{mapData.cloudGroup}]");

                //(3) 열 방법이 없는 구름. 해제 경로가 획득 하나뿐이라 Special 행이 없으면 그대로 영구 잠금이다.
                //    구름 밑 블록은 클릭·머지·드래그·보관 전부 막혀 있어 접근 불가이고,
                //    그 구름이 컨베이어로 0행까지 내려오면 GetUnboxingRowIndex 가 -1 을 돌려 라운드 내내 컨베이어가 멈춘다.
                //    맵 최하단 행(axisY 1)에 놓이면 라운드 진입 시점부터 그 상태다.
                if (isCovered && acquirableGroups.Contains(mapData.cloudGroup) == false)
                    DLogger.Error($"[FourDropItem] 열 방법이 없는 구름이다 — 이 그룹을 여는 MergeEvent_Special 행(value1 = 그룹)이 없다. row[{mapData.index}] round[{round}] cloudGroup[{mapData.cloudGroup}] axisY[{mapData.axisY}]");
            }
        }

        /// <summary>
        /// [손가락 가이드 ③] 성장 소모품(씨앗)을 지금 쓰면 화단이 실제로 늘어나는가.
        /// 이미 최대치면 <c>EventBlockControllerGrowItem</c> 이 <b>소모만 하고 안내</b>를 띄우므로(기획서 §4-1) 유도할 일이 아니다.
        ///
        /// 🔴 종전에는 <c>blockType Grow</c> 인 보드 블록을 찾아 그 <c>growCount</c> 를 봤다 —
        /// 화단이 UI 로 옮겨진 뒤로는 대상이 <c>MergeEventFourDropItemFieldPanel</c> 하나뿐이라 활성 화단 수만 보면 된다(계획서 `Q28`).
        /// </summary>
        public static bool CanUseGrowItem(int activeFieldCount)
        {
            return activeFieldCount < MergeEventHelper.GROW_MAX_COUNT;
        }

        /// <summary>
        /// [무한모드 순위 표기] MergeEvent_Rank.damageRank 를 기획서 v18 §5-1 규칙대로 문자열로 만든다.
        /// 1% 이상은 정수로, 1% 미만은 소수점 2째 자리까지("0.50" / "0.10").
        /// <b>표시 전용</b> 이며 값 판정에는 쓰지 않는다.
        ///
        /// 소수점 구분자를 InvariantCulture 로 못박는 이유: 이 문자열은 그대로 LIdx 포맷("Top {0}%")에 들어가는데,
        /// 쉼표를 소수 구분자로 쓰는 로케일에서 기기 문화권을 따르면 "0,50" 이 되어 표기가 깨진다.
        /// 그래서 float 를 <see cref="GameCore.Generic.StringExtensions"/> 의 L(params object[]) 에 직접 넘기지 말고
        /// 반드시 이 함수를 거친 문자열을 넘긴다.
        /// </summary>
        public static string FormatRankPercent(float damageRank)
        {
            //경계는 엄격히 1 미만이다. damageRank == 1 은 "1" 이지 "1.00" 이 아니다.
            if (damageRank < RANK_PERCENT_DECIMAL_THRESHOLD)
                return damageRank.ToString(RANK_PERCENT_DECIMAL_FORMAT, CultureInfo.InvariantCulture);

            return ((int)damageRank).ToString(CultureInfo.InvariantCulture);
        }

        #region 도감 완성 팝업 전환 시퀀스
        /// <summary>
        /// [FourDropItem] <b>도감 완성</b> 직후의 팝업 전환. 리드 지정 플로우 그대로다:
        /// <c>도감 완성 팝업 활성화 → 도감 팝업 비활성화 → 메인 팝업 비활성화
        /// → 도감 완성 팝업 종료 → 메인 팝업 활성화</c>.
        ///
        /// 🔴 <b>[기획 변경 2026-08-15] 이 시퀀스는 더 이상 무한모드 진입과 무관하다.</b>
        ///   진입 기준이 메인 블록 최종 단계 획득으로 바뀌어(<see cref="MergeEventHelper.IsFourDropItemInfinitePhase"/>)
        ///   도감 42종을 다 모으기 훨씬 전에 이미 무한 페이즈다. 여기 남은 것은 <b>도감 완성 축하 연출</b> 뿐이고,
        ///   재입장 시 화면은 <c>SetInfoAsync</c> 가 그 시점 페이즈대로 다시 그린다(대개 이미 무한모드 UI).
        ///
        /// 🔴 트리거는 <b>표준 <c>allDone</c> 경로</b>다(<see cref="MergeEvent.OpenCollectionAchievePopupAsync"/>) —
        ///   타 이벤트 전부가 같은 자리에서 <c>completePopupPrefabPath</c> 를 띄우므로 여기에 얹는 것이 표준화다.
        ///   도감 보상 수령 쪽에 따로 트리거를 두지 않는다(두 기준이 공존하면 같은 팝업이 두 번 뜬다).
        ///
        /// 🔴 <b>순서가 곧 안전장치다.</b> 완성 팝업을 **먼저 띄우고 닫힘 콜백까지 걸어 둔 뒤에** 두 팝업을 내린다 —
        ///   반대로 하면 메인이 닫히는 순간 이 흐름을 몰던 MonoBehaviour 가 사라져 팝업이 뜨지 않거나
        ///   재입장 예약이 걸리지 않는다. 그래서 오픈·예약은 호출자가 끝낸 상태로 받고, 여기서는 닫기만 한다.
        ///
        /// FourDropItem 이 아니면 <c>false</c> 를 돌려주고 아무 것도 하지 않는다 → 호출자가 기존 콜백을 그대로 건다(타 이벤트 동작 불변).
        /// </summary>
        /// <param name="mergeEventState">판정용 상태(이벤트 타입 확인).</param>
        /// <param name="achievePopup">이미 열려 있는 <c>completePopupPrefabPath</c> 팝업.</param>
        /// <param name="closeMainPopup">메인 팝업 닫기 수단(<see cref="MergeEvent.RequestClose"/>).</param>
        /// <returns>이 시퀀스가 흐름을 가져갔는가.</returns>
        public static bool TryPlayCollectionCompleteFlow(FsMergeEventState mergeEventState, UIPopupMergeEventAchieve achievePopup, Action closeMainPopup)
        {
            if (mergeEventState == null || achievePopup == null) return false;
            if (MergeEventHelper.GetMergeEventType(mergeEventState.id) != MergeEventEvType.FourDropItem) return false;

            //④→⑤ 재입장 예약을 **먼저** 건다. 아래에서 메인을 닫으면 이 자리로 다시 올 수 없다.
            //  콜백이 정적 메서드인 것도 같은 이유다 — 닫히는 팝업의 인스턴스를 붙잡으면 파괴와 함께 사라진다.
            achievePopup.SetCloseCallBack(ReopenMergeEventMainPopup);

            //②도감 팝업 비활성화. 이 경로(마지막 블록 획득)에서는 보통 닫혀 있지만,
            //  열어 둔 채 도달하는 경로가 생겨도 완성 팝업이 그 뒤에 가리지 않도록 명시적으로 내린다.
            UIPopupMergeEventBook bookPopup = UIManager.Instance.FindUIWindow<UIPopupMergeEventBook>();
            if (bookPopup != null) bookPopup.Close();

            //③메인 팝업 비활성화. 닫기 버튼과 같은 경로다(오프너가 넘긴 CloseAction).
            closeMainPopup?.Invoke();
            return true;
        }

        /// <summary>
        /// 메인 팝업 재입장. 로비 진입과 <b>같은 경로</b>를 쓴다 —
        /// <c>SetInfoAsync</c> 가 처음부터 다시 돌아 페이즈 판정·진행도 HUD 배치가 그 안에서 이뤄진다.
        /// </summary>
        private static void ReopenMergeEventMainPopup()
        {
            UIManager.OpenUIMsgAsync<UIPopupMergeEvent>().Forget();
        }
        #endregion

        /// <summary>
        /// [무한모드 순위] <c>MergeEvent_Rank</c> 조회에 먹일 <b>누적</b> 점수.
        ///
        /// 🔴 그 시트는 누적 축이다 — <c>damageMin/damageMax</c> 가 0~9999999 로 발행돼 있고 컬럼명도 '누적 대미지'다.
        /// 반면 <c>eventPoint</c> 는 라운드 보상 수령에서 <c>value1</c> 만큼 <b>차감</b>되므로 그것만 보면
        /// 라운드를 넘길 때마다 순위가 최하위 구간(0~100)으로 되돌아간다.
        /// → 차감된 분을 <c>infinityAccPoint</c> 에 옮겨 담고(FsProcessMergeEvent.DirectApi_TryGainRoundReward),
        ///   여기서 둘을 합쳐 <b>이벤트 시작부터의 총 획득량</b>을 만든다.
        ///
        /// 선례는 BossRaid 의 <c>MergeEventBossRaidHelper.GetTotalAccPoint</c> 로, 구조가 같다
        /// (그쪽은 <c>BossRaidState.accPoint + eventPoint</c>). BossRaid 필드를 재사용하지 않은 이유는
        /// 그 필드 주석이 경고하는 '한 필드 두 의미' 오염을 되풀이하지 않기 위해서다.
        ///
        /// 🔴 <b>누적분은 인자가 아니라 권위 상태에서 되읽는다.</b> 호출자가 넘기는 것은 UI 사본
        /// (<c>MergeEvent.mergeEventData</c> = <c>ActiveMergeEventState.Clone()</c>)이고, 그 사본의
        /// <c>infinityAccPoint</c> 는 <b>갱신되는 자리가 어디에도 없다</b> — 누적은 FakeServer 쪽에서만 오른다
        /// (<c>FsProcessMergeEvent</c> 의 반복 보상 처리). 사본 값을 그대로 쓰면 팝업을 연 시점 값(대개 0)에 박혀
        /// 실질 점수가 <c>eventPoint</c> 하나가 되는데, 그 값은 반복 보상마다 차감돼 0~repeatRewardPoint 를 순환하므로
        /// <b>순위가 최하위 구간(상위 100%)에 영구히 고정</b>된다.
        /// <c>eventPoint</c>·<c>boosterOpenCount</c> 가 각각 되읽기로 사본을 맞추는 것과 같은 규약이다.
        /// </summary>
        public static int GetInfinitePhaseRankScore(FsMergeEventState mergeEventState)
        {
            if (mergeEventState == null) return 0;

            FsWebManager.GetProcess<FsProcessMergeEvent>().DirectApi_MergeEvent_GetInfinityAccPoint(out int infinityAccPoint);

            return infinityAccPoint + mergeEventState.eventPoint;
        }

        /// <summary>
        /// [무한모드 순위 표기] 무한 페이즈일 때 순위 문구를 만든다. <b>표시 전용</b>.
        ///
        /// 기획서 §5-1 진행도 무한 모드가 지정한 형태는 <b>라벨 한 줄 + 퍼센트 한 줄</b>이다 —
        /// <c>"순위"(120611)</c> + <c>"\n{0}%"</c>. 그래서 라벨과 수치를 여기서 조립한다.
        /// 퍼센트 값의 소수 규칙은 <see cref="FormatRankPercent"/> 가 정한다(1% 미만만 소수점 2자리).
        ///
        /// 🔴 라벨이 미발행인 언어에서는 <c>rankIdx</c>(120506 = "상위 {0}%") 한 줄로 물러선다 —
        ///   2026-08-16 실측으로 120611 은 <c>text_cn</c> 에만 있고 ko/en/ja 에는 없다. 두 줄을 강행하면
        ///   그 언어에서 라벨 자리가 빈 줄로 남아 수치가 아래로 밀린다. 조회 전에 존재를 확인하는 것은
        ///   <c>MergeEventBoard</c> 의 <c>TXT_*</c> 가드와 같은 규약이다(<c>GetText</c> 는 못 찾으면 DLogger.Error 를 남긴다).
        ///
        /// score 를 인자로 받는 이유: 순위 조회에 먹일 점수가 게이지 점수와 다르기 때문이다.
        /// <c>eventPoint</c> 는 반복 보상마다 차감되는데 Rank 구간은 0~9999999 누적이라
        /// 호출자가 <see cref="GetInfinitePhaseRankScore"/> 로 만든 총합을 넘겨야 한다.
        /// </summary>
        public static bool TryGetInfinitePhaseRankText(FsMergeEventState mergeEventState, int score, out string rankText)
        {
            rankText = string.Empty;

            if (MergeEventHelper.IsFourDropItemInfinitePhase(mergeEventState) == false) return false;

            MergeEventRankTableData rankTable = TableManager.Instance.GetMergeEventRankTable(score, MergeEventEvType.FourDropItem);
            if (rankTable == null) return false;

            string rankPercent = FormatRankPercent(rankTable.damageRank);

            //[기획서 §5-1 정본] 라벨이 발행돼 있으면 두 줄로 조립한다.
            if (TableManager.Instance.FindData(TXT_RANK_LABEL, out TextTable _))
            {
                rankText = $"{TXT_RANK_LABEL.L()}\n{rankPercent}{RANK_PERCENT_SUFFIX}";
                return true;
            }

            //[폴백] 라벨 미발행 언어. rankIdx 마저 미발행(0)이면 빈 문구를 그리지 않는다.
            if (rankTable.rankIdx <= 0) return false;

            rankText = rankTable.rankIdx.L(rankPercent);
            return true;
        }

        #region 튜토리얼 (Confluence 986087485)
        /// <summary>
        /// 순서1 '이벤트 소개' 트리거. 공용 MergeEventHelper.OnStartTutorial 의 FourDropItem 분기에서만 부른다.
        /// 그 지점은 MergeEvent.SetInfoAsync 의 보드 로드 await 이후라, 이어지는 순서2가 보드 블록을 찾을 수 있다.
        /// </summary>
        public static void OnStartTutorial()
        {
            CommonTutorialManager.Instance.OnTutorialTrigger(
                new TutorialTriggerData(TutorialTrigger.OpenPopup, TutorTrigger_OpenPopup.MergeEventFourDropItem, 0));
        }

        /// <summary>
        /// 순서2 '생산 방법 안내'(Tutorial_Trigger endCondition = 72)의 포커싱·종료 대상 — <b>화단 패널</b>(UI).
        ///
        /// CommonTutorialManager.HandleTutorialEndProcess 가 targetClick 에서 Button 을 꺼내 onClick.Invoke() 로 터치를 대신한다.
        /// 🔴 [UI 변경 2026-08-14] 그 대상이 <b>패널이 아니라 화단 칸(Field01~04) 버튼</b>으로 옮겨졌다 —
        /// 패널 자신의 <c>UIButtonEx</c> 는 프리팹에서 꺼졌고(<c>m_Enabled: 0</c>) 리스너도 칸 버튼에만 붙는다.
        /// 대상 선택은 <see cref="MergeEventFourDropItemFieldPanel.GetDropButtonRect"/> 한 곳이 정한다
        /// (여기서 계층을 다시 뒤지면 프리팹이 바뀔 때 두 곳이 어긋난다).
        ///
        /// 유휴 가이드 ①(<see cref="IsGuidableFieldPanel"/>)과 달리 <b>재화 보유·빈 슬롯 조건을 보지 않는다</b> —
        /// 튜토리얼은 물뿌리개가 0인 최초 진입에서도 화단을 가리켜야 한다.
        ///
        /// 🔴 종전에는 <c>blockType Grow</c> 보드 블록을 찾았다 — CSV 에 그 값이 없어(501 실측 `bT 10` 0행)
        /// 이 단계가 <b>항상 실패하고 딤만 남았다</b>(계획서 `Q28`).
        /// </summary>
        public static RectTransform GetFirstGrowBlockRect()
        {
            MergeEvent mergeEvent = MergeEventHelper.GetMergeEvent();
            if (mergeEvent == null) return null;

            MergeEventFourDropItemFieldPanel fieldPanel = mergeEvent.EventBoard.FieldPanel;
            if (fieldPanel == null)
            {
                //null 을 돌려주면 endType 2 는 종료 수단을 잃어 딤만 남는다 → 원인을 바로 짚을 수 있게 남긴다.
                DLogger.Error("MergeEventFourDropItemHelper::GetFirstGrowBlockRect::FieldPanel 이 바인딩되지 않았다");
                return null;
            }

            return fieldPanel.GetDropButtonRect();
        }

        /// <summary>
        /// 튜토리얼 순서4 "열쇠 안내"(Confluence 986087485, actionCondition1=1 · actionCondition2=53) 포커싱 대상.
        /// 진행도 스크롤의 <b>열쇠 말풍선</b>(KeyBox)이다 — 구름 해금 연출이 열쇠를 쏘아 보내는 바로 그 지점이라
        /// 안내와 연출이 같은 곳을 가리킨다.
        /// 말풍선이 꺼져 있으면(이번 라운드에 열쇠 목표가 없음) null 을 돌려준다 —
        /// 화면에 없는 것을 가리키면 딤만 남고 유저가 무엇을 봐야 할지 알 수 없다.
        /// </summary>
        public static RectTransform GetFirstKeyBubbleRect()
        {
            MergeEvent mergeEvent = MergeEventHelper.GetMergeEvent();
            if (mergeEvent == null) return null;

            MergeEventCollectScroll collectScroll = mergeEvent.CollectScroll;
            if (collectScroll == null)
            {
                DLogger.Error("MergeEventFourDropItemHelper::GetFirstKeyBubbleRect::collectScroll 이 바인딩되지 않았다");
                return null;
            }

            Transform keyBox = collectScroll.KeyBox;
            if (keyBox == null || keyBox.gameObject.activeInHierarchy == false)
            {
                //null 을 돌려주면 포커싱 대상이 없어 튜토리얼이 그 단계에서 멈춘다 → 원인을 바로 짚을 수 있게 남긴다.
                DLogger.Error("MergeEventFourDropItemHelper::GetFirstKeyBubbleRect::열쇠 말풍선이 꺼져 있거나 없다");
                return null;
            }

            return keyBox as RectTransform;
        }

        /// <summary>
        /// 튜토리얼 순서5 "구름 안내"(Confluence 986087485, actionCondition1=1 · actionCondition2=54) 포커싱 대상.
        /// <b>다음에 열릴 그룹</b>의 구름을 가리킨다 — 기획서 §4-2 가 그 그룹만 밝게 표시하도록 정해 뒀으므로
        /// 화면에서 이미 강조돼 있는 칸과 안내 대상이 일치한다.
        ///
        /// 🔴 [기획 2026-08-15] 가리키는 단위가 <b>구름 한 칸 → 그 그룹 전체</b>로 바뀌었다.
        ///   그룹을 감싸는 영역은 <see cref="MergeEventBoard.TryGetCloudGroupFocusRect"/> 가 만든다.
        ///   그룹이 보드에 없으면 종전 경로(<see cref="MergeEventBoard.TryGetCloudCoveredBlock"/>)로 내려가 한 칸이라도 가리킨다.
        /// </summary>
        public static RectTransform GetFirstCloudBlockRect()
        {
            MergeEvent mergeEvent = MergeEventHelper.GetMergeEvent();
            if (mergeEvent == null) return null;

            int nextCloudGroup = GetNextCloudGroup();

            //[기획 2026-08-15] 구름은 **같은 그룹이 통째로** 강조되어야 한다 — 안내 문구가 '이 덩어리가 다음에 열린다' 이기 때문이다.
            //그룹 전체를 감싸는 영역 하나를 돌려주면 딤 구멍·손가락·대사창 위치가 전부 그 영역 기준으로 잡힌다
            //(튜토리얼 포커스는 대상의 GetWorldCorners 만 쓰므로 실제 블록일 필요가 없다).
            if (mergeEvent.EventBoard.TryGetCloudGroupFocusRect(nextCloudGroup, out RectTransform groupFocusRect))
                return groupFocusRect;

            //폴백: 그룹 영역을 못 만든 경우(다음 그룹이 화면 밖 라운드에 있거나 방금 걷혔다) 종전대로 구름 한 칸을 가리킨다.
            //안내가 아예 안 뜨고 튜토리얼이 멈추는 것보다 한 칸이라도 가리키는 편이 낫다(TryGetCloudCoveredBlock 규약과 같은 판단).
            if (mergeEvent.EventBoard.TryGetCloudCoveredBlock(nextCloudGroup, out EventBlockMain cloudBlock) == false)
            {
                DLogger.Error("MergeEventFourDropItemHelper::GetFirstCloudBlockRect::보드에 구름 블록이 없다");
                return null;
            }

            return cloudBlock.EventBlockView.BlockButton.transform as RectTransform;
        }
        #endregion

        #region 열쇠 비행 연출 (기획서 §6 영역 해금 연출)
        //진행도 말풍선에서 자물쇠로 날아가는 열쇠. Addressable 등록 주소 그대로다(UI.asset m_Address: FourDropItemKey).
        private const string KEY_FLIGHT_PREFAB_PATH = "FourDropItemKey";
        //비행 시간. 참고 영상(Gossip Harbor 01:23~01:28) 실측값 그대로다 — 섬광 86.00s → 자물쇠 도달 86.40s.
        //🔴 종전에는 0.25f 였다. 구름 걷힘이 이 비행을 기다려 주지 않아(=RefreshSlotViews 가 같은 프레임에 돌아)
        //  길게 잡으면 구름이 사라진 뒤에 열쇠가 도착했기 때문이다.
        //  이제는 걷힘 자체가 이 비행 뒤로 옮겨졌으므로(MergeEvent.PlayKeyFlightThenRevealCloudAsync) 그 제약이 없다.
        //  ⚠️ 늘린 만큼 컨베이어 이동도 함께 밀린다 — 걷힘이 전환 평가를 들고 있기 때문이다(그것이 기획서 §7-2 의도다).
        private const float KEY_FLIGHT_DURATION = 0.4f;

        //이 프리팹의 유일한 Spine 클립이자 연출 **전체**다(등장 → 도달 폭발 → 잔광, 실측 2.00초).
        //🔴 프리팹 startingAnimation 이 비어 있어 **아무도 재생하지 않고 있었다** — 열쇠가 셋업 포즈로 날아가기만 했다.
        //  길이는 여기 상수로 박지 않고 SkeletonData 에서 읽는다(아트가 클립을 늘려도 코드가 따라간다).
        private const string KEY_ANIM_ACT = "act";

        //🔴 [2026-08-13 정정] 종전에는 여기 KEY_FLIGHT_START_SCALE(1/3 → 1) 코드 크기 램프가 있었다. 제거한다 —
        //  act 이 **본 스케일을 스스로 애니메이션한다**(실측 0.45 → 0.61 → 폭발 0.83 → 0.52).
        //  둘을 함께 태우면 곱해져서 출발 크기가 0.15 로 쪼그라들고 도착해도 저작 크기에 닿지 못한다.
        //  크기는 아트 곡선이 소유하고 코드는 **위치만** 옮긴다.

        //스킨 이름(Spine_Fx_FourDropItem_Key_SkeletonData 는 Normal/Special 두 스킨을 갖는다).
        //프리팹 initialSkinName 이 Normal 로 고정돼 있어 특별 구간에서도 금색 열쇠만 나온다 →
        //기획서 §3-2 10번 *"특별한 아이템 구간에는 구름과 **열쇠의** 색상이 다르도록 구성"* 의 열쇠 축을 여기서 채운다.
        //구름·자물쇠 축은 MergeEventBoard 가 같은 판정(IsSpecialCloudGroup)으로 이미 처리하고 있어 두 축이 어긋나지 않는다.
        private const string KEY_SKIN_NORMAL  = "Normal";
        private const string KEY_SKIN_SPECIAL = "Special";

        /// <summary>
        /// [FourDropItem] 구름 그룹이 해금될 때 진행도 말풍선의 열쇠가 <b>자물쇠</b>로 날아가는 연출.
        /// 기획서 §6 영역 해금 연출 — "말풍선의 열쇠를 사용해 자물쇠를 풀고 구름이 걷히는 연출".
        ///
        /// <b>구름 걷힘이 이 연출을 기다린다</b>(기획서 §5-2 *"… → 열쇠 획득 연출 → 구름 걷힘 연출"*).
        /// 그래서 UniTaskVoid 가 아니라 await 가능한 UniTask 를 돌려준다 — 호출측이 종료 시점을 알아야 하기 때문이다.
        /// 기다리는 범위는 <b>비행 + <c>act</c> 완주</b>까지다(도달 폭발·잔광 포함) — 지시대로 열쇠가 완전히 끝난 뒤 구름이 걷힌다.
        /// 🔴 그렇다고 이것이 **게이트**가 되어서는 안 된다: 아래 조기 반환이나 취소로 비행이 없어도
        ///   호출측은 구름을 반드시 걷어야 한다(MergeEvent.PlayKeyFlightThenRevealCloudAsync 의 finally).
        ///   연출 실패가 '데이터는 해금인데 그림은 구름'(계획서 U10)으로 굳으면 안 된다.
        /// 시작점(말풍선)이나 도착점(자물쇠)을 못 구하면 조용히 넘긴다 — 그 경우는 연출만 없고 해금은 정상이다.
        /// </summary>
        public static UniTask PlayCloudUnlockKeyFlightAsync(MergeEventCollectScroll collectScroll, MergeEventBoard eventBoard, List<int> unlockedSlotIds, int cloudGroupId, CancellationToken token)
        {
            if (collectScroll == null || eventBoard == null) return UniTask.CompletedTask;

            //구름 폭발과 같은 조건을 본다(사유는 프로퍼티 주석). 공유하므로 둘 중 하나만 재생되는 어긋남이 없다.
            if (eventBoard.CanPlayCloudUnlockPresentation == false) return UniTask.CompletedTask;

            Transform keyBox = collectScroll.KeyBox;
            if (keyBox == null) return UniTask.CompletedTask;
            //말풍선이 꺼져 있으면(이번 라운드에 열쇠 목표가 없는 상태) 화면에 없는 지점에서 날아오게 된다 → 태우지 않는다.
            if (keyBox.gameObject.activeInHierarchy == false) return UniTask.CompletedTask;

            //도착점은 **화면에 떠 있는 자물쇠 그 자리**다 — 기획서 §6 "말풍선의 열쇠를 사용해 **자물쇠**를 풀고".
            //🔴 종전에는 해금 슬롯 무리의 중앙 칸(TryGetSlotGroupCenterPosition)이었다. 자물쇠는 칸에 스냅하지 않는
            //  구름 블록 좌표 평균에 놓이므로(MergeEventBoard.TryGetCloudLockPosition) 변이 짝수인 그룹에서 둘이
            //  반 칸 어긋났고, 열쇠가 자물쇠 옆에 꽂혔다(확인 문항 17). 이제 계산하지 않고 자물쇠 좌표를 그대로 쓴다.
            //  ⚠️ 이 시점의 자물쇠는 아직 **해금되는 그 그룹** 위에 있다 — 자물쇠를 다음 그룹으로 옮기는 것은
            //  구름 걷힘(RefreshSlotViews → RefreshCloudBrightness → RefreshCloudLock)이고, 그것이 이 비행 뒤로
            //  미뤄져 있기 때문이다(MergeEvent.PlayKeyFlightThenRevealCloudAsync).
            //자물쇠가 꺼져 있는 예외 경로(보드 전환 중)에서만 종전 중앙 칸으로 내려간다 — 연출만 반 칸 어긋나고 해금은 정상이다.
            Vector3 targetPosition;
            if (eventBoard.TryGetVisibleCloudLockPosition(out targetPosition) == false
                && eventBoard.TryGetSlotGroupCenterPosition(unlockedSlotIds, out targetPosition) == false) return UniTask.CompletedTask;

            return PlayKeyFlightAsync(keyBox.position, targetPosition, eventBoard.EffectParent,
                                      IsSpecialCloudGroup(cloudGroupId), token);
        }

        private static async UniTask PlayKeyFlightAsync(Vector3 startPosition, Vector3 endPosition, Transform parent, bool isSpecialSection, CancellationToken token)
        {
            //이 프리팹에는 AutoKillEffect 가 없어 직접 띄우면 반납되지 않고 쌓인다 → 회수 책임을 반드시 이 함수가 진다.
            //어느 경로로 빠져나가든(로드 실패·비행 중 취소·정상 종료) 아래 finally 하나가 반납한다.
            GameObject keyObject = null;
            try
            {
                keyObject = await ResourcesManager.InstantiateAsync(KEY_FLIGHT_PREFAB_PATH, parent, false, token);
                if (keyObject == null) return;

                //🔴 위치를 **가장 먼저** 잡는다. 스킨 교체·act 재생보다 뒤로 밀면 그 사이에 캔버스가 한 번 그려질 수 있고,
                //  그 프레임에 열쇠가 **프리팹 저작 좌표**에 노출된다 — FourDropItemKey.prefab 루트는
                //  anchoredPosition (241, -873) 로 저작돼 있어(화면 우하단 쪽) 말풍선과 전혀 다른 자리에서 튀어나온 것처럼 보인다.
                //  Addressables.InstantiateAsync 는 instantiateInWorldSpace 없이 부모에 붙여 그 저작 좌표를 그대로 들고 오므로,
                //  '인스턴스를 받은 직후'가 노출 없이 덮어쓸 수 있는 유일한 지점이다.
                Transform keyTransform = keyObject.transform;
                keyTransform.position   = startPosition;
                //크기는 act 이 소유한다 — 코드가 겹쳐 곱하지 않도록 1 로만 둔다(상수 선언부 정정 주석 참조).
                keyTransform.localScale = Vector3.one;

                //Spine 참조는 한 번만 찾아 스킨·재생이 함께 쓴다(런타임 반복 GetComponent 금지).
                SkeletonGraphic keySpine = keyObject.GetComponentInChildren<SkeletonGraphic>(true);
                ApplyKeySkin(keySpine, isSpecialSection);
                float actDuration = PlayKeyAct(keySpine);

                float elapsed = 0f;
                while (elapsed < KEY_FLIGHT_DURATION)
                {
                    elapsed += Time.deltaTime;

                    //참고 영상 실측(0→1 정규화): 0.125 지점에서 이미 28%, 0.25 에서 50%, 0.5 에서 66%, 0.625 에서 82% 진행했다.
                    //= 등속이 아니라 **빠르게 출발해 감속하며 꽂히는** 곡선이라 OutQuad 로 근사한다.
                    float progress = Mathf.Clamp01(elapsed / KEY_FLIGHT_DURATION);
                    float eased    = 1f - (1f - progress) * (1f - progress);

                    keyTransform.position = Vector3.Lerp(startPosition, endPosition, eased);
                    await UniTask.Yield(PlayerLoopTiming.Update, token);
                }

                keyTransform.position = endPosition;

                //[HL-915] 1223 "자물쇠가 열릴때". 열쇠가 자물쇠에 **꽂히는 이 순간**이지 비행 시작도, act 완주도 아니다 —
                //바로 아래 remainSeconds 대기(실측 약 2초)를 지나 재생하면 소리가 도달 폭발보다 한참 뒤에 난다.
                //해금 회차당 1회가 구조적으로 보장된다: 이 함수는 PlayCloudUnlockKeyFlightAsync 가 회차당 한 번만 부르고,
                //그쪽은 MergeEvent.PlayPendingCloudUnlockKeyFlightAsync 가 큐를 비우며 호출하는 단일 소비자다.
                SoundManager.Instance.PlaySound(SoundName.FourDropItem_LockOpen);

                //남은 act 구간(도달 폭발·잔광)을 자물쇠 자리에서 **끝까지** 재생하고 그 뒤에야 반환한다.
                //호출측이 이 await 뒤에 구름 걷힘을 붙여 두었으므로(MergeEvent.PlayKeyFlightThenRevealCloudAsync)
                //= 열쇠 연출이 완주한 다음에 구름이 걷힌다.
                //🔴 [2026-08-13] 종전에는 기다리지 않고 회수만 뒤로 넘겼다(구름과 폭발이 겹쳐 돌았다) —
                //  참고 영상(도달 0.45s → 구름 밝아짐 0.75s)을 따른 것이었으나, 지시로 순차 재생으로 바꾼다.
                //  ⚠️ 걷힘이 컨베이어 전환 평가까지 들고 있어(기획서 §7-2) act 길이(실측 2.00초)만큼 그 뒤도 함께 밀린다.
                //취소되면 여기서 즉시 빠져나오고 finally 가 반납한다 — 남은 폭발을 기다리느라 반납이 늦지 않는다.
                float remainSeconds = actDuration - elapsed;
                if (remainSeconds > 0f)
                    await UniTask.Delay(TimeSpan.FromSeconds(remainSeconds), cancellationToken: token).SuppressCancellationThrow();
            }
            finally
            {
                if (keyObject != null) ResourcesManager.Release(keyObject);
            }
        }

        /// <summary>
        /// <c>act</c> 을 처음부터 재생하고 그 길이(초)를 돌려준다. 없으면 0 — 비행만 하고 곧바로 회수된다.
        /// 풀에서 재사용된 인스턴스가 <b>직전 재생의 진행도를 물고 나오지 않도록</b> 트랙을 비우고 다시 건다
        /// (이게 없으면 두 번째 해금부터 폭발 장면에서 시작한다).
        /// </summary>
        private static float PlayKeyAct(SkeletonGraphic keySpine)
        {
            if (keySpine == null) return 0f;

            Spine.AnimationState state = keySpine.AnimationState;
            Spine.SkeletonData   data  = keySpine.SkeletonData;
            if (state == null || data == null) return 0f;

            Spine.Animation act = data.FindAnimation(KEY_ANIM_ACT);
            if (act == null) return 0f;

            state.ClearTracks();
            state.SetAnimation(0, KEY_ANIM_ACT, false);

            return act.Duration;
        }

        /// <summary>
        /// 열쇠 스킨을 구간 색에 맞춘다. 스킨이나 SkeletonGraphic 이 없으면 조용히 넘어간다 —
        /// 색이 안 바뀔 뿐 비행 자체는 그대로 돌아야 한다(연출은 순수 view 라 진행을 막지 않는다).
        /// 풀에서 재사용된 인스턴스가 직전 비행의 스킨을 물고 나올 수 있어 <b>일반 구간도 매번 명시</b>한다.
        /// </summary>
        private static void ApplyKeySkin(SkeletonGraphic keySpine, bool isSpecialSection)
        {
            if (keySpine == null) return;

            keySpine.Initialize(false);

            Spine.Skeleton     skeleton = keySpine.Skeleton;
            Spine.SkeletonData data     = keySpine.SkeletonData;
            if (skeleton == null || data == null) return;

            string skinName = isSpecialSection ? KEY_SKIN_SPECIAL : KEY_SKIN_NORMAL;
            if (data.FindSkin(skinName) == null) return;

            skeleton.SetSkin(skinName);
            //스킨만 바꾸면 슬롯이 직전 어태치먼트를 물고 있어 색이 섞인다 → 셋업 포즈로 되돌려 새 스킨으로 다시 해석시킨다.
            skeleton.SetSlotsToSetupPose();
        }
        #endregion
    }
}

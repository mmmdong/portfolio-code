using System.Collections.Generic;

using GameLogic.Define;
using GameLogic.Network;

// 캐릭터 카페 클라이언트 데이터 모델 + 결과 DTO 모음.
//  [2026-06-14 Fs 디커미션] 카페 Fs 데이터/핸들러 제거에 맞춰, 진행 읽기용 클라 DTO 와 연출용 결과 DTO 를 비-Fs 전역 타입으로 분리한다.
//   · 진행 데이터(오브젝트/미션 뷰)는 DataManagement 캐시 + 테이블에서 매번 파생(EventCharacterCafeHelper). 저장 대상 아님.
//   · 결과 DTO 는 실서버 응답/연출용(구 FsCharacterCafeDataHandler 중첩 → 본 파일로 이동).

#region 진행 읽기용 클라 DTO (구 Fs 데이터 대체)

// 맵 셀 1개의 오브젝트 진행 상태 (구 FsCharacterCafeObjectData) — DataManagement(stepList)+Map 테이블에서 파생 구성한다.
//  objectIdx 는 테이블상 중복 가능하므로 식별키는 맵 셀 단위 mapIdx(Event_CharacterCafeMap.index).
public class CafeObjectData
{
    public int mapIdx;
    public int objectIdx;
    public int remainInteractCount;   // 남은 상호작용 횟수 (interactCount 에서 차감)
    public CharacterCafeObjectState state = CharacterCafeObjectState.Locked;
    public bool isEasterEggCollected; // 이스터에그 보상 수령 여부 (2차)

    public CafeObjectData Clone()
    {
        return new CafeObjectData
        {
            mapIdx              = this.mapIdx,
            objectIdx           = this.objectIdx,
            remainInteractCount = this.remainInteractCount,
            state               = this.state,
            isEasterEggCollected = this.isEasterEggCollected,
        };
    }
}

// 미션 진행/완료/수령 상태의 파생 뷰 (구 FsCharacterCafeMission) — UI 셀(CharacterCafeMissionItem)이 소비.
//  EventCharacterCafeHelper.BuildMissionView 가 오브젝트 상태(Done)+테이블+수령기록에서 매번 생성한다(저장 대상 아님).
public class CafeMissionView
{
    public int index;
    public List<int> DoneObjects = new();
    public ExMissionState missionState = ExMissionState.None;

    public CafeMissionView Clone()
    {
        return new CafeMissionView
        {
            index        = this.index,
            DoneObjects  = new(this.DoneObjects),
            missionState = this.missionState,
        };
    }
}

#endregion

#region 결과 DTO (구 FsCharacterCafeDataHandler 중첩 → 분리)

// 서버 권위가 지급한 보상을 Content 레이어가 클라 지갑/인벤토리에 반영(또는 표시)할 때 참조하는 보상 묶음 마커.
public interface ICafeRewardResult
{
    RewardPacketData[] RewardPackets { get; }
}

// 오브젝트 1개 상호작용 결과
public class InteractObjectResult : ICafeRewardResult
{
    public int updateObjectIdx;
    public CharacterCafeObjectState updateState;
    public RewardPacketData[] rewards;
    public bool isFirstComplete;       // 횟수 소진으로 오브젝트가 "최종 완료"된 상호작용 1회만 true.
    public int gainedPoint;            // 이번 상호작용으로 누적된 이벤트 포인트(219) 증가량 — 보상 비행 연출과 함께 포인트 투사체 수량으로 사용(>0 일 때만).
    public RewardPacketData[] RewardPackets => rewards;
}

// 스토리 그룹 재생 결과
public class ViewStoryResult
{
    public int storyGroup;
    public bool isNewViewed;
}

// 포인트 획득/목표 달성 결과 — 이번 획득으로 도달했으나 미수령인 모든 단계(오름차순)
public class GainPointResult : ICafeRewardResult
{
    public List<int> reachedStepIdxes = new();
    public RewardPacketData[] rewards;
    public RewardPacketData[] RewardPackets => rewards;
}

// 포인트 단계 보상 수령 결과
public class ClaimPointRewardResult : ICafeRewardResult
{
    //이전까지 달성한 StepIdx
    public int prevclaimedStepIdx;
    //현재 달성한 StepIdx
    public int claimedStepIdx;
    public bool isAllStageCleared;
    public RewardPacketData[] rewards;
    public RewardPacketData[] RewardPackets => rewards;
}

// 오브젝트 상호작용 미션 보상 수령 결과
public class ClaimMissionRewardResult : ICafeRewardResult
{
    public int claimedMissionIdx;
    public bool isAllMissionCleared;
    public RewardPacketData[] rewards;
    public RewardPacketData[] RewardPackets => rewards;
}

// 클리어 보상 수령 결과 (흐름도 4-21)
public class ClaimClearRewardResult : ICafeRewardResult
{
    public RewardPacketData[] rewards;
    public RewardPacketData[] RewardPackets => rewards;
}

// 퍼즐 완성 결과
public class CompletePuzzleResult : ICafeRewardResult
{
    public int puzzleOrder;
    public bool isAllCleared;
    public RewardPacketData[] rewards;
    public RewardPacketData[] RewardPackets => rewards;
}

// 미니게임 클리어/라운드 진행 결과
public class ClearMiniGameResult
{
    public int objectIdx;
    public int curRound;
    public bool isClear;
    // 최종 클리어 시 진입 오브젝트의 맵 셀 식별자(mapIdx) — 응답 전(아직 Active)에 캡처. 메인 팝업이 완료 위치 도우미 대기에 사용(§7-5).
    //  objectIdx 는 중복 가능하므로 완료 오브젝트 식별/도우미 대기는 mapIdx 로 한다. (미클리어 시 0)
    public int mapIdx;
}

// 미니게임 라운드 시작(풍선 레이아웃 생성) 결과
public class StartMiniGameRoundResult
{
    public int round;
    public int[] balloonContents; // 슬롯별 내용 (-1=열쇠, 0=꽝, 1~=히든 보상 idx)
}

// 미니게임 풍선 터뜨리기 결과
public class PopBalloonResult : ICafeRewardResult
{
    public int balloonIndex;
    public int content;                // 터뜨린 풍선 내용 (-1=열쇠, 0=꽝, 1~=히든 보상 idx)
    public bool isKey;
    public RewardPacketData[] rewards;
    public bool isAllCollected;
    public RewardPacketData[] RewardPackets => rewards;
}

// 미니게임 상점 구매 결과
public class BuyMiniGameShopResult
{
    public int shopIndex;
    public int grantedCurrency;
}

#endregion

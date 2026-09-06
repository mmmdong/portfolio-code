namespace GameLogic.Define
{
    // 드림 벌룬 페스티벌 — 클라 시뮬/연출 구동용 메시지 (구현 명세서 §7-6).

    // 좌석 감소(클라 시뮬) → 게이지/순위 UI 갱신.
    public class SeatChangedMsg : BaseMessage
    {
        public int round;       // 현재 구름(단계)
        public int remainSeat;  // 잔여 좌석 수
        public int slotMax;     // 정원(순위 산출 모집단)
    }

    // 클라 판정 결과 → 성공/실패 연출 트리거. rank = 클라 산출(모집단 slotMax, §7-3), upperPercent = 상위 %(성공 팝업 트로피 §3-6).
    public class StageResultMsg : BaseMessage
    {
        public int round;
        public bool success;
        public int rank;
        public int upperPercent;
    }

    // 결과 팝업(_Success/_Fail)의 선택 결과 → 메인 팝업 하단 버튼 노출 결정(§5-1).
    // 메인의 라운드 진행 버튼은 기본 비활성이며, 결과 팝업에서 "쉬어가기"를 고른 경우에만 켜진다.
    // (rest=false = 라운드 진행/다시 시작 → 진행중으로 전이하므로 버튼은 계속 꺼진 상태)
    public class StageResultDecisionMsg : BaseMessage
    {
        public bool rest;   // true: 쉬어가기(닫기 포함) / false: 라운드 진행·다시 시작
    }

    // 라운드 시작 서버 확정(RQEventBalloonRoundStart 성공) → 경쟁자 모집 연출(§4-3) 트리거.
    // 시작 버튼은 메인(쉬는중)·성공 팝업·실패 팝업 3곳에 있으므로, 연출 재생은 각 버튼이 아니라
    // 서버 확정 시점 **한 곳**에서 방송한다(연출 담당 = 메인 팝업 단일 수신자).
    public class RoundStartedMsg : BaseMessage
    {
        public int round;   // 시작된 구름(단계)
    }

    // 쉬는중 안내 노출(마이드림파트너 안내창 활용, §7-4).
    // sequenceGroup = 발화한 조건의 MyDreamPartner_Sequence 그룹(§2-7) — 조건1(남은 에너지)=39 / 조건2(누적 사용 에너지)=40.
    // 그룹에 물린 대사(speechGroup 61 · speechType 200 이미지)·모션은 마이드림파트너가 선택한다.
    public class RestNotifyMsg : BaseMessage
    {
        public int sequenceGroup;
    }
}

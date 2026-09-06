namespace GameLogic.TrophyChallenge
{
    // 명세서.md §7.3 챌린지 상태별 GUI 표 1~6 / §7.3 ⑦⑧ 의 디폴트는 별도 처리.
    // 정렬·레드닷·UI 분기에 공통으로 쓰이는 단일 상태 enum (SSOT).
    public enum TrophyChallengeState
    {
        Locked = 0,         // ⑤ 잠금 (오픈 레벨 / openVersion 미충족, 시작 전)
        InProgress,         // ② 진행 가능
        Completed,          // ③ 완료, 보상 미수령 (UI 표기: 완료&미수령)
        Rewarded,           // ④ 완료, 보상 수령
        Expired,            // ⑥ 종료 (연계 이벤트 종료 → 최종 보상 분모에서 자동 제외)
    }
}

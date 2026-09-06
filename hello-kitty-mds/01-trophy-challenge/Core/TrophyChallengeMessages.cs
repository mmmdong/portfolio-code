namespace GameLogic.TrophyChallenge
{
    // 명세서.md §6.2, §7.4, §7.6 — 토스트, 레드닷, 최종 연출 등을 UI 가 받기 위한 메시지.
    // Message 버스(Pub/Sub)만 사용하므로 도메인 매니저는 UI 를 직접 모른다(OCP/DIP).

    public class OnTrophyChallengeMasterRefreshedMsg : BaseMessage
    {
    }

    public class OnTrophyChallengeInfoRefreshedMsg : BaseMessage
    {
    }

    public class OnTrophyChallengeProgressUpdatedMsg : BaseMessage
    {
        public long TrophyId;
        public int ChallengeId;
        public int ProgressCount;
    }

    public class OnTrophyChallengeCompletedMsg : BaseMessage
    {
        public long TrophyId;
        public int ChallengeId;
    }

    public class OnTrophyChallengeFinalRewardClaimedMsg : BaseMessage
    {
        public long TrophyId;
    }

    public class OnTrophyChallengeStateChangedMsg : BaseMessage
    {
        public long TrophyId;
    }

    // 명세서.md §7.3 — 챌린지 아이콘(PanelTrophyIconBase) 클릭 시 발행.
    // _Main 패널은 본 메시지를 받아 MissionTab / MissionClearTab 영역을 클릭된 챌린지 정보로 세팅한다.
    // (UI 슬롯 → 부모 패널 결합도를 끊고 Pub/Sub 으로 일원화)
    // Slot 은 _Main 패널이 클릭 이펙트(TrophyClickOpen/Close) 를 토글하기 위한 슬롯 참조.
    // 타입을 object 로 둬 GameLogic 어셈블리가 UI(PanelTrophyIconBaseSlot) 를 직접 의존하지 않도록 한다.
    public class OnTrophyChallengeMissionSelectedMsg : BaseMessage
    {
        public TrophyChallengeViewModel ViewModel;
        public object Slot;
    }
}

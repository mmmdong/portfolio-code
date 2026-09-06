using System;
using Cysharp.Threading.Tasks;
using Spine;
using Spine.Unity;
using UnityEngine;

// 캐릭터 카페 2차 서브콘텐츠 — 우사하나 풍선 게임 단일 풍선 아이템 (상세 892698729 §3-2)
// 그리드 컨트롤러가 UIBalloonItem 프리팹을 동적 생성/재사용하며, 내용(열쇠/보상/꽝)과 터뜨림 여부에 따라 비주얼을 적용한다.
// 풍선 1개의 데이터/터뜨림 판정은 서버 권위 — 본 클래스는 표시와 클릭 전달만 담당한다.
public class UIMiniGameBalloonItem : UIStatefulBase<UIMiniGameBalloonItem>
{
    // 풍선 내용 규약 (서버 권위) — MiniGameModel.balloonContents 와 동일
    public const int CONTENT_KEY = -1;
    public const int CONTENT_BLANK = 0;

    // Spine 애니메이션 이름 (Spine_UIPopupCharacterCafe_Balloon_Ani)
    private const string ANIM_IN = "In"; // 생성 시 등장 1회 재생(Idle 전)
    private const string ANIM_IDLE = "Idle"; // 등장(In) 완료 후 루프 재생
    private const string ANIM_POP = "Pop"; // 터뜨릴 때 1회 재생

    public class Info
    {
        public int index;
        public int content; // -1=열쇠, 0=꽝, 1~=히든 보상 idx
        public bool isPopped;
        public bool isImportant; // 중요 보상(roundRewardSign=1) 여부 — 보상 공개 시 BalloonGetImportant 연출
        public string skin;      // 풍선 Spine 스킨(색상) — 생성 시 팝업이 랜덤 배정
        public float inDelay;    // 등장(In) 애니 재생 딜레이(초) — 전체 생성 후 staggered 등장용
        public Action<int> onClick;
    }

    [SerializeField] private UIButtonEx balloonButton; // 풍선 비주얼 자식 — 터뜨리면 이것만 비활성(루트는 유지)
    [SerializeField] private SkeletonGraphic balloonSpine;
    private Info curInfo;

    private void OnEnable()
    {
        balloonButton?.onClick.AddListener(OnClickButton);
    }

    private void OnDisable()
    {
        balloonButton?.onClick.RemoveListener(OnClickButton);
    }

    // 풍선 생성/재생성(재오픈·재접속 복원 포함). 미터짐=Idle 루프 재생, 이미 터짐=연출 없이 숨김.
    public void SetData(Info info)
    {
        curInfo = info;
        ApplyVisual(false);
    }

    // 풍선을 방금 터뜨린 직후 — Pop 1회 재생 후 숨김.
    public void PlayPop(Info info)
    {
        curInfo = info;
        ApplyVisual(true);
    }

    // 터뜨림 여부/내용별 풍선 비주얼 적용. 재접속·재오픈 시 isPopped(서버 권위) 로 동일 상태 복원.
    private void ApplyVisual(bool isFreshPop)
    {
        if (null == curInfo)
            return;

        // 풍선 색상 스킨 적용 (생성 시 팝업이 배정한 랜덤 스킨) — 애니 재생 전에 먼저 반영
        ApplyBalloonSkin();

        bool popped = curInfo.isPopped;

        // 터뜨린 풍선은 재클릭 불가
        SetButtonEnable(!popped);

        // 미터짐 — 등장(In) 딜레이 후 In 1회 → Idle 루프 (딜레이 동안 숨김·클릭 차단)
        if (!popped)
        {
            SetBalloonState(StateRole.BalloonDefault);
            PlayBalloonIntroThenIdleAsync(curInfo.inDelay).Forget();
            return;
        }

        // 상태 role 이 바인딩돼 있으면 내용별 비주얼도 적용 (미바인딩 시 토글/Spine 이 피드백)
        StateRole poppedState = curInfo.content switch
        {
            CONTENT_KEY => StateRole.BalloonGetKey,
            CONTENT_BLANK => StateRole.BalloonDefault,
            _ => curInfo.isImportant ? StateRole.BalloonGetImportant : StateRole.BalloonGetReward, // 히든 보상(중요/일반)
        };
        SetBalloonState(poppedState);

        if (isFreshPop)
        {
            // 방금 터뜨림 — 풍선 유지한 채 Pop 1회 재생, 완료 시 숨김
            balloonSpine.gameObject.SetActive(true);
            PlayPopThenHide();
        }
        else
        {
            // 생성 시 이미 터진(복원) 풍선 — 연출 없이 즉시 숨김
            balloonSpine.gameObject.SetActive(false);
        }
    }

    // 풍선 색상 스킨 적용 (Spine 스킨 교체) — 스킨 미지정/미바인딩 시 스킵
    private void ApplyBalloonSkin()
    {
        if (null == balloonSpine || string.IsNullOrEmpty(curInfo.skin))
            return;
        balloonSpine.SetSkin(curInfo.skin);
    }

    // 등장(In) 딜레이 후 In 1회 재생 → 완료 후 Idle 루프. 딜레이 동안 풍선 숨김·클릭 차단(전체 생성 후 staggered 등장).
    //  딜레이는 그리드가 슬롯별로 부여(inDelay). 파괴(라운드 전환/팝업 종료) 시 대기 취소.
    private async UniTaskVoid PlayBalloonIntroThenIdleAsync(float delay)
    {
        if (null == balloonSpine)
            return;

        if (delay > 0f)
        {
            balloonSpine.gameObject.SetActive(false); // 등장 전 숨김
            SetButtonEnable(false);                    // 숨김 동안 클릭 차단
            bool canceled = await UniTask.Delay(TimeSpan.FromSeconds(delay), cancellationToken: gameObject.GetCancellationTokenOnDestroy())
                .SuppressCancellationThrow();
            if (canceled || this == null || null == balloonSpine)
                return;
            SetButtonEnable(true);
        }

        balloonSpine.gameObject.SetActive(true);
        balloonSpine.SetAnimation(0, ANIM_IN, false);
        balloonSpine.AddAnimation(0, ANIM_IDLE, true, 0f);
    }

    // Pop 1회 재생 후 완료 시점에 풍선 숨김 (애니 재생 실패 시 즉시 숨김 폴백)
    private void PlayPopThenHide()
    {
        var entry = balloonSpine.SetAnimation(0, ANIM_POP, false);
        if (null == entry)
        {
            balloonSpine.gameObject.SetActive(false);
            return;
        }

        entry.Complete += OnPopComplete;
    }

    private void OnPopComplete(TrackEntry entry)
    {
        entry.Complete -= OnPopComplete;
        balloonSpine.gameObject.SetActive(false);
    }

    private void SetBalloonState(StateRole role)
    {
        if (Stateful.HasState((int)role))
            Stateful.SetState((int)role);
    }

    private void SetButtonEnable(bool isEnable)
    {
        balloonButton.interactable = isEnable;
    }

    private void OnClickButton()
    {
        if (null == curInfo || curInfo.isPopped)
            return;
        curInfo.onClick?.Invoke(curInfo.index);
    }
}

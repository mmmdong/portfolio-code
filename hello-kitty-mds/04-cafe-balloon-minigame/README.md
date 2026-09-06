# 04. 생일 카페 — 풍선 게임 (Cafe Balloon MiniGame)

> 캐릭터 카페 이벤트의 서브 컨텐츠로 삽입되는 그리드형 풍선 터뜨리기 미니게임

---

## 컨텐츠 개요

**캐릭터 카페**는 카페 맵의 오브젝트들과 상호작용하며 진행하는 라이브 이벤트이고,
그중 특정 오브젝트에서 진입하는 것이 **풍선 게임**입니다.

```
카페 맵 → 미니게임 오브젝트 터치 → 풍선 게임 진입
                                        │
                        라운드 시작: N×M 그리드에 풍선 배치
                                        │
                        풍선 터뜨리기 (재화 소모)
                          ├─ 보상 획득
                          ├─ 꽝
                          └─ 열쇠 발견 → 잠금 해제
                                        │
                        라운드 클리어 → 다음 라운드 → ... → 오브젝트 완료(Done)
```

풍선 슬롯의 내용은 정수 하나로 표현됩니다.

```csharp
public IReadOnlyList<int> balloonContents;  // 슬롯별 내용 (-1=열쇠, 0=꽝, 1~=보상 idx)
```

---

## 아키텍처 — 서브 컨텐츠 합성 패턴

캐릭터 카페는 성격이 다른 서브 컨텐츠 4종(미션 / 포인트 / 퍼즐 / 미니게임)을 품습니다.
이들을 상속이 아니라 **인터페이스 + 합성**으로 붙였습니다.

```
              ContentEventCharacterCafe (메인 컨텐츠)
                          │
                          │ 합성 (List<ICharacterCafeSubContent>)
      ┌───────────────────┼───────────────────┬───────────────────┐
      ▼                   ▼                   ▼                   ▼
 MissionSub          PointSub            PuzzleSub          MiniGameSub
      │                   │                   │                   │
      └───────────────────┴───────────────────┴───────────────────┘
                          │
                 각자 자기 데이터 슬라이스만 읽음
                 변경은 공통 플로우(EventCharacterCafeHelper.TryUpdateData) 경유
```

```csharp
// 캐릭터 카페 서브 컨텐츠 공통 인터페이스 (기획 857473046)
// 메인 Content(ContentEventCharacterCafe)가 합성으로 들고 라이프사이클을 fan-out 한다.
// 각 서브는 자기 데이터 슬라이스(CharacterCafeData.PointData 등)만 읽고,
// 변경은 메인 Content 참조 없이 공통 플로우(EventCharacterCafeHelper.TryUpdateData)를 경유해 핸들러에 위임한다.
public interface ICharacterCafeSubContent
{
    void Initialize(LiveEventData eventData);      // 데이터 바인딩 (메인 Initialize 시 1회)
    void OnObjectInteractComplete(int objectIdx);  // 오브젝트 상호작용 완료 알림 — 각 서브가 자기 책임 여부를 판단
    void Refresh();                                // 데이터 동기화 후 UI 갱신
    void Release();
}
```

**핵심은 `OnObjectInteractComplete` 입니다.** 메인 컨텐츠가 "누가 처리할지" 판단하지 않고
모든 서브에 fan-out 하면, 각 서브가 **자기 책임인지 스스로 판단**합니다.
서브 컨텐츠를 추가해도 메인 컨텐츠에 분기가 늘지 않습니다.

또한 서브는 메인 참조를 갖지 않습니다 — 데이터 변경은 공통 헬퍼를 경유하므로
양방향 의존이 생기지 않습니다.

---

## 통신 설계 — 클라 생성 / 서버 검증

라운드 풍선 배치를 누가 만드느냐가 이 미니게임의 설계 쟁점이었습니다.

```csharp
//  [실서버 배선] 라운드 시작/풍선 터뜨리기/클리어는 RQCharacterCafeMiniGameUpdate(patch→snapshot)로 처리한다.
//   [클라 생성 전송] 라운드 시작 시 풍선 배치(레이아웃)는 클라가 생성(MiniGameModel 초기화)해 서버로 전송하고, 서버는 저장·검증한다.
//    [로컬 재화] 미니게임 스냅샷 패킷엔 재화 필드가 없어(서버 echo 저장 전용) 비용 소모·히든 보상 획득은 클라가 로컬로 처리한다(ApplyPopEconomyLocal).
//    클라는 응답 snapshot 을 모델에 투영해 표시한다.
//   이벤트 치트(에디터) 시에는 기존 가짜서버(Fs) 흐름을 유지한다(Fs도 동일 생성 로직 공용 — 본 클래스의 BuildMiniGameBalloonContents).
```

정리하면,

| 항목 | 주체 |
|---|---|
| 풍선 레이아웃 생성 | **클라** (서버가 저장·검증) |
| 진행 상태(스냅샷) | **서버** (patch → snapshot 응답) |
| 재화 소모·히든 보상 | **클라 로컬** (스냅샷에 재화 필드 없음) |

주목할 점은 **레이아웃 생성 로직을 실서버 경로와 FakeServer 경로가 공유**한다는 것입니다
(`BuildMiniGameBalloonContents`). 에디터 치트로 돌릴 때와 실서버로 돌릴 때
배치 규칙이 갈라지지 않습니다.

---

## 파일별 역할

### Core

| 파일 | 라인 | 역할 |
|---|---:|---|
| `ICharacterCafeSubContent.cs` | 19 | 서브 컨텐츠 공통 인터페이스 (합성 계약) |
| `CharacterCafeMiniGameSubContent.cs` | 1,460 | **미니게임 서브 컨텐츠 본체**. 라운드 진행/클리어/상점 구매 제한, 서버 동기화 |
| `CharacterCafeDataModels.cs` | 164 | 결과 DTO 정의 — `StartMiniGameRoundResult` / `PopBalloonResult` / `ClearMiniGameResult` / `BuyMiniGameShopResult` |
| `TableManager.MiniGame.cs` | 140 | 미니게임 테이블 조회 (partial class 확장) |

### UI

| 파일 | 라인 | 역할 |
|---|---:|---|
| `UIPopupMiniGameBalloon.cs` | 1,218 | 메인 인게임 팝업. 그리드 호스팅, 터뜨리기 위임, 사운드/연출 |
| `UIMiniGameBalloonGrid.cs` | 233 | 풍선 그리드 컨트롤러 — 동적 생성, 간격 보정, staggered 등장 |
| `UIMiniGameBalloonItem.cs` | 171 | 풍선 1개 (Spine 스킨으로 색상 배정) |
| `UIPopupMiniGameBalloonItemPurchasePopup.cs` | 351 | 아이템 구매 팝업 |
| `UIMiniGameBalloonPurchaseItem.cs` | 195 | 구매 아이템 1개 |
| `UIPopupMiniGameBalloonClear.cs` | 237 | 클리어 팝업 |
| `UIPopupMiniGameBalloonRoundInfoPopup.cs` | 236 | 라운드 정보 팝업 |
| `UIBalloonRewardItem.cs` | 82 | 보상 표시 아이템 |

---

## 기술적으로 볼 만한 부분

### 1. 이벤트 기반 팝업 간 결합 해제

미니게임 클리어로 진입 오브젝트가 완료되면, 메인 카페 팝업과 미니게임 팝업 **둘 다** 반응해야 합니다.
서로를 참조하는 대신 메시지 하나로 처리합니다.

```csharp
// 미니게임 클리어로 진입 오브젝트가 완료(Done) 전환됨을 메인 카페 팝업에 통지(맵/오브젝트 상태 재적용 트리거).
//  미니게임 팝업(UIPopupMiniGameBalloon)도 이 통지를 받아 자신을 닫는다(이벤트 기반 분리).
public class ObjectClearedMsg : BaseMessage
{
    public int objectIdx;
    // 완료된 진입 오브젝트의 맵 셀 식별자(mapIdx) — 메인 팝업이 완료 위치에 도우미를 대기시키는 데 사용.
    //  objectIdx 는 중복 가능하므로 완료 오브젝트 식별은 mapIdx 로 한다(0 이면 완료 위치 대기 생략 → 활성 오브젝트 폴백).
    public int mapIdx;
}
```

**식별자 선택의 근거가 주석에 있습니다** — `objectIdx` 는 중복 가능해서 위치 특정에 못 씁니다.
그래서 맵 셀 `mapIdx` 를 함께 실었고, 없을 때의 폴백까지 정의했습니다.

### 2. 동적 생성 오브젝트의 수명 귀속

```csharp
// 동적 생성 풍선의 수명을 귀속시키는 스코프. SetData 재호출/파괴 시 일괄 Release.
private readonly ResourceScope resourceScope = new();
```

라운드마다 풍선이 통째로 갈리는 구조라, 개별 해제를 추적하지 않고
**스코프 단위 일괄 해제**로 누수를 구조적으로 차단했습니다.

### 3. 데이터 배치를 평행 배열로

```csharp
public IReadOnlyList<int> balloonContents;    // 슬롯별 내용 (-1=열쇠, 0=꽝, 1~=보상 idx)
public IReadOnlyList<bool> balloonPopped;
public IReadOnlyList<bool> balloonImportant;  // 슬롯별 중요 보상 여부 (balloonContents 평행)
public IReadOnlyList<string> balloonSkins;    // 슬롯별 풍선 Spine 스킨(색상) — 생성 시 랜덤 배정 (balloonContents 평행)
public int columns;                           // 라운드 가로 풍선 수 (roundColsRows[0])
```

슬롯당 객체를 만들지 않고 평행 배열로 두어 **서버 스냅샷과 그대로 대응**시켰습니다.
읽기 전용 인터페이스(`IReadOnlyList`)로 노출해 UI가 데이터를 변조하지 못하게 막습니다.

### 4. 레이아웃 가독성 보정

```csharp
[SerializeField] private int wideColumnThreshold = 4;         // 이 컬럼 수 이상이면 가로 간격을 넓힌다(4·5열 빽빽함 완화)
[SerializeField] private float wideColumnExtraSpacingX = 40f; // 넓힐 가로 간격(디자인 기준, 셀/스케일과 함께 비례 적용)
[SerializeField] private float inStaggerInterval = 0.05f;     // 등장(In) 애니 staggered 딜레이 간격(초)
```

라운드마다 그리드 크기가 달라져 4·5열에서 풍선이 빽빽해지는 문제를, 하드코딩이 아니라
**임계값 + 보정치를 인스펙터로 노출**해 해결했습니다.
등장 애니는 랜덤 순번 × 간격으로 staggered 재생해 한꺼번에 튀어나오지 않게 했습니다.

### 5. 팝업 키 충돌 회피

```csharp
// 인포 팝업 — _Info 어드레서블 키 + 최초 진입 1회 강제 first-open 플래그.
//  메인 카페 이벤트(LiveEventType.CHARACTERCAFE)의 인포 first-open 키와 충돌하지 않도록 미니게임 전용 pid 키를 사용한다.
private const string INFO_POPUP_KEY = "UIPopupMiniGameBalloon_Info";
```

미니게임이 메인 카페와 **같은 `LiveEventType` 을 공유**하기 때문에 생기는 문제입니다.
"이벤트 단위 1회 노출" 플래그를 그대로 쓰면 둘 중 하나가 안 뜨므로 전용 키로 분리했습니다.
서브 컨텐츠 합성 구조가 만들어내는 부작용을 미리 잡은 사례입니다.

---

## git 이력상 기여 범위

| 파일 | 개별 커밋 |
|---|---|
| `CharacterCafeMiniGameSubContent.cs` | 3건 중 1건 |
| `UIPopupMiniGameBalloon.cs` | 3건 중 1건 |
| `UIPopupMiniGameBalloonRoundInfoPopup.cs` | 2건 중 1건 |
| 그 외 | 통합 머지 커밋으로 유입 |

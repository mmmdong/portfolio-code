# 03. 당근 수확 대소동 (Carrot Harvest)

> 20초 아케이드 미니게임 — 두더지 잡기 컨셉을 당근 수확으로 변경한 라이브 이벤트

---

## 컨텐츠 개요

N×N 격자의 구멍에서 당근이 튀어나오고, 노출 시간이 끝나기 전에 터치해 수확합니다.
한 판은 기본 20초이며, 점수로 최고 기록을 갱신하고 보상 단계를 밟습니다.

| 당근 종류 | 특징 |
|---|---|
| **일반 (Normal)** | 기본 점수, 1회 타격 |
| **레어 (Rare)** | 높은 점수, 1회 타격 |
| **슈퍼 레어 (SuperRare)** | 최고 점수. **보호 게이지(HP)** 를 가져 여러 번 때려야 수확 |

연속 성공하면 **콤보**가 쌓여 가산점이 붙고, 빈 구멍을 치면(경직) 콤보가 끊깁니다.

모든 수치는 `Event_CarrotSetting` 테이블에서 옵니다 — `grid`, `maxSpawn`, `objLife`,
`spawnGap`, `gameTime`, `superHP`.

---

## 아키텍처 — 순수 로직과 Unity의 완전 분리

이 컨텐츠의 설계 목표는 **게임 룰을 Unity 없이 돌릴 수 있게** 만드는 것이었습니다.

```
┌──────────────────────── 순수 C# (엔진 비의존) ────────────────────────┐
│                                                                       │
│   EventCarrotSpawnSelector      "무엇을 어디에"  등장 추첨            │
│            │                     가중치 + 천장 + 최대치               │
│            ▼                                                          │
│   EventCarrotBoardModel         구멍 점유 상태 머신                   │
│            │                     spawnGap 주기 스폰 / objLife 만료    │
│            │                     슈퍼 HP 판정                          │
│            ▼                                                          │
│   EventCarrotScoreModel         터치 → 점수 · 콤보                    │
│                                                                       │
└───────────────────────────────┬───────────────────────────────────────┘
                                │ Tick(spawned, expired) / TryHarvest
                                ▼
┌──────────────────────── MonoBehaviour (표현) ─────────────────────────┐
│   EventCarrotBoardView    N×N 구멍 생성 · 풀링 · 입력 라우팅          │
│   EventCarrotHole         구멍 1칸                                    │
│   EventCarrotView         당근 1개 (Spine 애니메이션)                 │
│   EventCarrotVerdictView  판정 표시                                   │
└───────────────────────────────────────────────────────────────────────┘
                                ▲
                                │
                    ContentEventCarrot (컨텐츠 컨트롤러)
```

세 모델은 `UnityEngine.Random` 외에 엔진 의존이 없어 그대로 단위 테스트가 가능합니다.

```csharp
/// 책임 분리:
///  - 등장 "종류/위치" 추첨   → EventCarrotSpawnSelector (본 모델이 소유·구동)
///  - "터치 → 점수/콤보"      → EventCarrotScoreModel
///  - 화면 표현/풀링/입력     → 후속 MonoBehaviour 보드 뷰(SetCarrotType/PlayAppear/PlayReturn/PlayExit)
///  - 본 모델                 → 구멍 점유 상태 + spawnGap 주기 스폰 + objLife 만료 복귀 + 슈퍼 HP 판정
```

---

## 파일별 역할

### Core — 순수 로직

| 파일 | 라인 | 역할 |
|---|---:|---|
| `ContentEventCarrot.cs` | 1,420 | 컨텐츠 컨트롤러. 모델 생성/구동, 보상 지급, 서버 동기화, 분석 로그 |
| `EventCarrotBoardModel.cs` | 248 | **보드 상태 머신**. 구멍 점유·스폰 주기·수명 만료·HP 판정 |
| `EventCarrotSpawnSelector.cs` | 106 | **등장 추첨기**. 가중치 랜덤 + 천장(pity) + 판당 최대치 제약 |
| `EventCarrotScoreModel.cs` | 119 | **점수·콤보 계산**. 2단계 콤보(일반/메가) + 유지 시간 |
| `EventCarrotType.cs` | 127 | 당근 종류 enum, 스폰 정보 struct, Spine 애니메이션 enum |

### View — 표현 계층

| 파일 | 라인 | 역할 |
|---|---:|---|
| `EventCarrotBoardView.cs` | 357 | N×N 보드 생성, 구멍 풀링, 입력 → 모델 라우팅 |
| `EventCarrotHole.cs` | 197 | 구멍 1칸 — 당근 배치/애니 재생 |
| `EventCarrotView.cs` | 118 | 당근 1개 Spine 애니메이션 제어 |
| `EventCarrotVerdictView.cs` | 108 | 수확 판정 표시 |
| `EventCarrotInGameText.cs` | 87 | 인게임 텍스트(점수·콤보) |

### UI

| 파일 | 라인 | 역할 |
|---|---:|---|
| `UIPopupEventCarrotInGame.cs` | 479 | 인게임 팝업 — 타이머·점수·보드 호스팅 |
| `UIPopupEventCarrotResult.cs` | 900 | 결과 팝업 — 점수 집계·보상 연출 |
| `CarrotRewardLoopScroll.cs` | 251 | 보상 단계 루프 스크롤 |

---

## 기술적으로 볼 만한 부분

### 1. 확률에 제약을 얹는 방법 — 천장(pity)과 최대치

순수 가중치 랜덤만으로는 "게임당 슈퍼 최대 N회", "M번 안 나오면 반드시 등장" 같은
기획 스펙을 만족할 수 없습니다. 기존 가중치 유틸을 **래핑**해서 제약을 얹었습니다.

```csharp
/// 등장 "종류"(가중치 랜덤 + 천장/최대치 제약)와 등장 "위치"(빈 구멍 셔플)를 분리해 처리한다.
/// - 종류 추첨: 누적 가중치 + 선형 스캔. 항목 3종뿐이라 Alias/이분탐색은 불필요(오버엔지니어링).
/// - 순수 가중치 랜덤만으로는 superMax(게임당 최대)·superPityCount(천장) 스펙을 충족하지 못하므로
///   기존 가중치 유틸 위에 제약 로직을 래핑한다.
/// - 가중치는 appearRate(만분율)를 정수 그대로 사용(부동소수 누적오차 방지).
```

세 가지 판단이 들어 있습니다.

- **알고리즘 선택의 근거를 남김** — 항목이 3종이라 Alias Method는 오버엔지니어링
- **부동소수 회피** — 만분율 정수를 그대로 씀
- **기존 유틸 재사용** — `GetWeightedRandomIndex()` / `ShuffleRandom()`

매 스폰마다 배열을 새로 만들지 않도록 `effectiveWeights` 재사용 버퍼를 둡니다.

### 2. GC 회피 — 호출측 버퍼 채우기

매 프레임 도는 `Tick` 이 결과 컬렉션을 새로 할당하지 않습니다.

```csharp
/// 매 프레임 Tick 가 "이번 틱에 새로 등장한 당근(spawned)"과 "노출 시간이 끝나 복귀하는 구멍(expired)"을
/// 호출측 재사용 버퍼에 채워 반환한다(GC 회피). 입력은 TryHarvest 로 구멍 단위 처리.
```

모바일에서 매 프레임 호출되는 경로라 할당을 원천 차단했습니다.

### 3. 구멍 상태를 struct 배열로 관리

```csharp
private struct Hole
{
    public bool Occupied;           // 당근 점유 중 여부
    public EventCarrotType Type;    // 점유 당근 종류
    public float RemainLife;        // 남은 노출 시간(0 이하 → 복귀 시작)
    public int RemainHp;            // 남은 타격 수(일반/레어 1, 슈퍼 superHp)
    public bool Returning;          // 복귀(Return) 애니 재생 중 — 점유 유지(수확 가능), 완료 시 ReleaseHole 로 비움
}
```

`Returning` 플래그가 중요합니다. 복귀 애니메이션이 재생되는 동안에도 **점유를 유지**해
플레이어가 아직 때릴 수 있게 하고, 애니가 끝나야 구멍을 비웁니다.
연출과 판정 사이의 틈을 없애는 처리입니다.

### 4. enum 값이 곧 배열 인덱스이자 애니메이션 트랙명

```csharp
// enum 정수값이 곧 가중치/점수 배열의 인덱스이므로 순서를 바꾸지 말 것.
public enum EventCarrotType
{
    Normal = 0, Rare = 1, SuperRare = 2,
}
```

```csharp
/// 당근 SkeletonGraphic 애니메이션 상태. enum 정수값 + 이름이 곧 Spine 애니메이션 트랙명과 1:1 대응한다.
/// 트랙명 규칙: "{(int)값}_{이름}" → "0_None", "1_Appear", "2_Idle", "3_Return", "4_Exit", "5_Hit".
```

CSV 배열 순서, Spine 트랙명, 코드 enum을 **하나의 규칙으로 묶어** 매핑 테이블을 없앴습니다.
대신 그 결합을 주석으로 명시해 순서를 함부로 바꾸지 못하게 했습니다.

### 5. 컨셉 변경 이력이 코드에 남아 있음

이 컨텐츠는 원래 **두더지 잡기**로 기획됐다가 당근 수확으로 바뀌었습니다.
클래스명과 테이블명은 `Carrot` 으로 정리됐지만, 일부 모델 주석에는 원본 테이블명
`Event_MoleSetting` 이 남아 있습니다. 실제 참조 테이블은 `Event_CarrotSetting` 입니다.

라이브 프로젝트에서 컨셉 변경이 코드에 어떤 흔적을 남기는지 보여주는 사례로 그대로 두었습니다.

---

## git 이력상 기여 범위

이 컨텐츠는 전 파일이 **통합 머지 커밋으로 유입**되어, git 상 개별 기여 이력이 남아 있지 않습니다.
포트폴리오로 제출하실 경우 이 항목을 확인하시고 사용하시기 바랍니다.

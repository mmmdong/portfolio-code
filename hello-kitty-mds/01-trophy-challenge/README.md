# 01. 트로피 챌린지 (Trophy Challenge)

> 시즌제 도전과제 메타 시스템 — 여러 라이브 이벤트의 플레이를 하나의 시즌 트로피로 묶는 상위 레이어

---

## 컨텐츠 개요

플레이어는 시즌마다 **트로피(Trophy)** 를 하나 받고, 그 안에 여러 개의 **챌린지(Challenge)** 가 들어 있습니다.
각 챌린지는 "드림 벌룬 페스티벌을 어려움 난이도로 클리어", "쇼핑로드 N회 완주" 같은
**다른 라이브 이벤트의 플레이 결과**를 조건으로 삼습니다.

- 챌린지를 개별 달성하면 그 챌린지의 보상을 수령
- 시즌 내 챌린지를 모두 채우면 **최종 보상** 수령
- 시즌·챌린지 각각이 독립된 기간(begin/end)을 가지며, 연계 이벤트가 먼저 끝나면 해당 챌린지만 종료 처리

즉 **다른 컨텐츠들의 진행을 수집하는 관문**이라, 결합도를 낮추는 설계가 핵심 과제였습니다.

---

## 아키텍처

```
                       ┌─────────────────────────────┐
                       │   TrophyChallengeManager    │  MonoSingleton
                       │  라이프사이클 / 메시지 발행   │
                       │  네트워크 호출 / 정책 조합    │
                       └──────────────┬──────────────┘
                                      │ 인터페이스로만 의존 (DIP)
          ┌───────────────────────────┼───────────────────────────┐
          ▼                           ▼                           ▼
┌───────────────────┐   ┌───────────────────────┐   ┌────────────────────┐
│ ITrophyChallenge  │   │ ITrophyChallenge      │   │ ITrophyChallenge   │
│    Repository     │   │    StateEvaluator     │   │     SortPolicy     │
│  캐시 R/W 추상    │   │  상태 분기 (순수함수)  │   │   정렬 전략        │
└───────────────────┘   └───────────────────────┘   └────────────────────┘
          │                           │                           │
          ▼                           ▼                           ▼
    UserData 캐시            TrophyChallengeState          ViewModel 정렬
                              (단일 상태 enum)
                                      │
                                      ▼
                      ┌───────────────────────────────┐
                      │  TrophyChallengeUIBridge      │
                      │  Messages (BaseMessage 버스)   │
                      └───────────────┬───────────────┘
                                      ▼
                                UI 팝업 / 패널 / 로비 진입 버튼
```

**설계 의도** — 매니저는 "조합과 오케스트레이션"만 하고, 판단은 전부 주입된 정책 객체가 합니다.
시즌마다 정렬 규칙이 바뀌거나 상태 분기가 추가되어도 `Initialize` 의 구현체 교체로 흡수합니다.

```csharp
// 트로피 챌린지 시스템 진입점 — LiveEventManager 와 유사한 위치(MonoSingleton)로 두되,
// 비즈니스 로직은 외부에서 주입한 정책 객체에 위임한다(DIP).
//
// 책임 분리:
//   - 본 매니저 : 라이프사이클 / 메시지 발행 / 네트워크 호출 / 정책 조합
//   - Repository : 캐시 R/W
//   - StateEvaluator : 챌린지 상태 분기
//   - SortPolicy : 챌린지 정렬 전략
```

---

## 파일별 역할

### Core — 시스템 코어

| 파일 | 라인 | 역할 |
|---|---:|---|
| `TrophyChallengeManager.cs` | 849 | 시스템 진입점(MonoSingleton). 정책 주입·조합, 진행도 수집(`OnProgressTrophyChallenge`), 서버 요청, 메시지 발행 |
| `TrophyChallengeState.cs` | 13 | 상태 단일 enum(SSOT) — `Locked / InProgress / Completed / Rewarded / Expired` |
| `TrophyChallengeViewModel.cs` | 40 | UI 표시용 뷰모델 (마스터·유저정보·챌린지·상태 묶음) |
| `TrophyChallengeMessages.cs` | 47 | 메시지 버스 이벤트 정의 (갱신/완료/보상 통지) |
| `TrophyChallengeUIBridge.cs` | 67 | 로직 ↔ UI 연결 지점. UI가 매니저 내부를 모르게 차단 |
| `Data/TrophyChallengeContentsData.cs` | 29 | 컨텐츠 정적 데이터 정의 |

### Core/Repository — 데이터 접근 추상

| 파일 | 라인 | 역할 |
|---|---:|---|
| `ITrophyChallengeRepository.cs` | 24 | 캐시 접근 인터페이스. 매니저·뷰모델이 `UserData` 구조를 직접 모르게 함 |
| `TrophyChallengeRepository.cs` | 65 | 구현체. 서버 응답이 비어 오는 완료/수령 API를 위해 **클라 캐시 보정 책임**을 가짐 |

```csharp
// 진행도는 RQTrophyChallengeUpdate 응답의 누적값(amount)으로 동기화한다(명세서 §3.3).
// 완료 / 최종 수령은 응답 페이로드가 없어(명세서 §3.4~3.5) 클라 캐시 보정 책임을 Repository 가 갖는다.
void SetProgress(long trophyId, int challengeId, int amount);
void MarkCompleted(long trophyId, int challengeId);
void MarkFinalRewardClaimed(long trophyId);
```

### Core/State — 상태 판정

| 파일 | 라인 | 역할 |
|---|---:|---|
| `ITrophyChallengeStateEvaluator.cs` | 19 | 한 챌린지의 현재 상태를 결정하는 단일 책임 인터페이스 |
| `TrophyChallengeStateEvaluator.cs` | 96 | 구현체. **시즌 기간 / 유저 기준 기간 / 챌린지 단위 기간 / 레벨 / 완료 여부** 를 순서대로 평가 |

이중 기간 판정이 특징입니다. 시즌(마스터)과 유저(개인 시작일)와 챌린지(연계 이벤트 기간)가
각각 독립된 기간을 갖기 때문에, 종료/시작전 판정이 세 축 모두를 대칭적으로 확인합니다.

```csharp
public static bool IsExpired(TrophyMasterPacketData master, TrophyInfoPacketData info, long currentEpochSeconds)
{
    if (null != master && master.end > 0 && master.end <= currentEpochSeconds)
        return true;

    if (null != info && info.endAt > 0 && info.endAt <= currentEpochSeconds)
        return true;

    return false;
}
```

전부 `static` 순수 함수라 Unity 없이 단위 테스트가 가능합니다.

### Core/Policy — 정렬 전략

| 파일 | 라인 | 역할 |
|---|---:|---|
| `ITrophyChallengeSortPolicy.cs` | 12 | 정렬 전략 인터페이스 |
| `TrophyChallengeSortPolicy.cs` | 37 | 기본 구현 — 보상 미획득 → 미완료 → 완료&수령 → 종료 순, 동일 상태 내 `index` 오름차순 |

### Helper / Network

| 파일 | 라인 | 원본 경로 | 역할 |
|---|---:|---|---|
| `Helper/TrophyChallengeHelper.cs` | 133 | `GameContents/_Helper/` | 표시 문구 포맷, 조건 텍스트 구성 |
| `Network/DataManager.TrophyChallenge.cs` | 133 | `GameLogic/GameData/` | 트로피 캐시 보관 (partial class 확장) |
| `Network/WrapWebManager.TrophyChallenge.cs` | 225 | `GameLogic/GameNetwork/WrapWebManager/` | 패킷 송수신 래퍼 (Info / Update / Complete / ClaimFinal) |

### UI

| 파일 | 라인 | 역할 |
|---|---:|---|
| `UI/UIPopupTrophyChallenge.cs` | 311 | 루트 팝업. 시작/메인/클리어 패널 전환 |
| `UI/PanelTrophyChallenge_Main.cs` | 549 | 메인 패널 — 트로피 진행도, 챌린지 리스트 |
| `UI/PanelMissionTab.cs` | 702 | 미션 탭 — 챌린지별 조건 문구·진행도·보상 수령 |
| `UI/UITrophyChallengeLobbyEntry.cs` | 161 | 로비 진입 버튼 + 레드닷 |

---

## 기술적으로 볼 만한 부분

### 1. 진행도 수집이 컨텐츠와 역방향 의존하지 않는다

각 라이브 이벤트는 자신이 트로피 챌린지에 기여한다는 사실만 알면 됩니다.

```csharp
OnProgressTrophyChallenge(MissionCommonConditionType.ShoppingRoad, difficulty, 1);
```

매니저가 서버 미션 그룹 목록을 순회해 조건 타입이 일치하는 챌린지를 찾고,
`Condition_Setting` 테이블의 `conditionValue` 로 조건 충족을 판정합니다.
컨텐츠 쪽은 "무슨 트로피의 몇 번 챌린지"인지 전혀 모릅니다.

### 2. 조건 판정에 두 가지 비교 계열이 공존한다

- **정확 일치형** (`IsIgnoreConditionValue`) — `value != conditionValue` 면 탈락
- **이상 비교형** (`IsDifficultyConditionValue`) — `value < conditionValue` 면 탈락

난이도처럼 "그 이상이면 인정"해야 하는 조건과, 특정 값만 인정해야 하는 조건을 분리했습니다.
`conditionValue == 0` 은 "조건 무관"으로 비교 자체를 건너뜁니다.

### 3. 중복 요청 가드

보상 수령 버튼은 StatefulUI 공유 버튼이라, 응답 대기 중 패널이 갱신되면 `interactable` 가드가 풀립니다.
매니저가 진행 중인 `(trophyId, challengeId)` 를 별도 추적해 같은 완료 요청의 중복 전송을 막습니다.

### 4. 시작 팝업 1회 노출 — 세션 가드 + 영속 가드 이중화

```csharp
// 같은 세션 내에서 마킹 전에 중복 자동 호출되지 않도록 막는 보조 가드. 영속 가드는 PlayerPrefs 의
// PREFS_KEY_LAST_SEEN_START_POPUP 키로 처리 (시즌 단위, trophyId 단조 증가 기반 단일 키 비교).
private readonly HashSet<long> autoOpenedStartPopupIds = new();
```

시즌마다 키를 늘리지 않고 **trophyId가 단조 증가하는 성질**을 이용해 단일 키 비교로 처리했습니다.

---

## git 이력상 기여 범위

이 저장소는 글로벌 브랜치를 스쿼시 머지로 받아오는 구조라 원 작성자 판별이 불가능합니다.
확인 가능한 개별 커밋 기준:

| 파일 | 개별 커밋 |
|---|---|
| `PanelMissionTab.cs` | 6건 중 3건 |
| `TrophyChallengeManager.cs` | 3건 중 1건 (드림 벌룬 미션 조건 추가) |
| 그 외 | 통합 머지 커밋으로 유입 |

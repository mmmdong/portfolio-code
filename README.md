# portfolio-code

Unity 클라이언트 개발자 **김동현** 포트폴리오의 코드 발췌 저장소입니다.

포트폴리오 본문: <https://mmmdong.github.io/projects/>

## 이 저장소의 성격

각 프로젝트에서 **제가 설계·작성한 핵심 시스템만** 골라 담았습니다.
전체 프로젝트 소스가 아니고, 그대로 빌드되지 않습니다.
씬·프리팹·아트·사운드·SDK·빌드 설정은 포함하지 않습니다.
테이블은 **구조 발췌만** 있습니다 — 전체 밸런스 데이터는 포함하지 않습니다.

코드를 읽는 목적은 하나입니다 — 포트폴리오 본문이 설명한 설계 판단이
실제 코드에서 어떻게 구현됐는지 확인하는 것.
그래서 디렉터리를 프로젝트가 아니라 **시스템 단위**로 나눴습니다.

## 구성

| 경로 | 파일 | 내용 | 본문 |
|---|---:|---|---|
| `unknown-heroes/ui-architecture` | 10 | View–SubView–Panel–PopUp 4계층 UI 구조와 풀링 | [3-2](https://mmmdong.github.io/portfolio/unknown-heroes/) |
| `unknown-heroes/gacha-probability` | 3 | 중첩 뽑기 테이블을 DFS로 순회하는 확률 산출 | [3-1 사례1](https://mmmdong.github.io/portfolio/unknown-heroes/) |
| `unknown-heroes/worldmap-drop` | 2 | 월드맵 드랍 아이템 목록 정렬·가시성 개선 | [3-1 사례2](https://mmmdong.github.io/portfolio/unknown-heroes/) |
| `unknown-heroes/appraisal` | 5 | 미확인 아이템 '감정' 콘텐츠, 슬롯머신 연출 | [3-3](https://mmmdong.github.io/portfolio/unknown-heroes/) |
| `unknown-heroes/tables` | 3 | 위 코드가 읽는 시트의 구조 발췌 | [3-1](https://mmmdong.github.io/portfolio/unknown-heroes/) |
| `pixel-heroic-legend/battle-state-machine` | 9 | 유닛 상태 전이 기반 전투 코어 | [3-1](https://mmmdong.github.io/portfolio/pixel-heroic-legend/) |
| `pixel-heroic-legend/pvp` | 7 | PVP 대전 유닛·전투·랭킹 UI | [3-2](https://mmmdong.github.io/portfolio/pixel-heroic-legend/) |
| `return-hero/world-boss` | 4 | 월드보스 신규 콘텐츠와 랭킹 정산 | [3-2](https://mmmdong.github.io/portfolio/return-hero/) |
| `eterna/steam-platform` | 7 | 플랫폼 추상화와 Steam·Android·iOS 로그인 분기 | [3-1](https://mmmdong.github.io/portfolio/eterna/) |
| `common-modules/chat-system` | 8 | 뒤끝 SDK 기반 채널별 실시간 채팅 | [본문](https://mmmdong.github.io/portfolio/chat-system/) |
| `common-modules/ad-manager` | 2 | 보상형 광고를 콜백 하나로 소비하는 싱글턴 | [본문](https://mmmdong.github.io/portfolio/applovin/) |
| `hello-kitty-mds/01-trophy-challenge` | 19 | 시즌제 도전과제 메타 — Repository / StateEvaluator / SortPolicy 분리 | [3-3](https://mmmdong.github.io/portfolio/hello-kitty-mds/) |
| `hello-kitty-mds/02-dream-balloon-festival` | 12 | 경쟁형 라이브 이벤트 — 좌석 감소를 시각의 결정론적 함수로 | [3-2](https://mmmdong.github.io/portfolio/hello-kitty-mds/) |
| `hello-kitty-mds/03-carrot-harvest` | 13 | 아케이드 미니게임 — 순수 C# 모델과 Unity 뷰 분리 | [3-4](https://mmmdong.github.io/portfolio/hello-kitty-mds/) |
| `hello-kitty-mds/04-cafe-balloon-minigame` | 12 | 서브 컨텐츠형 퍼즐 — 인터페이스 + 합성으로 메인에 결합 | [3-5](https://mmmdong.github.io/portfolio/hello-kitty-mds/) |
| `hello-kitty-mds/05-four-drop-item` | 8 | 머지판 연동 수집형 — 본편 공용 코드 무수정 additive 설계 | [3-1](https://mmmdong.github.io/portfolio/hello-kitty-mds/) |

코드 121개 파일 전부 자체 작성입니다. 서드파티 라이브러리 소스는 없습니다.
여기에 테이블 구조 발췌 3개가 더해집니다(아래).

`hello-kitty-mds/` 는 **운영 중인 상용 프로젝트**에서 발췌한 것이라 한 가지를 더 손봤습니다 — 주석에 있던 사내 이슈 트래커 키를 `ISSUE-NN` 으로 치환했습니다. 같은 원본 키는 파일을 넘나들어도 같은 라벨로 가므로, "서로 다른 이슈를 가리킨다" 는 주석의 의미는 그대로입니다. 자세한 내용은 [`hello-kitty-mds/README.md`](hello-kitty-mds/README.md) 에 적어 두었습니다.

## 테이블 발췌

테이블이 있는 프로젝트는 세 개지만, **프로젝트 글이 테이블을 언급하는 것은
미확인 용사단 하나**입니다. 픽셀 영웅 전설과 귀환병 전기 글에는 테이블 이야기가
없어서 올리지 않았습니다. 글이 설명하지 않는 데이터를 저장소에만 두면
읽는 사람이 맥락 없이 숫자만 보게 됩니다.

미확인 용사단 글이 이름을 대고 명세까지 실은 시트는 셋입니다.

| 파일 | 원본 | 열 | 원본 행 수 |
|---|---|---:|---:|
| `TABLE.Summon.schema.json` | 뽑기 테이블 | 28 | 105 |
| `DATA.LinkItem.schema.json` | 중첩 보상 참조 | 46 | 338 |
| `STAGE.StageDrop.schema.json` | 스테이지 드랍 | 36 | 5,000 |

각 파일에는 **열 구성과 원본 행 수, 대표 행 5개**만 있습니다.
전체 데이터는 넣지 않았습니다 — 서비스 중인 게임의 밸런스이고,
코드가 그 구조를 어떻게 다루는지 보이는 데는 다섯 행이면 충분합니다.

원본 파일에 들어 있던 **구글 스프레드시트 ID는 제거**했습니다. 살아있는
시트로 가는 접근 경로라, 숫자를 공개하느냐와 별개로 나가면 안 되는 값입니다.
쿠폰 계열 시트도 같은 파일 안에 있지만 넣지 않았습니다.

## 마스킹

공개 전 시크릿 스캔을 돌렸고, 검출된 값은 무엇이었는지 알 수 있는
자리표시자로 바꿨습니다. 값 자체는 이 저장소와 이력 어디에도 없습니다.

| 자리표시자 | 원래 값 | 위치 |
|---|---|---|
| `<ONESTORE_REWARDED_AD_UNIT_ID>` 외 3종 | AppLovin 보상형 광고 Ad Unit ID | `ad-manager/AdManager.cs` |
| `<GOOGLE_OAUTH_WEB_CLIENT_ID>` | Google OAuth 웹 클라이언트 ID | `steam-platform/Platform.cs` |
| `<PLAYFAB_TITLE_ID_DEV>` 외 3종 | PlayFab TitleId (개발 + 라이브 3서버) | `world-boss/WorldBossPopup.cs` |

스캐너는 OpenAI/Google API 키, OAuth 토큰, AWS 키, JWT, 사설키 블록,
내부 엔드포인트, PlayFab TitleId, AppLovin Ad Unit ID 패턴을 봅니다.
합성 시크릿을 주입해 실제로 검출되는지 확인한 뒤 통과 판정을 했습니다.

남아 있는 외부 URL은 Play 스토어·Steam 스토어·iTunes lookup API처럼
누구나 아는 공개 주소뿐입니다.

## 인코딩

원본 중 16개 파일이 CP949였습니다. 한글 주석이 깨지지 않도록
전 파일을 UTF-8(BOM 없음) · LF로 변환했습니다. 그 외 내용 변경은
위 마스킹이 전부입니다.

## 권리

상용 출시작에서 발췌한 코드가 포함돼 있어 오픈소스 라이선스를 붙이지 않습니다.
**채용 검토 목적의 열람용**이며, 복제·재배포·상업적 이용을 허용하지 않습니다.

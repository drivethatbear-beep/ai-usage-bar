# AI Usage Bar — 설계 스펙

- 작성일: 2026-10-01
- 상태: 사용자 승인된 설계를 문서화

## 1. 목적과 성공 기준

Windows 작업표시줄 **맨 왼쪽**에 Claude · Codex · Grok 의 **잔여 사용량**을 픽셀 도트 스타일로 상시 표시하는 프로그램.
누구나 `AIUsageBar-Setup.exe` 로 설치해서 쓸 수 있어야 한다.

성공 기준
- 작업표시줄만 보고 각 서비스의 남은 % 와 위험 상태(노랑/빨강)를 바로 알 수 있다.
- 마우스를 올리면 초기화까지 남은 시간, 플랜, 크레딧 등 상세가 보인다.
- 관리자 권한 없이 설치·제거되고, 별도 런타임 설치가 필요 없다.
- 사용자의 CLI 로그인을 절대 깨뜨리지 않는다(토큰 읽기 전용).

전제
- 각 사용자 PC 에 해당 CLI(Claude Code / Codex CLI / Grok CLI)가 로그인되어 있다.
- 로그인 안 된 서비스는 `--` 회색으로 표시하고 나머지는 정상 동작한다.

## 2. 데이터 소스 (2026-10-01 실측 확인)

| 서비스 | 1차 소스 | 대체 소스 | 표시 값 |
|---|---|---|---|
| Claude | `GET https://api.anthropic.com/api/oauth/usage` — 헤더 `Authorization: Bearer <claudeAiOauth.accessToken>`, `anthropic-beta: oauth-2025-04-20` (토큰: `%USERPROFILE%\.claude\.credentials.json`) | 없음(마지막 값 유지) | `five_hour.utilization`, `seven_day.utilization`(사용 %), `resets_at`, `subscriptionType` |
| Codex | `GET https://chatgpt.com/backend-api/wham/usage` — `Authorization: Bearer <tokens.access_token>`, `chatgpt-account-id: <tokens.account_id>` (`%USERPROFILE%\.codex\auth.json`) | 최신 `~/.codex/sessions/YYYY/MM/DD/*.jsonl` 의 마지막 `"rate_limits"` 객체 | `primary_window`/`secondary_window` 의 `used_percent`, `limit_window_seconds`, `reset_at`, `plan_type`, `credits.balance` |
| Grok | `~/.grok/sessions/**/usage.json` 의 `turns[]` (`endedAt`, `totalTokens`, `costUsdTicks`) | 없음 | 이번 주(월요일 00:00 로컬 기준) 비용 합계, 오늘 토큰 합계 |

- Codex 창 구분: `limit_window_seconds` ≤ 18000(5시간) 이면 `5H`, 그 외는 `WK`.
- Grok 비용: `costUsdTicks / 1e10` = USD. (예: 39,264,885,600 ticks = $3.93)
- Grok 은 한도 API 가 없으므로 **사용자 설정 주간 예산(USD)** 대비 잔여량을 계산한다. 예산 0 = 미설정 → 게이지 없이 금액만 표시.
- **토큰 갱신 금지.** refresh 를 하면 refresh token 이 회전되어 CLI 로그인이 깨질 수 있다. 매 조회마다 자격 파일을 다시 읽기만 한다. 401/403 → 상태 `NeedsLogin`.

## 3. 표시 규칙

- 모든 값은 **잔여량** 기준: `remaining = 100 - used` (0~100 클램프).
- `resets_at` 이 지났으면 다음 조회 전까지 잔여 100% 로 간주.
- 색상 단계: 잔여 > 20% 서비스 고유색 / 5~20% 노랑(#EF9F27) / < 5% 빨강(#E24B4A) 깜빡임(1Hz).
- 서비스 고유색: Claude #D97757, Codex #10A37F, Grok #E8E8E8.
- 데이터가 오래됨(마지막 성공 후 10분 초과) → 해당 블록 50% 흐리게.

## 4. 구성 요소

```
AIUsageBar.exe (.NET Framework 4.8, WinForms, x64/AnyCPU)
├─ Core/                      UI 무관, 단위 테스트 대상
│  ├─ UsageModel.cs           ServiceUsage, UsageWindow, ServiceStatus
│  ├─ Json.cs                 최소 JSON 파서 (외부 의존성 없음)
│  ├─ ClaudeParser.cs         oauth/usage 응답 → ServiceUsage
│  ├─ CodexParser.cs          wham/usage 응답 · 세션 rate_limits → ServiceUsage
│  └─ GrokAggregator.cs       usage.json 들 → 주간 비용/오늘 토큰 → ServiceUsage
├─ Providers/                 I/O 담당 (파일·HTTP)
│  ├─ IUsageProvider.cs       Task<ServiceUsage> FetchAsync()
│  ├─ ClaudeProvider.cs
│  ├─ CodexProvider.cs
│  └─ GrokProvider.cs
├─ UI/
│  ├─ PixelFont.cs            5×7 도트 폰트 (A-Z, 0-9, % $ . : / - 공백)
│  ├─ PixelRenderer.cs        도트 게이지·텍스트를 Graphics 에 그림
│  ├─ BarWindow.cs            작업표시줄 본체 창
│  ├─ TaskbarAttacher.cs      Shell_TrayWnd 부착 / 위치 계산 / 재부착
│  ├─ DetailPopup.cs          hover 상세 패널
│  └─ SettingsForm.cs         설정 창
├─ Settings.cs                %APPDATA%\AIUsageBar\settings.json 읽기/쓰기
├─ Scheduler.cs               주기 조회 + Grok FileSystemWatcher
└─ Program.cs                 단일 인스턴스(Mutex), 트레이 메뉴 없음(우클릭은 본체)
```

## 5. 작업표시줄 부착

1차: 자식 창 방식
- `FindWindow("Shell_TrayWnd")` → 본체 창을 `WS_CHILD` 로 바꾸고 `SetParent` 로 부착.
- 위치: 작업표시줄 클라이언트 영역 왼쪽 끝(x = 4px), 세로 가운데. 높이 = 작업표시줄 높이 − 8px.
- 작업표시줄이 세로(좌/우)면 블록을 세로로 쌓는다.
- `RegisterWindowMessage("TaskbarCreated")` 수신 시(탐색기 재시작) 재부착.
- 2초 주기 점검: 부모가 사라졌거나 작업표시줄 크기가 바뀌면 재계산.
- DPI: Per-Monitor V2 인식, 도트 크기 = 작업표시줄 높이에 비례(기본 48px → 도트 3px).

2차(대체): 부착 실패 시 `WS_EX_TOOLWINDOW | WS_EX_TOPMOST` 무테 창을 작업표시줄 왼쪽 위에 겹쳐 띄우고, 1초마다 `SetWindowPos(HWND_TOPMOST)` 로 최상위 유지. 전경 창이 전체화면이면 숨김.

가운데 정렬 작업표시줄에서는 Windows 위젯(날씨) 영역과 겹칠 수 있다 → 설치 마지막 화면과 README 에 "작업표시줄 설정 → 위젯 끄기" 안내.

## 6. 화면 레이아웃 (작업표시줄 48px 기준)

블록 3개를 가로로 배치, 블록 사이 점선 구분.

```
[CL] 5H ■■■■■■■■■□ 91%   [CX] WK ■■■■■■■■■□ 90%   [GK] WK ■■■■□□□□□□ 38%
     WK ■■■■■■■■■■ 95%        5H --                     $  7.6
```

- 라벨(CL/CX/GK)은 서비스 고유색, 행 라벨(5H/WK/$)은 회색.
- 게이지 10칸, 1칸 = 10%, 반올림.
- Hover → `DetailPopup`: 서비스별 플랜, 각 창 잔여 % 와 초기화까지 남은 시간(`1H10M`, `5D 23H`), Codex 크레딧, Grok 이번 주 사용/예산·오늘 토큰, 마지막 갱신 시각, 오류 상태.
- 우클릭 메뉴: 지금 새로고침 / 설정 / 시작 시 실행(체크) / 종료.
- 좌클릭: 상세 패널 고정 토글.

## 7. 설정 (`%APPDATA%\AIUsageBar\settings.json`)

| 키 | 기본값 | 설명 |
|---|---|---|
| `refreshSeconds` | 120 | Claude·Codex API 조회 주기(최소 60) |
| `grokWeeklyBudgetUsd` | 0 | 0 = 미설정 |
| `showClaude` / `showCodex` / `showGrok` | true | 블록 표시 여부 |
| `runAtStartup` | true | HKCU Run 키 등록 |

## 8. 오류 처리

| 상황 | 상태 | 표시 |
|---|---|---|
| 자격 파일 없음 | `NotInstalled` | `--` 회색 |
| HTTP 401/403 | `NeedsLogin` | `LOGIN` 회색, 패널에 "CLI 에서 다시 로그인" |
| 네트워크·5xx·파싱 실패 | `Error` | 마지막 값 유지 + 흐림, 패널에 실패 시각 |
| 429 | `Error` | 다음 조회를 2배 지연(최대 10분) |

HTTP 타임아웃 10초. 모든 조회는 UI 스레드 밖에서 실행.

## 9. 설치 파일

- Inno Setup 6, `PrivilegesRequired=lowest`, 설치 경로 `{localappdata}\Programs\AIUsageBar`.
- 시작 메뉴 바로가기, 선택 작업 "Windows 시작 시 실행"(기본 체크), 설치 후 실행.
- 제거 시 Run 키 삭제, 프로세스 종료. `%APPDATA%\AIUsageBar` 설정은 보존.
- 산출물: `dist\AIUsageBar-Setup.exe`. 빌드 스크립트 `build.ps1` 한 번으로 빌드→테스트→설치파일 생성.

## 10. 테스트

- `AIUsageBar.Tests` (xUnit, net48): 실측 응답 샘플(개인정보 제거)로 Claude/Codex 파서, Codex 세션 대체 파서, Grok 주간 합산(주 경계·오늘 경계), 잔여량·색 단계·초기화 경과 규칙 검증. PixelFont 모든 글리프가 5×7 인지 검증.
- 수동 검증: 이 PC 에서 설치 → 작업표시줄 왼쪽 표시 확인(스크린샷), 탐색기 재시작 후 재부착, hover 패널, 설정 변경 반영, 제거.

## 11. 범위 밖 (YAGNI)

- 토큰 갱신·로그인 기능, API 키 직접 입력
- 기록 그래프·알림 토스트
- 코드 서명(설치 시 SmartScreen 경고 가능 → README 에 안내)
- macOS/Linux

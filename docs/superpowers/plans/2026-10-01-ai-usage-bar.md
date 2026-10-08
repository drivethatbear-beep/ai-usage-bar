# AI Usage Bar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows 작업표시줄 맨 왼쪽에 Claude·Codex·Grok 잔여 사용량을 픽셀 도트로 표시하는 프로그램과 설치 파일을 만든다.

**Architecture:** UI 와 무관한 `Core`(JSON 파싱·잔여량 규칙)를 xUnit 으로 검증하고, `Providers` 가 파일·HTTP 를 읽어 `ServiceUsage` 를 만든다. `UI` 는 `Shell_TrayWnd` 에 자식 창으로 붙는 WinForms 창 하나에 도트로 그린다. Inno Setup 으로 사용자별 설치 파일을 만든다.

**Tech Stack:** C# (LangVersion latest), .NET Framework 4.8 WinForms, `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3, xUnit 2.9 + xunit.runner.visualstudio, Inno Setup 6.

**Spec:** `docs/superpowers/specs/2026-10-01-ai-usage-bar-design.md`

## Global Constraints

- 대상 프레임워크 `net48`, 외부 런타임 의존성 0 (앱 프로젝트에 NuGet 런타임 패키지 금지; ReferenceAssemblies 는 빌드 전용).
- 토큰 갱신 금지 — 자격 파일은 매 조회마다 읽기만 한다.
- 잔여량 `remaining = clamp(100 - used, 0, 100)`; `resets_at` 경과 시 100.
- 색: Claude `#D97757`, Codex `#10A37F`, Grok `#E8E8E8`, 노랑 `#EF9F27`(5 ≤ 잔여 ≤ 20), 빨강 `#E24B4A`(잔여 < 5, 1Hz 깜빡임), 빈 도트 `#3A3A3A`.
- 게이지 10칸, 칸 수 = `round(remaining / 10)`.
- 오래됨 기준: 마지막 성공 후 10분 초과 → 블록 50% 투명.
- 설정 파일 `%APPDATA%\AIUsageBar\settings.json`, 기본 `refreshSeconds=120`(최소 60), `grokWeeklyBudgetUsd=0`, `showClaude/showCodex/showGrok=true`, `runAtStartup=true`.
- HTTP 타임아웃 10초, 429 이면 다음 주기 2배(최대 600초).
- Grok 주 경계: 로컬 월요일 00:00. 비용 USD = `costUsdTicks / 1e10`.
- 설치: `PrivilegesRequired=lowest`, `{localappdata}\Programs\AIUsageBar`, 산출물 `dist\AIUsageBar-Setup.exe`.
- 앱 화면 문자열은 도트 폰트가 지원하는 대문자 영문·숫자·`% $ . : / -`·공백만 사용. 설정 창·README 는 한국어.

## Review Focus

1. Claude 토큰 만료(`expiresAt` 과거) 상태에서 CLI 를 오래 안 쓴 사용자 → 401 → `NeedsLogin`("LOGIN") 표시, 크래시·무한 재시도 없음. → Task 6 테스트.
2. 응답 필드가 `null`(예: `five_hour: null`, Codex `secondary_window: null`) → 해당 창만 생략, 나머지 정상. → Task 2·3 테스트.
3. Grok CLI 가 `usage.json` 을 쓰는 중이라 잘린/잠긴 파일 → 그 파일만 건너뛰고 합산 계속. → Task 4 테스트.
4. `resets_at` 이 이미 지난 오래된 값(PC 절전 후 복귀) → 100% 로 표시. → Task 2 테스트.
5. 세션 파일이 수백 개 → 매 이벤트마다 전부 다시 파싱하지 않도록 (경로, 수정시각) 캐시. → Task 4 테스트.

---

### Task 1: 솔루션 골격 + 최소 JSON 파서 + 모델

**Files:**
- Create: `AIUsageBar.sln`, `src/AIUsageBar/AIUsageBar.csproj`, `src/AIUsageBar/Program.cs`(임시 `Main` 빈 구현), `src/AIUsageBar/Core/Json.cs`, `src/AIUsageBar/Core/UsageModel.cs`
- Create: `tests/AIUsageBar.Tests/AIUsageBar.Tests.csproj`, `tests/AIUsageBar.Tests/JsonTests.cs`

**Interfaces:**
- Produces:
  - `static object Json.Parse(string text)` → `Dictionary<string,object>` / `List<object>` / `string` / `double` / `bool` / `null`. 잘못된 입력은 `FormatException`.
  - `static object Json.Get(object root, string path)` — 점 경로(`"a.b.0.c"`, 숫자는 배열 인덱스), 없으면 `null`.
  - `static double? Json.Num(object v)`, `static string Json.Str(object v)`.
  - `enum ServiceStatus { Ok, NotInstalled, NeedsLogin, Error }`, `enum Severity { Normal, Low, Critical }`
  - `class UsageWindow { string Label; double UsedPercent; DateTimeOffset? ResetsAt; double RemainingAt(DateTimeOffset now); Severity SeverityAt(DateTimeOffset now); int FilledDots(DateTimeOffset now); }`
  - `class ServiceUsage { string Service; string Tag /*"CL","CX","GK"*/; ServiceStatus Status; string Plan; List<UsageWindow> Windows; List<KeyValuePair<string,string>> Details; DateTimeOffset? LastSuccess; string Error; }`

- [ ] **Step 1:** 프로젝트 생성. 앱 csproj: SDK 스타일, `<TargetFramework>net48</TargetFramework>`, `<OutputType>WinExe</OutputType>`, `<UseWindowsForms>true</UseWindowsForms>`, `<LangVersion>latest</LangVersion>`, `<Nullable>disable</Nullable>`, `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 (`PrivateAssets=all`), `System.Net.Http` 프레임워크 참조, `<ApplicationHighDpiMode>` 대신 app.manifest 로 PerMonitorV2. 테스트 csproj: net48, xunit 2.9.x, xunit.runner.visualstudio 2.8.x, Microsoft.NET.Test.Sdk 17.x, 앱 프로젝트 참조. 앱에 `[assembly: InternalsVisibleTo("AIUsageBar.Tests")]`.
- [ ] **Step 2: Write failing tests** `JsonTests`:
  - `Parse_Object_Nested`: `{"a":{"b":[1,{"c":"x"}]},"t":true,"n":null}` → `Get(r,"a.b.1.c")=="x"`, `Get(r,"a.b.0")` is `1.0`, `Get(r,"t")` is `true`, `Get(r,"n")==null`, `Get(r,"zz.y")==null`.
  - `Parse_StringEscapes`: `"\"\\u00e9\\n\\\"\""` → `"é\n\""`.
  - `Parse_Numbers`: `-1.5e2` → `-150.0`; `62500` 문자열 `"62500"` 은 `Num` 이 `62500` 반환.
  - `Parse_Invalid_Throws`: `{"a":` → `FormatException`.
  - `Window_Remaining_Rules` (`UsageModelTests.cs`): now=2026-10-01T10:00Z; used 9, resets 11:10Z → remaining 91, FilledDots 9, Normal; used 85 → 15, Low; used 96 → 4, Critical; used 120 → 0; used 50, resets 09:00Z(과거) → 100.
- [ ] **Step 3:** `dotnet test` → FAIL(컴파일 오류).
- [ ] **Step 4:** 구현. `Json` 은 재귀 하강 파서(~150줄), `CultureInfo.InvariantCulture` 로 숫자 파싱.
- [ ] **Step 5:** `dotnet test` → 전부 PASS.
- [ ] **Step 6:** Commit `feat: solution skeleton, json parser, usage model`.

### Task 2: Claude 파서

**Files:** Create `src/AIUsageBar/Core/ClaudeParser.cs`, `tests/AIUsageBar.Tests/ClaudeParserTests.cs`, `tests/AIUsageBar.Tests/Samples/claude_usage.json`(2026-10-01 실측 응답에서 이메일·ID 제거, `five_hour.utilization 9.0` / `seven_day.utilization 5.0`).

**Interfaces:**
- Consumes: `Json`, `ServiceUsage`, `UsageWindow`.
- Produces: `static ServiceUsage ClaudeParser.Parse(string json, string subscriptionType, DateTimeOffset now)` — Windows: `five_hour`→`"5H"`, `seven_day`→`"WK"`(null 이면 생략). `Plan = subscriptionType?.ToUpperInvariant()`. `extra_usage.is_enabled==true` 면 Details 에 `("EXTRA", "$used/$limit")`.

- [ ] **Step 1: Failing tests:** `Parse_Sample`: Tag `"CL"`, Status Ok, Plan `"MAX"`, Windows `[5H used 9, WK used 5]`, 5H ResetsAt = `2026-10-01T11:10:00.295245+00:00`. `Parse_NullFiveHour`: `{"five_hour":null,"seven_day":{"utilization":40,"resets_at":"2026-10-02T00:00:00Z"}}` → Windows 1개 `WK`. `Parse_Garbage`: `"<html>"` → `FormatException`.
- [ ] **Step 2:** 실행 → FAIL. **Step 3:** 구현. **Step 4:** PASS. **Step 5:** Commit `feat: claude usage parser`.

### Task 3: Codex 파서 (API + 세션 대체)

**Files:** Create `src/AIUsageBar/Core/CodexParser.cs`, `tests/AIUsageBar.Tests/CodexParserTests.cs`, `Samples/codex_wham.json`(실측, 이메일·ID 제거), `Samples/codex_session.jsonl`(rate_limits 이벤트 2줄 — 앞줄 used 5, 마지막 줄 used 10).

**Interfaces:**
- Produces:
  - `static ServiceUsage CodexParser.ParseApi(string json, DateTimeOffset now)` — `rate_limit.primary_window`·`secondary_window` 각각: `limit_window_seconds <= 18000` → `"5H"`, 아니면 `"WK"`; `reset_at` 은 유닉스 초. Plan = `plan_type` 대문자. `credits.balance` 있으면 Details `("CREDITS", balance)`. 창 정렬: `5H` 먼저.
  - `static ServiceUsage CodexParser.ParseSessionTail(IEnumerable<string> lines, DateTimeOffset now)` — 마지막 `"rate_limits"` 를 포함한 줄을 파싱, `primary`/`secondary` 의 `used_percent`, `window_minutes`(≤300 → `5H`), `resets_at`; `plan_type`, `credits.balance`. 없으면 `null` 반환.

- [ ] **Step 1: Failing tests:** `ParseApi_Sample`: Plan `"PRO"`, Windows `[WK used 10, ResetsAt=FromUnixTimeSeconds(1791354699)]`, Details 포함 `("CREDITS","62500")`. `ParseApi_BothWindows`: primary 18000s used 30, secondary 604800s used 12 → `[5H 30, WK 12]`. `ParseSessionTail_UsesLast`: 샘플 → `WK used 10`. `ParseSessionTail_NoEvent`: 일반 줄만 → `null`.
- [ ] **Step 2–4:** FAIL → 구현 → PASS. **Step 5:** Commit `feat: codex usage parsers`.

### Task 4: Grok 집계기

**Files:** Create `src/AIUsageBar/Core/GrokAggregator.cs`, `tests/AIUsageBar.Tests/GrokAggregatorTests.cs`.

**Interfaces:**
- Produces:
  - `class GrokAggregator { GrokAggregator(Func<string,string> readFile); void Update(IEnumerable<(string path, DateTime mtimeUtc)> files); ServiceUsage Build(DateTimeOffset now, double weeklyBudgetUsd); internal int ParseCount; }`
  - (경로, mtime) 가 같으면 재파싱하지 않음(`ParseCount` 로 검증). 읽기/파싱 예외 파일은 건너뜀(다음 Update 때 재시도).
  - `Build`: `turns[].endedAt` 이 이번 주(로컬 월요일 00:00 이후) 인 `costUsdTicks` 합 → `weekCost`; 오늘(로컬 00:00 이후) `totalTokens` 합 → `todayTokens`. budget > 0 이면 Windows `[WK used = weekCost/budget*100, ResetsAt = 다음 월요일 00:00 로컬]`, 아니면 Windows 비움. Details: `("WEEK", "$12.40")` 또는 budget 있으면 `("WEEK", "$12.40 / $20")`, `("LEFT", "$7.60")`(budget 있을 때), `("TODAY", "18.0M TOK")`(K/M 단위, 소수 1자리). Tag `"GK"`, 파일 0개면 Status `NotInstalled`.

- [ ] **Step 1: Failing tests** (now = 2026-10-01 목요일 15:00 로컬, 테스트는 `TimeZoneInfo` 무관하도록 `now.ToLocalTime()` 기준 날짜로 픽스처 생성):
  - `Week_And_Today_Sums`: 파일 A 턴 3개 — 월요일 01:00 (cost 1e10 ticks, 1,000 tok), 오늘 09:00 (2e10, 2,000,000), 지난주 일요일 23:00 (5e10, 9) → budget 20: WK used 15(=3/20), Details WEEK `"$3.00 / $20"`, LEFT `"$17.00"`, TODAY `"2.0M TOK"`.
  - `NoBudget_NoWindow`: budget 0 → Windows 비어 있음, WEEK `"$3.00"`.
  - `Corrupt_File_Skipped`: 파일 B 내용 `{"turns":[` → 합계는 A 만, 예외 없음.
  - `Cache_By_Mtime`: 같은 목록으로 Update 두 번 → `ParseCount==2`(파일 2개 1회씩); A 의 mtime 바뀌면 3.
  - `No_Files_NotInstalled`.
- [ ] **Step 2–4:** FAIL → 구현 → PASS. **Step 5:** Commit `feat: grok weekly aggregator`.

### Task 5: 도트 폰트 + 렌더러

**Files:** Create `src/AIUsageBar/UI/PixelFont.cs`, `src/AIUsageBar/UI/PixelRenderer.cs`, `tests/AIUsageBar.Tests/PixelTests.cs`.

**Interfaces:**
- Produces:
  - `static class PixelFont { const int W=5, H=7; static bool[,] Glyph(char c); static int MeasureCols(string s) /* 글자당 5 + 간격 1, 마지막 간격 제외 */; }` 지원 문자: `A–Z 0–9 % $ . : / -` 공백. 소문자는 대문자로, 미지원 문자는 공백.
  - `class PixelRenderer { PixelRenderer(int dot /*px*/); void Text(Graphics g, int x, int y, string s, Color c); void Gauge(Graphics g, int x, int y, int filled, Color on); Size MeasureBar(IReadOnlyList<ServiceUsage> items); void DrawBar(Graphics g, IReadOnlyList<ServiceUsage> items, DateTimeOffset now, bool blinkPhase); }` 레이아웃은 스펙 §6: 블록 = 태그(2글자) + 행 최대 2개(행 라벨 2글자, 게이지 10칸, 잔여 `"91%"`), 블록 사이 점선 1열. 상태가 Ok 가 아니면 행 하나에 `"--"`(NotInstalled/Error 데이터 없음) 또는 `"LOGIN"`. Grok 예산 없음 → 행 `"$ 3.0"`.
  - 그리기는 `FillRectangle` 로 `dot×dot` 정사각형, 안티앨리어싱 끔. `LastSuccess` 가 `now` 보다 10분 넘게 이전이면 그 블록 색 알파 128.

- [ ] **Step 1: Failing tests:** `AllGlyphs_5x7`: 지원 문자 전부 `Glyph(c).GetLength(0)==7 && GetLength(1)==5`, 공백 외 글리프는 켜진 칸 ≥ 1, `'0'` 과 `'O'` 는 다른 패턴. `Measure`: `MeasureCols("91%")==17`. `MeasureBar_Stable`: 3블록 측정 폭이 `dot` 배수, `dot=3` 일 때 높이 ≤ 42. `DrawBar_NoThrow`: 40×200 비트맵에 Ok/NeedsLogin/NotInstalled 섞어서 그려도 예외 없음, 좌상단 영역에 Claude 색 픽셀 존재.
- [ ] **Step 2–4:** FAIL → 구현 → PASS. **Step 5:** Commit `feat: pixel font and renderer`.

### Task 6: 설정 · 프로바이더 · 스케줄러

**Files:** Create `src/AIUsageBar/Settings.cs`, `src/AIUsageBar/Providers/IUsageProvider.cs`, `ClaudeProvider.cs`, `CodexProvider.cs`, `GrokProvider.cs`, `src/AIUsageBar/Scheduler.cs`, `tests/AIUsageBar.Tests/ProviderTests.cs`, `SettingsTests.cs`.

**Interfaces:**
- Consumes: Task 2–4 파서.
- Produces:
  - `class Settings { int RefreshSeconds=120; double GrokWeeklyBudgetUsd=0; bool ShowClaude=true, ShowCodex=true, ShowGrok=true, RunAtStartup=true; static Settings Load(string path); void Save(string path); static string DefaultPath; }` — 파일 없음/손상 → 기본값. `RefreshSeconds < 60` → 60. JSON 쓰기는 손으로 직렬화(키는 스펙 §7 camelCase).
  - `interface IUsageProvider { string Tag { get; } Task<ServiceUsage> FetchAsync(CancellationToken ct); }`
  - `ClaudeProvider(string homeDir, HttpMessageHandler handler)`, `CodexProvider(string homeDir, HttpMessageHandler handler)`, `GrokProvider(string homeDir, Func<double> budget)` — `homeDir` 기본 `Environment.GetFolderPath(UserProfile)`. User-Agent `AIUsageBar/1.0`. 결과 매핑: 자격 파일 없음 → `NotInstalled`; 401/403 → `NeedsLogin`; 429 → `Error` + `ServiceUsage.Error="RATE LIMIT"`; 기타 실패 → `Error`. Codex 는 API 실패 시 `sessions` 최신 jsonl 로 대체(성공하면 Status Ok, Details 에 `("SOURCE","LOCAL")`).
  - `class Scheduler : IDisposable { event Action<ServiceUsage> Updated; Scheduler(IEnumerable<IUsageProvider> p, Func<int> refreshSeconds); void Start(); Task RefreshNowAsync(); }` — 프로바이더별 독립 타이머, 429 → 그 프로바이더만 다음 지연 2배(최대 600s), 성공 시 원복. 실패 시 이전 성공 값의 Windows/Details 를 유지하고 Status·Error 만 갱신해서 `Updated` 발생. Grok 은 `FileSystemWatcher`(`*.json`, 하위 포함) 변경 시 2초 디바운스 후 조회.

- [ ] **Step 1: Failing tests** (임시 디렉터리를 homeDir 로, 가짜 `HttpMessageHandler` 로):
  - `Claude_NoCredentials_NotInstalled`.
  - `Claude_401_NeedsLogin` — 만료된 `expiresAt` 를 가진 자격 파일, 핸들러 401 → `NeedsLogin`, 요청 1회만 발생(재시도·갱신 요청 없음), 자격 파일 내용 변경 없음.
  - `Claude_Ok_SendsHeaders` — 요청에 `Authorization: Bearer tok`, `anthropic-beta: oauth-2025-04-20` 존재, 결과 Ok.
  - `Codex_ApiFails_FallsBackToSession` — 핸들러 500, `sessions/2026/10/01/x.jsonl` 에 샘플 → Ok, `SOURCE=LOCAL`.
  - `Scheduler_KeepsLastValueOnError` — 가짜 프로바이더: 1회차 Ok(WK 10), 2회차 Error → 2회차 이벤트 Windows 에 WK 10 유지, Status Error.
  - `Settings_Roundtrip_And_Clamp` — RefreshSeconds 30 저장 → Load 후 60; 손상 파일 → 기본값.
- [ ] **Step 2–4:** FAIL → 구현 → PASS. **Step 5:** Commit `feat: settings, providers, scheduler`.

### Task 7: 작업표시줄 창 · 상세 패널 · 설정 창 · 진입점

**Files:** Create `src/AIUsageBar/UI/TaskbarAttacher.cs`, `UI/BarWindow.cs`, `UI/DetailPopup.cs`, `UI/SettingsForm.cs`, `src/AIUsageBar/app.manifest`; Modify `src/AIUsageBar/Program.cs`.

**Interfaces:**
- Consumes: `Scheduler`, `PixelRenderer`, `Settings`, 프로바이더들.
- Produces (수동 검증 대상):
  - `TaskbarAttacher(Form bar)`: `bool Attach()` — `FindWindow("Shell_TrayWnd")`, 창 스타일을 `WS_CHILD|WS_VISIBLE` 로 바꾸고 `SetParent`, 위치 x=4·세로 가운데, 높이 = 작업표시줄 높이 − 8, 도트 크기 = `max(2, 작업표시줄 높이 / 16)`. 세로 작업표시줄(폭 < 높이)이면 위쪽 끝·블록 세로 배치 플래그. 실패 시 `false` → 대체 모드(최상위 무테 `WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE`, 1초마다 `SetWindowPos(HWND_TOPMOST)`, 전경이 전체화면이면 숨김). `RegisterWindowMessage("TaskbarCreated")` 수신 시 재부착, 2초 점검 타이머.
  - `BarWindow : Form` — 무테, 더블버퍼, 배경 투명 대신 작업표시줄 색에 맞춘 `#1C1C1C`(밝은 테마면 `#F3F3F3`; `HKCU\...\Themes\Personalize\SystemUsesLightTheme` 로 판정, 글자 색은 밝은 테마일 때 `#202020`). 500ms 깜빡임 타이머. Hover 300ms → `DetailPopup` 표시, 좌클릭 → 고정 토글, 우클릭 → `ContextMenuStrip`(지금 새로고침 / 설정 / 시작 시 실행 ✓ / 종료).
  - `DetailPopup : Form` — 도트 폰트로 스펙 §6 내용, 바 바로 위(작업표시줄이 위쪽이면 아래)에 표시. 초기화 남은 시간 포맷 `1H10M`, `5D 23H`, 60초 미만 `<1M`. 하단 `UPDATED HH:MM:SS`, 상태가 Ok 가 아니면 `LOGIN NEEDED` / `ERROR HH:MM`.
  - `SettingsForm` — 한국어 레이블: 갱신 주기(초, 60–3600), Grok 주간 예산(USD, 0=사용 안 함), 표시할 서비스 체크 3개, 시작 시 실행. 저장 시 `Settings.Save` + Run 키(`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, 값 이름 `AIUsageBar`) 반영 + 바 즉시 다시 그림.
  - `Program.Main` — `Mutex("Global\\AIUsageBar")` 단일 인스턴스, PerMonitorV2, 프로바이더 3개 + Scheduler 시작, `Application.Run(new BarWindow(...))`. 처리 안 된 예외는 `%APPDATA%\AIUsageBar\error.log` 에 기록.

- [ ] **Step 1:** 구현 후 `dotnet build -c Release` → 경고 외 오류 0.
- [ ] **Step 2:** 실행 → 작업표시줄 맨 왼쪽에 3블록 표시를 스크린샷으로 확인(값이 Claude API·Codex API·Grok 로컬과 일치).
- [ ] **Step 3:** `Stop-Process explorer` 후 재부착 확인(10초 이내), hover 패널, 우클릭 메뉴, 설정에서 Grok 예산 20 입력 → GK 게이지 표시 확인.
- [ ] **Step 4:** `dotnet test` 회귀 PASS. **Step 5:** Commit `feat: taskbar bar window, popup, settings UI`.

### Task 8: 설치 파일 · 빌드 스크립트 · README

**Files:** Create `installer/AIUsageBar.iss`, `build.ps1`, `README.md`, `src/AIUsageBar/Assets/app.ico`(도트 스타일 32/16px 아이콘, 빌드 시 코드로 생성하지 말고 리소스로 포함).

**Interfaces:**
- `build.ps1`: `dotnet test` → `dotnet build src/AIUsageBar -c Release` → `ISCC.exe installer/AIUsageBar.iss` (ISCC 위치: `${env:LOCALAPPDATA}\Programs\Inno Setup 6`, `C:\Program Files (x86)\Inno Setup 6` 순으로 탐색, 없으면 설치 방법 안내 후 종료 코드 1). 버전 `1.0.0`.
- `.iss`: AppId 고정 GUID, `PrivilegesRequired=lowest`, `DefaultDirName={localappdata}\Programs\AIUsageBar`, 한국어+영어 언어, Tasks `startup`(기본 체크) → `HKCU\...\Run\AIUsageBar`, `[Run]` 설치 후 실행, `[UninstallRun]`/`[Code]` 로 실행 중 프로세스 종료(`taskkill /IM AIUsageBar.exe /F`), 완료 페이지 문구에 "가운데 정렬 작업표시줄이면 작업표시줄 설정에서 위젯을 끄세요".
- `README.md`(한국어): 기능, 요구 사항(각 CLI 로그인), 설치, 표시 읽는 법(잔여량·색), Grok 예산 설정, 위젯 겹침, SmartScreen 경고 안내, 데이터는 로컬 자격 정보로 각 공식 서비스에만 요청한다는 개인정보 설명, 빌드 방법.

- [ ] **Step 1:** `./build.ps1` → `dist/AIUsageBar-Setup.exe` 생성.
- [ ] **Step 2:** 설치 실행(관리자 권한 요청 없음 확인) → 작업표시줄 표시, 시작 메뉴 바로가기, Run 키 존재.
- [ ] **Step 3:** 제거 → 프로세스 종료·Run 키 삭제·설치 폴더 삭제 확인.
- [ ] **Step 4:** Commit `feat: installer, build script, README`.

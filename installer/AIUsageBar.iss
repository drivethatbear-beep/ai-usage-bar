; AI Usage Bar — per-user installer (no admin rights). Build with build.ps1.
#define AppName "AI Usage Bar"
#define AppExe "AIUsageBar.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define BinDir "..\src\AIUsageBar\bin\Release\net48"

[Setup]
AppId={{9297415D-5D72-4681-A7F8-DACD64D239A5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={localappdata}\Programs\AIUsageBar
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=AIUsageBar-Setup
SetupIconFile=..\src\AIUsageBar\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
CloseApplications=force
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.StartupTask=Windows 시작 시 자동 실행
english.StartupTask=Start automatically when Windows starts
korean.LaunchNow=AI Usage Bar 실행
english.LaunchNow=Launch AI Usage Bar
korean.FinishNote=작업표시줄 맨 왼쪽에 Claude · Codex · Grok 잔여 사용량이 표시됩니다.%n%n작업표시줄이 가운데 정렬이고 Windows 위젯(날씨)이 켜져 있으면 겹칠 수 있습니다. 작업표시줄 설정에서 위젯을 꺼 주세요.%n%n각 서비스는 이 PC의 CLI(Claude Code, Codex CLI, Grok CLI)에 로그인되어 있어야 표시됩니다.
english.FinishNote=Remaining Claude, Codex and Grok usage now shows at the far left of the taskbar.%n%nIf your taskbar is centered and Windows Widgets are on, they may overlap; turn Widgets off in taskbar settings.%n%nEach service needs its CLI (Claude Code, Codex CLI, Grok CLI) signed in on this PC.

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"

[Files]
Source: "{#BinDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\{#AppExe}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"

[Run]
; Start-with-Windows is a Task Scheduler sign-in task (starts right at logon, unlike the throttled Run key).
Filename: "{app}\{#AppExe}"; Parameters: "--startup on"; Flags: runhidden waituntilterminated; Tasks: startup
Filename: "{app}\{#AppExe}"; Parameters: "--startup off"; Flags: runhidden waituntilterminated; Tasks: not startup
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := ExpandConstant('{cm:FinishNote}');
end;

{ Ask a running copy to exit cleanly (restores the Win10 task band), then force it only if still alive. }
procedure StopRunningApp(Exe: String);
var
  Code, I: Integer;
begin
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 20 do
  begin
    Exec(ExpandConstant('{sys}\cmd.exe'), '/c tasklist /FI "IMAGENAME eq {#AppExe}" | find /I "{#AppExe}" >NUL', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code <> 0 then Exit;
    Sleep(150);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp(ExpandConstant('{app}\{#AppExe}'));
    { Remove the sign-in task (the app may have created it from its own menu as well). }
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "AIUsageBar" /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  { Older versions used the Run key. }
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AIUsageBar');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Stop a running copy so the exe can be replaced on upgrade. }
  StopRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := '';
end;

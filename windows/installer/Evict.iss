; Inno Setup 6 script for Evict Uninstaller
; Compile: ISCC.exe /DMyAppVersion=1.8.0 /DSourceDir=..\Portable Evict.iss   (output: ..\Installed\Evict-Setup-<version>.exe)
; (GitHub Actions does this automatically – see .github/workflows/windows.yml)

#define MyAppName "Evict Uninstaller"
#ifndef MyAppVersion
  #define MyAppVersion "1.8.0"
#endif
#ifndef MyAppNumericVersion
  ; Windows version resources require numbers; AppVersion and filenames retain the full beta label.
  #define MyAppNumericVersion MyAppVersion
#endif
#define MyAppPublisher "Krishna Bhunia"
#define MyAppURL "https://github.com/krishnabhunia/evict-uninstaller"
#define MyAppExeName "Evict.exe"
#ifndef SourceDir
  #define SourceDir "..\Portable"
#endif

[Setup]
AppId={{B7E7C1F4-3D2A-4F6B-9A1E-5C0D2E8F7A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppNumericVersion}
DefaultDirName={autopf}\Evict
DefaultGroupName=Evict
DisableProgramGroupPage=yes
; Per-user by default (no UAC); the user may choose "all users" in the dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\Installed
OutputBaseFilename=Evict-Setup-{#MyAppVersion}
SetupIconFile=..\src\Evict.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
ShowLanguageDialog=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "contextmenu"; Description: "Add ""Uninstall with Evict"" to the right-click menu of programs (.exe) and shortcuts"; GroupDescription: "Windows integration:"
Name: "sendto"; Description: "Add Evict to the ""Send to"" menu"; GroupDescription: "Windows integration:"; Flags: unchecked
Name: "autostart"; Description: "Start Evict with Windows (hidden in the notification area - detects installers automatically)"; GroupDescription: "Windows integration:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Easy Uninstall widget"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--widget"; Comment: "Floating drag-and-drop uninstall target"
Name: "{group}\Software Health scan"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--scan"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{usersendto}\Uninstall with Evict"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-file"; Tasks: sendto

[Registry]
; Optional autostart (per-user or all-users depending on the install mode); the app's own Settings toggle manages the same value.
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Evict"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Tasks: autostart; Flags: uninsdeletevalue
; Marker the app uses to know it was installed by Setup (→ updates download the installer, not the raw exe).
Root: HKA; Subkey: "Software\Evict"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Evict"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"
; Explorer context menu for .exe files and shortcuts (per-user or all-users depending on the install mode).
Root: HKA; Subkey: "Software\Classes\exefile\shell\EvictUninstall"; ValueType: string; ValueName: ""; ValueData: "Uninstall with Evict"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\exefile\shell\EvictUninstall"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\exefile\shell\EvictUninstall\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --uninstall-file ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\lnkfile\shell\EvictUninstall"; ValueType: string; ValueName: ""; ValueData: "Uninstall with Evict"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\lnkfile\shell\EvictUninstall"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\lnkfile\shell\EvictUninstall\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --uninstall-file ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent self-update started by Evict (…/EVICTUPDATE=1): bring the new version straight back up.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--updated"; Flags: nowait skipifnotsilent; Check: IsSelfUpdate

[UninstallRun]
; Safety net if Evict.exe could not run its own clean-up: remove the autostart entry.
; Scheduled tasks are removed only by the owner-aware self-cleanup below.
Filename: "{cmd}"; Parameters: "/C reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Evict /f"; Flags: runhidden; RunOnceId: "DelRun"

[Code]
var
  SelfCleanupHandled: Boolean;

const
  EvictMutex = 'EvictUninstaller.SingleInstance';
  EvictPipe = '\\.\pipe\EvictUninstaller.Args.v1';
  EvictGenericWrite = $40000000;
  EvictOpenExisting = 3;
  EvictShareAll = 7;
  EvictSynchronize = $00100000;
  EvictQueryLimitedInformation = $1000;
  EvictSecurityIdentification = $00010000;
  EvictSecurityQosPresent = $00100000;
  EvictPipeNowait = 1;
  EvictWaitObject = 0;
  EvictSharingViolation = 32;
  EvictLockViolation = 33;

// Win32 BOOL is a signed 32-bit integer; use Integer and explicit zero comparisons.
// HANDLE/UINT_PTR remain pointer-sized, and DWORD values retain their native width.
function EvictCreateFile(Name: String; Access, Share: DWORD; Security: UINT_PTR;
  Creation, Attributes: DWORD; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function EvictCloseHandle(Handle: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall';
function EvictWaitNamedPipe(Name: String; Timeout: DWORD): Integer;
  external 'WaitNamedPipeW@kernel32.dll stdcall';
function EvictSetPipeState(Pipe: THandle; var Mode: DWORD;
  CollectionCount, CollectionTimeout: UINT_PTR): Integer;
  external 'SetNamedPipeHandleState@kernel32.dll stdcall';
function EvictWriteFile(Handle: THandle; Buffer: AnsiString; Count: DWORD;
  var Written: DWORD; Overlapped: UINT_PTR): Integer;
  external 'WriteFile@kernel32.dll stdcall';
function EvictGetPipeServerPid(Pipe: THandle; var Pid: DWORD): Integer;
  external 'GetNamedPipeServerProcessId@kernel32.dll stdcall';
function EvictOpenProcess(Access: DWORD; Inherit: Integer; Pid: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function EvictQueryProcessImage(Process: THandle; Flags: DWORD; Name: String;
  var Size: DWORD): Integer;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function EvictWaitForProcess(Process: THandle; Timeout: DWORD): DWORD;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function EvictCurrentPid(): DWORD;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function EvictProcessSession(Pid: DWORD; var Session: DWORD): Integer;
  external 'ProcessIdToSessionId@kernel32.dll stdcall';

function CloseFailureMessage(): String;
begin
  Result := 'Evict is still running or could not be contacted. Setup will not replace its files.' +
    '' + #13#10 + 'Exit Evict from its notification-area icon, then run Setup again.' +
    '' + #13#10 + 'If security software reports a blocked Evict file, review its notifications ' +
    '(Bitdefender: Notifications). The Setup log records the Windows error; no protection changes are required.';
end;

// --exit is six ASCII/UTF-8 bytes, exactly CommandLineOptions.Pack(new[] { "--exit" }).
// Close the byte-pipe connection after writing so the running app receives EOF.
// Never extract or launch the bundled EXE merely to contact the current instance.
function AskEvictToExit(): Boolean;
var
  Pipe, Process: THandle;
  Pid, Session, SetupSession, ImageSize, Mode, Written, ErrorCode, WaitResult: DWORD;
  Image: String;
  Payload: AnsiString;
begin
  Result := False;
  Process := 0;
  if EvictWaitNamedPipe(EvictPipe, 2500) = 0 then
  begin
    ErrorCode := DLLGetLastError;
    Log('Evict exit pipe unavailable; Windows error ' + IntToStr(ErrorCode));
    exit;
  end;
  Pipe := EvictCreateFile(EvictPipe, EvictGenericWrite, 0, 0, EvictOpenExisting,
    EvictSecurityQosPresent or EvictSecurityIdentification, 0);
  if Pipe = THandle(-1) then
  begin
    ErrorCode := DLLGetLastError;
    Log('Could not open Evict exit pipe; Windows error ' + IntToStr(ErrorCode));
    exit;
  end;
  try
    if EvictGetPipeServerPid(Pipe, Pid) = 0 then
    begin
      ErrorCode := DLLGetLastError;
      Log('Could not identify Evict pipe server; Windows error ' + IntToStr(ErrorCode));
      exit;
    end;
    if EvictProcessSession(Pid, Session) = 0 then
    begin
      ErrorCode := DLLGetLastError;
      Log('Could not read Evict pipe server session; Windows error ' + IntToStr(ErrorCode));
      exit;
    end;
    if EvictProcessSession(EvictCurrentPid(), SetupSession) = 0 then
    begin
      ErrorCode := DLLGetLastError;
      Log('Could not read Setup session; Windows error ' + IntToStr(ErrorCode));
      exit;
    end;
    if Session <> SetupSession then
    begin
      Log('Refusing to close a pipe server outside this Windows session.');
      exit;
    end;
    Process := EvictOpenProcess(EvictSynchronize or EvictQueryLimitedInformation, 0, Pid);
    if Process = 0 then
    begin
      ErrorCode := DLLGetLastError;
      Log('Could not inspect/wait for Evict process; Windows error ' + IntToStr(ErrorCode));
      exit;
    end;
    ImageSize := 32768;
    SetLength(Image, ImageSize);
    if EvictQueryProcessImage(Process, 0, Image, ImageSize) = 0 then
    begin
      ErrorCode := DLLGetLastError;
      Log('Could not read Evict pipe server image; Windows error ' + IntToStr(ErrorCode));
      exit;
    end;
    SetLength(Image, ImageSize);
    if CompareText(ExtractFileName(Image), '{#MyAppExeName}') <> 0 then
    begin
      Log('Refusing to send --exit to an unexpected pipe server: ' + Image);
      exit;
    end;
    Log('Asking Evict to exit gracefully: PID ' + IntToStr(Pid) + ', ' + Image);
    // Nonblocking byte mode makes the tiny WriteFile return immediately, even if the app is stuck.
    Mode := EvictPipeNowait;
    if EvictSetPipeState(Pipe, Mode, 0, 0) = 0 then
    begin
      ErrorCode := DLLGetLastError;
      Log('Could not make Evict exit pipe nonblocking; Windows error ' + IntToStr(ErrorCode));
      exit;
    end;
    Payload := Utf8Encode('--exit');
    Written := 0;
    if EvictWriteFile(Pipe, Payload, Length(Payload), Written, 0) = 0 then
    begin
      ErrorCode := DLLGetLastError;
      Log('Could not write Evict exit request; bytes ' + IntToStr(Written) +
        ', Windows error ' + IntToStr(ErrorCode));
      exit;
    end;
    if Written <> DWORD(Length(Payload)) then
    begin
      Log('Evict exit request was only partially written; bytes ' + IntToStr(Written) +
        ' of ' + IntToStr(Length(Payload)));
      exit;
    end;
    Result := True;
  finally
    EvictCloseHandle(Pipe);
    if not Result and (Process <> 0) then
    begin
      EvictCloseHandle(Process);
      Process := 0;
    end;
  end;
  if Process <> 0 then
  begin
    try
      WaitResult := EvictWaitForProcess(Process, 10000);
      Result := WaitResult = EvictWaitObject;
      Log('Evict process exit wait result: ' + IntToStr(WaitResult));
    finally
      EvictCloseHandle(Process);
    end;
  end;
end;

procedure WaitForEvictExit(MaxMs: Integer);
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes(EvictMutex) and (Waited < MaxMs) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

function CloseRunningEvict(): Boolean;
begin
  Result := True;
  if not CheckForMutexes(EvictMutex) then exit;
  Result := AskEvictToExit();
  // A successful request is not enough: the process must terminate and release its mutex.
  Result := Result and not CheckForMutexes(EvictMutex);
  if not Result then Log(CloseFailureMessage());
end;

function IsSelfUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:EVICTUPDATE|0}') = '1';
end;

// Open the exact destination without truncating or changing it. A mapped/running EXE rejects write access.
// This also catches access-denied/blocked-file errors before the actual replacement starts.
function DestinationReady(const Filename: String; var ErrorCode: DWORD): Boolean;
var
  Handle: THandle;
begin
  ErrorCode := 0;
  Handle := EvictCreateFile(Filename, EvictGenericWrite, EvictShareAll, 0, EvictOpenExisting, 0, 0);
  if Handle <> THandle(-1) then
  begin
    EvictCloseHandle(Handle);
    Result := True;
  end
  else
  begin
    ErrorCode := DLLGetLastError;
    Result := (ErrorCode = 2) or (ErrorCode = 3); // first installation: destination absent
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Filename: String;
  ErrorCode: DWORD;
  Waited: Integer;
begin
  Result := '';
  if not CloseRunningEvict() then
  begin
    Result := CloseFailureMessage();
    exit;
  end;
  Filename := ExpandConstant('{app}\{#MyAppExeName}');
  Waited := 0;
  // Self-updates release their mutex before their final shutdown. Wait for this particular file's lock too.
  while not DestinationReady(Filename, ErrorCode) do
  begin
    if ((ErrorCode <> EvictSharingViolation) and (ErrorCode <> EvictLockViolation)) or (Waited >= 10000) then
    begin
      Log('Destination not ready: ' + Filename + '; Windows error ' + IntToStr(ErrorCode));
      Result := 'Setup cannot open ' + Filename + ' for replacement.' + #13#10 +
        'Windows error ' + IntToStr(ErrorCode) + ': ' + SysErrorMessage(ErrorCode) + #13#10 +
        'Close Evict and try again. If security software reports a blocked file, review its notifications ' +
        '(Bitdefender: Notifications). Keep the Setup log for diagnosis.';
      exit;
    end;
    Sleep(250);
    Waited := Waited + 250;
  end;
  Log('Destination is ready for replacement: ' + Filename);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  Log('Initializing Evict Setup ' + '{#MyAppVersion}');
  if IsSelfUpdate() then WaitForEvictExit(10000);
  while CheckForMutexes(EvictMutex) do
  begin
    if WizardSilent() or IsSelfUpdate() then
    begin
      Result := CloseRunningEvict();
      if not Result then Log(CloseFailureMessage());
      exit;
    end;
    if MsgBox('Evict Uninstaller is running (possibly minimized to the notification area).' +
      '' + #13#10 + #13#10 + 'Setup needs to close it before installing. Close Evict now?',
      mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
    if not CloseRunningEvict() then
      MsgBox(CloseFailureMessage(), mbError, MB_OK);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not CheckForMutexes(EvictMutex) then exit;
  if not UninstallSilent() then
    if MsgBox('Evict Uninstaller is running. Close it and continue uninstalling?',
      mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  Result := CloseRunningEvict();
  if not Result then
  begin
    Log(CloseFailureMessage());
    if not UninstallSilent() then MsgBox(CloseFailureMessage(), mbError, MB_OK);
  end;
end;


// Before the files are removed, Evict.exe itself removes its registry entries, scheduled task and menus, and asks
// (checkbox dialog) which of its data to delete: settings, history + registry backups, the installer-package backup,
// browser-settings backups and Windows' records of Evict.exe. A silent uninstall removes only the integration.
// If Evict.exe cannot run, the old Yes/No question about the data folder is the fallback.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ExePath, Params: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    SelfCleanupHandled := False;
    // A running Evict (tray / widget) would hold its files and could rewrite its settings while they are removed.
    if not CloseRunningEvict() then
      RaiseException(CloseFailureMessage());
    ExePath := ExpandConstant('{app}\{#MyAppExeName}');
    if UninstallSilent() then
      Params := '--self-cleanup integration'
    else
      Params := '--self-cleanup ask';
    if FileExists(ExePath) then
      if Exec(ExePath, Params, ExpandConstant('{app}'), SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
        SelfCleanupHandled := (ResultCode = 0);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Native libraries the single-file Evict.exe unpacked (a cache – safe to remove once Evict is gone).
    DelTree(AddBackslash(GetEnv('TEMP')) + '.net\Evict', True, True, True);
    if (not SelfCleanupHandled) and (not UninstallSilent()) and DirExists(ExpandConstant('{localappdata}\Evict')) then
      if MsgBox('Also remove Evict''s settings, uninstall history and install-monitor logs?' + #13#10 +
                '(Folder: ' + ExpandConstant('{localappdata}\Evict') + ')', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{localappdata}\Evict'), True, True, True);
  end;
end;


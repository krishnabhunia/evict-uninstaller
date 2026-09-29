; Inno Setup 6 script for Evict Uninstaller
; Compile: ISCC.exe /DMyAppVersion=1.7.0 /DSourceDir=..\Portable Evict.iss   (output: ..\Installed\Evict-Setup-<version>.exe)
; (GitHub Actions does this automatically – see .github/workflows/windows.yml)

#define MyAppName "Evict Uninstaller"
#ifndef MyAppVersion
  #define MyAppVersion "1.7.0"
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
VersionInfoVersion={#MyAppVersion}
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
; Safety net if Evict.exe could not run its own clean-up (see [Code]): release the exe, drop the scheduled scan + autostart entry.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillEvict"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""Evict Software Health scan"""; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{cmd}"; Parameters: "/C reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Evict /f"; Flags: runhidden; RunOnceId: "DelRun"

[Code]
var
  SelfCleanupHandled: Boolean;

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
    Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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

// Evict starts the new Setup with /SILENT /CLOSEAPPLICATIONS /NORESTART /EVICTUPDATE=1 when the user
// accepts an in-app update; the [Run] entry above relaunches Evict afterwards.
function IsSelfUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:EVICTUPDATE|0}') = '1';
end;

const
  // Held by every running Evict (Program.cs: Local\EvictUninstaller.SingleInstance) – installed, portable, tray or elevated.
  EvictMutex = 'EvictUninstaller.SingleInstance';

// True while any Evict.exe process exists (tasklist | find returns 0 when it finds the name).
function EvictProcessRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /NH /FI "IMAGENAME eq {#MyAppExeName}" | find /I "{#MyAppExeName}" >nul',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Waits up to MaxMs for the single-instance lock to go away.
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

// An Evict running as administrator ("Start as administrator", on by default since 1.7.0) cannot be force-closed by a
// Setup without administrator rights. Evict 1.7+ closes itself when another copy is started with --exit: Setup uses the
// Evict.exe it carries, the uninstaller the installed one.
procedure AskEvictToExit();
var
  Exe: String;
  ResultCode: Integer;
begin
  try
    if IsUninstaller() then
      Exe := ExpandConstant('{app}\{#MyAppExeName}')
    else
    begin
      ExtractTemporaryFile('{#MyAppExeName}');
      Exe := ExpandConstant('{tmp}\{#MyAppExeName}');
    end;
    if FileExists(Exe) then
      Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  except
    Log('Could not ask Evict to exit: ' + GetExceptionMessage());
  end;
end;

// Closes every Evict.exe and waits for the single-instance lock to go away.
// Returns False while an Evict is still running (e.g. an older version started as administrator while Setup is not elevated).
function CloseRunningEvict(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForEvictExit(8000);
  if CheckForMutexes(EvictMutex) then
  begin
    AskEvictToExit();
    WaitForEvictExit(10000);
  end;
  // The process can outlive its lock by a moment while it exits; give Windows time to release Evict.exe.
  Sleep(500);
  Result := not CheckForMutexes(EvictMutex);
end;

// Before anything is installed: if an Evict is running (any copy, any version) ask to close it – Setup cannot replace
// Evict.exe while it runs. Silent installs and Evict's own self-update close it without asking.
function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if IsSelfUpdate() then
  begin
    // The updating Evict released its lock and is exiting (saving its settings): give it up to 10 s, then make sure.
    Waited := 0;
    while EvictProcessRunning() and (Waited < 10000) do
    begin
      Sleep(500);
      Waited := Waited + 500;
    end;
    if EvictProcessRunning() then CloseRunningEvict();
    exit;
  end;
  while CheckForMutexes(EvictMutex) do
  begin
    if WizardSilent() then
    begin
      if not CloseRunningEvict() then Log('Evict is still running; the file replacement may need a restart.');
      exit;
    end;
    if MsgBox('Evict Uninstaller is running (possibly minimized to the notification area).' + #13#10 + #13#10 +
              'Setup needs to close it before installing. Close Evict now?', mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False; // user cancelled – leave everything as it is
      exit;
    end;
    if not CloseRunningEvict() then
      MsgBox('Evict could not be closed – it may be running as administrator.' + #13#10 +
             'Exit it from its notification-area icon (right-click → Exit), then click OK to try again.', mbError, MB_OK);
  end;
end;

// The uninstaller gets the same check (its own [Code] also force-closes Evict, but a prompt is friendlier).
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if UninstallSilent() or not CheckForMutexes(EvictMutex) then exit;
  if MsgBox('Evict Uninstaller is running. Close it and continue uninstalling?', mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    Result := False
  else
    CloseRunningEvict();
end;

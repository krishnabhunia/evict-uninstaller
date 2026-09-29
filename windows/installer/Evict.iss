; Inno Setup 6 script for Evict Uninstaller
; Compile: ISCC.exe /DMyAppVersion=1.2.0 /DSourceDir=..\publish Evict.iss
; (GitHub Actions does this automatically – see .github/workflows/build.yml)

#define MyAppName "Evict Uninstaller"
#ifndef MyAppVersion
  #define MyAppVersion "1.3.3"
#endif
#define MyAppPublisher "Krishna Bhunia"
#define MyAppURL "https://github.com/krishnabhunia/evict"
#define MyAppExeName "Evict.exe"
#ifndef SourceDir
  #define SourceDir "..\publish"
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
OutputDir=Output
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
; Make sure the widget / tray instance is not holding the exe, and drop the scheduled scan + autostart entries.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillEvict"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""Evict Software Health scan"""; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{cmd}"; Parameters: "/C reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Evict /f"; Flags: runhidden; RunOnceId: "DelRun"

[Code]
// Offer to remove settings, history and install logs when uninstalling.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(ExpandConstant('{localappdata}\Evict')) then
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

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

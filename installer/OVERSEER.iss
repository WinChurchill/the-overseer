; OVERSEER per-user setup.
;
; Built by scripts/Build-Installer.ps1 from an already-built release ZIP, so the
; installed files are exactly the files in that ZIP. Do not compile this directly;
; the build script passes these defines:
;   AppVersion   release version, e.g. 1.0.0-alpha.10
;   PayloadDir   folder holding the extracted ZIP
;   OutputDir    where the setup .exe is written
;   OutputName   setup file name without .exe
;
; Installs for the current user only (no admin prompt) into
; %LOCALAPPDATA%\Programs\OVERSEER. Upgrades replace the files in that same folder,
; so startup and Claude Code hook paths stay valid across versions. User data in
; %LOCALAPPDATA%\OVERSEER is never touched.

#ifndef AppVersion
  #error AppVersion is required; use scripts/Build-Installer.ps1
#endif
#ifndef PayloadDir
  #error PayloadDir is required; use scripts/Build-Installer.ps1
#endif
#ifndef OwnershipManifest
  #error OwnershipManifest is required; use scripts/Build-Installer.ps1
#endif

#define AppId "{4BD1C443-6251-4072-907B-DE45F918FB28}"
#define AppExe "TheOverseer.App.exe"

[Setup]
AppId={{#AppId}
AppName=OVERSEER
AppVersion={#AppVersion}
AppVerName=OVERSEER {#AppVersion}
AppPublisher=Winston Lin
AppPublisherURL=https://winchurchill.github.io/the-overseer/
AppSupportURL=https://github.com/WinChurchill/the-overseer/issues
AppUpdatesURL=https://github.com/WinChurchill/the-overseer/releases
VersionInfoVersion=1.0.0.0
VersionInfoProductTextVersion={#AppVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
DefaultDirName={autopf}\OVERSEER
DisableDirPage=auto
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableWelcomePage=no
SetupIconFile=..\src\TheOverseer.App\Assets\overseer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=OVERSEER
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
; Close a running OVERSEER from this folder before replacing its files. The app
; handles Windows' close request and exits cleanly.
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#OwnershipManifest}"; DestDir: "{app}"; DestName: "overseer-installed-files.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\OVERSEER"; Filename: "{app}\{#AppExe}"; Comment: "See what your AI coding agents are doing"
Name: "{autodesktop}\OVERSEER"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,OVERSEER}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'OVERSEER';

#include "InstallOwnership.iss"

function IsPreviousInstallFolder: Boolean;
var
  Previous: String;
begin
  Result := False;
  if not RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', Previous) then Exit;
  if CompareText(RemoveBackslash(Previous), RemoveBackslash(ExpandConstant('{app}'))) <> 0 then Exit;
  Result := FileExists(AddBackslash(Previous) + '{#AppExe}') and FileExists(AddBackslash(Previous) + 'release.json');
end;

// Remove the "start with Windows" entry only when it points at this folder, so
// a separate portable copy keeps its own startup entry.
procedure RemoveOwnStartupEntry;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, RunKey, RunValue, Command) then
    if IsOwnStartupCommand(Command, ExpandConstant('{app}\{#AppExe}')) then
      RegDeleteValue(HKCU, RunKey, RunValue);
end;

// Claude Code hooks installed from this folder would fail once the files are
// gone. Offer to remove them while the bridge's own uninstaller still exists.
procedure OfferClaudeHookRemoval;
var
  Settings, Uninstaller, Node: String;
  Text: AnsiString;
  Escaped: String;
  ResultCode: Integer;
begin
  if UninstallSilent then Exit;
  Settings := ExpandConstant('{%USERPROFILE}\.claude\settings.json');
  Uninstaller := ExpandConstant('{app}\integrations\claude-code\uninstall.mjs');
  if not FileExists(Settings) or not FileExists(Uninstaller) then Exit;
  if not LoadStringFromFile(Settings, Text) then Exit;
  Escaped := ExpandConstant('{app}\integrations\claude-code');
  StringChangeEx(Escaped, '\', '\\', True);
  if Pos(Lowercase(Escaped), Lowercase(String(Text))) = 0 then Exit;

  Node := FileSearch('node.exe', GetEnv('PATH'));
  if Node = '' then
  begin
    MsgBox('Claude Code still has OVERSEER hooks pointing at this folder. Node.js was not found, so they were left in place.' + #13#10#13#10 +
      'Remove the OVERSEER entries from ' + Settings + ' by hand, or reinstall OVERSEER and run its Claude Code uninstaller first.', mbInformation, MB_OK);
    Exit;
  end;
  if MsgBox('Claude Code has OVERSEER hooks pointing at this folder. Remove them now?' + #13#10#13#10 +
    'Only OVERSEER''s own hook entries are removed, and a backup of settings.json is kept.', mbConfirmation, MB_YESNO) = IDYES then
  begin
    if not Exec(Node, '"' + Uninstaller + '" --bridge="' + ExpandConstant('{app}\integrations\claude-code\bridge.mjs') + '" --apply', ExpandConstant('{app}\integrations\claude-code'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      MsgBox('Removing the hooks did not finish (exit code ' + IntToStr(ResultCode) + '). Check ' + Settings + ' for OVERSEER entries.', mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if IsPreviousInstallFolder then
      LoadOwnershipManifest(ExpandConstant('{app}\overseer-installed-files.txt'), PreviousOwnedFiles);
  end;
  if CurStep = ssPostInstall then RemoveObsoleteOwnedFiles;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    OfferClaudeHookRemoval;
    RemoveOwnStartupEntry;
  end;
end;

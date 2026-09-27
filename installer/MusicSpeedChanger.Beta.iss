; Music Speed Changer (Beta) — Inno Setup installer
; Installs side-by-side with the stable build: separate folder, AppId, and shortcuts.
; Builds Setup-MusicSpeedChanger-Beta-3.0.0-beta.2.exe from the published WinUI 3 build.
; Publish first (WinUI unpackaged self-contained ships the whole folder):
;   dotnet publish src/MusicSpeedChanger/MusicSpeedChanger.csproj -c Release -p:Platform=x64 -o dist/publish
; Attach the result to a GitHub *prerelease* (e.g. tag v3.0.0-beta.1) — the in-app
; updater only offers betas to Patreon-linked supporters.

#define MyAppName "Music Speed Changer Beta"
#define MyAppExeName "MusicSpeedChanger.exe"
#define MyAppVersion "3.0.0 Beta 2"
#define MyAppVersionFile "3.0.0-beta.2"
#define MyAppPublisher "EmAppleFlagship"

[Setup]
AppId={{7B4A8F2C-9E1D-4B6A-9F3C-MUSICSPEEDBETA}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MusicSpeedChangerBeta
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Setup-MusicSpeedChanger-Beta-{#MyAppVersionFile}
SetupIconFile=..\src\MusicSpeedChanger\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; WinUI 3 unpackaged app: exe + WindowsAppSDK runtime + .xbf/.pri/assets travel together.
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

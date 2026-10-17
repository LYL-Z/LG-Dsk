; User-scope x64 installer for the local LG Dsk candidate.
#ifndef AppIdValue
  #error AppIdValue is required.
#endif
#if AppIdValue != "GlassDesk.Independent"
  #error This installer must use the standard GlassDesk registration.
#endif
#ifndef ProductVersion
  #error ProductVersion is required.
#endif
#ifndef FileVersion
  #error FileVersion is required.
#endif
#ifndef PackageBaseName
  #error PackageBaseName is required.
#endif
#define StageDirectory GetEnv("GLASSDESK_PACKAGE_STAGE")
#if StageDirectory == ""
  #error GLASSDESK_PACKAGE_STAGE is required.
#endif

[Setup]
AppId={#AppIdValue}
AppName=LG Dsk
AppVersion={#ProductVersion}
AppVerName=LG Dsk {#ProductVersion}
DefaultDirName={localappdata}\Programs\LG Dsk
DefaultGroupName=LG Dsk
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; The branded install directory is deliberate. Existing GlassDesk installs are migrated below.
UsePreviousAppDir=no
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.22000
UninstallDisplayName=LG Dsk
UninstallDisplayIcon={app}\GlassDesk.exe
SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico
AppPublisher=LG Dsk
VersionInfoProductName=LG Dsk
VersionInfoProductVersion={#FileVersion}
VersionInfoVersion={#FileVersion}
VersionInfoTextVersion={#FileVersion}
VersionInfoProductTextVersion={#ProductVersion}
OutputBaseFilename={#PackageBaseName}-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "{#StageDirectory}\*"; DestDir: "{app}"; Excludes: "Data\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDirectory}\GlassDesk.ico"; DestDir: "{app}"; DestName: "GlassDesk-{#ProductVersion}.ico"; Flags: ignoreversion
Source: "Migrate-ExistingInstall.ps1"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\LG Dsk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\GlassDesk-{#ProductVersion}.ico"
Name: "{autodesktop}\LG Dsk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\GlassDesk-{#ProductVersion}.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1"; ValueName: "InstallLocation"; ValueType: string; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\GlassDesk.exe"; Parameters: "--autostart"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: IsGlassDeskUpdate

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ExitCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('Migrate-ExistingInstall.ps1');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  ScriptPath := ExpandConstant('{tmp}\Migrate-ExistingInstall.ps1');
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" -NewInstallDirectory "' + ExpandConstant('{app}') + '"';
  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := '无法启动 LG Dsk 安装迁移程序。旧安装尚未修改。';
  if (Result = '') and (ExitCode <> 0) then
    Result := '未能安全迁移旧版安装。为保护应用数据，安装已停止；可查看 %TEMP% 中的 LG-Dsk-install-migration.log。';
end;

function IsGlassDeskUpdate: Boolean;
begin
  Result := ExpandConstant('{param:GLASSDESKUPDATE|0}') = '1';
end;

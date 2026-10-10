#define AppVersion GetEnv("DUOCLICK_APP_VERSION")
#define PayloadDir GetEnv("DUOCLICK_PAYLOAD_DIR")
#define InstallerDir GetEnv("DUOCLICK_INSTALLER_DIR")
#define RepoRoot GetEnv("DUOCLICK_REPO_ROOT")
#define OutputDirectory GetEnv("DUOCLICK_OUTPUT_DIR")

[Setup]
AppId={{C2A64A4F-1E4E-4559-9A50-3A23B91F7A2B}
AppName=Duo Click
AppVersion={#AppVersion}
AppVerName=DuoClick {#AppVersion}
AppPublisher=DylanZhangzzz
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0
DefaultDirName={localappdata}\Programs\DuoClick
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
UsePreviousAppDir=yes
UsePreviousLanguage=yes
UsePreviousTasks=yes
AppMutex=Local\DuoClick.SingleInstance
CloseApplications=no
RestartApplications=no
SetupIconFile={#RepoRoot}\assets\duoclick.ico
UninstallDisplayIcon={app}\DuoClick.exe
UninstallDisplayName=Duo Click
OutputDir={#OutputDirectory}
OutputBaseFilename=DuoClick-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "{#InstallerDir}\languages\ChineseSimplified.isl"

[CustomMessages]
english.Windows10Required=Windows 10 or later is required to install DuoClick.
chinesesimplified.Windows10Required=DuoClick 需要 Windows 10 或更高版本。
english.DotNet48Missing=.NET Framework 4.8 or later is required. Install it from Microsoft, then run this installer again.
chinesesimplified.DotNet48Missing=需要 .NET Framework 4.8 或更高版本。请从 Microsoft 安装后重新运行此安装程序。
english.LaunchAtLogin=Launch DuoClick when I sign in
chinesesimplified.LaunchAtLogin=登录 Windows 时启动 DuoClick
english.AdditionalOptions=Additional options:
chinesesimplified.AdditionalOptions=其他选项：

[Tasks]
Name: "launchatlogin"; Description: "{cm:LaunchAtLogin}"; GroupDescription: "{cm:AdditionalOptions}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\DuoClick.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DuoClick.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.zh-CN.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\assets\logo.png"; DestDir: "{app}\assets"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\DuoClick"; Filename: "{app}\DuoClick.exe"; WorkingDir: "{app}"

[Registry]
; Always remove a previous opt-in value so unchecking the task also disables startup on upgrade.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DuoClick"; Flags: deletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DuoClick"; ValueData: """{app}\DuoClick.exe"""; Tasks: launchatlogin; Flags: uninsdeletevalue

[Code]
const
  DotNet48ReleaseKey = 528040;
  DotNetRegistryKey = 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full';

function IsDotNet48OrLaterInstalled: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKEY_LOCAL_MACHINE_32, DotNetRegistryKey, 'Release', Release) and
    (Release >= DotNet48ReleaseKey);
  if (not Result) and IsWin64 then
    Result := RegQueryDWordValue(HKEY_LOCAL_MACHINE_64, DotNetRegistryKey, 'Release', Release) and
      (Release >= DotNet48ReleaseKey);
end;

function InitializeSetup: Boolean;
begin
  if GetWindowsVersion < $0A000000 then
  begin
    MsgBox(CustomMessage('Windows10Required'), mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  if not IsDotNet48OrLaterInstalled then
  begin
    MsgBox(CustomMessage('DotNet48Missing'), mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

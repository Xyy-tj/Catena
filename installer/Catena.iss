#ifndef AppVersion
  #define AppVersion "0.6.2"
#endif

[Setup]
AppId={{B1ACB17D-B23D-4D7E-ACDB-F11E1AE7C630}
AppName=Catena
SetupIconFile=..\src\Catena.App\Assets\catena.ico
AppVersion={#AppVersion}
AppPublisher=Catena
DefaultDirName={autopf}\Catena
DefaultGroupName=Catena
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
LicenseFile=..\LICENSE
InfoBeforeFile=installation.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=Catena-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\Catena.App.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"
Source: "..\vendor\everything\Everything-Setup.exe"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\Catena"; Filename: "{app}\Catena.App.exe"

[Run]
Filename: "{app}\Catena.App.exe"; Description: "启动 Catena"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function RegisteredEverything(Root: Integer; Key: String): String;
var
  Directory: String;
begin
  Result := '';
  if RegQueryStringValue(Root, Key, 'InstallLocation', Directory) then
    if FileExists(AddBackslash(Directory) + 'Everything.exe') then
      Result := AddBackslash(Directory) + 'Everything.exe';
end;

function FindEverything(): String;
var
  I: Integer;
  Key: String;
begin
  Result := '';
  for I := 0 to 2 do begin
    case I of
      0: Key := 'Software\voidtools\Everything';
      1: Key := 'Software\voidtools\Everything 1.5a';
      2: Key := 'Software\voidtools\Everything-1.5a';
    end;
    Result := RegisteredEverything(HKLM64, Key);
    if Result <> '' then Exit;
    Result := RegisteredEverything(HKLM32, Key);
    if Result <> '' then Exit;
  end;
  if FileExists(ExpandConstant('{pf64}\Everything\Everything.exe')) then
    Result := ExpandConstant('{pf64}\Everything\Everything.exe')
  else if FileExists(ExpandConstant('{pf32}\Everything\Everything.exe')) then
    Result := ExpandConstant('{pf32}\Everything\Everything.exe');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  Installer: String;
begin
  Result := '';
  if FindEverything() <> '' then begin
    Log('Reusing the existing Everything installation.');
    Exit;
  end;
  WizardForm.PreparingLabel.Caption := '正在安装搜索组件 Everything，请稍候…';
  ExtractTemporaryFile('Everything-Setup.exe');
  Installer := ExpandConstant('{tmp}\Everything-Setup.exe');
  if GetSHA256OfFile(Installer) <> 'c42efad041d4c0bb4d4ac97ae7cbe89f153ec1fe078772392e749c7f5d5282d3' then begin
    Result := '搜索组件校验失败，请重新获取 Catena 安装包。';
    Exit;
  end;
  if not Exec(Installer,
    '/S -install-options "-app-data -disable-run-as-admin -install-service -install-start-menu-shortcuts -no-choose-volumes"',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    Result := '无法安装搜索组件。请允许安装程序使用管理员权限后重试。';
    Exit;
  end;
  if (ExitCode <> 0) or (FindEverything() = '') then
    Result := '搜索组件安装未完成。请重试安装。错误代码：' + IntToStr(ExitCode)
  else if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\Everything') then
    Result := '搜索后台服务安装未完成，请检查系统服务安装权限后重试。';
end;

// Everything is shared with other applications and has its own Windows uninstaller.
// Catena never uninstalls or stops that shared component.

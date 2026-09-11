; ClashTray x64 installer (Inno Setup 7.1+).
; The build script stages the App and Service into one shared self-contained
; directory. Inno Setup then compresses that directory and the optional Mihomo
; payload into a single LZMA2 solid installer.

#ifndef PackageVersion
  #define PackageVersion "0.1.0"
#endif
#ifndef Variant
  #define Variant "Full"
#endif
#ifndef PayloadRoot
  #define PayloadRoot ".stage\payload"
#endif
#ifndef OutputDirectory
  #define OutputDirectory "out"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif
#ifndef IncludeCore
  #define IncludeCore "1"
#endif

[Setup]
AppId={{F2E7EAF2-2F87-4A0D-9C2B-6A6B5E40B6A4}
AppName=ClashTray
AppVersion={#PackageVersion}
AppVerName=ClashTray {#PackageVersion}
SetupArchitecture=x64
AppPublisher=ClashTray Project
AppPublisherURL=https://github.com/arukas/ClashTray
AppSupportURL=https://github.com/arukas/ClashTray
DefaultDirName={autopf}\ClashTray
DefaultGroupName=ClashTray
DisableProgramGroupPage=yes
DisableWelcomePage=yes
UninstallDisplayName=ClashTray
UninstallDisplayIcon={app}\App\ClashTray.App.exe
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\ClashTray.Desktop.SingleInstance
CloseApplications=no
RestartApplications=no
SetupLogging=yes
OutputDir={#OutputDirectory}
OutputBaseFilename=ClashTray-Setup-{#Variant}
SetupIconFile={#RepoRoot}\src\ClashTray.App\Assets\App\ClashTray.ico
Compression=lzma2/max
SolidCompression=yes
VersionInfoVersion={#PackageVersion}
VersionInfoCompany=ClashTray Project
VersionInfoDescription=ClashTray Mihomo control center installer
VersionInfoProductName=ClashTray
VersionInfoProductVersion={#PackageVersion}
VersionInfoCopyright=Copyright (C) ClashTray Project
MinVersion=10.0.17763

[Files]
Source: "{#PayloadRoot}\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
#if IncludeCore == "1"
Source: "{#PayloadRoot}\Core\*"; DestDir: "{commonappdata}\ClashTray\core"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist
#endif

[InstallDelete]
; Remove the legacy C# installer layout when upgrading an existing install.
Type: filesandordirs; Name: "{app}\Service"
Type: files; Name: "{app}\ClashTray.Setup.exe"

[Icons]
Name: "{autoprograms}\ClashTray\ClashTray.lnk"; Filename: "{app}\App\ClashTray.App.exe"; WorkingDir: "{app}\App"; Comment: "ClashTray Mihomo control center"; IconFilename: "{app}\App\ClashTray.App.exe"

[Run]
Filename: "{app}\App\ClashTray.App.exe"; WorkingDir: "{app}\App"; Description: "启动 ClashTray"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  InstallerVariant = '{#Variant}';
  ServiceName = 'ClashTrayService';
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\ClashTrayService';
  ServiceExecutableName = 'ClashTray.Service.exe';
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupValueName = 'ClashTray';
  ErrorServiceDoesNotExist = 1060;
  ErrorServiceAlreadyRunning = 1056;
  ErrorServiceNotActive = 1062;
  ErrorServiceMarkedForDelete = 1072;
  ErrorServiceRequestTimeout = 1053;

var
  LastInstallerError: string;
  DeleteUserData: Boolean;

function RunSc(const Params: string; var ResultCode: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function ServiceExists(): Boolean;
begin
  Result := RegKeyExists(HKLM, ServiceKey);
end;

function IsOwnedServicePath(const ImagePath: string): Boolean;
var
  Candidate: string;
  ClosingQuote: Integer;
  ExecutableMarker: Integer;
begin
  Candidate := Trim(ImagePath);
  if (Length(Candidate) > 0) and (Candidate[1] = '"') then
  begin
    ClosingQuote := Pos('"', Copy(Candidate, 2, Length(Candidate)));
    if ClosingQuote > 0 then
    begin
      ClosingQuote := ClosingQuote + 1;
      Candidate := Copy(Candidate, 2, ClosingQuote - 2);
    end
    else
      StringChangeEx(Candidate, '"', '', True);
  end
  else
  begin
    ExecutableMarker := Pos(Lowercase(ServiceExecutableName), Lowercase(Candidate));
    if ExecutableMarker > 0 then
      Candidate := Copy(Candidate, 1, ExecutableMarker + Length(ServiceExecutableName) - 1);
  end;

  Result := (CompareText(ExtractFileName(Candidate), ServiceExecutableName) = 0)
    and (Pos('clashtray', Lowercase(Candidate)) > 0);
end;

function GetServiceImagePath(var ImagePath: string): Boolean;
begin
  Result := RegQueryStringValue(HKLM, ServiceKey, 'ImagePath', ImagePath);
end;

function FailWith(const Message: string): Boolean;
begin
  LastInstallerError := Message;
  Result := False;
end;

function StopAndRemoveOwnedService(): Boolean;
var
  ImagePath: string;
  ResultCode: Integer;
  Attempt: Integer;
begin
  Result := True;
  LastInstallerError := '';
  if not ServiceExists() then
    exit;

  if not GetServiceImagePath(ImagePath) then
  begin
    Result := FailWith('无法读取 ClashTrayService 的服务路径。为避免误删其他服务，安装已停止。');
    exit;
  end;

  if not IsOwnedServicePath(ImagePath) then
  begin
    Result := FailWith('系统中已有同名 ClashTrayService，但它不是 ClashTray 服务。请先检查该服务后再安装。');
    exit;
  end;

  if not RunSc('stop ' + ServiceName, ResultCode) then
  begin
    Result := FailWith('无法调用 Windows 服务控制器停止 ClashTrayService。');
    exit;
  end;
  if (ResultCode <> 0) and (ResultCode <> ErrorServiceNotActive) then
  begin
    Result := FailWith(Format('停止 ClashTrayService 失败（错误代码 %d）。', [ResultCode]));
    exit;
  end;

  if not RunSc('delete ' + ServiceName, ResultCode) then
  begin
    Result := FailWith('无法调用 Windows 服务控制器删除 ClashTrayService。');
    exit;
  end;
  if (ResultCode <> 0) and (ResultCode <> ErrorServiceDoesNotExist) and (ResultCode <> ErrorServiceMarkedForDelete) then
  begin
    Result := FailWith(Format('删除 ClashTrayService 失败（错误代码 %d）。', [ResultCode]));
    exit;
  end;

  for Attempt := 1 to 100 do
  begin
    if not ServiceExists() then
      exit;
    Sleep(100);
  end;

  Result := FailWith('ClashTrayService 仍在等待删除，请稍后重试。');
end;

function GetCurrentUserSid(): string;
var
  ResultCode: Integer;
  Output: TExecOutput;
  Line: string;
  StartIndex: Integer;
  EndIndex: Integer;
  I: Integer;
begin
  Result := '';
  if not ExecAndCaptureOutput(
    ExpandConstant('{sys}\whoami.exe'),
    '/user /fo csv /nh',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode,
    Output) then
    exit;
  if ResultCode <> 0 then
    exit;

  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    Line := Output.StdOut[I];
    StartIndex := Pos('S-1-', Line);
    if StartIndex = 0 then
      continue;

    EndIndex := StartIndex;
    while (EndIndex <= Length(Line)) and (Line[EndIndex] <> '"') and (Line[EndIndex] <> ',') and (Line[EndIndex] <> ' ') do
      Inc(EndIndex);
    Result := Copy(Line, StartIndex, EndIndex - StartIndex);
    exit;
  end;
end;

function GetX64DotNetHostPath(): string;
begin
  Result := '';
  if not IsWin64 then
    exit;

  Result := ExpandConstant('{autopf}\dotnet\dotnet.exe');
  if not FileExists(Result) then
    Result := '';
end;

function HasDotNetRuntime(const RuntimeName: string; var DetectedLine: string): Boolean;
var
  DotNetPath: string;
  ResultCode: Integer;
  Output: TExecOutput;
  Prefix: string;
  Line: string;
  I: Integer;
begin
  Result := False;
  DetectedLine := '';
  DotNetPath := GetX64DotNetHostPath();
  if DotNetPath = '' then
    exit;
  if not ExecAndCaptureOutput(
    DotNetPath,
    '--list-runtimes',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode,
    Output) then
    exit;
  if ResultCode <> 0 then
    exit;

  Prefix := Uppercase(RuntimeName + ' 10.');
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    Line := Trim(Output.StdOut[I]);
    if Pos(Prefix, Uppercase(Line)) = 1 then
    begin
      DetectedLine := Line;
      Result := True;
      exit;
    end;
  end;
end;

function HasWindowsAppRuntime(var DetectedVersion: string): Boolean;
var
  PowerShellPath: string;
  Params: string;
  UserSid: string;
  ResultCode: Integer;
  Output: TExecOutput;
  I: Integer;
begin
  Result := False;
  DetectedVersion := '';
  UserSid := GetCurrentUserSid();
  if UserSid = '' then
    exit;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellPath) then
    exit;

  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "'
    + '$ErrorActionPreference = ''Stop''; '
    + '$packages = @(Get-AppxPackage -User ''' + UserSid + ''' -Name ''Microsoft.WindowsAppRuntime.2'' -PackageTypeFilter Framework '
    + '| Where-Object { $_.Architecture.ToString() -eq ''X64'' -and $_.IsFramework '
    + '-and $_.Version -ge [version]''2.4.0.0'' '
    + '-and (Test-Path -LiteralPath $_.InstallLocation) } '
    + '| Sort-Object Version -Descending); '
    + 'if ($packages.Count -lt 1) { exit 1 }; '
    + 'Write-Output ($packages[0].Version.ToString())"';
  if not ExecAndCaptureOutput(
    PowerShellPath,
    Params,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode,
    Output) then
    exit;
  if ResultCode <> 0 then
    exit;

  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
    if Trim(Output.StdOut[I]) <> '' then
    begin
      DetectedVersion := Trim(Output.StdOut[I]);
      Result := True;
      exit;
    end;
end;

function CheckMiniPrerequisites(): Boolean;
var
  Missing: string;
  DotNetPath: string;
  CoreRuntimeLine: string;
  DesktopRuntimeLine: string;
  WindowsAppRuntimeVersion: string;
begin
  Missing := '';
  DotNetPath := GetX64DotNetHostPath();
  if DotNetPath = '' then
    Missing := Missing + #13#10 + '- x64 .NET host（C:\Program Files\dotnet\dotnet.exe）';
  if not HasDotNetRuntime('Microsoft.NETCore.App', CoreRuntimeLine) then
    Missing := Missing + #13#10 + '- Microsoft.NETCore.App 10.x（x64）';
  if not HasDotNetRuntime('Microsoft.WindowsDesktop.App', DesktopRuntimeLine) then
    Missing := Missing + #13#10 + '- Microsoft.WindowsDesktop.App 10.x（x64）';
  if not HasWindowsAppRuntime(WindowsAppRuntimeVersion) then
    Missing := Missing + #13#10 + '- 当前用户已注册且可访问的 Windows App Runtime 2.4+ framework（x64）';

  if Missing = '' then
  begin
    Log('Mini prerequisite check passed.');
    Result := True;
    exit;
  end;

  Log('Mini prerequisite check failed: ' + Missing);
  LastInstallerError := 'Mini 版本安装前检查未通过：' + Missing
    + #13#10#13#10 + '请先安装对应的 x64 运行时，并使用同一个 Windows 用户重新运行安装程序。'
    + #13#10 + '安装器会停止，不会创建或启动不兼容的服务。';
  Result := False;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if CompareText(InstallerVariant, 'Mini') = 0 then
  begin
    Result := CheckMiniPrerequisites();
    if not Result then
      MsgBox(LastInstallerError, mbError, MB_OK);
  end;
end;

function ServiceIsRunning(): Boolean;
var
  ResultCode: Integer;
  Output: TExecOutput;
  I: Integer;
begin
  Result := False;
  if not ExecAndCaptureOutput(
    ExpandConstant('{sys}\sc.exe'),
    'query ' + ServiceName,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode,
    Output) then
    exit;

  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
    if Pos('RUNNING', Uppercase(Output.StdOut[I])) > 0 then
    begin
      Result := True;
      exit;
    end;
end;

function InstallService(): Boolean;
var
  ServiceExecutablePath: string;
  UserSid: string;
  Params: string;
  ResultCode: Integer;
  Attempt: Integer;
begin
  Result := False;
  LastInstallerError := '';
  ServiceExecutablePath := ExpandConstant('{app}\App\ClashTray.Service.exe');
  if not FileExists(ServiceExecutablePath) then
  begin
    LastInstallerError := '安装包缺少 ClashTray.Service.exe。';
    exit;
  end;

  UserSid := GetCurrentUserSid();
  if UserSid = '' then
  begin
    LastInstallerError := '无法确定当前 Windows 用户 SID，不能安全配置服务管道权限。';
    exit;
  end;

  Params := 'create ' + ServiceName
    + ' binPath= "\"' + ServiceExecutablePath + '\" --user-sid=' + UserSid + '"'
    + ' start= auto type= own error= normal DisplayName= "ClashTray Service" obj= LocalSystem';
  if not RunSc(Params, ResultCode) then
  begin
    LastInstallerError := '无法调用 Windows 服务控制器创建 ClashTrayService。';
    exit;
  end;
  if ResultCode <> 0 then
  begin
    LastInstallerError := Format('创建 ClashTrayService 失败（错误代码 %d）。', [ResultCode]);
    exit;
  end;

  if not RunSc('description ' + ServiceName + ' "ClashTray Mihomo lifecycle and TUN service"', ResultCode) then
  begin
    LastInstallerError := '无法设置 ClashTrayService 描述。';
    exit;
  end;
  if ResultCode <> 0 then
  begin
    LastInstallerError := Format('设置 ClashTrayService 描述失败（错误代码 %d）。', [ResultCode]);
    exit;
  end;

  if not RunSc('start ' + ServiceName, ResultCode) then
  begin
    LastInstallerError := '无法启动 ClashTrayService。';
    exit;
  end;
  if (ResultCode <> 0) and (ResultCode <> ErrorServiceAlreadyRunning) then
  begin
    if ResultCode = ErrorServiceRequestTimeout then
      LastInstallerError := '启动 ClashTrayService 失败（错误代码 1053）。Mini 的框架依赖可能未能被服务进程加载；请安装 x64 .NET 10 运行时和 Windows App Runtime 2.4+，或改用 Full 版本。'
    else
      LastInstallerError := Format('启动 ClashTrayService 失败（错误代码 %d）。', [ResultCode]);
    exit;
  end;

  for Attempt := 1 to 100 do
  begin
    if ServiceIsRunning() then
    begin
      Result := True;
      exit;
    end;
    Sleep(100);
  end;

  LastInstallerError := 'ClashTrayService 未能在规定时间内进入运行状态。';
end;

function GetInstalledServiceExecutable(): string;
begin
  Result := ExpandConstant('{app}\App\ClashTray.Service.exe');
  if not FileExists(Result) then
    Result := ExpandConstant('{app}\Service\ClashTray.Service.exe');
end;

function RestoreOwnedProxyState(): Boolean;
var
  ServiceExecutablePath: string;
  ResultCode: Integer;
begin
  Result := False;
  ServiceExecutablePath := GetInstalledServiceExecutable();
  if not FileExists(ServiceExecutablePath) then
  begin
    LastInstallerError := '找不到 ClashTray 服务程序，无法在卸载前恢复 System Proxy。';
    exit;
  end;

  if not Exec(
    ServiceExecutablePath,
    '--restore-owned-proxy',
    ExtractFileDir(ServiceExecutablePath),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    LastInstallerError := '无法启动 ClashTray 的代理恢复流程。';
    exit;
  end;
  if ResultCode <> 0 then
  begin
    LastInstallerError := Format('恢复 ClashTray 拥有的 System Proxy 状态失败（错误代码 %d）。', [ResultCode]);
    exit;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;
  if CheckForMutexes('Local\ClashTray.Desktop.SingleInstance') then
  begin
    Result := '请先从托盘退出正在运行的 ClashTray，然后重新运行安装程序。';
    exit;
  end;

  if CompareText(InstallerVariant, 'Mini') = 0 then
  begin
    if not CheckMiniPrerequisites() then
    begin
      Result := LastInstallerError;
      exit;
    end;
  end;

  if not StopAndRemoveOwnedService() then
    Result := LastInstallerError;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if CompareText(InstallerVariant, 'Mini') = 0 then
      if not CheckMiniPrerequisites() then
      begin
        MsgBox(LastInstallerError, mbError, MB_OK);
        Abort;
      end;

    if not InstallService() then
    begin
      MsgBox(LastInstallerError, mbError, MB_OK);
      Abort;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
var
  Choice: Integer;
begin
  Result := False;
  if CheckForMutexes('Local\ClashTray.Desktop.SingleInstance') then
  begin
    MsgBox('请先从托盘退出正在运行的 ClashTray，然后重新运行卸载程序。', mbError, MB_OK);
    exit;
  end;

  Choice := MsgBox(
    '是否保留 ClashTray 的配置、订阅和日志？' + #13#10 + #13#10
      + '选择“是”保留数据，选择“否”删除数据，选择“取消”停止卸载。',
    mbConfirmation,
    MB_YESNOCANCEL);
  if Choice = IDCANCEL then
    exit;

  DeleteUserData := Choice = IDNO;
  Result := True;
end;

function PrepareToUninstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;
  if not StopAndRemoveOwnedService() then
  begin
    Result := LastInstallerError;
    exit;
  end;

  if not RestoreOwnedProxyState() then
    Result := LastInstallerError;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, StartupKey, StartupValueName);
    if DeleteUserData then
    begin
      DelTree(ExpandConstant('{localappdata}\ClashTray'), True, True, True);
      DelTree(ExpandConstant('{commonappdata}\ClashTray'), True, True, True);
    end;
  end;
end;

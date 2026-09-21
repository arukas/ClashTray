; ClashTray x64 installer (Inno Setup 7.1+).
; The build script stages the App and Service into one shared self-contained
; directory. Inno Setup compresses that directory and the Mihomo payload into
; one installer.

; The product version is owned by Directory.Build.props and must be passed in
; by Build-EXE.ps1 (resolved through Get-ProductVersion.ps1). There is no
; second hardcoded copy here, so installer output cannot drift from the tree.
#ifndef PackageVersion
  #error "PackageVersion is required; build through packaging\Build-EXE.ps1 so it is resolved from Directory.Build.props."
#endif
#ifndef PackageFileVersion
  #error "PackageFileVersion is required; build through packaging\Build-EXE.ps1."
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
OutputBaseFilename=ClashTray-{#PackageVersion}-win-x64-{#Variant}
SetupIconFile={#RepoRoot}\src\ClashTray.App\Assets\App\ClashTray.ico
Compression=lzma2/max
SolidCompression=yes
VersionInfoVersion={#PackageFileVersion}
VersionInfoCompany=ClashTray Project
VersionInfoDescription=ClashTray Mihomo control center installer
VersionInfoProductName=ClashTray
VersionInfoProductVersion={#PackageFileVersion}
VersionInfoCopyright=Copyright (C) ClashTray Project
MinVersion=10.0.17763

[Files]
Source: "{#PayloadRoot}\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\Dashboard\*"; DestDir: "{commonappdata}\ClashTray\ui"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\Core\*"; DestDir: "{commonappdata}\ClashTray\core"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\ClashTray\core"; Permissions: users-readexec admins-full system-full
Name: "{commonappdata}\ClashTray\ui"; Permissions: users-readexec admins-full system-full

[InstallDelete]
; Remove the legacy C# installer layout when upgrading an existing install.
Type: filesandordirs; Name: "{app}\Service"
Type: files; Name: "{app}\ClashTray.Setup.exe"
Type: filesandordirs; Name: "{commonappdata}\ClashTray\ui"

[Icons]
Name: "{autoprograms}\ClashTray\ClashTray.lnk"; Filename: "{app}\App\ClashTray.App.exe"; WorkingDir: "{app}\App"; Comment: "ClashTray Mihomo control center"; IconFilename: "{app}\App\ClashTray.App.exe"

[Run]
Filename: "{app}\App\ClashTray.App.exe"; WorkingDir: "{app}\App"; Description: "启动 ClashTray"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
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

function GetActiveSessionUserSid(): string;
var
  PowerShellPath: string;
  Params: string;
  ResultCode: Integer;
  Output: TExecOutput;
  I: Integer;
  Line: string;
begin
  Result := '';
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellPath) then
  begin
    Log('Setup: PowerShell not found; cannot resolve the active-session user SID.');
    exit;
  end;

  // The spawned PowerShell shares the installer's session. Resolving the user
  // through the session's explorer.exe owner yields the interactive account
  // that will run ClashTray, even when the installer itself was elevated with
  // over-the-shoulder administrator credentials (where whoami would report the
  // administrator instead).
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "'
    + '$ErrorActionPreference = ''Stop''; '
    + '$sessionId = (Get-Process -Id $PID).SessionId; '
    + '$explorer = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq ''explorer.exe'' -and $_.SessionId -eq $sessionId } | Select-Object -First 1; '
    + 'if ($null -eq $explorer) { exit 2 }; '
    + '$owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner; '
    + 'if ($owner.ReturnValue -ne 0) { exit 3 }; '
    + '$account = New-Object System.Security.Principal.NTAccount($owner.Domain, $owner.User); '
    + 'Write-Output $account.Translate([System.Security.Principal.SecurityIdentifier]).Value"';
  if not ExecAndCaptureOutput(
    PowerShellPath,
    Params,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode,
    Output) then
  begin
    Log('Setup: failed to run the active-session SID probe.');
    exit;
  end;
  if ResultCode <> 0 then
  begin
    Log(Format('Setup: active-session SID probe exited with code %d.', [ResultCode]));
    exit;
  end;

  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    Line := Trim(Output.StdOut[I]);
    if Pos('S-1-', Line) = 1 then
    begin
      Result := Line;
      Log('Setup: active-session user SID is ' + Result + '.');
      exit;
    end;
  end;

  Log('Setup: active-session SID probe returned no SID line.');
end;

function GetCurrentUserSid(): string; forward;

function ResolveInstallUserSid(): string;
begin
  // Prefer the interactive session user so over-the-shoulder elevation keeps
  // per-user decisions (Windows App Runtime registration, service --user-sid)
  // anchored to the account that will actually run ClashTray.
  Result := GetActiveSessionUserSid();
  if Result = '' then
  begin
    Log('Setup: falling back to the installer identity (whoami) for the user SID.');
    Result := GetCurrentUserSid();
  end;
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
  begin
    Log('Setup: failed to run whoami for the installer identity SID.');
    exit;
  end;
  if ResultCode <> 0 then
  begin
    Log(Format('Setup: whoami exited with code %d.', [ResultCode]));
    exit;
  end;

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
    Log('Setup: installer identity (whoami) user SID is ' + Result + '.');
    exit;
  end;

  Log('Setup: whoami output contained no SID.');
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

  UserSid := ResolveInstallUserSid();
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
      LastInstallerError := '启动 ClashTrayService 失败（错误代码 1053）。服务未及时响应启动请求；请查看 Windows 事件日志，并把 %ProgramData%\ClashTray\logs 下最新的服务日志发给项目仓库以便排查。'
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

  if not StopAndRemoveOwnedService() then
    Result := LastInstallerError;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
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

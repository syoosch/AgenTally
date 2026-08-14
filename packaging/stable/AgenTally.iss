#ifndef MyAppVersion
  #error MyAppVersion must be defined by the Stable publisher.
#endif
#ifndef MyAppCommit
  #error MyAppCommit must be defined by the Stable publisher.
#endif
#ifndef SourceDir
  #error SourceDir must be defined by the Stable publisher.
#endif
#ifndef OutputDir
  #error OutputDir must be defined by the Stable publisher.
#endif

[Setup]
AppId={{A59B3C1C-D735-4D8E-9357-4DF501455822}
AppName=AgenTally
AppVersion={#MyAppVersion}
AppVerName=AgenTally {#MyAppVersion}
AppPublisher=AgenTally
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription=AgenTally installer ({#MyAppCommit})
DefaultDirName={code:GetDefaultInstallDir}
DefaultGroupName=AgenTally
DisableDirPage=auto
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
UsePreviousTasks=yes
DisableProgramGroupPage=yes
AllowNoIcons=no
AllowNetworkDrive=no
AllowUNCPath=no
AllowRootDirectory=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsedUserAreasWarning=no
OutputDir={#OutputDir}
OutputBaseFilename=AgenTally-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\..\assets\icon\AgenTally.ico
UninstallDisplayIcon={app}\AgenTally.UI.exe
UninstallDisplayName=AgenTally {#MyAppVersion}
Uninstallable=yes
UninstallLogMode=append
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
ChangesAssociations=no
ChangesEnvironment=no
DiskSpanning=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "StableMaintenance.ps1"; Flags: dontcopy noencryption
Source: "Invoke-AgenTallyStableMaintenance.ps1"; Flags: dontcopy noencryption
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\AgenTally\Stable"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{group}\AgenTally"; Filename: "{app}\AgenTally.UI.exe"; WorkingDir: "{app}"; IconFilename: "{app}\AgenTally.UI.exe"
Name: "{userdesktop}\AgenTally"; Filename: "{app}\AgenTally.UI.exe"; WorkingDir: "{app}"; IconFilename: "{app}\AgenTally.UI.exe"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{userdesktop}\AgenTally.lnk"; Check: ShouldDeleteDesktopShortcut

[Run]
Filename: "{app}\AgenTally.UI.exe"; Parameters: "{code:GetPostInstallParameters}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,AgenTally}"; Flags: postinstall nowait skipifsilent

[Code]
const
  StableInstallRecordSubkey = 'Software\AgenTally\Stable';
  StableInstallLocationValue = 'InstallLocation';

var
  InstallInspectionCompleted: Boolean;
  InstallMaintenanceCompleted: Boolean;
  InstallRunState: String;
  DeleteOwnedDesktopShortcut: Boolean;
  UninstallInspectionCompleted: Boolean;
  UninstallMaintenanceCompleted: Boolean;
  UninstallRunState: String;
  UninstallDesktopShortcutState: String;
  UninstallStartMenuShortcutState: String;
  PreservedDesktopShortcut: Boolean;
  PreservedStartMenuShortcut: Boolean;

function GetDefaultInstallDir(Param: String): String;
begin
  if not RegQueryStringValue(
      HKCU, StableInstallRecordSubkey, StableInstallLocationValue, Result) then
    Result := ExpandConstant('{localappdata}\Programs\AgenTally');
end;

function HasLegacyDefaultInstallation: Boolean;
var
  InstallRoot: String;
begin
  InstallRoot := ExpandConstant('{localappdata}\Programs\AgenTally');
  Result :=
    FileExists(AddBackslash(InstallRoot) + 'AgenTally.UI.exe') and
    FileExists(AddBackslash(InstallRoot) + 'AgenTally.Core.exe') and
    FileExists(AddBackslash(InstallRoot) + 'unins000.exe') and
    FileExists(AddBackslash(InstallRoot) + 'unins000.dat') and
    FileExists(AddBackslash(InstallRoot) + 'StableMaintenance.ps1') and
    FileExists(AddBackslash(InstallRoot) + 'Invoke-AgenTallyStableMaintenance.ps1');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
var
  RecordedInstallRoot: String;
begin
  Result := False;
  if PageID <> wpSelectDir then
    exit;

  Result := RegQueryStringValue(
    HKCU, StableInstallRecordSubkey, StableInstallLocationValue,
    RecordedInstallRoot) or HasLegacyDefaultInstallation;
end;

function RunMaintenance(const ScriptDirectory, InstallRoot, Mode: String;
  const StatePath, ResultPath: String; const DesktopShortcutRequested,
  RemoveData: Boolean): Boolean;
var
  NormalizedInstallRoot: String;
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  ResultCode := -1;
  NormalizedInstallRoot := RemoveBackslashUnlessRoot(InstallRoot);
  PowerShellPath := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
  ScriptPath := AddBackslash(ScriptDirectory) +
    'Invoke-AgenTallyStableMaintenance.ps1';
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
    '-File "' + ScriptPath + '" -Mode ' + Mode +
    ' -InstallRoot ' + AddQuotes(NormalizedInstallRoot);
  if StatePath <> '' then
    Parameters := Parameters + ' -StatePath ' + AddQuotes(StatePath);
  if ResultPath <> '' then
    Parameters := Parameters + ' -ResultPath ' + AddQuotes(ResultPath);
  if DesktopShortcutRequested then
    Parameters := Parameters + ' -DesktopShortcutRequested';
  if RemoveData then
    Parameters := Parameters + ' -RemoveData';

  Result := Exec(PowerShellPath, Parameters, ScriptDirectory, SW_HIDE,
    ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    Log(Format('AgenTally Stable maintenance failed. Mode=%s ExitCode=%d', [Mode, ResultCode]));
end;

function ReadMaintenanceState(const StatePath, Name: String): String;
var
  Lines: TArrayOfString;
  Prefix: String;
  Index: Integer;
begin
  Result := '';
  Prefix := Name + '=';
  if not LoadStringsFromFile(StatePath, Lines) then
    exit;

  for Index := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos(Prefix, Lines[Index]) = 1 then
    begin
      Result := Copy(Lines[Index], Length(Prefix) + 1, Length(Lines[Index]));
      exit;
    end;
  end;
end;

function ReadMaintenanceError(const ResultPath: String): String;
var
  Lines: TArrayOfString;
  Index: Integer;
begin
  Result := '';
  if LoadStringsFromFile(ResultPath, Lines) then
  begin
    for Index := 0 to GetArrayLength(Lines) - 1 do
    begin
      if Result = '' then
        Result := Lines[Index]
      else
        Result := Result + #13#10 + Lines[Index];
    end;
  end;
  Result := Trim(Result);
end;

function IsRunStateValid(const Value: String): Boolean;
begin
  Result := (Value = 'none') or (Value = 'ui') or (Value = 'background');
end;

function IsShortcutStateValid(const Value: String): Boolean;
begin
  Result := (Value = 'missing') or (Value = 'owned') or
    (Value = 'foreign') or (Value = 'reparse');
end;

function MaintenanceFailureMessage(const Prefix, ResultPath: String): String;
var
  Detail: String;
begin
  Detail := ReadMaintenanceError(ResultPath);
  if Detail = '' then
    Result := Prefix
  else
    Result := Prefix + #13#10 + #13#10 + Detail;
end;

function ShouldDeleteDesktopShortcut: Boolean;
begin
  Result := DeleteOwnedDesktopShortcut;
end;

function GetPostInstallParameters(Param: String): String;
begin
  if InstallRunState = 'background' then
    Result := '--background'
  else
    Result := '';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  StatePath: String;
  ResultPath: String;
  InstallMode: String;
  DesktopShortcutState: String;
  StartMenuShortcutState: String;
begin
  Result := '';
  if InstallMaintenanceCompleted then
    exit;

  try
    ExtractTemporaryFile('StableMaintenance.ps1');
    ExtractTemporaryFile('Invoke-AgenTallyStableMaintenance.ps1');

    StatePath := ExpandConstant('{tmp}\AgenTally-install-state.txt');
    ResultPath := ExpandConstant('{tmp}\AgenTally-install-error.txt');
    if not InstallInspectionCompleted then
    begin
      DeleteFile(StatePath);
      DeleteFile(ResultPath);
      if not RunMaintenance(
          ExpandConstant('{tmp}'), ExpandConstant('{app}'), 'InspectInstall',
          StatePath, ResultPath, WizardIsTaskSelected('desktopicon'), False) then
      begin
        Result := MaintenanceFailureMessage(
          '无法验证 AgenTally 的安装目录、已有安装身份或快捷方式。',
          ResultPath);
        exit;
      end;

      InstallMode := ReadMaintenanceState(StatePath, 'installMode');
      InstallRunState := ReadMaintenanceState(StatePath, 'runState');
      DesktopShortcutState := ReadMaintenanceState(
        StatePath, 'desktopShortcut');
      StartMenuShortcutState := ReadMaintenanceState(
        StatePath, 'startMenuShortcut');
      if ((InstallMode <> 'first') and (InstallMode <> 'upgrade')) or
          (not IsRunStateValid(InstallRunState)) or
          (not IsShortcutStateValid(DesktopShortcutState)) or
          (not IsShortcutStateValid(StartMenuShortcutState)) then
      begin
        Result := 'AgenTally 安装维护预检返回了不完整或无效的状态，安装已中止。';
        exit;
      end;
      DeleteOwnedDesktopShortcut :=
        (not WizardIsTaskSelected('desktopicon')) and
        (DesktopShortcutState = 'owned');

      if InstallRunState <> 'none' then
      begin
        if WizardSilent then
        begin
          Result := 'AgenTally 正在运行；静默安装不会自动关闭正在运行的应用。';
          exit;
        end;
        if MsgBox(
            'AgenTally 当前正在运行。继续安装会先安全退出应用，最多等待 20 秒；不会强制结束进程。是否继续？',
            mbConfirmation, MB_OKCANCEL or MB_DEFBUTTON1) <> IDOK then
        begin
          Result := '安装已取消，AgenTally 仍保持原状态。';
          exit;
        end;
      end;

      InstallInspectionCompleted := True;
    end;

    DeleteFile(ResultPath);
    if RunMaintenance(ExpandConstant('{tmp}'), ExpandConstant('{app}'),
        'PrepareInstall', '', ResultPath,
        WizardIsTaskSelected('desktopicon'), False) then
      InstallMaintenanceCompleted := True
    else
    begin
      InstallInspectionCompleted := False;
      Result := MaintenanceFailureMessage(
        '无法安全关闭已有的 AgenTally。安装尚未开始复制程序文件，请先完全退出后重试。',
        ResultPath);
    end;
  except
    Result := '无法准备 AgenTally 安装或升级：' + GetExceptionMessage;
  end;
end;

procedure PreserveForeignShortcut(const ShortcutPath, BackupPath: String;
  const ShortcutState: String; var Preserved: Boolean);
begin
  Preserved := False;
  if (ShortcutState <> 'foreign') or (not FileExists(ShortcutPath)) then
    exit;

  DeleteFile(BackupPath);
  if not CopyFile(ShortcutPath, BackupPath, False) then
    RaiseException('无法临时保护非 AgenTally 所有的同名快捷方式：' + ShortcutPath);
  Preserved := True;
end;

procedure RestoreForeignShortcut(const ShortcutPath, BackupPath: String;
  const Preserved: Boolean);
begin
  if not Preserved then
    exit;

  ForceDirectories(ExtractFileDir(ShortcutPath));
  if not CopyFile(BackupPath, ShortcutPath, False) then
    MsgBox(
      '卸载已完成，但无法恢复卸载前发现的非 AgenTally 同名快捷方式：' +
      ShortcutPath + #13#10 + #13#10 + '临时备份：' + BackupPath,
      mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RemoveData: Boolean;
  Choice: Integer;
  StatePath: String;
  ResultPath: String;
  DesktopShortcutPath: String;
  StartMenuShortcutPath: String;
  DesktopBackupPath: String;
  StartMenuBackupPath: String;
begin
  DesktopShortcutPath := ExpandConstant('{userdesktop}\AgenTally.lnk');
  StartMenuShortcutPath := ExpandConstant('{group}\AgenTally.lnk');
  DesktopBackupPath := ExpandConstant('{tmp}\AgenTally-foreign-desktop.lnk');
  StartMenuBackupPath := ExpandConstant('{tmp}\AgenTally-foreign-startmenu.lnk');

  if CurUninstallStep = usPostUninstall then
  begin
    RestoreForeignShortcut(
      DesktopShortcutPath, DesktopBackupPath, PreservedDesktopShortcut);
    RestoreForeignShortcut(
      StartMenuShortcutPath, StartMenuBackupPath, PreservedStartMenuShortcut);
    if RegValueExists(
        HKCU, StableInstallRecordSubkey, StableInstallLocationValue) then
    begin
      if not RegDeleteValue(
          HKCU, StableInstallRecordSubkey, StableInstallLocationValue) then
        MsgBox('AgenTally 已卸载，但无法清除其安装位置记录。', mbError, MB_OK);
    end;
    RegDeleteKeyIfEmpty(HKCU, StableInstallRecordSubkey);
    RegDeleteKeyIfEmpty(HKCU, 'Software\AgenTally');
    exit;
  end;

  if (CurUninstallStep <> usUninstall) or UninstallMaintenanceCompleted then
    exit;

  StatePath := ExpandConstant('{tmp}\AgenTally-uninstall-state.txt');
  ResultPath := ExpandConstant('{tmp}\AgenTally-uninstall-error.txt');
  if not UninstallInspectionCompleted then
  begin
    DeleteFile(StatePath);
    DeleteFile(ResultPath);
    if not RunMaintenance(
        ExpandConstant('{app}'), ExpandConstant('{app}'), 'InspectUninstall',
        StatePath, ResultPath, False, False) then
    begin
      MsgBox(
        MaintenanceFailureMessage(
          '无法验证当前 AgenTally 安装，卸载已中止。', ResultPath),
        mbError, MB_OK);
      Abort;
    end;

    UninstallRunState := ReadMaintenanceState(StatePath, 'runState');
    UninstallDesktopShortcutState := ReadMaintenanceState(
      StatePath, 'desktopShortcut');
    UninstallStartMenuShortcutState := ReadMaintenanceState(
      StatePath, 'startMenuShortcut');
    if (not IsRunStateValid(UninstallRunState)) or
        (not IsShortcutStateValid(UninstallDesktopShortcutState)) or
        (not IsShortcutStateValid(UninstallStartMenuShortcutState)) then
    begin
      MsgBox(
        'AgenTally 卸载维护预检返回了不完整或无效的状态，卸载已中止。',
        mbError, MB_OK);
      Abort;
    end;
    if UninstallRunState <> 'none' then
    begin
      if UninstallSilent then
        Abort;
      if MsgBox(
          'AgenTally 当前正在运行。继续卸载会先安全退出应用，最多等待 20 秒；不会强制结束进程。是否继续？',
          mbConfirmation, MB_OKCANCEL or MB_DEFBUTTON1) <> IDOK then
        Abort;
    end;

    UninstallInspectionCompleted := True;
  end;

  RemoveData := False;
  if not UninstallSilent then
  begin
    Choice := MsgBox(
      '是否同时删除 AgenTally 的全部本地应用数据？' + #13#10 + #13#10 +
      '“是”：删除统计数据库、设置、日志和运行状态。' + #13#10 +
      '“否”：保留统计数据库和设置，之后重新安装可继续使用。' + #13#10 +
      '卸载只会处理上面列出的 AgenTally 本地应用数据。',
      mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON2);
    if Choice = IDCANCEL then
      Abort;
    RemoveData := Choice = IDYES;
  end;

  DeleteFile(ResultPath);
  if not RunMaintenance(
      ExpandConstant('{app}'), ExpandConstant('{app}'), 'PrepareUninstall',
      '', ResultPath, False, RemoveData) then
  begin
    MsgBox(
      MaintenanceFailureMessage(
        '无法安全关闭或清理 AgenTally。卸载已中止，没有强制结束任何进程，也不会把残留误报为成功。',
        ResultPath),
      mbError, MB_OK);
    Abort;
  end;

  PreserveForeignShortcut(
    DesktopShortcutPath, DesktopBackupPath,
    UninstallDesktopShortcutState, PreservedDesktopShortcut);
  PreserveForeignShortcut(
    StartMenuShortcutPath, StartMenuBackupPath,
    UninstallStartMenuShortcutState, PreservedStartMenuShortcut);

  UninstallMaintenanceCompleted := True;
end;

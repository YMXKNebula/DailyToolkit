; The setup is deliberately 32-bit; the installed application is Windows x64.
#ifndef AppVersion
  #error AppVersion must be supplied by package-installer.ps1.
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by package-installer.ps1.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef InstallerId
  #define InstallerId "DailyToolkit.Desktop"
#endif
#ifndef ProductName
  #define ProductName "DailyToolkit"
#endif
#ifndef StartupName
  #define StartupName "DailyToolkit"
#endif

[Setup]
AppId={#InstallerId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=YMXKNebula
AppPublisherURL=https://github.com/YMXKNebula/DailyToolkit
AppSupportURL=https://github.com/YMXKNebula/DailyToolkit/issues
AppUpdatesURL=https://github.com/YMXKNebula/DailyToolkit/releases
DefaultDirName={localappdata}\Programs\{#ProductName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=DailyToolkit-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\DailyToolkit.Desktop\Assets\dt.ico
UninstallDisplayIcon={app}\DailyToolkit.exe
UninstallDisplayName={#ProductName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoDescription=DailyToolkit 安装程序
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallLogging=yes
SetupMutex={#InstallerId}.Setup
LicenseFile=..\LICENSE

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
Name: "migratestartup"; Description: "{cm:MigrateStartup}"; Check: HasOtherStartup

[Files]
Source: "{#PayloadDir}\DailyToolkit.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\LICENSE-DailyToolkit.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\microsoft.*.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\microsoft.windowsdesktop.app.runtime-LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\licenses\Windows-SDK-License.rtf"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\docs\INSTALLER_README.txt"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\DailyToolkit.exe"; Parameters: "--show"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\DailyToolkit.exe"; Parameters: "--show"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DailyToolkit.exe"; Parameters: "--show"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#ProductName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: StartupMigrationSucceeded

[CustomMessages]
chinesesimplified.DesktopIcon=创建桌面快捷方式
english.DesktopIcon=Create a desktop shortcut
chinesesimplified.MigrateStartup=将已有的开机自启改为安装版（管理员自启需要授权）
english.MigrateStartup=Move existing startup to this installation (administrator startup requires permission)
chinesesimplified.CloseFailed=DailyToolkit 尚未退出，可能有笔记未能保存。请先保存笔记并从托盘退出，再重试。
english.CloseFailed=DailyToolkit is still running. Save your note and exit from the tray, then retry.
chinesesimplified.StartupFailed=软件已经安装，但自启迁移未完成。请在安装版设置中关闭并重新启用开机自启；管理员自启需要 Windows 授权。
english.StartupFailed=Installation completed, but startup migration did not finish. Disable and re-enable startup in this installation; administrator startup requires Windows permission.
chinesesimplified.CleanupFailed=未能清理本安装目录的自启入口，卸载已停止。请在软件设置中关闭开机自启后重试。
english.CleanupFailed=Startup for this installation could not be removed. Uninstall has stopped. Disable startup in the application settings and retry.
chinesesimplified.Downgrade=已安装的版本较新。请使用同版或更新版本的安装包。
english.Downgrade=A newer version is installed. Use an installer for the same or a newer version.
chinesesimplified.DataDirectory=不能安装到笔记和个人设置目录。请选择单独的程序目录。
english.DataDirectory=Do not install into the notes and settings directory. Choose a separate application directory.

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#InstallerId}_is1';
var
  OwnerSid: String;
  MigrationSucceeded: Boolean;
  MigrationOnly: Boolean;
  MigrationExitCode: Integer;

function GetCurrentProcess: LongWord;
  external 'GetCurrentProcess@kernel32.dll stdcall';
function OpenProcessToken(Process: LongWord; Access: LongWord; var Token: LongWord): Boolean;
  external 'OpenProcessToken@advapi32.dll stdcall';
function GetTokenInformation(Token: LongWord; InfoClass: Integer; Buffer: LongWord; Size: LongWord; var Required: LongWord): Boolean;
  external 'GetTokenInformation@advapi32.dll stdcall';
function GlobalAlloc(Flags: LongWord; Size: LongWord): LongWord;
  external 'GlobalAlloc@kernel32.dll stdcall';
procedure CopyPointer(var Dest: LongWord; Source: LongWord; Size: LongWord);
  external 'RtlMoveMemory@kernel32.dll stdcall';
function ConvertSidToStringSid(Sid: LongWord; var Text: LongWord): Boolean;
  external 'ConvertSidToStringSidW@advapi32.dll stdcall';
function LookupAccountName(SystemName, Account: String; Sid: LongWord; var SidSize: LongWord; Domain: String; var DomainSize, SidType: LongWord): Boolean;
  external 'LookupAccountNameW@advapi32.dll stdcall';
function CopyString(Buffer: String; Source: LongWord; Count: Integer): LongWord;
  external 'lstrcpynW@kernel32.dll stdcall';
function LocalFree(Memory: LongWord): LongWord;
  external 'LocalFree@kernel32.dll stdcall';
function GlobalFree(Memory: LongWord): LongWord;
  external 'GlobalFree@kernel32.dll stdcall';
function CloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetTopWindow(Parent: LongWord): LongWord;
  external 'GetTopWindow@user32.dll stdcall';
function GetWindow(Window: LongWord; Command: LongWord): LongWord;
  external 'GetWindow@user32.dll stdcall';
function GetProp(Window: LongWord; Name: String): LongWord;
  external 'GetPropW@user32.dll stdcall';
function RegisterWindowMessage(Name: String): LongWord;
  external 'RegisterWindowMessageW@user32.dll stdcall';
function PostMessage(Window: LongWord; Message: LongWord; WParam, LParam: LongWord): Boolean;
  external 'PostMessageW@user32.dll stdcall';
function GetWindowThreadProcessId(Window: LongWord; var ProcessId: LongWord): LongWord;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function OpenProcess(Access: LongWord; Inherit: Boolean; ProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: LongWord; Flags: LongWord; Buffer: String; var Size: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function CreateFile(Name: String; Access, Share, Security, Disposition, Flags, Template: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function WriteFile(Handle: LongWord; Buffer: String; Size: LongWord; var Written: LongWord; Overlapped: LongWord): Boolean;
  external 'WriteFile@kernel32.dll stdcall';
procedure ExitProcess(Code: LongWord);
  external 'ExitProcess@kernel32.dll stdcall';

function SidText(Sid: LongWord): String;
var Text: LongWord; Value: String;
begin
  if not ConvertSidToStringSid(Sid, Text) then RaiseException('Cannot read SID.');
  try
    Value := StringOfChar(#0, 184); CopyString(Value, Text, 184);
    Result := Copy(Value, 1, Pos(#0, Value) - 1);
  finally LocalFree(Text); end;
end;

function AccountSid(Account: String): String;
var Sid, SidSize, DomainSize, SidType: LongWord; Domain: String;
begin
  Result := ''; SidSize := 0; DomainSize := 0; Domain := '';
  LookupAccountName('', Account, 0, SidSize, Domain, DomainSize, SidType);
  if SidSize = 0 then Exit;
  Sid := GlobalAlloc(0, SidSize);
  if Sid = 0 then Exit;
  try
    Domain := StringOfChar(#0, DomainSize);
    if LookupAccountName('', Account, Sid, SidSize, Domain, DomainSize, SidType) then Result := SidText(Sid);
  finally GlobalFree(Sid); end;
end;

function CurrentSid: String;
var Token, Required, Buffer, Sid: LongWord;
begin
  Result := '';
  if not OpenProcessToken(GetCurrentProcess, $0008, Token) then RaiseException('Cannot read installer identity.');
  try
    Required := 0;
    GetTokenInformation(Token, 1, 0, 0, Required);
    Buffer := GlobalAlloc(0, Required);
    if Buffer = 0 then RaiseException('Cannot allocate identity buffer.');
    try
      if not GetTokenInformation(Token, 1, Buffer, Required, Required) then RaiseException('Cannot read installer identity.');
      CopyPointer(Sid, Buffer, 4);
      Result := SidText(Sid);
    finally GlobalFree(Buffer); end;
  finally CloseHandle(Token); end;
  if Result = '' then RaiseException('Installer identity is empty.');
end;

function SamePath(Left, Right: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(Left)), RemoveBackslashUnlessRoot(ExpandFileName(Right))) = 0;
end;

function InstalledExecutable: String;
begin Result := ExpandConstant('{app}\DailyToolkit.exe'); end;

function WindowExecutable(Window: LongWord): String;
var ProcessId, Process, Size: LongWord; Buffer: String;
begin
  Result := ''; GetWindowThreadProcessId(Window, ProcessId);
  Process := OpenProcess($1000, False, ProcessId);
  if Process = 0 then Exit;
  try
    Size := 32768; Buffer := StringOfChar(#0, Size);
    if QueryFullProcessImageName(Process, 0, Buffer, Size) then Result := Copy(Buffer, 1, Size);
  finally CloseHandle(Process); end;
end;

function CanReplaceExecutable: Boolean;
var Handle: LongWord;
begin
  Result := not FileExists(InstalledExecutable);
  if Result then Exit;
  Handle := CreateFile(InstalledExecutable, $80000000, 0, 0, 3, 0, 0);
  Result := Handle <> $FFFFFFFF;
  if Result then CloseHandle(Handle);
end;

function CloseApplication(OnlyInstalled: Boolean): Boolean;
var Window, MessageId: LongWord; Attempts: Integer; Matching: Boolean;
begin
  MessageId := RegisterWindowMessage('DailyToolkit.ExitApplication');
  Window := GetTopWindow(0);
  while Window <> 0 do begin
    if GetProp(Window, 'DailyToolkit.MainWindow.' + OwnerSid) <> 0 then begin
      if (not OnlyInstalled) or SamePath(WindowExecutable(Window), InstalledExecutable) then
        PostMessage(Window, MessageId, 0, 0);
    end;
    Window := GetWindow(Window, 2);
  end;
  Result := False;
  for Attempts := 1 to 150 do begin
    Matching := False; Window := GetTopWindow(0);
    while Window <> 0 do begin
      if GetProp(Window, 'DailyToolkit.MainWindow.' + OwnerSid) <> 0 then
        if (not OnlyInstalled) or SamePath(WindowExecutable(Window), InstalledExecutable) then Matching := True;
      Window := GetWindow(Window, 2);
    end;
    if not OnlyInstalled then
      Matching := Matching or CheckForMutexes('Local\DailyToolkit.Desktop.' + OwnerSid);
    if (not Matching) and CanReplaceExecutable then begin Result := True; Exit; end;
    Sleep(100);
  end;
end;

function ReadRunExecutable: String;
var Value: String; EndQuote, Index: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKCU64, RunKey, '{#StartupName}', Value) then Exit;
  if Copy(Value, 1, 1) <> '"' then Exit;
  { Iterate Unicode characters; Pos can return an ANSI byte offset on DBCS paths. }
  EndQuote := 0;
  for Index := 2 to Length(Value) do
    if Value[Index] = '"' then begin EndQuote := Index; Break; end;
  if EndQuote = 0 then Exit;
  if Copy(Value, EndQuote, Length(Value)) <> '" --startup' then Exit;
  Result := Copy(Value, 2, EndQuote - 2);
  if CompareText(ExtractFileName(Result), 'DailyToolkit.exe') <> 0 then Result := '';
end;

function SchedulerFolder: Variant;
var Scheduler: Variant;
begin
  Scheduler := CreateOleObject('Schedule.Service'); Scheduler.Connect;
  Result := Scheduler.GetFolder('\');
end;

function FindStartupTask(Folder: Variant): Variant;
var Tasks: Variant; Index: Integer;
begin
  Result := Unassigned;
  Tasks := Folder.GetTasks(1);
  for Index := 1 to Tasks.Count do
    if CompareText(Tasks.Item(Index).Name, '{#StartupName}-' + OwnerSid) = 0 then begin
      Result := Tasks.Item(Index); Exit;
    end;
end;

function OwnedTask(Task: Variant): Boolean;
var Definition: Variant; Account, Resolved: String;
begin
  Result := False; if VarIsEmpty(Task) then Exit;
  Definition := Task.Definition;
  if Definition.RegistrationInfo.Description <> 'DailyToolkit automatic startup. Owner: ' + OwnerSid then Exit;
  Account := Definition.Principal.UserId;
  Resolved := Account;
  if CompareText(Account, OwnerSid) <> 0 then Resolved := AccountSid(Account);
  Log('Startup principal SID match: ' + IntToStr(Ord(CompareText(Resolved, OwnerSid) = 0)));
  if CompareText(Resolved, OwnerSid) <> 0 then Exit;
  Result := (Definition.Actions.Count = 1) and
    (CompareText(ExtractFileName(Definition.Actions.Item(1).Path), 'DailyToolkit.exe') = 0) and
    (Definition.Actions.Item(1).Arguments = '--startup --admin-task');
end;

function HasOtherStartup: Boolean;
var Run: String; Folder, Task: Variant;
begin
  Result := False; Run := ReadRunExecutable;
  if (Run <> '') and not SamePath(Run, InstalledExecutable) then Result := True;
  try
    Folder := SchedulerFolder; Task := FindStartupTask(Folder);
    if OwnedTask(Task) then
      if not SamePath(Task.Definition.Actions.Item(1).Path, InstalledExecutable) then Result := True;
  except Log('Startup inspection failed: ' + GetExceptionMessage); end;
end;

procedure SaveTaskXml(FileName, Xml: String);
var Handle, Size, Written: LongWord; Buffer: String;
begin
  { schtasks expects UTF-16 task XML, including the existing UTF-16 declaration. }
  Buffer := #$FEFF + Xml; Size := Length(Buffer) * 2;
  Handle := CreateFile(FileName, $40000000, 0, 0, 2, $80, 0);
  if Handle = $FFFFFFFF then RaiseException('Cannot create startup definition.');
  try
    if not WriteFile(Handle, Buffer, Size, Written, 0) or (Written <> Size) then
      RaiseException('Cannot write startup definition.');
  finally CloseHandle(Handle); end;
end;

function MigrateStartup(TargetDirectory: String): Boolean;
var Folder, Task, Definition, Action, Registered: Variant; Run, Security, RegisteredDir, XmlFile: String; Code, Index: Integer; Output: TExecOutput; Executed: Boolean;
begin
  Result := False;
  if not RegQueryStringValue(HKCU64, UninstallKey, 'InstallLocation', RegisteredDir) then Exit;
  if not SamePath(RegisteredDir, TargetDirectory) then Exit;
  if not FileExists(AddBackslash(TargetDirectory) + 'DailyToolkit.exe') then Exit;
  Folder := SchedulerFolder; Task := FindStartupTask(Folder);
  if (not VarIsEmpty(Task)) and (not OwnedTask(Task)) then RaiseException('The startup task is not owned by DailyToolkit.');
  if OwnedTask(Task) then begin
    if not SamePath(Task.Definition.Actions.Item(1).Path, AddBackslash(TargetDirectory) + 'DailyToolkit.exe') then begin
      if not IsAdmin then begin
        Result := ShellExec('runas', ExpandConstant('{srcexe}'),
          '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MIGRATEONLY=1 /OWNER=' + OwnerSid + ' /TARGET="' + TargetDirectory + '"',
          '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
        Exit;
      end;
      Log('Reading startup task definition and security.');
      Definition := Task.Definition; Security := Task.GetSecurityDescriptor(7);
      Log('Updating startup action paths.');
      Action := Definition.Actions.Item(1);
      Action.Path := AddBackslash(TargetDirectory) + 'DailyToolkit.exe';
      Action.WorkingDirectory := TargetDirectory;
      Log('Registering updated startup task.');
      XmlFile := ExpandConstant('{tmp}\DailyToolkit-startup.xml');
      try
        SaveTaskXml(XmlFile, Definition.XmlText);
        Executed := ExecAndCaptureOutput(ExpandConstant('{sys}\schtasks.exe'), '/Create /TN "{#StartupName}-' + OwnerSid + '" /XML "' + XmlFile + '" /F',
          '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
        for Index := 0 to GetArrayLength(Output.StdOut) - 1 do Log(Output.StdOut[Index]);
        for Index := 0 to GetArrayLength(Output.StdErr) - 1 do Log(Output.StdErr[Index]);
        if not Executed or (Code <> 0) then
          RaiseException('Startup registration failed: ' + IntToStr(Code));
      finally DeleteFile(XmlFile); end;
      Registered := FindStartupTask(Folder);
      if Registered.GetSecurityDescriptor(7) <> Security then Registered.SetSecurityDescriptor(Security, 0);
      if Registered.GetSecurityDescriptor(7) <> Security then RaiseException('Startup security was not retained.');
      Log('Startup task migration completed.');
    end;
  end;
  Run := ReadRunExecutable;
  if (Run <> '') and not SamePath(Run, AddBackslash(TargetDirectory) + 'DailyToolkit.exe') then
    if not RegWriteStringValue(HKCU64, RunKey, '{#StartupName}', '"' + AddBackslash(TargetDirectory) + 'DailyToolkit.exe" --startup') then Exit;
  Result := True;
end;

function StartupMigrationSucceeded: Boolean;
begin Result := MigrationSucceeded; end;

function InitializeSetup: Boolean;
begin
  OwnerSid := CurrentSid; MigrationSucceeded := True; MigrationExitCode := 1;
  MigrationOnly := ExpandConstant('{param:MIGRATEONLY|0}') = '1';
  if MigrationOnly then begin
    if IsAdmin and (ExpandConstant('{param:OWNER|}') = OwnerSid) then begin
      try if MigrateStartup(ExpandConstant('{param:TARGET|}')) then MigrationExitCode := 0;
      except Log('Startup migration failed: ' + GetExceptionMessage); end;
    end;
    ExitProcess(MigrationExitCode); Result := False; Exit;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var PreviousVersion, Directory, DataDirectory: String; PreviousPacked, CurrentPacked: Int64;
begin
  Result := ''; Directory := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  DataDirectory := RemoveBackslashUnlessRoot(ExpandConstant('{localappdata}\DailyToolkit'));
  if SamePath(Directory, DataDirectory) or
    (CompareText(Copy(Directory, 1, Length(DataDirectory) + 1), DataDirectory + '\') = 0) then begin
    Result := CustomMessage('DataDirectory'); Exit;
  end;
  if RegQueryStringValue(HKCU64, UninstallKey, 'DisplayVersion', PreviousVersion) then
    if StrToVersion(PreviousVersion, PreviousPacked) and StrToVersion('{#AppVersion}', CurrentPacked) and
      (ComparePackedVersion(PreviousPacked, CurrentPacked) > 0) then begin
      Result := CustomMessage('Downgrade'); Exit;
    end;
  if not CloseApplication(False) then Result := CustomMessage('CloseFailed');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('migratestartup') then begin
    try MigrationSucceeded := MigrateStartup(ExpandConstant('{app}'));
    except MigrationSucceeded := False; Log('Startup migration failed: ' + GetExceptionMessage); end;
    if not MigrationSucceeded then SuppressibleMsgBox(CustomMessage('StartupFailed'), mbError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0; if not MigrationSucceeded then Result := 2;
end;

function InitializeUninstall: Boolean;
begin
  OwnerSid := CurrentSid; Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Folder, Task: Variant; Run: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if not CloseApplication(True) then begin
    RaiseException(CustomMessage('CloseFailed'));
  end;
  try
    Folder := SchedulerFolder; Task := FindStartupTask(Folder);
    Log('Startup task found: ' + IntToStr(Ord(not VarIsEmpty(Task))));
    if OwnedTask(Task) then
      if SamePath(Task.Definition.Actions.Item(1).Path, InstalledExecutable) then
        Folder.DeleteTask('{#StartupName}-' + OwnerSid, 0);
    Run := ReadRunExecutable;
    Log('Startup Run recognized: ' + IntToStr(Ord(Run <> '')));
    if (Run <> '') and SamePath(Run, InstalledExecutable) then
      if not RegDeleteValue(HKCU64, RunKey, '{#StartupName}') then RaiseException('Startup removal failed.');
  except
    Log('Startup cleanup failed: ' + GetExceptionMessage);
    RaiseException(CustomMessage('CleanupFailed'));
  end;
end;

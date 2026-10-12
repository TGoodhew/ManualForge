; ManualForge's installer (#26). Built by tools\build-installer.ps1, which publishes the program into
; one folder and passes the pinned prerequisites in as defines; see that script for what each is.
;
; Per user, into %LOCALAPPDATA%\Programs\ManualForge, the path every MCP registration expects. Only
; what we build is carried. Everything else is downloaded from its publisher and checked against a
; SHA-256 pinned at build time:
;   * .NET 10 Desktop Runtime - machine-wide, the one administrator prompt, only when missing;
;   * Windows App Runtime 1.8 - only when missing;
;   * Visual C++ Redistributable - machine-wide, only when missing or older than ONNX Runtime needs;
;   * NVIDIA's CUDA, cuDNN and TensorRT - by `manualforge setup gpu`, after NVIDIA's licences are accepted.
; Uninstalling takes away everything this put on the machine, and never touches the manual library.

#ifndef AppVersion
  #error Build with tools\build-installer.ps1, which supplies the version and the pinned downloads.
#endif

#define AppName "ManualForge"
#define AppExe "ManualForge.App.exe"
#define Cli "manualforge.exe"

[Setup]
AppId={{A6AE7B23-BB07-4BEE-9AD0-0B669785EDE5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Tony Goodhew
AppPublisherURL=https://github.com/TGoodhew/ManualForge
AppSupportURL=https://github.com/TGoodhew/ManualForge/issues
VersionInfoVersion=0.1.0.0
DefaultDirName={localappdata}\Programs\ManualForge
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile={#RepoDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=ManualForge-Setup-{#AppVersion}
SetupIconFile={#RepoDir}\src\ManualForge.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; An upgrade first stops ManualForge's own processes, naming the app that started each MCP server
; (PrepareToInstall, below). This catches anything else holding the program's files. The clients
; start the new server the next time they need it.
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes

[Tasks]
Name: gpu; Description: "Download NVIDIA's libraries for the GPU (CUDA and cuDNN, about 1.7 GB)"; Check: HasNvidiaGpu
Name: gpu\tensorrt; Description: "Also TensorRT, which reads about twice as fast (another 1.9 GB)"; Check: HasNvidiaGpu
Name: mcp; Description: "Connect ManualForge to the AI apps on this PC (Claude, VS Code and others)"
Name: path; Description: "Let a terminal run `manualforge` from any folder"; Flags: unchecked

[InstallDelete]
; An upgrade replaces the program whole, so nothing from an older build lingers beside the new one.
Type: filesandordirs; Name: "{app}\*"

[UninstallDelete]
; The folder is recorded as created only by the install that made it, and an upgrade's
; [InstallDelete] takes the uninstall log of the installs before it with everything else.
Type: dirifempty; Name: "{app}"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; AppUserModelID: "TGoodhew.ManualForge"; Comment: "Search your manuals"

[Registry]
Root: HKCU; Subkey: "Software\ManualForge"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\ManualForge"; ValueType: string; ValueName: "Library"; ValueData: "{code:Library}"

[Run]
Filename: "{app}\{#Cli}"; Parameters: "setup gpu --accept-nvidia-licences {code:TensorRtArgument}"; StatusMsg: "Downloading NVIDIA's libraries. This takes a while; progress is in the window that opens."; Flags: waituntilterminated; Tasks: gpu; Check: NvidiaLicencesAccepted
Filename: "{app}\{#Cli}"; Parameters: "setup models"; StatusMsg: "Fetching the OCR models..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#Cli}"; Parameters: "setup mcp --library ""{code:Library}"""; StatusMsg: "Connecting ManualForge to the AI apps on this PC..."; Flags: runhidden waituntilterminated; Tasks: mcp
Filename: "{app}\{#AppExe}"; Description: "Start ManualForge"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; Before the files go, while the program that knows how is still there.
Filename: "{app}\{#Cli}"; Parameters: "setup mcp --remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMcp"
Filename: "{app}\{#Cli}"; Parameters: "setup gpu --remove"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveGpu"

[Code]
const
  EnvironmentKey = 'Environment';
  StateKey = 'Software\ManualForge';
  LibraryVariable = 'MANUALFORGE_LIBRARY';
  DotNetKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  VcRuntimeKey = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  { ONNX Runtime is built with Visual Studio 17.10 or later, whose std::mutex crashes on a runtime
    older than 14.40. }
  VcRuntimeMinorNeeded = 40;

var
  LibraryPage: TInputDirWizardPage;
  NvidiaPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  UninstallLibrary, UninstallRuntimes: String;
  Stopped, InstalledSummary: String;

{ ---------------------------------------------------------------- what the machine has }

function HasNvidiaGpu: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\nvidia-smi.exe'));
end;

{ The runtime's own folder is what .NET looks in, so that is checked first. Its installer records the
  x64 runtime only in the 32-bit view of the registry (WOW6432Node); looking only in the 64-bit view
  found nothing, and every install ran the .NET installer again, with its permission prompt. }
function HasDotNet10: Boolean;
var
  Names: TArrayOfString;
  Found: TFindRec;
  I: Integer;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found) then
  try
    repeat
      if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        Result := True;
    until Result or not FindNext(Found);
  finally
    FindClose(Found);
  end;
  if Result then Exit;

  if RegGetValueNames(HKLM32, DotNetKey, Names) or RegGetValueNames(HKLM64, DotNetKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
        Result := True;
end;

function HasVcRuntime: Boolean;
var
  Installed, Major, Minor: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Installed', Installed) and (Installed = 1) and
    RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Major', Major) and (Major = 14) and
    RegQueryDWordValue(HKLM64, VcRuntimeKey, 'Minor', Minor) and (Minor >= VcRuntimeMinorNeeded);
end;

function HasWindowsAppRuntime18: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "if (Get-AppxPackage -Name Microsoft.WindowsAppRuntime.1.8) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ ---------------------------------------------------------------- answers the [Run] lines use }

function Library(Param: String): String;
begin
  Result := LibraryPage.Values[0];
end;

function TensorRtArgument(Param: String): String;
begin
  if WizardIsTaskSelected('gpu\tensorrt') then Result := '' else Result := '--no-tensorrt';
end;

function NvidiaLicencesAccepted: Boolean;
begin
  Result := NvidiaPage.Values[0];
end;

{ ---------------------------------------------------------------- pages }

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
var
  Existing: String;
begin
  LibraryPage := CreateInputDirPage(wpLicense, 'Your manuals',
    'Which folder holds the PDF manuals ManualForge should search?',
    'ManualForge reads and searches this folder. It never deletes a manual: every file it replaces with ' +
    'a searchable copy is kept, untouched, in an _Originals folder inside it.', False, '');
  LibraryPage.Add('Library folder:');
  { /LIBRARY="D:\Manuals" answers this page on the command line, for a silent install. }
  Existing := ExpandConstant('{param:LIBRARY|}');
  if Existing = '' then
    if not RegQueryStringValue(HKCU, StateKey, 'Library', Existing) then
      if not RegQueryStringValue(HKCU, EnvironmentKey, LibraryVariable, Existing) then
        Existing := ExpandConstant('{userdocs}\Manuals');
  LibraryPage.Values[0] := Existing;

  NvidiaPage := CreateInputOptionPage(wpSelectTasks, 'NVIDIA''s licences',
    'The GPU libraries come from NVIDIA, under NVIDIA''s licences.',
    'ManualForge downloads them straight from NVIDIA and checks each against the SHA-256 NVIDIA publishes. ' +
    'Please read the licences they come under:' + #13#10#13#10 +
    'CUDA:  https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/LICENSE.txt' + #13#10 +
    'cuDNN:  https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/LICENSE.txt' + #13#10 +
    'TensorRT:  https://docs.nvidia.com/deeplearning/tensorrt/latest/reference/sla.html' + #13#10#13#10 +
    'Without them ManualForge runs on the CPU, about ten times slower. `manualforge setup gpu` fetches them later.',
    False, False);
  NvidiaPage.Add('I accept NVIDIA''s licences for these libraries');

  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading what ManualForge runs on...', @OnDownloadProgress);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = NvidiaPage.ID) and not WizardIsTaskSelected('gpu');
end;

{ ---------------------------------------------------------------- the runtimes, before the files }

procedure InstallRuntimes;
var
  Code: Integer;
  NeedDotNet, NeedAppRuntime, NeedVcRuntime: Boolean;
begin
  NeedDotNet := not HasDotNet10;
  NeedAppRuntime := not HasWindowsAppRuntime18;
  NeedVcRuntime := not HasVcRuntime;
  Log(Format('Runtimes missing: .NET 10 %d, Windows App Runtime 1.8 %d, Visual C++ %d', [Ord(NeedDotNet), Ord(NeedAppRuntime), Ord(NeedVcRuntime)]));
  if not NeedDotNet and not NeedAppRuntime and not NeedVcRuntime then
    Exit;

  DownloadPage.Clear;
  if NeedDotNet then
    DownloadPage.Add('{#DotNetUrl}', 'windowsdesktop-runtime-{#DotNetVersion}-win-x64.exe', '{#DotNetSha256}');
  if NeedAppRuntime then
    DownloadPage.Add('{#AppRuntimeUrl}', 'WindowsAppRuntimeInstall-x64.exe', '{#AppRuntimeSha256}');
  if NeedVcRuntime then
    DownloadPage.Add('{#VcRedistUrl}', 'VC_redist.x64.exe', '{#VcRedistSha256}');

  DownloadPage.Show;
  try
    DownloadPage.Download;   { raises if a download fails or its SHA-256 differs }

    if NeedVcRuntime then
    begin
      DownloadPage.SetText('Installing the Visual C++ Redistributable...', 'Windows asks for permission: it is installed for every user of this PC.');
      Log('Installing the Visual C++ Redistributable {#VcRedistVersion}');
      { 1638: a newer one is already there. }
      if not ShellExec('runas', ExpandConstant('{tmp}\VC_redist.x64.exe'),
        '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 3010) and (Code <> 1638)) then
        RaiseException('The Visual C++ Redistributable did not install (code ' + IntToStr(Code) + ').');
      Log('Visual C++ Redistributable: exit code ' + IntToStr(Code));
      RegWriteDWordValue(HKCU, StateKey, 'InstalledVcRuntime', 1);
    end;

    if NeedDotNet then
    begin
      DownloadPage.SetText('Installing the .NET 10 Desktop Runtime...', 'Windows asks for permission: it is installed for every user of this PC.');
      Log('Installing the .NET 10 Desktop Runtime {#DotNetVersion}');
      if not ShellExec('runas', ExpandConstant('{tmp}\windowsdesktop-runtime-{#DotNetVersion}-win-x64.exe'),
        '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 3010)) then
        RaiseException('The .NET 10 Desktop Runtime did not install (code ' + IntToStr(Code) + ').');
      Log('.NET 10 Desktop Runtime: exit code ' + IntToStr(Code));
      RegWriteDWordValue(HKCU, StateKey, 'InstalledDotNet', 1);
    end;

    if NeedAppRuntime then
    begin
      DownloadPage.SetText('Installing the Windows App Runtime 1.8...', '');
      Log('Installing the Windows App Runtime 1.8');
      if not Exec(ExpandConstant('{tmp}\WindowsAppRuntimeInstall-x64.exe'), '--quiet', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
        if not ShellExec('runas', ExpandConstant('{tmp}\WindowsAppRuntimeInstall-x64.exe'), '--quiet', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
          RaiseException('The Windows App Runtime 1.8 did not install (code ' + IntToStr(Code) + ').');
      Log('Windows App Runtime 1.8: exit code ' + IntToStr(Code));
      RegWriteDWordValue(HKCU, StateKey, 'InstalledWindowsAppRuntime', 1);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpReady then
  try
    InstallRuntimes;
  except
    SuppressibleMsgBox(GetExceptionMessage, mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ ---------------------------------------------------------------- an upgrade: what is running, and whose }

{ The app a process belongs to, from its program. Claude Code inside VS Code is claude.exe under the
  VS Code extension, so it is named before the walk reaches Code.exe. }
function KnownClient(const Name, Path: String): String;
begin
  Result := '';
  if Name = 'claude.exe' then
  begin
    if (Pos('\windowsapps\claude_', Path) > 0) or (Pos('\anthropicclaude\', Path) > 0) then
      Result := 'Claude Desktop'
    else
      Result := 'Claude Code';
  end
  else if Name = 'code.exe' then Result := 'VS Code'
  else if Name = 'code - insiders.exe' then Result := 'VS Code Insiders'
  else if Name = 'devenv.exe' then Result := 'Visual Studio'
  else if Name = 'cursor.exe' then Result := 'Cursor'
  else if Name = 'windsurf.exe' then Result := 'Windsurf'
  else if Name = 'lm studio.exe' then Result := 'LM Studio'
  else if Name = 'codex.exe' then Result := 'Codex'
  else if Name = 'explorer.exe' then Result := 'Windows (started by you)';
end;

{ Walks up from a process to the first app it knows, or names the parent when it knows none. }
function OwnerOf(Service: Variant; Pid: Integer): String;
var
  Found, Item: Variant;
  Level: Integer;
  Name, Path, Known, Text: String;
begin
  Result := 'unknown';
  for Level := 1 to 5 do
  begin
    Found := Service.ExecQuery(Format('SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = %d', [Pid]));
    if Found.Count = 0 then Exit;
    Pid := Found.ItemIndex(0).ParentProcessId;
    Found := Service.ExecQuery(Format('SELECT Name, ExecutablePath FROM Win32_Process WHERE ProcessId = %d', [Pid]));
    if Found.Count = 0 then Exit;
    Item := Found.ItemIndex(0);
    Text := Item.Name;
    Name := Lowercase(Text);
    if VarIsNull(Item.ExecutablePath) then Path := '' else
    begin
      Path := Item.ExecutablePath;
      Path := Lowercase(Path);
    end;
    Known := KnownClient(Name, Path);
    if Known <> '' then
    begin
      Result := Known;
      Exit;
    end;
    if Level = 1 then Result := Text;
  end;
end;

{ ManualForge's own processes, from this install's folder, each with the app that started it. }
function FindRunning(var Pids: TArrayOfInteger): String;
var
  Locator, Service, Found, Item: Variant;
  I, N: Integer;
  App, Path, Name: String;
begin
  Result := '';
  SetArrayLength(Pids, 0);
  App := Lowercase(AddBackslash(ExpandConstant('{app}')));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ProcessId, Name, ExecutablePath FROM Win32_Process WHERE ' +
      'Name = ''ManualForge.Mcp.exe'' OR Name = ''manualforge.exe'' OR Name = ''ManualForge.App.exe''');
    for I := 0 to Found.Count - 1 do
    begin
      Item := Found.ItemIndex(I);
      if VarIsNull(Item.ExecutablePath) then Continue;
      Path := Item.ExecutablePath;
      Path := Lowercase(Path);
      if Copy(Path, 1, Length(App)) <> App then Continue;
      Name := Item.Name;
      N := GetArrayLength(Pids);
      SetArrayLength(Pids, N + 1);
      Pids[N] := Item.ProcessId;
      Result := Result + #13#10 + Format('  %s (process %d), started by %s', [Name, Pids[N], OwnerOf(Service, Pids[N])]);
    end;
  except
    Log('Could not list running processes: ' + GetExceptionMessage);
  end;
end;

{ Says what is running and whose it is, then stops it. False if the user would rather it kept running. }
function StopRunning(Silent: Boolean; const Why: String): Boolean;
var
  Pids: TArrayOfInteger;
  Running: String;
  I, Code: Integer;
begin
  Result := True;
  Running := FindRunning(Pids);
  if Running = '' then Exit;

  Log('ManualForge is running:' + Running);
  if not Silent then
    if MsgBox('ManualForge is running:' + Running + #13#10#13#10 + Why, mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;

  for I := 0 to GetArrayLength(Pids) - 1 do
    Exec(ExpandConstant('{sys}\taskkill.exe'), Format('/PID %d /F', [Pids[I]]), '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1000);
  Stopped := Running;
  Log('Stopped:' + Stopped);
end;

{ Before the files are replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopRunning(WizardSilent,
      'Setup stops these to replace the program. The apps that started an MCP server start the new one the ' +
      'next time they need it; if one shows an error for ManualForge, restart that app.') then
    Result := 'ManualForge is still running, so nothing was changed. Close it and run Setup again.';
end;

{ Which build is installed and what it runs on: in the log always, and on the last page when there is one. }
function InstalledBuild: String;
var
  Output: TExecOutput;
  Code, I: Integer;
  Line: String;
begin
  if InstalledSummary = '' then
  begin
    if ExecAndCaptureOutput(ExpandConstant('{app}\{#Cli}'), 'version', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then
      if GetArrayLength(Output.StdOut) > 0 then
        InstalledSummary := Output.StdOut[0];
    if ExecAndCaptureOutput(ExpandConstant('{app}\{#Cli}'), 'gpu', '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then
      for I := 0 to GetArrayLength(Output.StdOut) - 1 do
      begin
        Line := Output.StdOut[I];
        Log('gpu: ' + Line);
        if Pos('Active provider', Line) = 1 then
          InstalledSummary := InstalledSummary + #13#10 + 'Runs on: ' + Trim(Copy(Line, Pos(':', Line) + 1, Length(Line)));
      end;
    Log('Installed: ' + InstalledSummary);
  end;
  Result := InstalledSummary;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Summary: String;
begin
  if CurPageID <> wpFinished then Exit;

  Summary := InstalledBuild;
  if Stopped <> '' then
    Summary := Summary + #13#10#13#10 + 'Stopped for the upgrade:' + Stopped;
  WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + Summary;
  WizardForm.FinishedLabel.AdjustHeight;
  WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
end;

{ ---------------------------------------------------------------- the environment, recorded so it can be undone }

procedure AddToPath(const Dir: String);
var
  Path: String;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Path) then Path := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') > 0 then Exit;
  if (Path <> '') and (Copy(Path, Length(Path), 1) <> ';') then Path := Path + ';';
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Path + Dir);
  RegWriteDWordValue(HKCU, StateKey, 'AddedToPath', 1);
end;

procedure RemoveFromPath(const Dir: String);
var
  Path: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Path) then Exit;
  Path := ';' + Path + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(Path));
  if P = 0 then Exit;
  Delete(Path, P, Length(Dir) + 1);
  Path := Copy(Path, 2, Length(Path) - 2);
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Path);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Existing: String;
begin
  if CurStep = ssPostInstall then
  begin
    { The library for the command line and anything else that reads it. Set only if it was not
      already set to something; uninstalling then takes away only what this put there. }
    if not RegQueryStringValue(HKCU, EnvironmentKey, LibraryVariable, Existing) then
    begin
      RegWriteStringValue(HKCU, EnvironmentKey, LibraryVariable, Library(''));
      RegWriteDWordValue(HKCU, StateKey, 'SetLibraryVariable', 1);
    end;

    if WizardIsTaskSelected('path') then
      AddToPath(ExpandConstant('{app}'));
  end;

  { A silent install has no last page, so the log is the only place this is said. }
  if CurStep = ssDone then
    InstalledBuild;
end;

{ ---------------------------------------------------------------- uninstalling }

function Flag(const Name: String): Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKCU, StateKey, Name, Value) and (Value = 1);
end;

{ Running, ManualForge holds its own files and the GPU libraries open, and they could not be removed. }
function InitializeUninstall: Boolean;
begin
  Result := StopRunning(UninstallSilent,
    'Uninstalling stops these first. The apps that started an MCP server no longer have ManualForge once it is removed.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data, Message: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Read before the [Registry] key goes. }
    if not RegQueryStringValue(HKCU, StateKey, 'Library', UninstallLibrary) then UninstallLibrary := '';
    if Flag('SetLibraryVariable') then
      RegDeleteValue(HKCU, EnvironmentKey, LibraryVariable);
    if Flag('AddedToPath') then
      RemoveFromPath(ExpandConstant('{app}'));

    UninstallRuntimes := '';
    if Flag('InstalledDotNet') then UninstallRuntimes := UninstallRuntimes + #13#10 + '  .NET 10 Desktop Runtime';
    if Flag('InstalledWindowsAppRuntime') then UninstallRuntimes := UninstallRuntimes + #13#10 + '  Windows App Runtime 1.8';
    if Flag('InstalledVcRuntime') then UninstallRuntimes := UninstallRuntimes + #13#10 + '  Microsoft Visual C++ Redistributable';
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    { Models, the TensorRT engines built for this card, logs and what was learned about the GPU.
      The GPU libraries themselves were already removed by `setup gpu --remove`. }
    Data := ExpandConstant('{localappdata}\ManualForge');
    if DirExists(Data) then
      if UninstallSilent or (MsgBox('Also remove ManualForge''s models, logs and what it learned about your GPU?' + #13#10#13#10 + Data,
          mbConfirmation, MB_YESNO) = IDYES) then
        DelTree(Data, True, True, True);

    if not UninstallSilent then
    begin
      Message := 'ManualForge is removed.' + #13#10#13#10 +
        'Your manuals were not touched. The searchable copies stay searchable in any PDF reader, and every original ' +
        'ManualForge replaced is still in the _Originals folder inside' + #13#10 + '  ' + UninstallLibrary + #13#10 +
        'The README''s "Uninstall" section says how to put the originals back, if you want them.';
      if UninstallRuntimes <> '' then
        Message := Message + #13#10#13#10 + 'These were installed for ManualForge:' + UninstallRuntimes + #13#10 +
          'Other apps may use them too. Remove them from Settings > Apps if nothing else needs them.';
      MsgBox(Message, mbInformation, MB_OK);
    end;
  end;
end;

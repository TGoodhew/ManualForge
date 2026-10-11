; ManualForge's installer (#26). Built by tools\build-installer.ps1, which publishes the program into
; one folder and passes the pinned prerequisites in as defines; see that script for what each is.
;
; Per user, into %LOCALAPPDATA%\Programs\ManualForge, the path every MCP registration expects. Only
; what we build is carried. Everything else is downloaded from its publisher and checked against a
; SHA-256 pinned at build time:
;   * .NET 10 Desktop Runtime - machine-wide, the one administrator prompt, only when missing;
;   * Windows App Runtime 1.8 - only when missing;
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
; Closes what holds the program's files during an upgrade: the app, and MCP servers that Claude and
; the other clients started. They start the new one next time they need it.
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

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; AppUserModelID: "TGoodhew.ManualForge"; Comment: "Search your manuals"

[Registry]
Root: HKCU; Subkey: "Software\ManualForge"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\ManualForge"; ValueType: string; ValueName: "Library"; ValueData: "{code:Library}"

[Run]
Filename: "{app}\{#Cli}"; Parameters: "setup gpu --accept-nvidia-licences {code:TensorRtArgument}"; StatusMsg: "Downloading NVIDIA's libraries. This takes a while; progress is in the window that opens."; Flags: waituntilterminated; Tasks: gpu; Check: NvidiaLicencesAccepted
Filename: "{app}\{#Cli}"; Parameters: "gpu"; StatusMsg: "Fetching the OCR models and checking the GPU..."; Flags: runhidden waituntilterminated
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

var
  LibraryPage: TInputDirWizardPage;
  NvidiaPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  UninstallLibrary, UninstallRuntimes: String;

{ ---------------------------------------------------------------- what the machine has }

function HasNvidiaGpu: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\nvidia-smi.exe'));
end;

function HasDotNet10: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, DotNetKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
        Result := True;
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
begin
  DownloadPage.Clear;
  if not HasDotNet10 then
    DownloadPage.Add('{#DotNetUrl}', 'windowsdesktop-runtime-{#DotNetVersion}-win-x64.exe', '{#DotNetSha256}');
  if not HasWindowsAppRuntime18 then
    DownloadPage.Add('{#AppRuntimeUrl}', 'WindowsAppRuntimeInstall-x64.exe', '{#AppRuntimeSha256}');
  if DownloadPage.Count = 0 then
    Exit;

  DownloadPage.Show;
  try
    DownloadPage.Download;   { raises if a download fails or its SHA-256 differs }

    if not HasDotNet10 then
    begin
      DownloadPage.SetText('Installing the .NET 10 Desktop Runtime...', 'Windows asks for permission: it is installed for every user of this PC.');
      if not ShellExec('runas', ExpandConstant('{tmp}\windowsdesktop-runtime-{#DotNetVersion}-win-x64.exe'),
        '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 3010)) then
        RaiseException('The .NET 10 Desktop Runtime did not install (code ' + IntToStr(Code) + ').');
      RegWriteDWordValue(HKCU, StateKey, 'InstalledDotNet', 1);
    end;

    if not HasWindowsAppRuntime18 then
    begin
      DownloadPage.SetText('Installing the Windows App Runtime 1.8...', '');
      if not Exec(ExpandConstant('{tmp}\WindowsAppRuntimeInstall-x64.exe'), '--quiet', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
        if not ShellExec('runas', ExpandConstant('{tmp}\WindowsAppRuntimeInstall-x64.exe'), '--quiet', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
          RaiseException('The Windows App Runtime 1.8 did not install (code ' + IntToStr(Code) + ').');
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
end;

{ ---------------------------------------------------------------- uninstalling }

function Flag(const Name: String): Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKCU, StateKey, Name, Value) and (Value = 1);
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

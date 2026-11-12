#ifndef AppVersion
  #define AppVersion "0.3.21"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ThermalDesk-win-x64"
#endif
#ifndef ArtifactDir
  #define ArtifactDir "..\artifacts"
#endif

[Setup]
AppId={{7B6872A4-B583-46B5-BA6C-25E6F8EB4664}
AppName=ThermalDesk
AppVersion={#AppVersion}
AppPublisher=ThermalDesk contributors
AppPublisherURL=https://github.com/Jeycob/ThermalDesk
DefaultDirName={autopf}\ThermalDesk
DefaultGroupName=ThermalDesk
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\ThermalDesk.exe
SetupIconFile=..\src\ThermalDesk.App\Resources\ThermalDesk.ico
LicenseFile=..\LICENSE
InfoBeforeFile=Install-notes.txt
OutputDir={#ArtifactDir}
#ifdef SandboxTest
OutputBaseFilename=ThermalDesk-{#AppVersion}-SandboxTest-x64
#else
OutputBaseFilename=ThermalDesk-{#AppVersion}-Setup-x64
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
AppMutex=Local\ThermalDesk.SingleInstance
PrivilegesRequired=admin
#ifdef SandboxTest
PrivilegesRequiredOverridesAllowed=commandline
Uninstallable=no
CreateAppDir=yes
CreateUninstallRegKey=no
#endif

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Startup-Migration.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

#ifndef SandboxTest
[Icons]
Name: "{group}\ThermalDesk"; Filename: "{app}\ThermalDesk.exe"; WorkingDir: "{app}"; Comment: "Restores your saved profile. Hold Shift for Preview."; AppUserModelID: "ThermalDesk.Desktop"
Name: "{autodesktop}\ThermalDesk"; Filename: "{app}\ThermalDesk.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "ThermalDesk.Desktop"
[Run]
Filename: "{app}\ThermalDesk.exe"; Parameters: "--preview --show-window"; Description: "Open ThermalDesk in Preview (no hardware control)"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser
#endif

[Code]
function ThermalDeskRunning: Boolean;
var
  Locator, Service, Processes: Variant;
begin
  Result := True;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''ThermalDesk.exe''');
    Result := Processes.Count > 0;
  except
    Log('Could not check running ThermalDesk processes. Installation is blocked for hardware safety.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ThermalDeskRunning then
    Result := 'ThermalDesk is running, or its process state could not be checked. In ThermalDesk choose Stop, then Exit from the tray icon. Setup never terminates fan control automatically. Then retry.';
end;

function RunStartupHelper(Mode: String): Boolean;
var
  Code: Integer;
  Parameters: String;
begin
  Code := -1;
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\installer\Startup-Migration.ps1') + '" -Mode ' + Mode + ' -AppPath "' + ExpandConstant('{app}\ThermalDesk.exe') + '"';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure UninstallError(Message: String);
begin
  Log(Message);
  if not UninstallSilent then
    MsgBox(Message, mbError, MB_OK);
end;

function InitializeUninstall: Boolean;
begin
  Result := not ThermalDeskRunning;
  if not Result then
    UninstallError('Please choose Stop, then Exit in ThermalDesk before uninstalling. Your saved profile will not be deleted.');
#ifndef SandboxTest
  if Result then
  begin
    Result := RunStartupHelper('PlanRemove');
    if not Result then
      UninstallError('The existing startup entry could not be safely checked. Open ThermalDesk as its startup owner, remove Start at sign-in, exit, and retry. No application files have been removed.');
  end;
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
#ifndef SandboxTest
  if CurUninstallStep = usUninstall then
  begin
    // This runs only after the uninstall confirmation. Unlike UninstallRun,
    // a helper failure is checked and Abort prevents application file removal.
    if ThermalDeskRunning then
    begin
      UninstallError('ThermalDesk has started again. Choose Stop, then Exit, and retry. No application files have been removed.');
      Abort;
    end;
    if not RunStartupHelper('Remove') then
    begin
      UninstallError('Startup cleanup failed. Uninstall was stopped before removing application files. Remove Start at sign-in in ThermalDesk, exit, and retry.');
      Abort;
    end;
  end;
#endif
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
#ifndef SandboxTest
  if CurStep = ssPostInstall then
  begin
    if not RunStartupHelper('Apply') then
      MsgBox('ThermalDesk was installed, but the existing startup path could not be updated. Open ThermalDesk and re-register Start at sign-in before removing an older portable copy. No hardware control was started.', mbInformation, MB_OK);
  end;
#endif
end;

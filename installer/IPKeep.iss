#ifndef PublishDir
  #error PublishDir must point to the complete published application.
#endif
#ifndef OutputDir
  #error OutputDir must point to the installer output directory.
#endif
#ifndef ProductVersion
  #define ProductVersion "1.0.0"
#endif
#ifndef BetaNumber
  #define BetaNumber "2"
#endif
#define ReleaseVersion ProductVersion + "-preview." + BetaNumber
#define NumericVersion ProductVersion + "." + BetaNumber

[Setup]
AppId={{B43D7C64-8CCA-44BD-B5E8-01BFE865A142}
AppName=IPKeep
AppVersion={#ReleaseVersion}
AppVerName=IPKeep {#ReleaseVersion} (unsigned beta)
AppPublisher=IPKeep
AppPublisherURL=https://ipkeep.net/
AppSupportURL=https://github.com/calxibe/ipkeep-windows/issues
AppUpdatesURL=https://github.com/calxibe/ipkeep-windows/releases
DefaultDirName={autopf}\IPKeep\app
UsePreviousAppDir=no
DisableDirPage=yes
DefaultGroupName=IPKeep
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=IPKeep-{#ReleaseVersion}-windows-x64-unsigned-setup
SetupIconFile={#PublishDir}\Assets\IPKeep.ico
UninstallDisplayIcon={app}\IPKeep.exe
VersionInfoVersion={#NumericVersion}
VersionInfoDescription=IPKeep unsigned beta installer
VersionInfoCompany=IPKeep
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
InfoBeforeFile=beta-notice.txt
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UninstallLogging=yes
SignedUninstaller=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; These files are first so preflight can extract its self-contained helper quickly.
Source: "{#PublishDir}\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "service\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\IPKeep"; Filename: "{app}\IPKeep.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\IPKeep"; Filename: "{app}\IPKeep.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\IPKeep\Installer"; ValueType: string; ValueName: "Version"; ValueData: "{#NumericVersion}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\IPKeep.exe"; Parameters: "--settings"; Description: "Open IPKeep"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: ServiceUpgradeSucceeded

[Code]
var
  UpgradeFailed: Boolean;
  HelperExtracted: Boolean;

function ServiceUpgradeSucceeded: Boolean;
begin
  Result := not UpgradeFailed;
end;

function RunHelper(const Filename, Operation: String): Integer;
var
  Code: Integer;
begin
  Result := -1;
  if Exec(Filename, Operation, ExtractFileDir(Filename), SW_HIDE,
      ewWaitUntilTerminated, Code) then Result := Code;
  Log(Format('IPKeep helper %s returned %d', [Operation, Result]));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstalledVersion: String;
  InstalledPacked, NewPacked: Int64;
  Code: Integer;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\IPKeep\app')) <> 0 then
  begin
    Result := 'IPKeep must be installed in its default Program Files folder.';
    Exit;
  end;
  if RegQueryStringValue(HKLM, 'Software\IPKeep\Installer', 'Version', InstalledVersion) then
    if StrToVersion(InstalledVersion, InstalledPacked) and StrToVersion('{#NumericVersion}', NewPacked) then
    if ComparePackedVersion(InstalledPacked, NewPacked) > 0 then
    begin
      Result := 'A newer IPKeep version is already installed. Use the newer installer.';
      Exit;
    end;
  if not HelperExtracted then
  begin
    ExtractTemporaryFiles('{app}\service\*');
    HelperExtracted := True;
  end;
  Code := RunHelper(ExpandConstant('{tmp}\') + '{app}\service\IPKeep.Service.exe', '--installer-check');
  if Code <> 0 then
    Result := 'IPKeep could not verify the existing installation. Check administrator access, installation folder permissions, and whether another service uses the IPKeep name. No service changes were made.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    UpgradeFailed := RunHelper(ExpandConstant('{app}\service\IPKeep.Service.exe'), '--installer-upgrade') <> 0;
    if UpgradeFailed then
      SuppressibleMsgBox('The desktop was installed, but the background service could not be updated. The previous service files are retained when rollback succeeds. Open IPKeep Settings and use Service maintenance to repair it before continuing.', mbError, MB_OK, IDOK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and UpgradeFailed then
    WizardForm.FinishedLabel.Caption := 'IPKeep needs attention: the desktop is installed, but its background service update failed. Open Settings and repair the service.';
end;

function GetCustomSetupExitCode: Integer;
begin
  if UpgradeFailed then Result := 20 else Result := 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if RunHelper(ExpandConstant('{app}\service\IPKeep.Service.exe'), '--installer-remove') <> 0 then
      RaiseException('IPKeep could not stop or remove its background service. Uninstall was stopped to preserve the files. Close IPKeep and Windows Services, then try again.');
end;

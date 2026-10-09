; DynamicBay installer (Inno Setup 6). Built by tools\build-release.ps1 or the GitHub release workflow.
; The app is published self-contained (.NET runtime included), so nothing else has to be installed on the target PC.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef PackageDir
  #define PackageDir "..\artifacts\package"
#endif

[Setup]
AppId={{6E3C2B1A-6E1B-4C3E-9A55-0D1B5A7E9F21}
AppName=DynamicBay
AppVersion={#AppVersion}
AppVerName=DynamicBay {#AppVersion}
AppPublisher=SuperSparrow
AppPublisherURL=https://github.com/SuperSparrow-sys/dynamic-island-win
AppSupportURL=https://github.com/SuperSparrow-sys/dynamic-island-win/issues
AppUpdatesURL=https://github.com/SuperSparrow-sys/dynamic-island-win/releases
DefaultDirName={autopf}\DynamicBay
DefaultGroupName=DynamicBay
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts
OutputBaseFilename=DynamicBay-Setup-{#AppVersion}
SetupIconFile=..\src\DynamicBay\Assets\DynamicBay.ico
UninstallDisplayIcon={app}\DynamicBay.exe
UninstallDisplayName=DynamicBay
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; A running DynamicBay is closed via Restart Manager; during auto-update (/UPDATE) setup waits for it to exit.
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.TaskNotifications=Mitteilungen anderer Apps in der Insel anzeigen (WhatsApp, Outlook, ...)
english.TaskNotifications=Show notifications from other apps in the island (WhatsApp, Outlook, ...)
german.TaskDesktop=Desktop-Verknüpfung erstellen
english.TaskDesktop=Create a desktop shortcut
german.DeleteData=Sollen auch deine Einstellungen, die Zwischenablage-Historie und die Dateiablage gelöscht werden?
english.DeleteData=Also delete your settings, clipboard history and shelf?
german.LaunchApp=DynamicBay jetzt starten
german.CloseApp=DynamicBay läuft noch. Bitte über das Tray-Symbol beenden und dann OK klicken.
english.LaunchApp=Launch DynamicBay now
english.CloseApp=DynamicBay is still running. Please quit it from the tray icon, then click OK.

[Tasks]
Name: "notifications"; Description: "{cm:TaskNotifications}"
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\DynamicBay.msix"; DestDir: "{app}"; Flags: ignoreversion; Tasks: notifications
Source: "{#PackageDir}\DynamicBay.cer"; DestDir: "{app}"; Flags: ignoreversion; Tasks: notifications

[Icons]
Name: "{autoprograms}\DynamicBay"; Filename: "{app}\DynamicBay.exe"
Name: "{autodesktop}\DynamicBay"; Filename: "{app}\DynamicBay.exe"; Tasks: desktopicon

[Run]
; Trust the package signing certificate so the identity package can be registered (needed for notification access).
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore TrustedPeople ""{app}\DynamicBay.cer"""; Flags: runhidden waituntilterminated; Tasks: notifications; StatusMsg: "Zertifikat wird eingerichtet..."
; Start as the logged-in user (not elevated) - the app registers its identity package for that user on first run.
Filename: "{app}\DynamicBay.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Restore Windows banners, autostart, Claude hooks and remove the identity package.
Filename: "{app}\DynamicBay.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "DynamicBayCleanup"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople ""DynamicBay Open Source"""; Flags: runhidden waituntilterminated; RunOnceId: "DynamicBayCert"

[Code]
function IsUpdate: Boolean;
begin
  Result := Pos('/UPDATE', UpperCase(GetCmdTail)) > 0;
end;

function InitializeSetup: Boolean;
var
  i: Integer;
begin
  Result := True;
  // Auto-update: DynamicBay launched this setup and is shutting down - give it a moment to exit.
  i := 0;
  while CheckForMutexes('DynamicBay.SingleInstance') and (i < 40) do
  begin
    Sleep(250);
    i := i + 1;
  end;
  if CheckForMutexes('DynamicBay.SingleInstance') and not WizardSilent then
    Result := MsgBox(CustomMessage('CloseApp'), mbConfirmation, MB_OKCANCEL) = IDOK;
end;

// Auto-update (silent, /UPDATE): start the new version again as the logged-in user.
// Setup runs elevated; launching through explorer.exe hands the start to the (non-elevated) desktop shell.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and IsUpdate then
  begin
    Log('Update finished - restarting DynamicBay');
    if not Exec(ExpandConstant('{win}\explorer.exe'), AddQuotes(ExpandConstant('{app}\DynamicBay.exe')), '', SW_SHOWNORMAL, ewNoWait, Code) then
      Log('Restart via explorer failed: ' + IntToStr(Code));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\DynamicBay'), True, True, True);
  end;
end;

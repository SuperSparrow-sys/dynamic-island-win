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
german.CloseAppUninstall=DynamicBay läuft noch. Bitte über das Tray-Symbol beenden und dann Wiederholen klicken.
english.CloseAppUninstall=DynamicBay is still running. Please quit it from the tray icon, then click Retry.

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
; Certificate and identity package are set up in CurStepChanged (ssPostInstall), before the app starts.
; Start as the logged-in user (not elevated).
Filename: "{app}\DynamicBay.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Restore Windows banners, autostart, Claude hooks and remove the identity package.
Filename: "{app}\DynamicBay.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "DynamicBayCleanup"
; Every copy of the signing certificate (each update may have added another one), only this exact subject in this store.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object {{ $_.Subject -eq 'CN=DynamicBay Open Source' } | Remove-Item -Force"""; Flags: runhidden waituntilterminated; RunOnceId: "DynamicBayCert"
; Firewall rules of the link between paired devices (added in AddFirewallRules).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DynamicBay Link"""; Flags: runhidden waituntilterminated; RunOnceId: "DynamicBayFirewall"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DynamicBay Link (Tailscale)"""; Flags: runhidden waituntilterminated; RunOnceId: "DynamicBayFirewallTailscale"

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
  if IsUpdate then
  begin
    i := 0;
    while CheckForMutexes('DynamicBay.SingleInstance') and (i < 40) do
    begin
      Sleep(250);
      i := i + 1;
    end;
  end;
  if CheckForMutexes('DynamicBay.SingleInstance') and not WizardSilent then
    Result := MsgBox(CustomMessage('CloseApp'), mbConfirmation, MB_OKCANCEL) = IDOK;
end;

// Link between paired DynamicBays (docs/ARCHITECTURE.md, "Link"): TCP 43823 for the encrypted connection, UDP 43824 for
// the announcements. Private and domain networks, and the Tailscale adapter (any profile) for Tailscale addresses.
// Deleted first, so updates never pile up copies of the same rule.
procedure AddFirewallRules;
var
  Code: Integer;
  Netsh, Prog, Rule: String;
begin
  Netsh := ExpandConstant('{sys}\netsh.exe');
  Prog := ' dir=in action=allow program="' + ExpandConstant('{app}\DynamicBay.exe') + '" enable=yes';
  Exec(Netsh, 'advfirewall firewall delete rule name="DynamicBay Link"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Netsh, 'advfirewall firewall delete rule name="DynamicBay Link (Tailscale)"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Rule := 'advfirewall firewall add rule name="DynamicBay Link"' + Prog + ' profile=private,domain';
  if not Exec(Netsh, Rule + ' protocol=TCP localport=43823', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Log('Firewall rule (TCP) failed: ' + IntToStr(Code));
  if not Exec(Netsh, Rule + ' protocol=UDP localport=43824', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Log('Firewall rule (UDP) failed: ' + IntToStr(Code));
  // Tailscale in any network profile, but only on the Tailscale adapter: 100.64.0.0/10 alone would also match
  // carrier-grade NAT (mobile hotspots, some providers). netsh cannot bind a rule to an adapter, PowerShell can.
  // Without a Tailscale adapter (not installed) the rule is skipped; the private/domain rule above still works.
  Rule := '-NoProfile -NonInteractive -Command "foreach ($r in @(@(''TCP'',43823),@(''UDP'',43824))) { ' +
    'New-NetFirewallRule -DisplayName ''DynamicBay Link (Tailscale)'' -Direction Inbound -Action Allow -Enabled True ' +
    '-Program ''' + ExpandConstant('{app}\DynamicBay.exe') + ''' -Profile Any -InterfaceAlias ''Tailscale'' ' +
    '-RemoteAddress 100.64.0.0/10,fd7a:115c:a1e0::/48 -Protocol $r[0] -LocalPort $r[1] -ErrorAction Stop | Out-Null }"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Rule, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Log('Firewall rule (Tailscale) not added, no Tailscale adapter?: ' + IntToStr(Code));
end;

// Auto-update (silent, /UPDATE): start the new version again as the logged-in user.
// Setup runs elevated; launching through explorer.exe hands the start to the (non-elevated) desktop shell.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  App: String;
begin
  if CurStep = ssPostInstall then
    AddFirewallRules;
  // Identity package (needed for notification access): trust its signing certificate and register the package for
  // the logged-in user now, while the app is closed. The running app could only defer the registration, and a
  // deferred one loses the external location - Windows then refuses to start the exe (0x80073CF9).
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('notifications') then
  begin
    App := ExpandConstant('{app}');
    if not Exec(ExpandConstant('{sys}\certutil.exe'), '-f -addstore TrustedPeople "' + App + '\DynamicBay.cer"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Log('certutil failed: ' + IntToStr(Code));
    if not ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Add-AppxPackage -Path ''' + App + '\DynamicBay.msix'' -ExternalLocation ''' + App + ''' -ForceUpdateFromAnyVersion"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Log('Identity package registration failed: ' + IntToStr(Code))
    else
      // Registered with the shipped certificate: drop the older copies earlier builds left in TrustedPeople.
      Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$keep = (New-Object Security.Cryptography.X509Certificates.X509Certificate2 ''' + App + '\DynamicBay.cer'').Thumbprint; ' +
        'Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq ''CN=DynamicBay Open Source'' -and $_.Thumbprint -ne $keep } | Remove-Item -Force"',
        '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  if (CurStep = ssPostInstall) and IsUpdate then
  begin
    Log('Update finished - restarting DynamicBay');
    if not Exec(ExpandConstant('{win}\explorer.exe'), AddQuotes(ExpandConstant('{app}\DynamicBay.exe')), '', SW_SHOWNORMAL, ewNoWait, Code) then
      Log('Restart via explorer failed: ' + IntToStr(Code));
  end;
end;

// A running DynamicBay keeps its files open and would switch the banners off again: ask to quit it first.
// No AppMutex entry - that would also stop the silent auto-update, which DynamicBay starts while still running.
function InitializeUninstall: Boolean;
var
  i: Integer;
begin
  Result := True;
  if UninstallSilent then
  begin
    i := 0;
    while CheckForMutexes('DynamicBay.SingleInstance') and (i < 40) do
    begin
      Sleep(250);
      i := i + 1;
    end;
    exit;
  end;
  while CheckForMutexes('DynamicBay.SingleInstance') do
    if MsgBox(CustomMessage('CloseAppUninstall'), mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
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

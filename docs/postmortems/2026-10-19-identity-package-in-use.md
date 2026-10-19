# 2026-10-19: Nach dem Update auf 1.1.12 startet DynamicBay nicht mehr aus dem Startmenü

## Was passiert ist

Nach dem Update von 1.1.11 auf 1.1.12 lief DynamicBay zwar weiter, aber jeder neue Start zeigte „Diese App kann nicht
geöffnet werden … Erweiterte Optionen … Reparieren“. `Get-AppxPackage DynamicBay` meldete noch 1.1.11 mit Status
`Modified, NeedsRemediation`, die Dateien in `C:\Program Files\DynamicBay` waren schon 1.1.12.

## Ursache

- Der Installer kopierte die Dateien bis 13:04:58. Um 13:04:59 wurde DynamicBay gestartet, über Explorer und ohne
  Argumente, also vermutlich per Klick auf Taskleiste oder Startmenü. Die Insel war zu diesem Zeitpunkt seit 17 s weg.
  Der Neustart durch den Installer kam erst um 13:05:01.
- Erst danach, um 13:05:01, rief der Installer `Add-AppxPackage` auf (PowerShell braucht etwa 3 s zum Starten).
  Windows verweigerte die Aktualisierung, weil die App lief (0x80073D02, AppXDeploymentServer/Operational,
  Ereignisse 638, 419, 401). Das Zurücksetzen auf 1.1.11 schlug ebenfalls fehl (Ereignis 751, „Das Paket muss neu
  installiert werden“).
- Der Update-Test findet das nicht. Er startet die App erst, wenn der Installer fertig ist.

## Behebung

- Der Installer schließt unmittelbar vor der Registrierung jede DynamicBay aus `{app}` (`CloseRunningApp`) und
  versucht die Registrierung ein zweites Mal, wenn sie trotzdem scheitert (`RegisterIdentity`). Der Neustart nach dem
  Update folgt wie bisher.
- Akut auf einem betroffenen PC: DynamicBay beenden (Infobereich → Beenden), dann als der angemeldete Benutzer
  `Add-AppxPackage -Path "C:\Program Files\DynamicBay\DynamicBay.msix" -ExternalLocation "C:\Program Files\DynamicBay" -ForceUpdateFromAnyVersion`
  Alternativ den Installer des nächsten Releases ausführen.

## Prüfen

1. Test `Installer_closes_a_copy_started_during_the_update_before_it_registers_the_identity_package`.
2. Nach dem nächsten Update: `Get-AppxPackage DynamicBay` zeigt die neue Version mit Status `Ok`.

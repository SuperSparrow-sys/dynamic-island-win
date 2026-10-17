# 2026-10-11: Nach dem Update auf 1.0.17 startet DynamicBay nicht mehr

## Was passiert ist

Nach dem Update von 1.0.16 auf 1.0.17 zeigte Windows beim Start von `DynamicBay.exe` nur „Fehler bei der
Installation. Wenden Sie sich an den Softwarehersteller.“ Die App lief nicht, also kam auch kein weiteres Update an.

## Ursache

- Fehleranalyse Runde 1 (Befund 33, Commit `cba9fe8`) ließ die laufende App eine neuere Version des
  Identitäts-Pakets registrieren: `AddPackageByUriAsync` mit `DeferRegistrationWhenPackagesAreInUse`, weil das Paket
  gerade benutzt wird. Windows übernimmt so eine Registrierung erst, wenn die App beendet ist.
- Die verzögerte Registrierung verliert den externen Ort (`ExternalLocationUri`). Beim Übernehmen scheitert sie mit
  0x80073CF9 („muss mit einem externen Speicherort installiert werden“, Ereignisanzeige →
  AppXDeploymentServer/Operational, Ereignis 404).
- Ab dann bleibt die Registrierung hängen, und Windows verweigert jeden Start der exe (CreateProcess-Fehler 15609 =
  0x3CF9).
- Die Tests und die CI fanden das nicht: Sie prüfen nie die installierte App über ein Update hinweg.

## Behebung

- 1.0.18: Der Installer vertraut dem Zertifikat und registriert `DynamicBay.msix` mit dem Installationsordner als
  externem Ort, solange die App geschlossen ist (`installer/DynamicBay.iss`, `CurStepChanged`). Das repariert auch
  hängende Installationen. Die App registriert nur noch selbst, wenn sie gar keine Identität hat.
- Akut auf einem betroffenen PC (als der angemeldete Benutzer):
  `Add-AppxPackage -Path "C:\Program Files\DynamicBay\DynamicBay.msix" -ExternalLocation "C:\Program Files\DynamicBay" -ForceUpdateFromAnyVersion`

## Was sich deshalb ändert

- `tools/update-smoke.ps1` installiert ältere Releases, aktualisiert wie der In-App-Updater auf den neuen Installer
  und prüft nach jedem Schritt, dass die App startet und `identity: True` meldet. `release.yml` veröffentlicht nur,
  wenn das besteht. Von Hand: Workflow „Update smoke test“, mit `release` = `1.0.17` muss er fehlschlagen.
- Ein festes Signaturzertifikat statt eines neuen pro Build (`tools/new-signing-cert.ps1`, nach der Einrichtung entfernt, in der Git-Historie). Der Installer räumt die
  alten Kopien aus TrustedPeople auf.
- `CLAUDE.md` verbietet die verzögerte Registrierung.

## Prüfen

1. Workflow „Update smoke test“ ohne Eingaben: muss grün sein (1.0.15 → 1.0.16 → aktueller Stand).
2. Mit `release` = `1.0.17`: muss rot sein (zeigt, dass der Test den Fehler findet).
3. Auf einem PC mit 1.0.17: Installer von 1.0.18 ausführen, App startet, Log zeigt `1.0.18 starting (identity: True)`.

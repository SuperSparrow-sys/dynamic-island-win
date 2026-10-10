fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |fertig (Live-Test) |# Anforderungen und Status

Alle Wünsche aus der Planung und Entwicklung, mit Stand der Umsetzung.
Legende: **fertig**, **in Arbeit**, **offen**, **Live-Test** (gebaut, wird zum Schluss gemeinsam mit echten Konten geprüft).

## Grundlagen
| # | Anforderung | Status |
|---|---|---|
| 1 | Dynamic Island für Windows, dynamisch, orientiert an NotchNook (Mac) | fertig |
| 2 | Apple-ähnliches Design und Spring-Animationen, konzentrische Rundungen, keine ovalen Knöpfe | fertig |
| 3 | Keine Emojis: SVG-Icons (Lucide, Simple Icons) oder selbst gestaltet | fertig |
| 4 | Passende, einheitliche Schrift überall (Inter), auch im Claude-Bereich und in Menüs | fertig |
| 5 | Design selbst per Screenshots prüfen, regelmäßig Screenshots an den Nutzer | fortlaufend |

## Insel
| # | Anforderung | Status |
|---|---|---|
| 6 | Leerlauf: breiter, flacher, langgezogener Strich | fertig |
| 7 | Mehrere Aktivitäten nebeneinander in einer Insel | fertig |
| 8 | Mit der Maus verschiebbar, Ausrichtung/Aufklapp-Richtung je nach Position (Kante, Ecke) | fertig |
| 9 | Verstecken: Tastenkürzel, Tray, Auto-Hide, Vollbild, App-Ausnahmen, Nicht stören | fertig |
| 10 | Ebene wählbar: schwebend über allem oder nur auf dem Desktop | fertig (Desktop-Modus: Live-Test) |
| 11 | Mehrere Bildschirme: Insel auf jeden Bildschirm ziehbar oder auf allen gespiegelt | fertig |
| 12 | Fenster ragt nie auf den Nachbarbildschirm (Taskleiste auf Bildschirm 2 verschwand) | fertig, nicht mehr reproduzierbar |
| 13 | Komplett anpassbar: Widgets wählen/sortieren (auch ohne Timer), Uhr, Inhalte der kompakten Insel, Tabs | fertig |
| 14 | Jedes Modul abschaltbar, auch das Claude-Plugin | fertig |
| 15 | Einstellungsseite | fertig |

## Funktionen
| # | Anforderung | Status |
|---|---|---|
| 16 | Dateiablage (Dateien rein- und rausziehen) | fertig |
| 17 | Screenshots über das Snipping Tool, Zwischenablage (Bilder und Text) in der Insel sichtbar, 50 Einträge, bleibt nach Neustart | fertig |
| 18 | Alle Windows-Banner und den Mitteilungston abschalten, solange DynamicBay läuft (Ausnahmen möglich) | fertig |
| 19 | Spotify: lokal ohne Login, Musiksymbol in der Insel; optional Login, Anmeldung bleibt gespeichert | fertig (Login: Live-Test) |
| 20 | Apple Music: Anzeige und Steuerung | fertig |
| 21 | Apple Music mit Login | offen (braucht eigenes Apple-Entwicklerkonto, siehe docs/INTEGRATIONS.md) |
| 22 | Benachrichtigungen aller Apps in der Insel | fertig, nach Installation getestet (Identität registriert, 26 Mitteilungen gelesen) |
| 23 | WhatsApp und andere Messenger: Nachrichten-Widget (Antworten technisch nicht möglich) | fertig (Live-Test) |
| 24 | Kalender: Google und Apple/iCloud mit Anmeldung, dazu ICS-Links (Outlook) | fertig (Live-Test) |
| 25 | iCloud: Speichern in iCloud Drive, dazu OneDrive (alle Konten), Google Drive, Dropbox | fertig |
| 26 | Schnellstart-Apps (auch Discord, WhatsApp) aus Liste oder Ordner wählen, nur ausgewählte in der Insel | fertig |
| 27 | Einheitliche App-Kacheln im Insel-Design (dunkel/grau, gut erkennbar), große Symbol-Palette bei Bedarf geladen | fertig |
| 28 | Claude: Claude-Code-Sitzungen, Agenten-Status, Remote Control, Sitzungen aller PCs | fertig (Hooks: Live-Test) |
| 29 | Weitere Apple-Funktionen: Akku, Bluetooth, Timer/Pomodoro, Uhr, System | fertig |

## Auslieferung
| # | Anforderung | Status |
|---|---|---|
| 30 | Öffentliches GitHub-Repo (MIT) SuperSparrow-sys/dynamic-island-win | fertig, Vorab-Version v1.0.0-beta.1 mit Installer veröffentlicht |
| 31 | Installer (Inno Setup, klassisch) als .exe für andere PCs, bringt alles mit (.NET inklusive) | fertig, auf diesem PC installiert und getestet |
| 32 | Automatischer Build und Release über GitHub Actions | fertig |
| 33 | Tests (34 Unit-Tests, Snapshot- und Live-Test-Skripte) | fertig |
| 34 | Doku zu Struktur und Fehlersuche (ARCHITECTURE, TROUBLESHOOTING, DEVELOPMENT, INTEGRATIONS) | fertig |
| 35 | Abschluss-Live-Check gemeinsam: OneDrive, Spotify, Mitteilungen, Kalender, Bluetooth, Claude | offen |

## Büro und Arbeit (Oktober 2026)
Reihenfolge: zuerst alles ohne Anmeldung (Paket A), dann Microsoft 365 mit einer einmaligen Anmeldung (Paket B),
dann die optionale Zeiterfassung (Paket C).

### Paket A – ohne Anmeldung
| # | Anforderung | Status |
|---|---|---|
| 36 | Beitreten-Knopf: Teams- und Zoom-Links im Termin erkennen; Einblendung kurz vor Beginn und Knopf in der Kalender-Karte | offen |
| 37 | Meeting-Modus: Teams/Zoom nutzt das Mikrofon → „Nicht stören“, Meetingdauer, Stumm- und Kamera-Status; danach zurück | offen |
| 38 | Bildschirmfreigabe oder Präsentation: Insel wird zum Strich (auch bei Musik), Mitteilungen ohne Text | offen |
| 39 | Text aus Screenshots (OCR, Windows-eigene Texterkennung) kopieren | offen |
| 40 | Fokus-Sitzung: Pomodoro schaltet „Nicht stören“ mit ein, kleine Tagesübersicht | offen |
| 41 | Dock-Profile: Position der Insel je Bildschirm-Anordnung merken und beim An-/Abdocken wiederherstellen | offen |

### Paket B – Microsoft 365 (eine Anmeldung)
| # | Anforderung | Status |
|---|---|---|
| 42 | Microsoft-Anmeldung (Microsoft Graph), Anmeldung bleibt gespeichert | offen |
| 43 | Outlook- und Exchange-Kalender in der Kalender-Karte (mit Teams-Links für den Beitreten-Knopf) | offen |
| 44 | Teams-Status sehen und umschalten (optional mit dem Meeting-Modus gekoppelt) | offen |
| 45 | Microsoft To Do als Widget: heutige Aufgaben, abhaken | offen |

### Paket C – optional
| # | Anforderung | Status |
|---|---|---|
| 46 | Zeiterfassung als Widget: Start/Stopp pro Projekt, CSV-Export | fertig (Live-Test) |

### Paket D – Kontakte und Anrufe
| # | Anforderung | Status |
|---|---|---|
| 47 | Kontakte-Widget: häufige Kontakte (Microsoft-Kontakte und Personen, mit denen man oft arbeitet) mit Knöpfen für Teams-Chat, Teams-Anruf und Telefon | offen |
| 48 | Telefonanrufe über Smartphone-Link starten (tel:-Links gehen an das verknüpfte Handy) | offen |
| 49 | Eingehende Anrufe (Smartphone-Link, Teams) als Einblendung in der Insel; ein Telefonat zählt wie ein Meeting (still) | offen |
| 50 | Lautstärkeregler nur für die Musik in der Medienkarte: Spotify über die eigene Lautstärke (Web API), Apple Music und andere Player über die App-Lautstärke im Windows-Mixer | fertig (Live-Test) |

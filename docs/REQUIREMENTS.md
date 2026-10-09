# Anforderungen und Status

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
| 18 | Windows-Banner (z. B. Snipping Tool) unterdrücken, solange DynamicBay läuft | fertig |
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

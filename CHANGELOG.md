# Änderungen

Die Release-Notizen auf GitHub entstehen aus den Commits; hier steht, was seit dem letzten Release dazukam.

## 1.1.22 (18.11.2026)

- Kopfleiste: Windows-Systemschrift statt Inter, angeheftete Apps in ihren eigenen Farben wie in der Taskleiste,
  Bluetooth immer sichtbar (mit Punkten, wenn ein Gerät verbunden ist). Angeheftete Apps wie Obsidian oder VS Code
  finden ihr offenes Fenster auch über das Programm hinter dem Startmenü-Eintrag. Auf dem Desktop steht kein App-Name
  mehr (vorher etwa „Dell Display and Peripheral Manager“). Die Leiste verschwindet nur noch bei Filmen, Spielen und
  Präsentationen im Vollbild, nicht mehr, wenn die Maus über CursorHop auf einen anderen PC wechselt.
- Kopfleiste → „Auswahl“: je Bildschirm ein Schalter, gemerkt auf diesem PC. Beim ersten Wählen ist der Bildschirm
  der Einstellungen eingeschaltet.

## 1.1.21 (18.11.2026)

- Ton an das andere Gerät: Im lokalen Netz kommt der Ton etwa 40 ms früher (Puffer 60 statt 100 ms). Wird die
  Verbindung unruhig, etwa im WLAN neben Bluetooth-Kopfhörern, wächst der Puffer von selbst, statt immer wieder
  auszusetzen.
- Neue Kopfleiste wie am Mac (Einstellungen → Darstellung → Kopfleiste): links ein Windows-Menü (Einstellungen,
  Task-Manager, Energie sparen, Neustart, Sperren), die Apps aus Schnellstart als Taskleistenersatz und die aktive App
  mit Fenstermenü; rechts das laufende Lied (Klick öffnet den Player), Bluetooth, Akku, Netz, Lautstärke, Datum und
  Uhrzeit, jedes einzeln abschaltbar. Windows hält den Streifen frei wie für die Taskleiste, maximierte Fenster enden
  darunter. Die Leiste nimmt als weichen Verlauf die Farben des Fensters oder Hintergrunds darunter auf, die Schrift
  wird hell oder dunkel passend dazu. Wahlweise bei der Insel, auf den Zweitbildschirmen oder überall; jeder PC merkt
  sich das selbst, ein Export nimmt es nicht mit. An der Leiste öffnet die Insel erst nach kurzem Verweilen und zeichnet
  keinen Schatten. Angeheftete Apps holen ein offenes Fenster nach vorn wie in der Taskleiste. Bei Filmen und Spielen im
  Vollbild verschwindet die Leiste auf diesem Bildschirm.

## 1.1.20 (25.10.2026)

1.1.19 wurde nicht veröffentlicht (der Release-Lauf fand den Fehler beim Ton unten).

- Ton an das andere Gerät: Schaltete das empfangende Gerät den Ton ab, meldete das sendende manchmal „Verbindung
  verloren“ statt des Grunds.
- Neues Widget „Wetter“ (Open-Meteo): jetzt und die nächsten Stunden, breit dazu die Kurve der nächsten 24 Stunden und
  drei Tage. Ort, °C oder °F und Größe unter Einstellungen → Kalender und Zeit → Wetter.

## 1.1.18 (24.10.2026)

- Mikrofon: Seit den Windows-Updates vom Oktober (26H2) bemerkte DynamicBay ein laufendes Mikrofon nicht mehr (Anzeige,
  Anrufmodus, „Stumm?“). Es kommt jetzt aus den Aufnahme-Sitzungen von Windows; Sprechpausen bis 5 s zählen mit.
- Kamera: Seit den Windows-Updates vom 14.10. (26H2) erkannte DynamicBay eine laufende Kamera nicht mehr; die Automation
  „Kamera an“ blieb wirkungslos. Die Kamera kommt jetzt von Media Foundation und zählt erst nach 3 s, damit Apps, die
  sie kurz öffnen (LogiTune), das Panel nicht flackern lassen.
- Automationen: Die Meldung „Zuhause“ des Servers legte nach dem Update ein zusätzliches, leeres iPhone-Fokus-Profil
  „Zuhause“ an; das entfällt und wird beim Start entfernt.

## 1.1.17 (24.10.2026)

1.1.16 wurde nicht veröffentlicht (der Release-Lauf fand den Fehler bei der Lautstärke unten).

- Ton an das andere Gerät: Wurde dort gleich nach dem Start die Lautstärke verstellt, konnte der Startwert des
  sendenden Geräts sie danach wieder auf voll setzen.
- Automationen statt Fokus-Profile (Einstellungen → Ruhe und Fokus): Zu Hause, Unterwegs, Kamera an, Fokus-Sitzung
  oder Pomodoro, Nicht stören am PC und iPhone-Fokus. Jede schaltet, solange sie gilt, was du
  einstellst, mit einem Satz zu jeder Einstellung; die obere gewinnt. Die Anzeigetafel geht aus, wenn du weg bist
  (nach einer Minute, kurze WLAN-Abbrüche zählen nicht), in 10 s, sobald die Kamera an ist, und im Fokus Schlafen, und
  danach wieder nach Wochenplan. Unterwegs stellt ein grünes Teams auf grau (offline), Rot und Gelb bleiben. Deine
  Fokus-Profile werden übernommen.

## 1.1.15 (24.10.2026)

- Anzeigetafel: Ein Fokus kann das Panel auch auf den Wochenplan stellen und es an seinem Ende ausschalten, bis
  der Fokus wiederkommt. Damit geht das Panel aus, wenn du das Haus verlässt, und an, wenn du heimkommst
  (Fokus-Profil → „Anzeigetafel am Ende“).

## 1.1.14 (24.10.2026)

- iPhone-Fokus: Eine Kurzbefehle-Automation meldet Arbeit, Schule, Schlafen, Zuhause oder jeden anderen Fokus an
  DynamicBay (Einstellungen → Ruhe und Fokus, Anleitung in docs/INTEGRATIONS.md). Jeder Fokus schaltet, was du
  einstellst: Nicht stören, Mitteilungen ohne Text, Insel ausblenden, Teams-Status, stumme Apps und ausgeblendete
  Widgets. Ein gekoppelter Laptop übernimmt den Fokus. Der Installer öffnet dafür Port 43825 in der Firewall, nur für
  Heimnetz und Tailscale.
- Anzeigetafel: Ein neues Widget schaltet das Panel der Anzeigetafel an und aus und stellt die Helligkeit ein, bis
  zum nächsten Zeitpunkt im Wochenplan. In jedem Fokus-Profil lässt sich festlegen, ob das Panel dimmt oder ausgeht,
  solange der Fokus läuft (Einstellungen → Geräte → Anzeigetafel, erst nach „Anzeigetafel einbinden“).
- DynamicBay-Server: PC und Laptop lassen sich mit dem eigenen Server auf dem Homeserver koppeln (Einstellungen →
  Geräte). Er teilt Fokus-Profile und stumme Apps, nimmt den iPhone-Fokus auch an, wenn der PC aus ist, und schaltet
  die Anzeigetafel. Jedes Gerät kann seine eigene Liste behalten. Ohne Server bleibt alles wie bisher.

## 1.1.13 (19.10.2026)

- Installer: Wird DynamicBay während eines Updates gestartet, scheiterte die Registrierung des Identitäts-Pakets, und
  jeder weitere Start meldete „Diese App kann nicht geöffnet werden“. Der Installer schließt eine solche Kopie jetzt
  vor der Registrierung und versucht es bei einem Fehler ein zweites Mal. Er repariert auch eine schon betroffene
  Installation.
- Audio-Karte: Läuft der Ton auf mehreren Geräten, etwa am Laptop über die Kopplung, über Windows „Geteiltes Audio“
  oder auf zwei Ausgaben gleichzeitig, steht oben „Lautstärke“: ein Regler für alle, der das Verhältnis beibehält, und
  einer je Gerät. Am gekoppelten Gerät ändert er nur den weitergeleiteten Ton und wird je Gerät gemerkt; das Gerät
  zeigt den empfangenen Ton ebenfalls mit Regler, beide Seiten folgen einander. Ein Gerät mit älterer Version zeigt
  „ab 1.1.13“. Stummgeschaltete Ausgaben erscheinen gedimmt, ihr Regler hebt die Stummschaltung auf.

## 1.1.12 (19.10.2026)

- Medien: Laufen mehrere Player gleichzeitig (Spotify und YouTube im Browser), wird die Medienkarte zum Stapel mit
  einem Punkt pro Player. Scrollen über der Karte wechselt die Seite, darunter steht die App. Die gewählte Seite bleibt,
  bis ihr Player verschwindet oder ein anderer zu spielen beginnt. Ein kurzer Halt beim Liedwechsel zählt nicht.
- Mitteilungen vom Handy öffnen beim Klick die passende App unter Windows statt Smartphone-Link: Gmail und Mail in
  Outlook, Claude in Claude, WhatsApp, Teams, Spotify und andere in ihrer Desktop-App. Ohne passende App bleibt es bei
  Smartphone-Link.
- Das Mausrad über dem Lautstärkebalken der Medienkarte ändert wieder die Lautstärke, statt die Widget-Reihe zu
  scrollen.

## 1.1.11 (19.10.2026)

- Claude/Rubus: Fragt Claude am Ende einer Antwort etwas oder wartet auf eine Auswahl, zeigt die Session „Wartet auf
  dich“ wie in der Claude-App und meldet sich mit „Claude braucht dich“ und dem Satz der Frage (ab Rubus 0.2.1). Die
  nächste Frage derselben Session meldet sich erneut.
- Claude/Rubus: Sessions, die mit einem Fehler stehen bleiben, zeigen „Fehler“ und melden sich mit „Claude ist stehen
  geblieben“. Mit Esc unterbrochene Sessions zeigen „Unterbrochen“ und lösen kein „Claude ist fertig“ mehr aus. Die
  Zeitangabe und die Reihenfolge folgen dem letzten Ereignis der Session.

## 1.1.10 (17.10.2026)

- Claude: Sessions aus Rubus. Einstellungen → Apps und Skripte → Claude → *Sessions aus Rubus* (Vorgabe aus). Rubus
  meldet seine Claude-Sessions an `127.0.0.1:43822/rubus/`; sie stehen in der Claude-Karte neben den lokalen und
  zählen in der kompakten Anzeige mit. Freigaben erscheinen als Frage mit *Erlauben* / *Ablehnen* (oder nur
  *In Rubus öffnen*), dazu „Claude braucht dich“ und „Claude ist fertig“. Details: docs/INTEGRATIONS.md#rubus.
- Claude: überall das Claude-Zeichen statt der Funken (Simple Icons). In der kompakten Insel atmet es, statt sich zu
  drehen.
- Einblendungen mit Frage können einen einzigen Knopf haben.
- Testinstanzen hören mit `DYNAMICBAY_HOOK_PORT` auf einem eigenen Port auf Hooks und Rubus.

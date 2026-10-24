# Änderungen

Die Release-Notizen auf GitHub entstehen aus den Commits; hier steht, was seit dem letzten Release dazukam.

## Unveröffentlicht

- iPhone-Fokus: Eine Kurzbefehle-Automation meldet Arbeit, Schule, Schlafen, Zuhause oder jeden anderen Fokus an
  DynamicBay (Einstellungen → Ruhe und Fokus, Anleitung in docs/INTEGRATIONS.md). Jeder Fokus schaltet, was du
  einstellst: Nicht stören, Mitteilungen ohne Text, Insel ausblenden, Teams-Status, stumme Apps und ausgeblendete
  Widgets. Ein gekoppelter Laptop übernimmt den Fokus. Der Installer öffnet dafür Port 43825 in der Firewall, nur für
  Heimnetz und Tailscale.
- Anzeigetafel: Ein neues Widget schaltet das Panel der Anzeigetafel an und aus und stellt die Helligkeit ein, bis
  zum nächsten Zeitpunkt im Wochenplan. In jedem Fokus-Profil lässt sich festlegen, ob das Panel dimmt oder ausgeht,
  solange der Fokus läuft (Einstellungen → Geräte → Anzeigetafel).

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

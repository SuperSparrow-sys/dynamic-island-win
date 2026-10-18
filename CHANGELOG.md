# Änderungen

Die Release-Notizen auf GitHub entstehen aus den Commits; hier steht, was seit dem letzten Release dazukam.

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

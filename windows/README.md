# Multi Claude für Windows · WPF-Edition

Based on Multi Claude, created by Richard KPL. Axel (AFielen) entwickelt und pflegt die Codex-Edition. Der C#-Port basiert auf der Python-Version 2.3.0, Stand `68a1bd07eddbf54eba8349323b0cc4ac5183c501`. [Originalprojekt](https://github.com/RichardKPL/multi-claude) · [Multi Claude Codex](https://github.com/AFielen/Multi-Claude-Codex)

## Start

Das vollständige win-x64-Paket in einen beschreibbaren lokalen Ordner entpacken und **Multi Claude.exe** starten. Die .NET-Desktop-Laufzeit ist im Paket enthalten. Der Manager legt beim ersten Start seine `perfis.db` neben der EXE an. Über **Neues Profil** lassen sich Konten anlegen. **Strg+K** sucht über alle Profile. Bei der Anmeldung den Rückgabelink aus dem Browser im Dialog des gewählten Profils einfügen und bestätigen.

Eine eigene Testbasis lässt sich beim Start ausdrücklich angeben. Der folgende PowerShell-Aufruf verwendet einen separaten Ordner im Build-Verzeichnis.

```powershell
& '.\dist\wpf-win-x64\Multi Claude.exe' --manager-home "$PWD\build\wpf-testkatalog"
```

**Übersicht** zeigt den gemeldeten Fünf-Stunden- und Wochenverbrauch mit Rücksetzzeitpunkt. Dazu im isolierten Profil die Limitanzeige einschalten und Claude Code verwenden. Sobald dessen Statuszeile einen Messwert liefert, kann der Manager ihn anzeigen. Der Tab **Claude Code** summiert die in der lokalen Profilhistorie gespeicherten Token. Die Schaltfläche zum Nachladen lässt die aktive Ansicht stehen. Bei gemeinsam genutzter Code-Konfiguration lässt sich Verbrauch keinem einzelnen Profil zuordnen.

**CPU und RAM** werden etwa alle drei Sekunden aktualisiert, solange der Manager geöffnet ist. Die Anzeige umfasst Claude Desktop sowie isolierte Claude-Code-Sitzungen, die diese Manager-Version über die Terminal-Schaltfläche oder den kopierten Profilbefehl gestartet hat. Laufende Unterprozesse wie Test-Worker zählen zum jeweiligen Profil. Unabhängig gestartete Terminals und gemeinsam genutzte Code-Sitzungen lassen sich keinem Profil eindeutig zuordnen.

Der CPU-Wert gibt den Anteil an der verfügbaren CPU-Zeit über alle logischen Prozessoren an. Er erscheint ab der zweiten Messung. RAM zeigt den privaten physischen Arbeitsspeicher in MiB oder GiB. Auf älteren Windows-Versionen verwendet der Manager den gesamten Arbeitssatz der Prozesse, der auch gemeinsam genutzte Speicherseiten enthält. Ein Strich kennzeichnet einen fehlenden Messwert.

## Entwicklung und Prüfung

Im Repository-Stamm auf Windows x64 mit .NET-10-SDK ausführen. Die Tests benötigen eine interaktive Windows-Sitzung und Python mit Tk für die Referenzdaten. Der Rauchtest kompiliert mit dem Windows-.NET-Framework-Compiler einen fiktiven Desktop-Starter und zeichnet dessen Startparameter in einem temporären Ordner auf. Die C#-Anwendung selbst benötigt kein Python.

```powershell
dotnet run --project windows/Checks/Checks.csproj -c Release
dotnet publish windows/MultiClaude/MultiClaude.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist/wpf-win-x64
python windows/Checks/smoke_published.py "dist/wpf-win-x64/Multi Claude.exe"
```

Die Prüfungen decken SQLite-Übernahme, Bearbeiten und Sortieren, Schutz externer Ordner, Tastenkürzel, Windows-Verknüpfungen, Code-Ressourcenfreigabe, Statuszeile und WPF-Bedienung ab. Der Rauchtest öffnet zwei von Python 2.3.0 registrierte Profile vor und nach einem vollständigen Prozessneustart. Er vergleicht die ursprünglichen IDs, die gespeicherten Pfade und die tatsächlichen Desktop- und Code-Pfade beider Kindprozesse.

## Daten bei einem späteren Wechsel

Der vorhandene 2.3.0-Katalog kann im selben Manager-Ordner weiterverwendet werden. `perfis.id`, `perfis.pasta` und `profile_options.code_key` bleiben den Profilen zugeordnet. Relative Desktop-Pfade beziehen sich weiterhin auf den Katalogordner. Der Start eines isolierten Profils verwendet `claude-code\<code_key>`. Die Auswahl, Fenstergeometrie, Sprache, Darstellung, Reihenfolge und letzte Öffnung werden in SQLite gespeichert. Windows-Verknüpfungen verwenden den ursprünglichen Katalog und die stabile Code-Kennung. Beim Start protokolliert der Manager seine Version in `log.txt` und entfernt die drei veralteten Einträge `login_pasta`, `login_ok` und `login_url` aus `config`, falls sie vorhanden sind.

Bei **Import aus einem anderen Katalog** bleibt dessen Code-Basis als `wpf_code_home_<code_key>` in der Zieltabelle `config` gespeichert. Bei einer Kollision numerischer IDs erhält der importierte Eintrag eine freie lokale Zeilennummer. Diese Zusammenführung ist eine separate Nutzeraktion. Für die Übernahme **am bisherigen Speicherort** entfällt sie. Ältere Kataloge erhalten fehlende Python-kompatible Spalten und Profiloptionen. Das Aktivieren der Limitanzeige schreibt erst auf Anforderung in die Code-Konfiguration des ausgewählten Profils.

Das Paket ergänzt die mitgelieferte Bildauswahl unter `gallery\`. Eigene Bilder bleiben unter `profile_pictures\`. Wer die Codex-Edition weiterentwickelt und verteilt, findet beide Autorenhinweise und Projektlinks in [AUTHORS](../AUTHORS). Für die Weitergabe gelten die [GPL-3.0](../LICENSE) und der [Originalhinweis](../NOTICE).

# Multi Claude + Codex für Windows · WPF-Edition

Diese lokal gebaute WPF-Edition verwaltet Claude- und Codex-Konten. Sie basiert auf der Python-Version 2.3.0, Stand `68a1bd07eddbf54eba8349323b0cc4ac5183c501`. Die Download-Schaltflächen im Haupt-README führen zum veröffentlichten Claude-Original. Based on Multi Claude, created by Richard KPL. [Originalprojekt](https://github.com/RichardKPL/multi-claude)

## Start

Das vollständige win-x64-Paket in einen beschreibbaren Benutzerordner wie `Dokumente\Multi Claude` entpacken und **Multi Claude.exe** starten. Für Paket und Katalog einen Ort außerhalb der Codex-Datenordner wählen, auch außerhalb von `%USERPROFILE%\.codex\worktrees` und einem eigens gesetzten `CODEX_HOME`. Die .NET-Desktop-Laufzeit ist im Paket enthalten. Der Manager legt beim ersten Start seine `perfis.db` neben der EXE an. **Strg+K** sucht Profile nach Name oder Anwendung.

- **Codex** unter **Neues Profil → Anwendung** auswählen, einen Namen vergeben und speichern. **Öffnen** startet die installierte Codex-Desktop-App. Dort anmelden oder im Tab **Codex CLI** auf **Anmelden** klicken. Die Desktop-App und das über **Terminal öffnen** gestartete Windows-Terminal nutzen für dieses Profil dasselbe Konto. Für das nächste Konto ein weiteres Profil anlegen.
- **Claude** ist beim Anlegen vorausgewählt. Für die Desktop-Anmeldung den Rückgabelink aus dem Browser im Dialog des gewählten Claude-Profils einfügen und bestätigen.

Jedes neue Codex-Profil beginnt mit eigenen Einstellungen und einer eigenen Anmeldung. Im Manager-Ordner liegen die Desktop-Daten unter `codex-desktop\<uuid>` und das native Windows-`CODEX_HOME` unter `codex-home\<uuid>`. Beide verwenden dieselbe dauerhafte Profilkennung. Die Anmeldung im bisherigen Codex-Standardprofil und dessen Einstellungen werden nicht kopiert. Beim Entfernen des Profils bleibt sein `codex-home\<uuid>` erhalten. WSL und Cloud-Sitzungen verwenden eigene Daten. Beim Verschieben des portablen Managers den gesamten Ordner mitnehmen und anschließend Windows-Verknüpfungen und kopierte Terminalbefehle neu erstellen.

Für einen Test direkt aus dem Repository legt dieser PowerShell-Aufruf den Katalog im temporären Benutzerordner an.

```powershell
& '.\dist\wpf-win-x64\Multi Claude.exe' --manager-home (Join-Path $env:TEMP 'multi-claude-wpf-testkatalog')
```

Bei Claude-Profilen zeigt **Übersicht** den gemeldeten Fünf-Stunden- und Wochenverbrauch mit Rücksetzzeitpunkt. Dazu im isolierten Profil die Limitanzeige einschalten und Claude Code verwenden. Sobald dessen Statuszeile einen Messwert liefert, kann der Manager ihn anzeigen. Der Tab **Claude Code** summiert die in der lokalen Profilhistorie gespeicherten Token. Die Schaltfläche zum Nachladen lässt die aktive Ansicht stehen. Bei gemeinsam genutzter Code-Konfiguration lässt sich Verbrauch keinem einzelnen Profil zuordnen.

**CPU und RAM** werden etwa alle drei Sekunden aktualisiert, solange der Manager geöffnet ist. Die Anzeige umfasst die Desktop-Prozesse beider Anwendungen sowie isolierte Claude-Code- und Codex-CLI-Sitzungen, die diese Manager-Version über die Terminal-Schaltfläche oder den kopierten Profilbefehl gestartet hat. Laufende Unterprozesse wie Test-Worker zählen zum jeweiligen Profil. Unabhängig gestartete Terminals und gemeinsam genutzte Code-Sitzungen lassen sich keinem Profil eindeutig zuordnen.

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

Sobald ein Katalog Codex-Profile enthält, diesen Katalog mit der Windows-Edition **Multi Claude + Codex** öffnen. Frühere Manager-Versionen kennen den Anbietertyp noch nicht.

Der vorhandene 2.3.0-Katalog kann im selben Manager-Ordner weiterverwendet werden. Seine bestehenden Profile werden automatisch als Claude übernommen. `perfis.id`, `perfis.pasta` und `profile_options.code_key` bleiben den Profilen zugeordnet. Relative Desktop-Pfade beziehen sich weiterhin auf den Katalogordner. Der Start eines isolierten Claude-Profils verwendet `claude-code\<code_key>`. Die Auswahl, Fenstergeometrie, Sprache, Darstellung, Reihenfolge und letzte Öffnung werden in SQLite gespeichert. Windows-Verknüpfungen verwenden den ursprünglichen Katalog und die stabile Code-Kennung. Beim Start protokolliert der Manager seine Version in `log.txt` und entfernt die drei veralteten Einträge `login_pasta`, `login_ok` und `login_url` aus `config`, falls sie vorhanden sind.

Bei **Import aus einem anderen Katalog** bleibt dessen Code-Basis als `wpf_code_home_<code_key>` in der Zieltabelle `config` gespeichert. Bei einer Kollision numerischer IDs erhält der importierte Eintrag eine freie lokale Zeilennummer. Diese Zusammenführung ist eine separate Nutzeraktion. Für die Übernahme **am bisherigen Speicherort** entfällt sie. Ältere Kataloge erhalten fehlende Python-kompatible Spalten und Profiloptionen. Das Aktivieren der Limitanzeige schreibt erst auf Anforderung in die Code-Konfiguration des ausgewählten Profils.

Das Paket ergänzt die mitgelieferte Bildauswahl unter `gallery\`. Eigene Bilder bleiben unter `profile_pictures\`. Lizenz und Originalattribution stehen in `LICENSE` und `NOTICE`.

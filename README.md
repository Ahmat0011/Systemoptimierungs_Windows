# SystemOptimizer PRO

Windows-Wartungswerkzeug (WPF, .NET 10) zum Aktualisieren, Bereinigen, Reparieren und
Deinstallieren von Software – mit Live-Protokoll, Fortschrittsanzeige und automatischer
Aktualisierung der Programmlisten.

## Funktionen

| Bereich | Inhalt |
|---|---|
| **Update Center** | Software-Updates über `winget` (Machine-/User-Scope inkl. MS Store) und Hardware-Treiber über die Microsoft Update COM-API (Pflicht- und optionale Treiber) |
| **System-Reparatur** | `DISM /RestoreHealth` und `sfc /scannow` mit Fortschrittsauswertung |
| **Bereinigung & Optimierung** | Temp-Dateien, Papierkorb, Shader-/Thumbnail-Caches, Prefetch, Windows-Update-Downloads, WinSxS-Komponentenspeicher, DNS-Cache – jeder Schritt mit eigenem Zeitlimit |
| **Deinstallieren** | Registry-basierte Programme und Windows-Apps (UWP), getrennte Ansichten und Suchfelder je Tab, optionale Reste-Bereinigung |
| **System-Leistung** | RAM-Working-Sets optimieren, Autostart-Einträge verwalten (inkl. per Task-Manager deaktivierter Einträge) |

Weitere Eigenschaften:

- **Live-Aktualisierung** der Programmlisten über `RegNotifyChangeKeyValue` – Änderungen durch
  Setup-Programme, `winget` oder den Store erscheinen ohne manuelles Neuladen.
- **Massentauglich**: Suche und „Alle auswählen“ sind für 10.000+ Einträge optimiert
  (entprellte Suche, gebündelte Auswahl, `DeferRefresh` beim Listenaufbau).
- **Absturzsicher**: globale Exception-Handler schreiben nach
  `%LocalAppData%\SystemOptimierer\crash.log` (mit Größenbegrenzung).
- **Sicherheitsnetze**: geschützte Schlüsselwörter und Ordner-/Registry-Sperrlisten verhindern
  das Entfernen von Systemkomponenten; Benutzerdaten werden bei der Reste-Bereinigung nur
  gelöscht, wenn der Ordner leer ist.

## Voraussetzungen

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (zum Bauen) bzw. .NET 10 Desktop-Runtime
- Administratorrechte (im Manifest fest verdrahtet: `requireAdministrator`)
- Für Updates: `winget` (App Installer) und Internetzugang

## Bauen und starten

```powershell
# Debug-Build
dotnet build SystemOptimierer.sln -c Debug

# Starten
.\bin\Debug\net10.0-windows\SystemOptimierer.exe
```

Release-Build (Single-File, ohne separate PDB):

```powershell
dotnet build SystemOptimierer.sln -c Release
dotnet publish SystemOptimierer.csproj -c Release -r win-x64 --self-contained false
```

## Projektstruktur

```
App.xaml(.cs)              Anwendungseinstieg, globale Fehlerbehandlung
MainWindow.xaml(.cs)       Oberfläche (Styles, Seiten, Code-Behind für Fenster/Log)
ViewModels\                MainViewModel: Zustand, Befehle, Live-Aktualisierung
Models\                    SoftwareItem, UpdateItem, StartupItem
Services\                  Update-, Treiber-, Reparatur-, Bereinigungs-, Deinstallations-,
                           Leistungs- und Speicherdienste (jeweils mit Interface)
Core\                      AsyncRelayCommand, CoalescingRefresh, ProcessJob,
                           ProgressInfo/StepProgress, ConsoleProgressParser
scripts\Sign-Application.ps1  Codesignatur für die Auslieferung
_archive_alte_dateien\     Archivierte Altdateien (vom Build ausgeschlossen)
```

## Hinweise

- Die Bereinigung des Komponentenspeichers (`DISM /StartComponentCleanup /ResetBase`) ist
  **irreversibel**: installierte Updates können danach nicht mehr deinstalliert werden.
- Deinstallierte Programme werden über die Registry verifiziert; Reste werden erst nach
  bestätigter Deinstallation entfernt.
- Das Projekt analysiert sich mit den empfohlenen .NET-Analyzern (`AnalysisLevel=latest-recommended`);
  der Build läuft mit 0 Warnungen.

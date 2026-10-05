using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    /// <summary>
    /// Sucht und installiert WHQL-zertifizierte Treiber-Updates über die Microsoft Windows Update COM-API.
    /// Deep-Scan: Findet sowohl Pflicht-Treiber als auch optionale Treiber (z. B. Intel, Nvidia, Realtek),
    /// die vom Standard-Scan oft ignoriert werden (BrowseOnly=1).
    /// </summary>
    public class DriverService : IDriverService
    {
        // Eine einzige Suche mit ODER-Verknüpfung statt zwei getrennter Suchen:
        // gleiches Ergebnis, aber nur ein Server-Durchlauf (gemessen: ~10 s statt ~41 s).
        private const string DriverSearchCriteria =
            "IsInstalled=0 and Type='Driver' and BrowseOnly=0 or IsInstalled=0 and Type='Driver' and BrowseOnly=1";

        // Fortschrittsmeldungen des Skripts: "##PROGRESS|<Schritt>|<Anteil 0-1 oder -1>|<Detail>"
        // Sie steuern die Fortschrittsanzeige und erscheinen nicht im Log.
        private const string ProgressMarker = "##PROGRESS|";

        private const string ScriptHelpers = @"
function Report-Progress([int]$step, [double]$fraction, [string]$detail) {
    Write-Host ('##PROGRESS|' + $step + '|' + $fraction.ToString([System.Globalization.CultureInfo]::InvariantCulture) + '|' + $detail)
}
";

        private const string SearchScriptPrefix = @"
Report-Progress 0 0 'Windows-Update-Dienst wird gestartet...'
Write-Host 'Stelle sicher, dass der Windows Update Dienst (wuauserv) läuft...'
Start-Service wuauserv -ErrorAction SilentlyContinue
$session  = New-Object -ComObject Microsoft.Update.Session
$session.ClientApplicationID = 'SystemOptimierer'
$searcher = $session.CreateUpdateSearcher()
$searcher.IncludePotentiallySupersededUpdates = $false
Write-Host 'Verbinde mit Microsoft Windows Update Servern...'
Report-Progress 1 (-1) 'Microsoft-Server werden nach Treibern durchsucht...'
$result = $searcher.Search(""" + DriverSearchCriteria + @""")
$mandatory = @($result.Updates | Where-Object { -not $_.BrowseOnly })
$optional  = @($result.Updates | Where-Object { $_.BrowseOnly })
";

        private static readonly string[] CheckSteps = { "Windows Update vorbereiten", "Nach Treibern suchen", "Ergebnisse auswerten" };
        private static readonly double[] CheckWeights = { 1, 8, 1 };
        private static readonly string[] InstallSteps = { "Windows Update vorbereiten", "Nach Treibern suchen", "Treiber herunterladen", "Treiber installieren" };
        private static readonly double[] InstallWeights = { 1, 4, 4, 6 };

        private readonly IPowerShellService _ps;

        public DriverService(IPowerShellService powerShellService)
        {
            _ps = powerShellService;
        }

        public async Task CheckDriversAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            log("--- DEEP-SCAN: SUCHE NACH OFFIZIELLEN TREIBER-UPDATES ---");

            string script = ScriptHelpers + @"
try {
" + SearchScriptPrefix + @"
    Report-Progress 2 0.5 'Gefundene Treiber werden ausgewertet...'
    Write-Host ''
    Write-Host '[PFLICHT-TREIBER] Erforderliche Treiber-Updates:'
    if ($mandatory.Count -eq 0) {
        Write-Host '  -> Alle Pflicht-Treiber sind aktuell.'
    } else {
        Write-Host ('  -> Gefunden: ' + $mandatory.Count + ' Pflicht-Treiber')
        foreach ($u in $mandatory) { Write-Host ('  [PFLICHT] ' + $u.Title) }
    }

    Write-Host ''
    Write-Host '[OPTIONALE TREIBER] Optionale Hardware-Updates (Intel/Nvidia/Realtek/AMD):'
    if ($optional.Count -eq 0) {
        Write-Host '  -> Keine optionalen Treiber-Updates gefunden.'
    } else {
        Write-Host ('  -> Gefunden: ' + $optional.Count + ' optionale Treiber')
        foreach ($u in $optional) { Write-Host ('  [OPTIONAL] ' + $u.Title) }
    }

    $total = $mandatory.Count + $optional.Count
    Write-Host ''
    Write-Host ('GESAMT: ' + $total + ' Treiber-Updates verfügbar (Pflicht: ' + $mandatory.Count + ', Optional: ' + $optional.Count + ')')
    Report-Progress 2 1 ('' + $total + ' Treiber-Update(s) gefunden')
} catch {
    Write-Host ('[FEHLER] Treibersuche fehlgeschlagen: ' + $_.Exception.Message)
}
";
            var output = CreateOutputHandler(log, new StepProgress(progress, CheckWeights), CheckSteps);
            await _ps.RunScriptAsync(script, output, output, ct);
            log("--- TREIBERSUCHE ABGESCHLOSSEN ---");
        }

        public async Task InstallDriversAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            log("--- INSTALLIERE OFFIZIELLE TREIBER (PFLICHT + OPTIONAL) ---");

            // Download und Installation erfolgen PRO Treiber: So gibt es echten Fortschritt und
            // pro Treiber eine eigene Ergebniszeile ("[ERFOLG] Treiber installiert: <Titel>" bzw.
            // "[FEHLER] Treiber nicht installiert (<Grund>): <Titel>") für die Statusanzeige.
            string script = ScriptHelpers + @"
function Get-ResultText($code) {
    switch ($code) { 2 { 'Erfolgreich' } 3 { 'Erfolgreich mit Fehlern' } 4 { 'Fehlgeschlagen' } 5 { 'Abgebrochen' } default { 'Code ' + $code } }
}
try {
" + SearchScriptPrefix + @"
    $all = @($mandatory) + @($optional)
    foreach ($u in $mandatory) { Write-Host ('[PFLICHT] Vorgemerkt: ' + $u.Title) }
    foreach ($u in $optional)  { Write-Host ('[OPTIONAL] Vorgemerkt: ' + $u.Title) }

    if ($all.Count -eq 0) {
        Write-Host 'Keine Treiber-Updates zur Installation gefunden.'
        Report-Progress 3 1 'Keine Treiber-Updates vorhanden'
        exit
    }

    # ---- Download (einzeln, damit der Fortschritt sichtbar ist) ----
    Write-Host ('' + $all.Count + ' Treiber werden heruntergeladen...')
    $downloader = $session.CreateUpdateDownloader()
    for ($i = 0; $i -lt $all.Count; $i++) {
        $u = $all[$i]
        Report-Progress 2 ($i / $all.Count) ('Lade ' + ($i + 1) + ' von ' + $all.Count + ': ' + $u.Title)
        if (-not $u.EulaAccepted) { $u.AcceptEula() }
        if (-not $u.IsDownloaded) {
            $coll = New-Object -ComObject Microsoft.Update.UpdateColl
            $coll.Add($u) | Out-Null
            $downloader.Updates = $coll
            try { $downloader.Download() | Out-Null } catch { }
        }
    }
    Report-Progress 2 1 'Download abgeschlossen'

    $ready = @($all | Where-Object { $_.IsDownloaded })
    foreach ($u in ($all | Where-Object { -not $_.IsDownloaded })) {
        Write-Host ('[FEHLER] Treiber nicht installiert (Download fehlgeschlagen): ' + $u.Title)
    }
    if ($ready.Count -eq 0) {
        Write-Host 'Es konnte kein Treiber heruntergeladen werden.'
        exit
    }

    # ---- Installation (einzeln) ----
    Write-Host 'Installiere Treiber (Bildschirm könnte kurz flackern)...'
    $installer = $session.CreateUpdateInstaller()
    $rebootRequired = $false
    for ($i = 0; $i -lt $ready.Count; $i++) {
        $u = $ready[$i]
        Report-Progress 3 ($i / $ready.Count) ('Installiere ' + ($i + 1) + ' von ' + $ready.Count + ': ' + $u.Title)
        $coll = New-Object -ComObject Microsoft.Update.UpdateColl
        $coll.Add($u) | Out-Null
        $installer.Updates = $coll
        try {
            $r = $installer.Install()
            $code = $r.GetUpdateResult(0).ResultCode
            if ($r.RebootRequired) { $rebootRequired = $true }
        } catch {
            $code = 4
        }
        if ($code -eq 2 -or $code -eq 3) { Write-Host ('[ERFOLG] Treiber installiert: ' + $u.Title) }
        else { Write-Host ('[FEHLER] Treiber nicht installiert (' + (Get-ResultText $code) + '): ' + $u.Title) }
    }
    Report-Progress 3 1 'Installation abgeschlossen'

    Write-Host 'Installation abgeschlossen.'
    if ($rebootRequired) {
        Write-Host 'HINWEIS: Ein Neustart wird empfohlen, um alle Treiber zu aktivieren.'
    }
} catch {
    Write-Host ('[FEHLER] Treiberinstallation fehlgeschlagen: ' + $_.Exception.Message)
}
";
            var output = CreateOutputHandler(log, new StepProgress(progress, InstallWeights), InstallSteps);
            await _ps.RunScriptAsync(script, output, output, ct);
            log("--- TREIBERINSTALLATION ABGESCHLOSSEN ---");
        }

        /// <summary>
        /// Trennt Fortschrittsmarker (→ Fortschrittsanzeige) von normalen Ausgaben (→ Log).
        /// </summary>
        private static Action<string> CreateOutputHandler(Action<string> log, StepProgress steps, string[] stepTitles)
        {
            int currentStep = -1;
            return line =>
            {
                if (!line.StartsWith(ProgressMarker, StringComparison.Ordinal))
                {
                    log(line);
                    return;
                }

                string[] parts = line.Substring(ProgressMarker.Length).Split('|', 3);
                if (parts.Length < 3 ||
                    !int.TryParse(parts[0], out int step) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction))
                    return;

                step = Math.Clamp(step, 0, stepTitles.Length - 1);
                if (step != currentStep)
                {
                    currentStep = step;
                    steps.Begin(step, stepTitles[step], parts[2]);
                }

                if (fraction < 0) steps.ReportIndeterminate(parts[2]);
                else steps.Report(fraction, parts[2]);
            };
        }
    }
}

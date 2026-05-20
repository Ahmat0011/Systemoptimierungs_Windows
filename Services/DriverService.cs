using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    /// <summary>
    /// Sucht und installiert WHQL-zertifizierte Treiber-Updates über die Microsoft Windows Update COM-API.
    /// Deep-Scan: Findet sowohl Pflicht-Treiber als auch optionale Treiber (z. B. Intel, Nvidia, Realtek),
    /// die vom Standard-Scan oft ignoriert werden (BrowseOnly=1).
    /// </summary>
    public class DriverService : IDriverService
    {
        private readonly IPowerShellService _ps;

        public DriverService(IPowerShellService powerShellService)
        {
            _ps = powerShellService;
        }

        public async Task CheckDriversAsync(Action<string> log, CancellationToken ct)
        {
            log("--- DEEP-SCAN: SUCHE NACH OFFIZIELLEN TREIBER-UPDATES ---");

            string script = @"
try {
    Write-Host 'Stelle sicher, dass der Windows Update Dienst (wuauserv) läuft...'
    Start-Service wuauserv -ErrorAction SilentlyContinue
    $session  = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()
    # IncludePotentiallySupersededUpdates: findet auch ältere Versionen, die noch relevant sein können
    $searcher.IncludePotentiallySupersededUpdates = $false

    Write-Host 'Verbinde mit Microsoft Windows Update Servern...'

    # Pflicht-Treiber (BrowseOnly=0)
    Write-Host ''
    Write-Host '[PFLICHT-TREIBER] Durchsuche erforderliche Treiber-Updates...'
    $mandatory = $searcher.Search(""IsInstalled=0 and Type='Driver' and BrowseOnly=0"")
    if ($mandatory.Updates.Count -eq 0) {
        Write-Host '  -> Alle Pflicht-Treiber sind aktuell.'
    } else {
        Write-Host ('  -> Gefunden: ' + $mandatory.Updates.Count + ' Pflicht-Treiber')
        foreach ($u in $mandatory.Updates) {
            Write-Host ('  [PFLICHT] ' + $u.Title)
        }
    }

    # Optionale Treiber (BrowseOnly=1) – z.B. Intel, Nvidia, Realtek, AMD
    Write-Host ''
    Write-Host '[OPTIONALE TREIBER] Durchsuche optionale Hardware-Updates (Intel/Nvidia/Realtek/AMD)...'
    $optional = $searcher.Search(""IsInstalled=0 and Type='Driver' and BrowseOnly=1"")
    if ($optional.Updates.Count -eq 0) {
        Write-Host '  -> Keine optionalen Treiber-Updates gefunden.'
    } else {
        Write-Host ('  -> Gefunden: ' + $optional.Updates.Count + ' optionale Treiber')
        foreach ($u in $optional.Updates) {
            Write-Host ('  [OPTIONAL] ' + $u.Title)
        }
    }

    $total = $mandatory.Updates.Count + $optional.Updates.Count
    Write-Host ''
    Write-Host ('GESAMT: ' + $total + ' Treiber-Updates verfügbar (Pflicht: ' + $mandatory.Updates.Count + ', Optional: ' + $optional.Updates.Count + ')')

} catch {
    Write-Host ('FEHLER bei der Treibersuche: ' + $_.Exception.Message)
}
";
            await _ps.RunScriptAsync(script, log, log, ct);
            log("--- TREIBERSUCHE ABGESCHLOSSEN ---");
        }

        public async Task InstallDriversAsync(Action<string> log, CancellationToken ct)
        {
            log("--- INSTALLIERE OFFIZIELLE TREIBER (PFLICHT + OPTIONAL) ---");

            string script = @"
try {
    Write-Host 'Stelle sicher, dass der Windows Update Dienst (wuauserv) läuft...'
    Start-Service wuauserv -ErrorAction SilentlyContinue
    $session  = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()

    Write-Host 'Suche nach allen Treiber-Updates...'

    $mandatory = $searcher.Search(""IsInstalled=0 and Type='Driver' and BrowseOnly=0"")
    $optional  = $searcher.Search(""IsInstalled=0 and Type='Driver' and BrowseOnly=1"")

    $updatesToInstall = New-Object -ComObject Microsoft.Update.UpdateColl

    foreach ($u in $mandatory.Updates) {
        if ($u.EulaAccepted -eq $false) { $u.AcceptEula() }
        $updatesToInstall.Add($u) | Out-Null
        Write-Host ('[PFLICHT] Vorgemerkt: ' + $u.Title)
    }
    foreach ($u in $optional.Updates) {
        if ($u.EulaAccepted -eq $false) { $u.AcceptEula() }
        $updatesToInstall.Add($u) | Out-Null
        Write-Host ('[OPTIONAL] Vorgemerkt: ' + $u.Title)
    }

    if ($updatesToInstall.Count -eq 0) {
        Write-Host 'Keine Treiber-Updates zur Installation gefunden.'
        exit
    }

    Write-Host ('' + $updatesToInstall.Count + ' Treiber werden heruntergeladen...')
    $downloader          = $session.CreateUpdateDownloader()
    $downloader.Updates  = $updatesToInstall
    $downloader.Download()

    Write-Host 'Installiere Treiber (Bildschirm koennte kurz flackern)...'
    $installer          = $session.CreateUpdateInstaller()
    $installer.Updates  = $updatesToInstall
    $installResult      = $installer.Install()

    Write-Host ('Installation abgeschlossen! Ergebnis-Code: ' + $installResult.ResultCode)
    if ($installResult.RebootRequired) {
        Write-Host 'HINWEIS: Ein Neustart wird empfohlen, um alle Treiber zu aktivieren.'
    }

} catch {
    Write-Host ('FEHLER bei der Treiberinstallation: ' + $_.Exception.Message)
}
";
            await _ps.RunScriptAsync(script, log, log, ct);
            log("--- TREIBERINSTALLATION ABGESCHLOSSEN ---");
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class OptimizerService : IOptimizerService
    {
        private readonly ICommandExecutionService _cmd;
        private readonly IPowerShellService _ps;

        public OptimizerService(ICommandExecutionService commandService, IPowerShellService powerShellService)
        {
            _cmd = commandService;
            _ps = powerShellService;
        }

        public async Task CheckUpdatesAsync(Action<string> log, CancellationToken ct)
        {
            log("--- SUCHE NACH UPDATES ---");
            await _cmd.RunCommandAsync("winget", "source update --accept-source-agreements", log, log, ct);
            await _cmd.RunCommandAsync("winget", "upgrade --accept-source-agreements", log, log, ct);
            log("--- SUCHE ABGESCHLOSSEN ---");
        }

        public async Task InstallUpdatesAsync(Action<string> log, CancellationToken ct)
        {
            log("--- INSTALLIERE STABILE UPDATES ---");
            await _cmd.RunCommandAsync("winget", "upgrade --all --accept-package-agreements --accept-source-agreements", log, log, ct);
            log("--- INSTALLATION ABGESCHLOSSEN ---");
        }

        public async Task RepairSystemAsync(Action<string> log, CancellationToken ct)
        {
            log("--- STARTE WINDOWS REPARATUR ---");
            log("Schritt 1: DISM (Das kann dauern...)");
            await _cmd.RunCommandAsync("DISM", "/Online /Cleanup-Image /RestoreHealth", log, log, ct);
            
            if (!ct.IsCancellationRequested)
            {
                log("Schritt 2: SFC Scan...");
                await _cmd.RunCommandAsync("sfc", "/scannow", log, log, ct);
            }
            log("--- REPARATUR ABGESCHLOSSEN ---");
        }

        public async Task OptimizeNetworkAsync(Action<string> log, CancellationToken ct)
        {
            log("--- DEAKTIVIERE P2P DOWNLOADS ---");
            await _cmd.RunCommandAsync("reg", @"add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config"" /v DODownloadMode /t REG_DWORD /d 0 /f", log, log, ct);
            log("Einstellung erfolgreich angewendet!");
        }

        public async Task CleanupSystemAsync(Action<string> log, CancellationToken ct)
        {
            log("--- STARTE MASTER-BEREINIGUNG (SICHER & GRÜNDLICH) ---");
            
            log("1. Lösche temporäre Dateien...");
            await _cmd.RunCommandAsync("cmd", "/c del /q /f /s %temp%\\*", log, log, ct);
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Temp\*", log, log, ct);

            log("2. Leere den Papierkorb auf ALLEN Laufwerken (C:, D: etc.)...");
            await _ps.RunScriptAsync("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", log, log, ct);

            log("3. Lösche DirectX-Shadercache & Thumbnails...");
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s %LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db", log, log, ct);
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s %LocalAppData%\NVIDIA\DXCache\*", log, log, ct);

            log("4. Lösche Prefetch-Dateien...");
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Prefetch\*", log, log, ct);

            log("5. Windows Update Reinigung...");
            await _cmd.RunCommandAsync("net", "stop wuauserv /y", log, log, ct);
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\SoftwareDistribution\Download\*", log, log, ct);
            await _cmd.RunCommandAsync("net", "start wuauserv", log, log, ct);

            log("6. Leere DNS-Cache...");
            await _cmd.RunCommandAsync("ipconfig", "/flushdns", log, log, ct);
            
            log("--- MASTER-BEREINIGUNG ERFOLGREICH ABGESCHLOSSEN ---");
        }

        public async Task CheckDriversAsync(Action<string> log, CancellationToken ct)
        {
            log("--- SUCHE NACH OFFIZIELLEN TREIBER-UPDATES ---");
            string script = @"
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                Write-Host 'Verbinde mit Microsoft Windows Update Servern...'
                $result = $searcher.Search(""IsInstalled=0 and Type='Driver'"")
                if ($result.Updates.Count -eq 0) {
                    Write-Host 'Super! Alle Treiber sind auf dem neuesten Stand.'
                } else {
                    Write-Host ('Gefundene Treiber-Updates: ' + $result.Updates.Count)
                    foreach ($u in $result.Updates) {
                        Write-Host ('- ' + $u.Title)
                    }
                }
            } catch {
                Write-Host 'Fehler bei der Treibersuche: ' $_.Exception.Message
            }
            ";
            await _ps.RunScriptAsync(script, log, log, ct);
            log("--- TREIBERSUCHE ABGESCHLOSSEN ---");
        }

        public async Task InstallDriversAsync(Action<string> log, CancellationToken ct)
        {
            log("--- INSTALLIERE OFFIZIELLE TREIBER ---");
            string script = @"
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                Write-Host 'Suche nach fehlenden Treibern...'
                $result = $searcher.Search(""IsInstalled=0 and Type='Driver'"")
                if ($result.Updates.Count -eq 0) {
                    Write-Host 'Keine Treiber-Updates zur Installation gefunden.'
                    exit
                }
                $updatesToInstall = New-Object -ComObject Microsoft.Update.UpdateColl
                foreach ($u in $result.Updates) {
                    if ($u.EulaAccepted -eq $false) { $u.AcceptEula() }
                    $updatesToInstall.Add($u) | Out-Null
                }
                Write-Host 'Lade Treiber sicher von Microsoft herunter...'
                $downloader = $session.CreateUpdateDownloader()
                $downloader.Updates = $updatesToInstall
                $downloader.Download()
                
                Write-Host 'Installiere Treiber (Das kann dauern, Bildschirm könnte flackern)...'
                $installer = $session.CreateUpdateInstaller()
                $installer.Updates = $updatesToInstall
                $installResult = $installer.Install()
                Write-Host 'Installation abgeschlossen!'
            } catch {
                Write-Host 'Fehler bei der Installation: ' $_.Exception.Message
            }
            ";
            await _ps.RunScriptAsync(script, log, log, ct);
            log("--- TREIBERINSTALLATION ABGESCHLOSSEN ---");
        }
    }
}

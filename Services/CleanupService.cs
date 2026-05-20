using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class CleanupService : ICleanupService
    {
        private readonly ICommandExecutionService _cmd;
        private readonly IPowerShellService _ps;

        public CleanupService(ICommandExecutionService commandService, IPowerShellService powerShellService)
        {
            _cmd = commandService;
            _ps  = powerShellService;
        }

        public async Task CleanupSystemAsync(Action<string> log, CancellationToken ct)
        {
            log("--- STARTE MASTER-BEREINIGUNG (SICHER & GRÜNDLICH) ---");

            log("[1/6] Lösche temporäre Dateien...");
            await _cmd.RunCommandAsync("cmd", "/c del /q /f /s %temp%\\*",                          log, log, ct);
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Temp\*",                 log, log, ct);
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s D:\Temp\* 2>nul",                   log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[2/6] Leere den Papierkorb auf ALLEN Laufwerken (C: und D:)...");
            await _ps.RunScriptAsync("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[3/6] Lösche DirectX-Shadercache & Thumbnails...");
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s %LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db", log, log, ct);
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s %LocalAppData%\NVIDIA\DXCache\*",   log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[4/6] Lösche Prefetch-Dateien...");
            await _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Prefetch\*",             log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[5/6] Windows Update Cache bereinigen...");
            await _cmd.RunCommandAsync("net",  "stop wuauserv /y",                                  log, log, ct);
            await _cmd.RunCommandAsync("cmd",  @"/c del /q /f /s C:\Windows\SoftwareDistribution\Download\*", log, log, ct);
            await _cmd.RunCommandAsync("net",  "start wuauserv",                                    log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[6/6] Leere DNS-Cache...");
            await _cmd.RunCommandAsync("ipconfig", "/flushdns",                                     log, log, ct);

            log("--- MASTER-BEREINIGUNG ERFOLGREICH ABGESCHLOSSEN ---");
        }

        public async Task OptimizeNetworkAsync(Action<string> log, CancellationToken ct)
        {
            log("--- DEAKTIVIERE P2P WINDOWS-UPDATE-DOWNLOADS ---");
            await _cmd.RunCommandAsync("reg",
                @"add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config"" /v DODownloadMode /t REG_DWORD /d 0 /f",
                log, log, ct);
            log("P2P-Downloads erfolgreich deaktiviert!");
            log("--- NETZWERK OPTIMIERUNG ABGESCHLOSSEN ---");
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class CleanupService : ICleanupService
    {
        private readonly ICommandExecutionService _cmd;
        private readonly IPowerShellService _ps;

        // Per-step timeout – prevents a single stuck sub-command from blocking the whole cleanup.
        private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(90);

        public CleanupService(ICommandExecutionService commandService, IPowerShellService powerShellService)
        {
            _cmd = commandService;
            _ps  = powerShellService;
        }

        public async Task CleanupSystemAsync(Action<string> log, CancellationToken ct)
        {
            log("--- STARTE MASTER-BEREINIGUNG (SICHER & GRÜNDLICH) ---");

            // [1/6] Temp files
            // Output is redirected to nul so that per-file deletion messages do not flood
            // the UI dispatcher and cause the window to appear frozen.
            log("[1/6] Lösche temporäre Dateien (kann einen Moment dauern)...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s ""%temp%\*"" > nul 2>&1",           log, log, innerCt),
                ct, DefaultStepTimeout, log);
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Temp\* > nul 2>&1",      log, log, innerCt),
                ct, DefaultStepTimeout, log);
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s D:\Temp\* > nul 2>&1",              log, log, innerCt),
                ct, TimeSpan.FromSeconds(30), log);
            log("[1/6] \u2713 Temporäre Dateien bereinigt.");

            if (ct.IsCancellationRequested) return;

            // [2/6] Recycle bin
            log("[2/6] Leere den Papierkorb auf ALLEN Laufwerken (C: und D:)...");
            await RunWithStepTimeoutAsync(
                innerCt => _ps.RunScriptAsync("Clear-RecycleBin -Force -ErrorAction SilentlyContinue", log, log, innerCt),
                ct, DefaultStepTimeout, log);
            log("[2/6] \u2713 Papierkorb geleert.");

            if (ct.IsCancellationRequested) return;

            // [3/6] DirectX shader cache & thumbnails
            log("[3/6] Lösche DirectX-Shadercache & Thumbnails...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s ""%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db"" > nul 2>&1", log, log, innerCt),
                ct, DefaultStepTimeout, log);
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s ""%LocalAppData%\NVIDIA\DXCache\*"" > nul 2>&1",                           log, log, innerCt),
                ct, DefaultStepTimeout, log);
            log("[3/6] \u2713 DirectX-Shadercache & Thumbnails bereinigt.");

            if (ct.IsCancellationRequested) return;

            // [4/6] Prefetch files
            log("[4/6] Lösche Prefetch-Dateien...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Prefetch\* > nul 2>&1",  log, log, innerCt),
                ct, DefaultStepTimeout, log);
            log("[4/6] \u2713 Prefetch-Dateien bereinigt.");

            if (ct.IsCancellationRequested) return;

            // [5/6] Windows Update Cache
            log("[5/6] Windows Update Cache bereinigen...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("net", "stop wuauserv /y", log, log, innerCt),
                ct, TimeSpan.FromSeconds(30), log);
            try
            {
                if (!ct.IsCancellationRequested)
                    await RunWithStepTimeoutAsync(
                        innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\SoftwareDistribution\Download\* > nul 2>&1", log, log, innerCt),
                        ct, DefaultStepTimeout, log);
            }
            finally
            {
                // Always restart the Windows Update service – even if the operation was cancelled –
                // so the system is never left with a stopped wuauserv.
                await _cmd.RunCommandAsync("net", "start wuauserv", log, log, CancellationToken.None);
            }
            log("[5/6] \u2713 Windows Update Cache bereinigt.");

            if (ct.IsCancellationRequested) return;

            // [6/6] DNS cache
            log("[6/6] Leere DNS-Cache...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("ipconfig", "/flushdns", log, log, innerCt),
                ct, TimeSpan.FromSeconds(15), log);
            log("[6/6] \u2713 DNS-Cache geleert.");

            log("--- MASTER-BEREINIGUNG ERFOLGREICH ABGESCHLOSSEN ---");
        }

        /// <summary>
        /// Runs <paramref name="operation"/> with a per-step timeout layered on top of
        /// the caller's <paramref name="ct"/>.  If only the step timeout fires the warning
        /// is logged and execution continues with the next step.  If the caller's token is
        /// cancelled the <see cref="OperationCanceledException"/> propagates normally.
        /// </summary>
        private static async Task RunWithStepTimeoutAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken ct,
            TimeSpan timeout,
            Action<string> log)
        {
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(timeout);
            try
            {
                await operation(stepCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Only the per-step timeout fired; log and continue with the next step.
                log($"[WARNUNG] Schritt hat das Zeitlimit ({timeout.TotalSeconds}s) überschritten und wurde übersprungen.");
            }
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

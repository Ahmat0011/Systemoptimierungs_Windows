using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class CleanupService : ICleanupService
    {
        // ===== Nativer Papierkorb-Aufruf (leert den Papierkorb in [3/8]) =====
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        // ===== Konfiguration =====
        // Muster: ALLE Ordner, die mit "tw-" anfangen UND mit ".tmp" enden.
        // Der Mittelteil (GUID) ist egal -> trifft jede Variante automatisch.
        private const string TwTempPattern = "tw-*.tmp";

        // true  = wie cleanmgr "Windows Update-Bereinigung": räumt ALLE ersetzten
        //         Komponenten auf (die ~570 MB). Alte Updates danach nicht mehr
        //         deinstallierbar (irreversibel) -> genau wie cleanmgr.
        // false = sanfter: nur sicher entbehrliche Komponenten.
        private const bool ResetComponentStoreBase = true;

        // DISM /StartComponentCleanup /ResetBase kann lange dauern -> eigener Timeout.
        private static readonly TimeSpan ComponentCleanupTimeout = TimeSpan.FromMinutes(45);

        // HARTES Zeitlimit für die tw-Ordner-Bereinigung: verhindert jeden Hänger.
        private static readonly TimeSpan TwCleanupTimeout = TimeSpan.FromSeconds(60);

        // Per-step timeout für alle anderen Teilschritte.
        private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(90);

        private readonly ICommandExecutionService _cmd;
        private readonly IPowerShellService _ps;

        public CleanupService(ICommandExecutionService commandService, IPowerShellService powerShellService)
        {
            _cmd = commandService;
            _ps = powerShellService;
        }

        public async Task CleanupSystemAsync(Action<string> log, CancellationToken ct)
        {
            log("--- STARTE MASTER-BEREINIGUNG (SICHER & GRÜNDLICH) ---");

            // ------------------------------------------------------------------
            // [1/8] tw-*.tmp Ordner HART löschen (mit Timeout -> KEIN Hänger mehr)
            // Kein Papierkorb mehr: die Papierkorb-API (SHFileOperation) hängt an
            // diesem SYSTEM-geschützten Pfad an einem unsichtbaren Dialog.
            // ------------------------------------------------------------------
            log("[1/8] Lösche verwaiste tw-*.tmp Ordner im systemprofile-Pfad...");
            string systemProfileLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\config\systemprofile\AppData\Local");
            await DeleteTwTempFoldersWithTimeoutAsync(systemProfileLocal, log, ct, TwCleanupTimeout);
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [2/8] Temp-Dateien
            // ------------------------------------------------------------------
            log("[2/8] Lösche temporäre Dateien (kann einen Moment dauern)...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s ""%temp%\*"" > nul 2>&1", log, log, innerCt),
                ct, DefaultStepTimeout, log);
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Temp\* > nul 2>&1", log, log, innerCt),
                ct, DefaultStepTimeout, log);
            if (Directory.Exists(@"D:\Temp"))
            {
                await RunWithStepTimeoutAsync(
                    innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s D:\Temp\* > nul 2>&1", log, log, innerCt),
                    ct, TimeSpan.FromSeconds(30), log);
            }
            log("[2/8] ✓ Temporäre Dateien bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [3/8] Papierkorb leeren
            // ------------------------------------------------------------------
            log("[3/8] Leere den Papierkorb auf ALLEN Laufwerken...");
            await Task.Run(() =>
            {
                _ = SHEmptyRecycleBin(IntPtr.Zero, null,
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            }, ct);
            log("[3/8] ✓ Papierkorb geleert.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [4/8] DirectX-Shadercache & Thumbnails
            // ------------------------------------------------------------------
            log("[4/8] Lösche DirectX-Shadercache & Thumbnails...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s ""%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db"" > nul 2>&1", log, log, innerCt),
                ct, DefaultStepTimeout, log);
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s ""%LocalAppData%\NVIDIA\DXCache\*"" > nul 2>&1", log, log, innerCt),
                ct, DefaultStepTimeout, log);
            log("[4/8] ✓ DirectX-Shadercache & Thumbnails bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [5/8] Prefetch
            // ------------------------------------------------------------------
            log("[5/8] Lösche Prefetch-Dateien...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s C:\Windows\Prefetch\* > nul 2>&1", log, log, innerCt),
                ct, DefaultStepTimeout, log);
            log("[5/8] ✓ Prefetch-Dateien bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [6/8] Windows Update Download-Cache
            // ------------------------------------------------------------------
            log("[6/8] Windows Update Download-Cache bereinigen...");
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
                await _cmd.RunCommandAsync("net", "start wuauserv", log, log, CancellationToken.None);
            }
            log("[6/8] ✓ Windows Update Download-Cache bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [7/8] Windows Update-Bereinigung (Component Store / WinSxS) = die ~570 MB
            // ------------------------------------------------------------------
            string dismArgs = "/Online /Cleanup-Image /StartComponentCleanup" + (ResetComponentStoreBase ? " /ResetBase" : "");
            log("[7/8] Windows Update-Bereinigung (Component Store) – DAS KANN MEHRERE MINUTEN DAUERN...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("DISM", dismArgs, log, log, innerCt),
                ct, ComponentCleanupTimeout, log);
            log("[7/8] ✓ Windows Update-Bereinigung abgeschlossen.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [8/8] DNS-Cache
            // ------------------------------------------------------------------
            log("[8/8] Leere DNS-Cache...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("ipconfig", "/flushdns", log, log, innerCt),
                ct, TimeSpan.FromSeconds(15), log);
            log("[8/8] ✓ DNS-Cache geleert.");

            log("--- MASTER-BEREINIGUNG ERFOLGREICH ABGESCHLOSSEN ---");
        }

        // ======================================================================
        // tw-Ordner-Bereinigung MIT hartem Timeout (Hänger-Schutz)
        // ======================================================================
        /// <summary>
        /// Führt die tw-Bereinigung in einem eigenen Task aus und legt ein hartes
        /// Zeitlimit darüber. Wird das Limit erreicht, läuft der Rest im Hintergrund
        /// weiter (fire-and-forget) und die Bereinigung macht SOFORT mit dem nächsten
        /// Schritt weiter -> die App kann hier nie wieder hängen bleiben.
        /// </summary>
        private static async Task DeleteTwTempFoldersWithTimeoutAsync(
            string twDir, Action<string> log, CancellationToken ct, TimeSpan timeout)
        {
            // CancellationToken.None am work-Task: der User-Abbruch soll den
            // Lösch-Thread nicht mitten in einer Datei-Operation killen.
            var work = Task.Run(() => DeleteTwTempFolders(twDir, log), CancellationToken.None);
            var delay = Task.Delay(timeout, CancellationToken.None);

            var finished = await Task.WhenAny(work, delay);

            if (finished == delay)
            {
                log($"[WARNUNG] tw-Bereinigung hat das Zeitlimit ({timeout.TotalSeconds:F0}s) erreicht. " +
                    "Rest läuft im Hintergrund weiter, Bereinigung wird fortgesetzt.");
                // work läuft bewusst weiter (löscht nur Müll) – kein await.
            }
            else
            {
                // work ist fertig -> evtl. Exception abfangen und loggen.
                try { await work; }
                catch (Exception ex) { log($"[WARNUNG] tw-Bereinigung: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Löscht ALLE Ordner, die auf tw-*.tmp passen, HART (kein Papierkorb).
        /// Pro Ordner try/catch + rd-Fallback, damit ein einzelner Fehlschlag
        /// die Schleife nicht stoppt.
        /// </summary>
        private static void DeleteTwTempFolders(string twDir, Action<string> log)
        {
            if (!Directory.Exists(twDir))
            {
                log("[1/8] ✓ Zielverzeichnis nicht vorhanden, nichts zu tun.");
                return;
            }

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(twDir, TwTempPattern);
            }
            catch (Exception ex)
            {
                log($"[WARNUNG] tw-Ordner konnten nicht aufgelistet werden: {ex.Message}");
                return;
            }

            if (folders.Length == 0)
            {
                log("[1/8] ✓ Keine verwaisten tw-*.tmp Ordner gefunden.");
                return;
            }

            int deleted = 0, failed = 0;
            foreach (var folder in folders)
            {
                // 1) Versuch: managed, rekursiv hart löschen
                try
                {
                    Directory.Delete(folder, true);
                    if (!Directory.Exists(folder)) { deleted++; continue; }
                }
                catch
                {
                    // fällt durch zum Fallback
                }

                // 2) Fallback: rd /s /q (robuster gegen schreibgeschützte/gesperrte Inhalte)
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(
                        "cmd.exe", $"/c rd /s /q \"{folder}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p != null && p.WaitForExit(8000))
                    {
                        if (!Directory.Exists(folder)) deleted++; else failed++;
                    }
                    else
                    {
                        if (p != null) { try { p.Kill(true); } catch { } }
                        failed++;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            log($"[1/8] ✓ tw-Ordner verarbeitet: {deleted} gelöscht, {failed} fehlgeschlagen (von {folders.Length}).");
        }

        /// <summary>
        /// Teilschritt mit eigenem Timeout. Nur Schritt-Timeout -> Warnung + weiter.
        /// User-Abbruch (ct) -> OperationCanceledException propagiert normal.
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
                log($"[WARNUNG] Schritt hat das Zeitlimit ({timeout.TotalSeconds:F0}s) überschritten und wurde übersprungen.");
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
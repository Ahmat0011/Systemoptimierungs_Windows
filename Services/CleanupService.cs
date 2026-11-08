using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

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

        /// <summary>Windows-Ordner (statt fest verdrahtetem "C:\Windows", das auf anderen Laufwerken falsch wäre).</summary>
        private static readonly string WindowsDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        /// <summary>Ermitteltes Systemlaufwerk (z. B. "C:\"), auf dem Windows installiert ist.</summary>
        private static readonly string SystemDriveRoot =
             GetSystemDriveRoot();

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
            _cmd = commandService ?? throw new ArgumentNullException(nameof(commandService));
            _ps = powerShellService ?? throw new ArgumentNullException(nameof(powerShellService));
        }

        public async Task CleanupSystemAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(log);

            log("--- STARTE MASTER-BEREINIGUNG (SICHER & GRÜNDLICH) ---");
            string driveLabel = SystemDriveRoot.TrimEnd('\\');
            long freeBefore = GetFreeBytes(SystemDriveRoot);

            try
            {
                // Gewichte = ungefährer Zeitanteil der 8 Schritte (DISM dauert mit Abstand am längsten)
                var steps = new StepProgress(progress, 1, 2, 1, 1, 1, 1, 8, 1);
                await RunCleanupStepsAsync(log, steps, ct);
            }
            finally
            {
                // Auch bei Abbruch anzeigen, was bis dahin tatsächlich freigegeben wurde
                long freeAfter = GetFreeBytes(SystemDriveRoot);
                if (freeBefore >= 0 && freeAfter >= 0)
                {
                    long freed = freeAfter - freeBefore;
                    log(freed > 0
                        ? $"[ERGEBNIS] Auf Laufwerk {driveLabel} wurden {FormatBytes(freed)} Speicherplatz freigegeben."
                        : $"[ERGEBNIS] Auf Laufwerk {driveLabel} wurde kein zusätzlicher Speicherplatz frei (bereits sauber).");
                }
            }
        }

        private async Task RunCleanupStepsAsync(Action<string> log, StepProgress steps, CancellationToken ct)
        {
            // ------------------------------------------------------------------
            // [1/8] tw-*.tmp Ordner HART löschen (mit Timeout -> KEIN Hänger mehr)
            // Kein Papierkorb mehr: die Papierkorb-API (SHFileOperation) hängt an
            // diesem SYSTEM-geschützten Pfad an einem unsichtbaren Dialog.
            // ------------------------------------------------------------------
            steps.Begin(0, "Verwaiste Update-Ordner entfernen", "Systemprofil wird durchsucht...");
            log("[1/8] Lösche verwaiste tw-*.tmp Ordner im systemprofile-Pfad...");
            string systemProfileLocal = Path.Combine(
                WindowsDirectory,
                @"System32\config\systemprofile\AppData\Local");
            await DeleteTwTempFoldersWithTimeoutAsync(systemProfileLocal, log, TwCleanupTimeout, ct);
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [2/8] Temp-Dateien
            // ------------------------------------------------------------------
            steps.Begin(1, "Temporäre Dateien löschen", "Benutzer-Temp-Ordner");
            log("[2/8] Lösche temporäre Dateien (kann einen Moment dauern)...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", DeleteFolderContentsCommand("%temp%"), log, log, innerCt),
                DefaultStepTimeout, log, ct);
            if (ct.IsCancellationRequested) return;

            string windowsTemp = Path.Combine(WindowsDirectory, "Temp");
            steps.Report(0.5, windowsTemp);
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", DeleteFolderContentsCommand(windowsTemp), log, log, innerCt),
                DefaultStepTimeout, log, ct);
            if (ct.IsCancellationRequested) return;

            if (Directory.Exists(@"D:\Temp"))
            {
                steps.Report(0.8, @"D:\Temp");
                await RunWithStepTimeoutAsync(
                    innerCt => _cmd.RunCommandAsync("cmd", DeleteFolderContentsCommand(@"D:\Temp"), log, log, innerCt),
                    TimeSpan.FromSeconds(30), log, ct);
            }
            log("[2/8] ✓ Temporäre Dateien bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [3/8] Papierkorb leeren
            // ------------------------------------------------------------------
            steps.Begin(2, "Papierkorb leeren", "Alle Laufwerke");
            log("[3/8] Leere den Papierkorb auf ALLEN Laufwerken...");
            await Task.Run(() =>
            {
                try
                {
                    _ = SHEmptyRecycleBin(IntPtr.Zero, null,
                        SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                }
                catch
                {
                    // Ignorieren, falls Papierkorb bereits leer ist oder ein Laufwerk nicht bereit ist.
                }
            }, ct);
            log("[3/8] ✓ Papierkorb geleert.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [4/8] DirectX-Shadercache & Thumbnails
            // ------------------------------------------------------------------
            steps.Begin(3, "Shadercache & Miniaturansichten", "Miniaturansichten-Cache");
            log("[4/8] Lösche DirectX-Shadercache & Thumbnails...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("cmd", @"/c del /q /f /s ""%LocalAppData%\Microsoft\Windows\Explorer\thumbcache_*.db"" > nul 2>&1", log, log, innerCt),
                DefaultStepTimeout, log, ct);

            // Windows-DirectX-Shadercache sowie die Caches von NVIDIA, AMD und Intel (werden bei Bedarf neu erstellt)
            string[] shaderCaches =
            {
                @"%LocalAppData%\D3DSCache",
                @"%LocalAppData%\NVIDIA\DXCache",
                @"%LocalAppData%\AMD\DxCache",
                @"%LocalAppData%\Intel\ShaderCache"
            };
            for (int i = 0; i < shaderCaches.Length; i++)
            {
                if (ct.IsCancellationRequested) return;
                string shaderCache = shaderCaches[i];
                steps.Report((i + 1) / (double)(shaderCaches.Length + 1), "Shadercache: " + shaderCache.Replace("%LocalAppData%\\", ""));
                await RunWithStepTimeoutAsync(
                    innerCt => _cmd.RunCommandAsync("cmd", DeleteFolderContentsCommand(shaderCache), log, log, innerCt),
                    DefaultStepTimeout, log, ct);
            }
            log("[4/8] ✓ DirectX-Shadercache & Thumbnails bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [5/8] Windows-Fehlerberichte & Speicherabbilder (Crash Dumps)
            // ------------------------------------------------------------------
            steps.Begin(4, "Absturzberichte & Dumps löschen", "Windows-Fehlerberichterstattung & CrashDumps");
            log("[5/8] Lösche Fehlerberichte und temporäre Speicherabbilder (Crash Dumps)...");
            string localCrashDumps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
            string werArchive = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportArchive");
            string werQueue = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportQueue");

            if (Directory.Exists(localCrashDumps))
            {
                await RunWithStepTimeoutAsync(
                    innerCt => _cmd.RunCommandAsync("cmd", DeleteFolderContentsCommand(localCrashDumps), log, log, innerCt),
                    DefaultStepTimeout, log, ct);
            }
            if (Directory.Exists(werArchive) && !ct.IsCancellationRequested)
            {
                await RunWithStepTimeoutAsync(
                    innerCt => _cmd.RunCommandAsync("cmd", DeleteFolderContentsCommand(werArchive), log, log, innerCt),
                    DefaultStepTimeout, log, ct);
            }
            if (Directory.Exists(werQueue) && !ct.IsCancellationRequested)
            {
                await RunWithStepTimeoutAsync(
                    innerCt => _cmd.RunCommandAsync("cmd", DeleteFolderContentsCommand(werQueue), log, log, innerCt),
                    DefaultStepTimeout, log, ct);
            }
            log("[5/8] ✓ Fehlerberichte & Absturz-Dumps bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [6/8] Windows Update Download-Cache
            // ------------------------------------------------------------------
            steps.Begin(5, "Windows Update Download-Cache", "Update-Dienst wird angehalten...");
            log("[6/8] Windows Update Download-Cache bereinigen...");

            // War der Dienst vorher schon aus, wird er nachher NICHT gestartet – sonst würde die
            // Bereinigung eine vom Benutzer bewusst deaktivierte Windows-Update-Funktion aktivieren.
            bool updateServiceWasRunning = await IsServiceRunningAsync("wuauserv", ct);

            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("net", "stop wuauserv /y", log, log, innerCt),
                TimeSpan.FromSeconds(30), log, ct);
            try
            {
                if (!ct.IsCancellationRequested)
                {
                    steps.Report(0.4, "Heruntergeladene Update-Dateien werden gelöscht");
                    await RunWithStepTimeoutAsync(
                        innerCt => _cmd.RunCommandAsync("cmd",
                            DeleteFolderContentsCommand(Path.Combine(WindowsDirectory, "SoftwareDistribution", "Download")),
                            log, log, innerCt),
                        DefaultStepTimeout, log, ct);
                }
            }
            finally
            {
                if (updateServiceWasRunning)
                {
                    steps.Report(0.9, "Update-Dienst wird wieder gestartet...");
                    using var restartCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _cmd.RunCommandAsync("net", "start wuauserv", log, log, restartCts.Token);
                }
                else
                {
                    log("[6/8] Windows Update-Dienst war bereits deaktiviert – er bleibt deaktiviert.");
                }
            }
            log("[6/8] ✓ Windows Update Download-Cache bereinigt.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [7/8] Windows Update-Bereinigung (Component Store / WinSxS) = die ~570 MB
            // ------------------------------------------------------------------
            string dismArgs = "/Online /Cleanup-Image /StartComponentCleanup" + (ResetComponentStoreBase ? " /ResetBase" : "");
            steps.Begin(6, "Windows Update-Bereinigung (WinSxS)", "Komponentenspeicher wird analysiert – das kann mehrere Minuten dauern");
            steps.ReportIndeterminate("Komponentenspeicher wird analysiert – das kann mehrere Minuten dauern");
            log("[7/8] Windows Update-Bereinigung (Component Store) – DAS KANN MEHRERE MINUTEN DAUERN...");

            var dismOutput = new ConsoleProgressParser(log, (fraction, _) =>
            {
                if (fraction.HasValue) steps.Report(fraction.Value, "Komponentenspeicher wird bereinigt");
                else steps.ReportIndeterminate("Komponentenspeicher wird bereinigt");
            });
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("DISM", dismArgs, dismOutput.Write, dismOutput.Write, innerCt),
                ComponentCleanupTimeout, log, ct);
            log("[7/8] ✓ Windows Update-Bereinigung abgeschlossen.");
            if (ct.IsCancellationRequested) return;

            // ------------------------------------------------------------------
            // [8/8] DNS-Cache
            // ------------------------------------------------------------------
            steps.Begin(7, "DNS-Cache leeren", "ipconfig /flushdns");
            log("[8/8] Leere DNS-Cache...");
            await RunWithStepTimeoutAsync(
                innerCt => _cmd.RunCommandAsync("ipconfig", "/flushdns", log, log, innerCt),
                TimeSpan.FromSeconds(15), log, ct);
            log("[8/8] ✓ DNS-Cache geleert.");
            steps.Complete("Alle Schritte abgeschlossen");

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
            string twDir, Action<string> log, TimeSpan timeout, CancellationToken ct)
        {
            var work = Task.Run(() => DeleteTwTempFolders(twDir, log, ct), CancellationToken.None);
            var delay = Task.Delay(timeout, ct);

            var finished = await Task.WhenAny(work, delay);

            if (finished == delay)
            {
                if (ct.IsCancellationRequested) return;

                log($"[WARNUNG] tw-Bereinigung hat das Zeitlimit ({timeout.TotalSeconds:F0}s) erreicht. " +
                    "Rest läuft im Hintergrund weiter, Bereinigung wird fortgesetzt.");
            }
            else
            {
                try { await work; }
                catch (Exception ex) { log($"[WARNUNG] tw-Bereinigung: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Löscht ALLE Ordner, die auf tw-*.tmp passen, HART (kein Papierkorb).
        /// Pro Ordner try/catch + rd-Fallback, damit ein einzelner Fehlschlag
        /// die Schleife nicht stoppt.
        /// </summary>
        private static void DeleteTwTempFolders(string twDir, Action<string> log, CancellationToken ct)
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
                if (ct.IsCancellationRequested) break;

                // 1) Versuch: managed, rekursiv hart löschen
                try
                {
                    ClearReadOnlyAttributes(folder);
                    Directory.Delete(folder, true);
                    if (!Directory.Exists(folder))
                    {
                        deleted++;
                        continue;
                    }
                }
                catch
                {
                    // fällt durch zum Fallback
                }

                // 2) Fallback: rd /s /q (robuster gegen schreibgeschützte/gesperrte Inhalte)
                try
                {
                    var psi = new ProcessStartInfo("cmd.exe", $"/c rd /s /q \"{folder}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null && p.WaitForExit(8000))
                    {
                        if (!Directory.Exists(folder)) deleted++;
                        else failed++;
                    }
                    else
                    {
                        if (p != null)
                        {
                            try { p.Kill(true); } catch { }
                        }
                        failed++;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            if (!ct.IsCancellationRequested)
            {
                log($"[1/8] ✓ tw-Ordner verarbeitet: {deleted} gelöscht, {failed} fehlgeschlagen (von {folders.Length}).");
            }
        }

        private static void ClearReadOnlyAttributes(string directoryPath)
        {
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                if (!dirInfo.Exists) return;
                dirInfo.Attributes &= ~FileAttributes.ReadOnly;

                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }
            }
            catch
            {
                // Ignorieren, falls einzelne Dateien gesperrt sind.
            }
        }

        /// <summary>
        /// Teilschritt mit eigenem Timeout. Nur Schritt-Timeout -> Warnung + weiter.
        /// User-Abbruch (ct) -> kehrt sauber zurück.
        /// </summary>
        private static async Task RunWithStepTimeoutAsync(
            Func<CancellationToken, Task> operation,
            TimeSpan timeout,
            Action<string> log,
            CancellationToken ct)
        {
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(timeout);
            try
            {
                await operation(stepCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // wird unten gemeldet
            }

            // RunCommandAsync wirft bei Abbruch keine Exception, sondern beendet den Prozess und
            // liefert -1. Deshalb das Zeitlimit hier explizit prüfen und melden.
            if (stepCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                log($"[WARNUNG] Schritt hat das Zeitlimit ({timeout.TotalSeconds:F0}s) überschritten und wurde übersprungen.");
            }
        }

        /// <summary>
        /// Löscht alle Dateien UND Unterordner in einem Ordner (der Ordner selbst bleibt bestehen).
        /// Gesperrte Elemente werden still übersprungen. "del /s" allein würde leere Ordner zurücklassen.
        /// </summary>
        private static string DeleteFolderContentsCommand(string folder) =>
            $@"/c if exist ""{folder}\"" (del /q /f /s ""{folder}\*"" > nul 2>&1 & for /d %d in (""{folder}\*"") do @rd /s /q ""%d"" > nul 2>&1)";

        /// <summary>
        /// Prüft, ob ein Windows-Dienst gerade läuft. "sc query" gibt den Statusnamen
        /// (RUNNING/STOPPED) sprachunabhängig aus, die Überschriften sind lokalisiert.
        /// </summary>
        private async Task<bool> IsServiceRunningAsync(string serviceName, CancellationToken ct)
        {
            bool running = false;
            await _cmd.RunCommandAsync("sc", $"query {serviceName}", line =>
            {
                if (line.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) running = true;
            }, _ => { }, ct);
            return running;
        }

        private static string GetSystemDriveRoot()
        {
            try
            {
                string? root = Path.GetPathRoot(WindowsDirectory);
                if (!string.IsNullOrWhiteSpace(root)) return root;
            }
            catch
            {
            }
            return @"C:\";
        }

        private static long GetFreeBytes(string driveName)
        {
            try
            {
                var drive = new DriveInfo(driveName);
                return drive.IsReady ? drive.AvailableFreeSpace : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string FormatBytes(long bytes)
        {
            double kb = bytes / 1024.0;
            if (kb < 1024.0) return $"{kb:F0} KB";

            double mb = kb / 1024.0;
            return mb >= 1024.0 ? $"{mb / 1024.0:F2} GB" : $"{mb:F1} MB";
        }

        public async Task OptimizeNetworkAsync(Action<string> log, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(log);

            log("--- DEAKTIVIERE P2P WINDOWS-UPDATE-DOWNLOADS ---");
            await Task.Run(() =>
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", true);
                    if (key != null)
                    {
                        key.SetValue("DODownloadMode", 0, Microsoft.Win32.RegistryValueKind.DWord);
                        log("[ERFOLG] P2P-Downloads erfolgreich deaktiviert!");
                    }
                    else
                    {
                        log("[FEHLER] Registry-Schlüssel für Übermittlungsoptimierung konnte nicht geöffnet werden.");
                    }
                }
                catch (Exception ex)
                {
                    log($"[FEHLER] P2P-Einstellung konnte nicht gesetzt werden: {ex.Message}");
                }
            }, ct);
            log("--- NETZWERK OPTIMIERUNG ABGESCHLOSSEN ---");
        }
    }
}

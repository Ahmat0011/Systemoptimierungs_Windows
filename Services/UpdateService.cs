using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    /// <summary>
    /// Führt Software-Updates über Winget durch.
    /// Deep-Scan: Beide Installations-Scopes (machine & user) sowie MS Store werden geprüft.
    /// WICHTIG: --include-unknown wird NIEMALS verwendet. Nur stabile, offizielle Versionen.
    /// </summary>
    public class UpdateService : IUpdateService
    {
        private readonly ICommandExecutionService _cmd;

        public UpdateService(ICommandExecutionService commandService)
        {
            _cmd = commandService;
        }

        public async Task CheckUpdatesAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            log("--- DEEP-SCAN: SUCHE NACH STABILEN SOFTWARE-UPDATES ---");
            var steps = new StepProgress(progress, 1, 2, 2);

            steps.Begin(0, "Paketquellen aktualisieren");
            steps.ReportIndeterminate("winget- und Microsoft-Store-Quellen werden synchronisiert...");
            log("[1/3] Aktualisiere alle Winget-Quellen (winget + msstore)...");
            await RunWingetAsync("source update --accept-source-agreements --disable-interactivity", log, ct);

            if (ct.IsCancellationRequested) return;

            steps.Begin(1, "Systemweite Programme prüfen");
            steps.ReportIndeterminate("Installierte Programme werden mit den Quellen verglichen...");
            log("[2/3] Scanne Machine-Scope (systemweite Installationen)...");
            await RunWingetAsync("upgrade --scope machine --accept-source-agreements --disable-interactivity", log, ct);

            if (ct.IsCancellationRequested) return;

            steps.Begin(2, "Benutzer-Programme & Store-Apps prüfen");
            steps.ReportIndeterminate("Benutzerspezifische Installationen werden geprüft...");
            log("[3/3] Scanne User-Scope (benutzerspezifische Installationen & MS Store)...");
            await RunWingetAsync("upgrade --scope user --accept-source-agreements --disable-interactivity", log, ct);

            steps.Report(1, "Suche abgeschlossen");
            log("--- DEEP-SCAN ABGESCHLOSSEN ---");
        }

        public Task<int> InstallSingleUpdateAsync(string updateId, Action<string> log, CancellationToken ct)
        {
            string arguments = $"upgrade --id \"{updateId}\" --exact --accept-package-agreements --accept-source-agreements --disable-interactivity --silent";
            return RunWingetAsync(arguments, log, ct);
        }

        public async Task InstallUpdatesAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            log("--- INSTALLIERE ALLE STABILEN UPDATES (DEEP-INSTALL) ---");
            var steps = new StepProgress(progress, 1, 4, 4);

            steps.Begin(0, "Paketquellen aktualisieren");
            steps.ReportIndeterminate("winget- und Microsoft-Store-Quellen werden synchronisiert...");
            log("[1/3] Aktualisiere alle Winget-Quellen...");
            await RunWingetAsync("source update --accept-source-agreements --disable-interactivity", log, ct);

            if (ct.IsCancellationRequested) return;

            steps.Begin(1, "Systemweite Updates installieren");
            steps.ReportIndeterminate("Updates werden heruntergeladen und installiert...");
            log("[2/3] Installiere Updates (Machine-Scope)...");
            await RunWingetAsync("upgrade --all --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity --silent", log, ct);

            if (ct.IsCancellationRequested) return;

            steps.Begin(2, "Benutzer-Updates installieren");
            steps.ReportIndeterminate("Updates werden heruntergeladen und installiert...");
            log("[3/3] Installiere Updates (User-Scope / MS Store)...");
            await RunWingetAsync("upgrade --all --scope user --accept-package-agreements --accept-source-agreements --disable-interactivity --silent", log, ct);

            steps.Report(1, "Installation abgeschlossen");
            log("--- INSTALLATION ALLER STABILEN UPDATES ABGESCHLOSSEN ---");
        }

        /// <summary>
        /// winget schreibt UTF-8 und gibt während des Wartens Spinner-Zeichen (- \ | /) als eigene
        /// Zeilen aus. Diese werden herausgefiltert, damit sie das Log nicht fluten.
        /// </summary>
        private Task<int> RunWingetAsync(string arguments, Action<string> log, CancellationToken ct)
        {
            void Forward(string line)
            {
                if (!IsSpinnerLine(line)) log(line);
            }
            return _cmd.RunCommandAsync("winget", arguments, Forward, Forward, ct, new UTF8Encoding(false));
        }

        private static bool IsSpinnerLine(string line)
        {
            string trimmed = line.Trim();
            return trimmed.Length == 1 && "-\\|/".Contains(trimmed[0]);
        }
    }
}

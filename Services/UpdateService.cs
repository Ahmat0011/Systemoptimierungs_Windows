using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    /// <summary>
    /// Führt Software-Updates über Winget durch.
    /// Deep-Scan: Beide Installations-Scopes (machine &amp; user) sowie Pakete ohne Scope-Metadaten und MS Store werden geprüft.
    /// WICHTIG: --include-unknown wird NIEMALS verwendet. Nur stabile, offizielle Versionen.
    /// </summary>
    public class UpdateService : IUpdateService
    {
        private readonly ICommandExecutionService _cmd;

        public UpdateService(ICommandExecutionService commandService)
        {
            _cmd = commandService ?? throw new ArgumentNullException(nameof(commandService));
        }

        public async Task CheckUpdatesAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(log);

            log("--- DEEP-SCAN: SUCHE NACH STABILEN SOFTWARE-UPDATES ---");
            var steps = new StepProgress(progress, 1, 2, 2, 2);

            steps.Begin(0, "Paketquellen aktualisieren");
            steps.ReportIndeterminate("winget- und Microsoft-Store-Quellen werden synchronisiert...");
            log("[1/4] Aktualisiere alle Winget-Quellen (winget + msstore)...");
            int sourceExitCode = await RunWingetAsync("source update --accept-source-agreements --disable-interactivity", log, ct);

            if (ct.IsCancellationRequested) return;

            if (sourceExitCode == -1)
            {
                log("[FEHLER] 'winget' konnte nicht gestartet werden. Bitte den App Installer (Windows-Paketmanager) installieren.");
                steps.Complete("Abgebrochen: winget nicht verfügbar");
                return;
            }

            steps.Begin(1, "Systemweite Programme prüfen");
            steps.ReportIndeterminate("Systemweite Installationen (Machine-Scope) werden verglichen...");
            log("[2/4] Scanne Machine-Scope (systemweite Installationen)...");
            await RunWingetAsync("upgrade --scope machine --accept-source-agreements --disable-interactivity", log, ct);

            if (ct.IsCancellationRequested) return;

            steps.Begin(2, "Benutzer-Programme & Store-Apps prüfen");
            steps.ReportIndeterminate("Benutzerspezifische Installationen (User-Scope) werden geprüft...");
            log("[3/4] Scanne User-Scope (benutzerspezifische Installationen & MS Store)...");
            await RunWingetAsync("upgrade --scope user --accept-source-agreements --disable-interactivity", log, ct);

            if (ct.IsCancellationRequested) return;

            steps.Begin(3, "Scope-unabhängige Pakete prüfen");
            steps.ReportIndeterminate("Programme ohne Scope-Zuordnung werden abgeglichen...");
            log("[4/4] Prüfe restliche Pakete ohne explizite Scope-Zuordnung...");
            await RunWingetAsync("upgrade --accept-source-agreements --disable-interactivity", log, ct);

            steps.Complete("Suche abgeschlossen");
            log("--- DEEP-SCAN ABGESCHLOSSEN ---");
        }

        public Task<int> InstallSingleUpdateAsync(string updateId, Action<string> log, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(log);
            if (string.IsNullOrWhiteSpace(updateId))
                return Task.FromResult(-1);

            string safeId = updateId.Trim().Replace("\"", string.Empty);
            string arguments = $"upgrade --id \"{safeId}\" --exact --accept-package-agreements --accept-source-agreements --disable-interactivity --silent";
            return RunWingetAsync(arguments, log, ct);
        }

        public async Task InstallUpdatesAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(log);

            log("--- INSTALLIERE ALLE STABILEN UPDATES (DEEP-INSTALL) ---");
            var steps = new StepProgress(progress, 1, 4, 4);

            steps.Begin(0, "Paketquellen aktualisieren");
            steps.ReportIndeterminate("winget- und Microsoft-Store-Quellen werden synchronisiert...");
            log("[1/3] Aktualisiere alle Winget-Quellen...");
            int sourceExitCode = await RunWingetAsync("source update --accept-source-agreements --disable-interactivity", log, ct);

            if (ct.IsCancellationRequested) return;

            if (sourceExitCode == -1)
            {
                log("[FEHLER] 'winget' konnte nicht gestartet werden. Bitte den App Installer (Windows-Paketmanager) installieren.");
                steps.Complete("Abgebrochen: winget nicht verfügbar");
                return;
            }

            steps.Begin(1, "Systemweite Updates installieren");
            steps.ReportIndeterminate("Updates werden heruntergeladen und installiert...");
            log("[2/3] Installiere Updates (Machine-Scope)...");
            await RunWingetAsync("upgrade --all --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity --silent", log, ct);

            if (ct.IsCancellationRequested) return;

            steps.Begin(2, "Benutzer-Updates installieren");
            steps.ReportIndeterminate("Updates werden heruntergeladen und installiert...");
            log("[3/3] Installiere Updates (User-Scope / MS Store)...");
            await RunWingetAsync("upgrade --all --scope user --accept-package-agreements --accept-source-agreements --disable-interactivity --silent", log, ct);

            steps.Complete("Installation abgeschlossen");
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
            if (string.IsNullOrWhiteSpace(line)) return false;

            ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty) return false;

            if (trimmed.Length == 1 && "-\\|/".Contains(trimmed[0]))
                return true;

            // Manche winget-Versionen schreiben mehrere durch Backspace/Leerzeichen getrennte Spinner-Zeichen in eine Zeile
            foreach (char c in trimmed)
            {
                if (c != '-' && c != '\\' && c != '|' && c != '/' && c != '\b' && c != '\r' && c != ' ')
                    return false;
            }
            return true;
        }
    }
}

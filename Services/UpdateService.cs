using System;
using System.Threading;
using System.Threading.Tasks;

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

        public async Task CheckUpdatesAsync(Action<string> log, CancellationToken ct)
        {
            log("--- DEEP-SCAN: SUCHE NACH STABILEN SOFTWARE-UPDATES ---");

            log("[1/3] Aktualisiere alle Winget-Quellen (winget + msstore)...");
            await _cmd.RunCommandAsync("winget",
                "source update --accept-source-agreements",
                log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[2/3] Scanne Machine-Scope (systemweite Installationen)...");
            await _cmd.RunCommandAsync("winget",
                "upgrade --scope machine --accept-source-agreements",
                log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[3/3] Scanne User-Scope (benutzerspezifische Installationen & MS Store)...");
            await _cmd.RunCommandAsync("winget",
                "upgrade --scope user --accept-source-agreements",
                log, log, ct);

            log("--- DEEP-SCAN ABGESCHLOSSEN ---");
        }

        public async Task InstallUpdatesAsync(Action<string> log, CancellationToken ct)
        {
            log("--- INSTALLIERE ALLE STABILEN UPDATES (DEEP-INSTALL) ---");

            log("[1/3] Aktualisiere alle Winget-Quellen...");
            await _cmd.RunCommandAsync("winget",
                "source update --accept-source-agreements",
                log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[2/3] Installiere Updates (Machine-Scope)...");
            await _cmd.RunCommandAsync("winget",
                "upgrade --all --scope machine --accept-package-agreements --accept-source-agreements",
                log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[3/3] Installiere Updates (User-Scope / MS Store)...");
            await _cmd.RunCommandAsync("winget",
                "upgrade --all --scope user --accept-package-agreements --accept-source-agreements",
                log, log, ct);

            log("--- INSTALLATION ALLER STABILEN UPDATES ABGESCHLOSSEN ---");
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class RepairService : IRepairService
    {
        private readonly ICommandExecutionService _cmd;

        public RepairService(ICommandExecutionService commandService)
        {
            _cmd = commandService;
        }

        public async Task RepairSystemAsync(Action<string> log, CancellationToken ct)
        {
            log("--- STARTE WINDOWS TIEFENREPARATUR ---");

            log("[Schritt 1/2] DISM: Component Store wiederherstellen (Das kann dauern...)");
            await _cmd.RunCommandAsync("DISM",
                "/Online /Cleanup-Image /RestoreHealth",
                log, log, ct);

            if (ct.IsCancellationRequested) return;

            log("[Schritt 2/2] SFC: Systemdateien scannen und reparieren...");
            await _cmd.RunCommandAsync("sfc", "/scannow", log, log, ct);

            log("--- REPARATUR ABGESCHLOSSEN ---");
        }
    }
}

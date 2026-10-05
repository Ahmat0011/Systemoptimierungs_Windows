using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public class RepairService : IRepairService
    {
        private readonly ICommandExecutionService _cmd;

        public RepairService(ICommandExecutionService commandService)
        {
            _cmd = commandService;
        }

        public async Task RepairSystemAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            log("--- STARTE WINDOWS TIEFENREPARATUR ---");
            var steps = new StepProgress(progress, 1, 1);

            log("[Schritt 1/2] DISM: Component Store wiederherstellen (Das kann dauern...)");
            steps.Begin(0, "DISM – Komponentenspeicher wiederherstellen", "Abbild wird überprüft...");
            var dismOutput = new ConsoleProgressParser(log, (fraction, _) =>
            {
                if (fraction.HasValue) steps.Report(fraction.Value, "Komponentenspeicher wird überprüft und repariert");
            });
            int dismExit = await _cmd.RunCommandAsync("DISM",
                "/Online /Cleanup-Image /RestoreHealth",
                dismOutput.Write, dismOutput.Write, ct);

            if (ct.IsCancellationRequested) return;

            if (dismExit == 0)
                log("[ERFOLG] DISM: Der Komponentenspeicher ist intakt bzw. wurde repariert.");
            else
                log($"[WARNUNG] DISM wurde mit Exit-Code {dismExit} beendet. SFC wird trotzdem ausgeführt.");

            log("[Schritt 2/2] SFC: Systemdateien scannen und reparieren...");
            // sfc schreibt umgeleitete Ausgaben als UTF-16 – ohne passende Kodierung ist das Log unlesbar.
            steps.Begin(1, "SFC – Systemdateien prüfen", "Systemüberprüfung wird gestartet...");
            var sfcOutput = new ConsoleProgressParser(log, (fraction, _) =>
            {
                if (fraction.HasValue) steps.Report(fraction.Value, "Geschützte Systemdateien werden überprüft");
            });
            int sfcExit = await _cmd.RunCommandAsync("sfc", "/scannow", sfcOutput.Write, sfcOutput.Write, ct, Encoding.Unicode);

            if (ct.IsCancellationRequested) return;

            log(sfcExit == 0
                ? "[ERFOLG] SFC: Überprüfung abgeschlossen (Details siehe Ausgabe oben)."
                : $"[WARNUNG] SFC wurde mit Exit-Code {sfcExit} beendet (Details siehe Ausgabe oben).");

            steps.Report(1, "Reparatur abgeschlossen");
            log("--- REPARATUR ABGESCHLOSSEN ---");
        }
    }
}

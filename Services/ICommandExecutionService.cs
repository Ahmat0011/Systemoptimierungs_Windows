using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface ICommandExecutionService
    {
        /// <summary>
        /// Führt einen Befehl asynchron aus und leitet stdout/stderr zeilenweise weiter.
        /// Wirft bei Abbruch KEINE Exception, sondern liefert -1 und beendet den Prozessbaum.
        /// </summary>
        /// <param name="outputEncoding">
        /// Kodierung der Ausgabe. Standard (null) = OEM-Codepage wie bei allen klassischen
        /// Konsolenprogrammen. Ausnahmen: sfc schreibt UTF-16 (<see cref="Encoding.Unicode"/>).
        /// </param>
        Task<int> RunCommandAsync(
            string command,
            string arguments,
            Action<string> onOutput,
            Action<string> onError,
            CancellationToken cancellationToken,
            Encoding? outputEncoding = null);

        /// <summary>
        /// Startet einen Befehl (z. B. einen Deinstaller) und wartet, bis er UND alle von ihm
        /// gestarteten Kindprozesse beendet sind. Viele Deinstaller starten sich selbst neu und
        /// beenden den ursprünglichen Prozess sofort – ohne diese Wartelogik gälte die
        /// Deinstallation zu früh als abgeschlossen.
        /// </summary>
        /// <param name="isFinished">
        /// Optional: wird während des Wartens auf Kindprozesse zyklisch geprüft. Liefert es true
        /// (z. B. weil der Registry-Eintrag verschwunden ist), wird nach kurzer Nachlaufzeit nicht
        /// länger auf Kindprozesse gewartet (etwa auf einen vom Deinstaller geöffneten Browser).
        /// </param>
        /// <returns>Exit-Code des gestarteten Prozesses oder -1 bei Fehler/Abbruch.</returns>
        Task<int> RunCommandAndWaitForProcessTreeAsync(
            string command,
            string arguments,
            Action<string> onOutput,
            Action<string> onError,
            Func<bool>? isFinished,
            CancellationToken cancellationToken);
    }
}

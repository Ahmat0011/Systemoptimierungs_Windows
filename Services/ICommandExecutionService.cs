using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface ICommandExecutionService
    {
        /// <summary>
        /// Führt einen Prozess aus und liefert den Exit-Code zurück (0 = Erfolg, -1 = Fehler/Abbruch).
        /// </summary>
        Task<int> RunCommandAsync(
            string command,
            string arguments,
            Action<string> onOutput,
            Action<string> onError,
            CancellationToken cancellationToken);
    }
}

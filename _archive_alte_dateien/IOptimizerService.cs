using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface IOptimizerService
    {
        Task CheckUpdatesAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task InstallUpdatesAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task RepairSystemAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task OptimizeNetworkAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task CleanupSystemAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task CheckDriversAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task InstallDriversAsync(Action<string> onLog, CancellationToken cancellationToken);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface ICleanupService
    {
        Task CleanupSystemAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task OptimizeNetworkAsync(Action<string> onLog, CancellationToken cancellationToken);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface ICleanupService
    {
        Task CleanupSystemAsync(Action<string> onLog, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
        Task OptimizeNetworkAsync(Action<string> onLog, CancellationToken cancellationToken);
    }
}

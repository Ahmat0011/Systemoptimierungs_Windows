using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface ICleanupService
    {
        Task CleanupSystemAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct);
        Task OptimizeNetworkAsync(Action<string> log, CancellationToken ct);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface IUpdateService
    {
        Task CheckUpdatesAsync(Action<string> onLog, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
        Task<int> InstallSingleUpdateAsync(string updateId, Action<string> onLog, CancellationToken cancellationToken);
        Task InstallUpdatesAsync(Action<string> onLog, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
    }
}

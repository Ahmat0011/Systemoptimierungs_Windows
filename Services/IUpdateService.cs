using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface IUpdateService
    {
        Task CheckUpdatesAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct);
        Task<int> InstallSingleUpdateAsync(string updateId, Action<string> log, CancellationToken ct);
        Task InstallUpdatesAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct);
    }
}

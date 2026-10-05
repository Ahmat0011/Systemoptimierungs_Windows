using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface IDriverService
    {
        Task CheckDriversAsync(Action<string> onLog, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
        Task InstallDriversAsync(Action<string> onLog, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
    }
}

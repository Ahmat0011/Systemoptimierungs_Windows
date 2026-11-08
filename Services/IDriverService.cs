using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface IDriverService
    {
        Task CheckDriversAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct);
        Task InstallDriversAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct);
    }
}

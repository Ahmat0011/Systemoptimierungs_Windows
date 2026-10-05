using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface IRepairService
    {
        Task RepairSystemAsync(Action<string> onLog, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
    }
}

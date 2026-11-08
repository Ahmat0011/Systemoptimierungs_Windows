using System;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public interface IRepairService
    {
        Task RepairSystemAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct);
    }
}

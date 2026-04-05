using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface IRepairService
    {
        Task RepairSystemAsync(Action<string> onLog, CancellationToken cancellationToken);
    }
}

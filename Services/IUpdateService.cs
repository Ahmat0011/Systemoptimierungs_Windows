using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface IUpdateService
    {
        Task CheckUpdatesAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task InstallUpdatesAsync(Action<string> onLog, CancellationToken cancellationToken);
    }
}

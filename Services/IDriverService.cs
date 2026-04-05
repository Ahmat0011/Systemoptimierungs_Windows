using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface IDriverService
    {
        Task CheckDriversAsync(Action<string> onLog, CancellationToken cancellationToken);
        Task InstallDriversAsync(Action<string> onLog, CancellationToken cancellationToken);
    }
}

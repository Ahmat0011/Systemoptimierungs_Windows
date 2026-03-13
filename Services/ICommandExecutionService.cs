using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface ICommandExecutionService
    {
        Task RunCommandAsync(string command, string arguments, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken);
    }
}

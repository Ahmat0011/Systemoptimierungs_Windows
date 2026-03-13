using System;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public interface IPowerShellService
    {
        Task RunScriptAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken);
    }
}

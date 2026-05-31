using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public interface IPerformanceService
    {
        Task BoostRamAsync(Action<string> log, CancellationToken cancellationToken);
        List<StartupItem> GetStartupItems();
        Task ToggleStartupItemAsync(StartupItem item);
    }
}

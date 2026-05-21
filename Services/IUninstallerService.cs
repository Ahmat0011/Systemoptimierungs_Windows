using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public interface IUninstallerService
    {
        List<SoftwareItem> GetInstalledSoftware();
        Task<List<SoftwareItem>> GetInstalledUwpAppsAsync(Action<string> log, CancellationToken cancellationToken);
        Task UninstallSoftwareAsync(SoftwareItem item, bool cleanLeftovers, Action<string> log, CancellationToken cancellationToken);
    }
}

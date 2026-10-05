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

        /// <summary>
        /// Deinstalliert ein Programm und prüft anschließend, ob es wirklich entfernt wurde.
        /// </summary>
        /// <returns>true nur, wenn das Programm nachweislich nicht mehr installiert ist.</returns>
        Task<bool> UninstallSoftwareAsync(SoftwareItem item, bool cleanLeftovers, Action<string> log, CancellationToken cancellationToken);

        /// <summary>Günstiger Fingerabdruck der registrierten Store-Pakete, um unnötige PowerShell-Scans zu vermeiden.</summary>
        int GetStoreAppsFingerprint();
    }
}

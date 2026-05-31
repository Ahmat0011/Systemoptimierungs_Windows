using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public interface IRecoveryService
    {
        List<string> GetAvailableDrives();
        Task<List<RecoverableFile>> ScanDeletedFilesAsync(
            string driveLetter,
            bool includeDocs,
            bool includeImages,
            bool includeVideos,
            bool includeMusic,
            Action<string> log,
            CancellationToken cancellationToken);
        Task RestoreFilesAsync(
            List<RecoverableFile> files,
            string targetDirectory,
            Action<string> log,
            CancellationToken cancellationToken);
    }
}

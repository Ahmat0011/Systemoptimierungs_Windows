using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public interface IDeepRecoveryService
    {
        // Holt die echten physischen Laufwerke (z.B. \\.\PhysicalDrive0) statt nur logische Buchstaben
        List<string> GetPhysicalDrives();

        // Scannt eine Festplatte tief nach verlorenen Partitionsstrukturen (TestDisk-Kernfunktion)
        Task<List<string>> ScanLostPartitionsAsync(string physicalDrive, Action<string> logCallback, CancellationToken ct);

        // Liest Sektoren direkt aus, um formatierte Dateien zu finden (Deep Carving)
        Task DeepSectorCarveAsync(string physicalDrive, string targetFolder, Action<string> logCallback, CancellationToken ct);

        // Scannt rohe Sektordaten blockweise nach gelöschten Dateien (TestDisk Sektoren-Methode)
        Task<List<RecoverableFile>> ScanPhysicalSectorsAsync(
            string physicalDrive,
            bool includeDocs,
            bool includeImages,
            bool includeVideos,
            bool includeMusic,
            Action<RecoverableFile> onFileFound,
            Action<string> logCallback,
            CancellationToken ct);
    }
}

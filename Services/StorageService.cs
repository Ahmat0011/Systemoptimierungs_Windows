using System;
using System.IO;

namespace SystemOptimierer.Services
{
    public class StorageService : IStorageService
    {
        public string GetDriveSpaceString(string driveLetter)
        {
            try
            {
                var drive = new DriveInfo(driveLetter);
                if (!drive.IsReady)
                {
                    return $"{driveLetter.ToUpper()}: Nicht bereit";
                }
                
                double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                double totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                
                return $"{driveLetter.ToUpper()}: {freeGb:F0} GB frei von {totalGb:F0} GB";
            }
            catch
            {
                return $"{driveLetter.ToUpper()}: Nicht verfügbar";
            }
        }
    }
}

using System;
using System.IO;

namespace SystemOptimierer.Services
{
    public class StorageService : IStorageService
    {
        public string GetDriveSpaceString(string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                return "Unbekannt: Nicht verfügbar";

            string cleanLetter = char.ToUpperInvariant(driveLetter.Trim()[0]).ToString();

            try
            {
                var drive = new DriveInfo(cleanLetter);
                if (!drive.IsReady)
                {
                    return $"{cleanLetter}: Nicht bereit";
                }

                double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                double totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);

                return $"{cleanLetter}: {freeGb:F0} GB frei von {totalGb:F0} GB";
            }
            catch
            {
                return $"{cleanLetter}: Nicht verfügbar";
            }
        }
    }
}

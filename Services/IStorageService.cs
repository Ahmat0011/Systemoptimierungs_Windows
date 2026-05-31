using System;

namespace SystemOptimierer.Services
{
    public interface IStorageService
    {
        string GetDriveSpaceString(string driveLetter);
    }
}

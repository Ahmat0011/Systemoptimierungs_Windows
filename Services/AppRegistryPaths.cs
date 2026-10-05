namespace SystemOptimierer.Services
{
    /// <summary>
    /// Registry-Pfade, aus denen installierte Programme und Store-Apps gelesen bzw. überwacht werden.
    /// </summary>
    internal static class AppRegistryPaths
    {
        /// <summary>Uninstall-Zweig (HKLM 64/32 Bit sowie HKCU) der klassischen Desktop-Programme.</summary>
        public const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>Uninstall-Zweig der 32-Bit-Programme auf 64-Bit-Systemen (HKLM).</summary>
        public const string UninstallWow6432 = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>Pro Benutzer registrierte Store-Pakete (HKCU) – je Paket ein Unterschlüssel.</summary>
        public const string StorePackagesCurrentUser = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        /// <summary>Für alle Benutzer installierte Store-Pakete (HKLM) – je Paket ein Unterschlüssel.</summary>
        public const string StorePackagesAllUsers = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";
    }
}

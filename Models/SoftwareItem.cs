namespace SystemOptimierer.Models
{
    public class SoftwareItem
    {
        public string Name { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string DisplayVersion { get; set; } = string.Empty;
        public string UninstallString { get; set; } = string.Empty;
        public string InstallDate { get; set; } = string.Empty;
        public string EstimatedSize { get; set; } = string.Empty;
        public string RegistryPath { get; set; } = string.Empty;
        public bool IsUwp { get; set; }
        public string PackageFullName { get; set; } = string.Empty;
    }
}

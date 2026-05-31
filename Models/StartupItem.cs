namespace SystemOptimierer.Models
{
    public class StartupItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string RegistryKeyPath { get; set; } = string.Empty; // "HKLM" or "HKCU"
        public bool IsEnabled { get; set; }
    }
}

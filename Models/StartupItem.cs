namespace SystemOptimierer.Models
{
    public class StartupItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string RegistryKeyPath { get; set; } = string.Empty; // "HKCU", "HKLM" oder "HKLM (32-Bit)"
        public bool IsEnabled { get; set; }

        /// <summary>Eintrag steht in "Run", wurde aber über Task-Manager/Einstellungen deaktiviert (StartupApproved).</summary>
        public bool IsDisabledByWindows { get; set; }
    }
}

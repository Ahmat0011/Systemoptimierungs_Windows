using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemOptimierer.Models
{
    public class SoftwareItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Name { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string DisplayVersion { get; set; } = string.Empty;
        public string UninstallString { get; set; } = string.Empty;
        public string InstallDate { get; set; } = string.Empty;
        public string EstimatedSize { get; set; } = string.Empty;
        public string RegistryPath { get; set; } = string.Empty;
        public string RegistryKey { get; set; } = string.Empty;
        public bool IsUwp { get; set; }
        public string PackageFullName { get; set; } = string.Empty;

        // Für die präzise Sortierung nach Installationsdatum und Größe im DataGrid
        public DateTime? InstallDateTime { get; set; }
        public string InstallDateDisplay { get; set; } = "Unbekannt";
        public long EstimatedSizeKb { get; set; }

        /// <summary>
        /// Eindeutige Kennung für den Abgleich bei Live-Aktualisierungen
        /// (Registry-Pfad bei Desktop-Programmen, Paketname bei Store-Apps).
        /// </summary>
        public string Identity => IsUwp
            ? "uwp:" + PackageFullName
            : "reg:" + (string.IsNullOrEmpty(RegistryPath) ? Name : RegistryPath);

        /// <summary>Prüft, ob sich die in der Liste angezeigten Daten unterscheiden (z. B. nach einem Update).</summary>
        public bool HasSameDisplayData(SoftwareItem other) =>
            Name == other.Name &&
            Publisher == other.Publisher &&
            DisplayVersion == other.DisplayVersion &&
            InstallDateDisplay == other.InstallDateDisplay &&
            InstallDateTime == other.InstallDateTime &&
            EstimatedSizeKb == other.EstimatedSizeKb;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
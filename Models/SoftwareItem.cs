using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemOptimierer.Models
{
    public class SoftwareItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _name = string.Empty;
        private string _publisher = string.Empty;
        private string _displayVersion = string.Empty;
        private string _uninstallString = string.Empty;
        private string _installDate = string.Empty;
        private string _estimatedSize = string.Empty;
        private DateTime? _installDateTime;
        private string _installDateDisplay = "Unbekannt";
        private long _estimatedSizeKb;

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

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Identity));
                }
            }
        }

        public string Publisher
        {
            get => _publisher;
            set
            {
                if (_publisher != value)
                {
                    _publisher = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayVersion
        {
            get => _displayVersion;
            set
            {
                if (_displayVersion != value)
                {
                    _displayVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        public string UninstallString
        {
            get => _uninstallString;
            set
            {
                if (_uninstallString != value)
                {
                    _uninstallString = value;
                    OnPropertyChanged();
                }
            }
        }

        public string InstallDate
        {
            get => _installDate;
            set
            {
                if (_installDate != value)
                {
                    _installDate = value;
                    OnPropertyChanged();
                }
            }
        }

        public string EstimatedSize
        {
            get => _estimatedSize;
            set
            {
                if (_estimatedSize != value)
                {
                    _estimatedSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public string RegistryPath { get; set; } = string.Empty;
        public string RegistryKey { get; set; } = string.Empty;
        public bool IsUwp { get; set; }
        public string PackageFullName { get; set; } = string.Empty;

        // Für die präzise Sortierung nach Installationsdatum und Größe im DataGrid
        public DateTime? InstallDateTime
        {
            get => _installDateTime;
            set
            {
                if (_installDateTime != value)
                {
                    _installDateTime = value;
                    OnPropertyChanged();
                }
            }
        }

        public string InstallDateDisplay
        {
            get => _installDateDisplay;
            set
            {
                if (_installDateDisplay != value)
                {
                    _installDateDisplay = value;
                    OnPropertyChanged();
                }
            }
        }

        public long EstimatedSizeKb
        {
            get => _estimatedSizeKb;
            set
            {
                if (_estimatedSizeKb != value)
                {
                    _estimatedSizeKb = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Eindeutige Kennung für den Abgleich bei Live-Aktualisierungen
        /// (Registry-Pfad bei Desktop-Programmen, Paketname bei Store-Apps).
        /// </summary>
        public string Identity => IsUwp
            ? "uwp:" + PackageFullName
            : "reg:" + (string.IsNullOrEmpty(RegistryPath) ? Name : RegistryPath);

        /// <summary>Prüft, ob sich die in der Liste angezeigten oder für die Deinstallation relevanten Daten unterscheiden.</summary>
        public bool HasSameDisplayData(SoftwareItem? other) =>
            other is not null &&
            Name == other.Name &&
            Publisher == other.Publisher &&
            DisplayVersion == other.DisplayVersion &&
            UninstallString == other.UninstallString &&
            EstimatedSize == other.EstimatedSize &&
            InstallDateDisplay == other.InstallDateDisplay &&
            InstallDateTime == other.InstallDateTime &&
            EstimatedSizeKb == other.EstimatedSizeKb;

        /// <summary>Übernimmt geänderte Werte aus einem neuen Scan-Eintrag direkt in dieses Objekt.</summary>
        public void UpdateFrom(SoftwareItem other)
        {
            if (other is null) return;
            Name = other.Name;
            Publisher = other.Publisher;
            DisplayVersion = other.DisplayVersion;
            UninstallString = other.UninstallString;
            InstallDate = other.InstallDate;
            EstimatedSize = other.EstimatedSize;
            InstallDateTime = other.InstallDateTime;
            InstallDateDisplay = other.InstallDateDisplay;
            EstimatedSizeKb = other.EstimatedSizeKb;
            RegistryPath = other.RegistryPath;
            RegistryKey = other.RegistryKey;
            PackageFullName = other.PackageFullName;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

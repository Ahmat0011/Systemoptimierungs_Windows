using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemOptimierer.Models
{
    /// <summary>
    /// Eintrag aus den Autostart-Zweigen. Benachrichtigt die Oberfläche bei Statusänderungen,
    /// damit nach dem Umschalten kein Neuaufbau der kompletten Liste nötig ist.
    /// </summary>
    public class StartupItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _path = string.Empty;
        private string _registryKeyPath = string.Empty;
        private bool _isEnabled;
        private bool _isDisabledByWindows;

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                OnPropertyChanged();
            }
        }

        public string Path
        {
            get => _path;
            set
            {
                if (_path == value) return;
                _path = value;
                OnPropertyChanged();
            }
        }

        /// <summary>"HKCU", "HKLM" oder "HKLM (32-Bit)".</summary>
        public string RegistryKeyPath
        {
            get => _registryKeyPath;
            set
            {
                if (_registryKeyPath == value) return;
                _registryKeyPath = value;
                OnPropertyChanged();
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Eintrag steht in "Run", wurde aber über Task-Manager/Einstellungen deaktiviert (StartupApproved).</summary>
        public bool IsDisabledByWindows
        {
            get => _isDisabledByWindows;
            set
            {
                if (_isDisabledByWindows == value) return;
                _isDisabledByWindows = value;
                OnPropertyChanged();
            }
        }

        public void UpdateFrom(StartupItem other)
        {
            if (other is null) return;
            Name = other.Name;
            Path = other.Path;
            RegistryKeyPath = other.RegistryKeyPath;
            IsEnabled = other.IsEnabled;
            IsDisabledByWindows = other.IsDisabledByWindows;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

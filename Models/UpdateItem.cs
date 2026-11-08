using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemOptimierer.Models
{
    public class UpdateItem : INotifyPropertyChanged
    {
        private string _status = "Ausstehend";
        private string _detail = string.Empty;
        private string _name = string.Empty;
        private string _type = string.Empty;
        private string _currentVersion = string.Empty;
        private string _availableVersion = string.Empty;
        private string _updateId = string.Empty;

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Detail
        {
            get => _detail;
            set
            {
                if (_detail != value)
                {
                    _detail = value;
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
                }
            }
        }

        /// <summary>"Software-Update" oder "Treiber-Update".</summary>
        public string Type
        {
            get => _type;
            set
            {
                if (_type != value)
                {
                    _type = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentVersion
        {
            get => _currentVersion;
            set
            {
                if (_currentVersion != value)
                {
                    _currentVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AvailableVersion
        {
            get => _availableVersion;
            set
            {
                if (_availableVersion != value)
                {
                    _availableVersion = value;
                    OnPropertyChanged();
                }
            }
        }

        public string UpdateId
        {
            get => _updateId;
            set
            {
                if (_updateId != value)
                {
                    _updateId = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

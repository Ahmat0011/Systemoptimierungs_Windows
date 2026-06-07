using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemOptimierer.Models
{
    public class UpdateItem : INotifyPropertyChanged
    {
        private string _status = "Ausstehend";
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

        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // "Software-Update" or "Treiber-Update"
        public string CurrentVersion { get; set; } = string.Empty;
        public string AvailableVersion { get; set; } = string.Empty;
        public string UpdateId { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

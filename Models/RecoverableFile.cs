using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemOptimierer.Models
{
    public class RecoverableFile : INotifyPropertyChanged
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
        public string OriginalPath { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string DateDeleted { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty; // "Dokumente", "Bilder", "Videos", "Musik"
        public string SourcePath { get; set; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

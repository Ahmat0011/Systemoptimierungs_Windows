using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SystemOptimierer.Core;
using SystemOptimierer.Models;
using SystemOptimierer.Services;

namespace SystemOptimierer.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        // Dependency Services
        private readonly IUpdateService _updateService;
        private readonly IDriverService _driverService;
        private readonly IRepairService _repairService;
        private readonly ICleanupService _cleanupService;
        private readonly IUninstallerService _uninstallerService;
        private readonly IStorageService _storageService;
        private readonly IPerformanceService _performanceService;
        private readonly IRecoveryService _recoveryService;
        private readonly IDeepRecoveryService _deepRecoveryService;

        // Nullable field (Warnung behoben)
        private string? _validField = null;

        // INotifyPropertyChanged Implementierung
        public event PropertyChangedEventHandler? PropertyChanged;

        // Vollständiger Konstruktor (Fehler behoben)
        public MainViewModel(
            IUpdateService updateService,
            IDriverService driverService,
            IRepairService repairService,
            ICleanupService cleanupService,
            IUninstallerService uninstallerService,
            IStorageService storageService,
            IPerformanceService performanceService,
            IRecoveryService recoveryService,
            IDeepRecoveryService deepRecoveryService)
        {
            _updateService = updateService;
            _driverService = driverService;
            _repairService = repairService;
            _cleanupService = cleanupService;
            _uninstallerService = uninstallerService;
            _storageService = storageService;
            _performanceService = performanceService;
            _recoveryService = recoveryService;
            _deepRecoveryService = deepRecoveryService;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Beispiel-Methode mit korrekter Variablenverwendung
        private void ExampleMethod()
        {
            int x = 5; // Jetzt tatsächlich verwendet
            Console.WriteLine(x);
        }

        // [Rest des bestehenden Codes...]
    }
}
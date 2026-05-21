using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SystemOptimierer.Core;
using SystemOptimierer.Models;
using SystemOptimierer.Services;

namespace SystemOptimierer.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IUpdateService  _updateService;
        private readonly IDriverService  _driverService;
        private readonly IRepairService  _repairService;
        private readonly ICleanupService _cleanupService;
        private readonly IUninstallerService _uninstallerService;
        private CancellationTokenSource? _cts;

        public MainViewModel(
            IUpdateService  updateService,
            IDriverService  driverService,
            IRepairService  repairService,
            ICleanupService cleanupService,
            IUninstallerService uninstallerService)
        {
            _updateService  = updateService;
            _driverService  = driverService;
            _repairService  = repairService;
            _cleanupService = cleanupService;
            _uninstallerService = uninstallerService;

            // Update Center
            CheckUpdatesCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates suchen",       ct => _updateService.CheckUpdatesAsync(Log, ct)),   _ => !IsBusy);
            InstallUpdatesCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates installieren", ct => _updateService.InstallUpdatesAsync(Log, ct)), _ => !IsBusy);
            CheckDriversCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber suchen",       ct => _driverService.CheckDriversAsync(Log, ct)),   _ => !IsBusy);
            InstallDriversCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber installieren", ct => _driverService.InstallDriversAsync(Log, ct)), _ => !IsBusy);

            // System-Reparatur
            RepairSystemCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("System reparieren",   ct => _repairService.RepairSystemAsync(Log, ct)),    _ => !IsBusy);

            // Bereinigung & Optimierung
            CleanupSystemCommand  = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("System bereinigen",   ct => _cleanupService.CleanupSystemAsync(Log, ct),  false), _ => !IsBusy);
            OptimizeNetworkCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Netzwerk optimieren", ct => _cleanupService.OptimizeNetworkAsync(Log, ct), false), _ => !IsBusy);

            // Uninstaller (Software & Apps Manager)
            LoadAppsCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Software & Apps laden", ct => LoadAppsAsync(ct), false), _ => !IsBusy);
            UninstallCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Deinstallation", ct => UninstallSelectedAppAsync(ct), false), _ => !IsBusy && (SelectedSoftware != null || SelectedUwpApp != null));

            // Global
            CancelCommand  = new RelayCommand(_ => CancelOperation(), _ => IsBusy);

            Log("System Optimierer gestartet. Bereit für Befehle...\n");

            // Apps beim Start im Hintergrund laden
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                await ExecuteWithBusyStateAsync("Software & Apps laden", ct => LoadAppsAsync(ct), false);
            });
        }

        #region Properties

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsNotBusy));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsNotBusy => !_isBusy;

        private string _logText = string.Empty;
        public string LogText
        {
            get => _logText;
            set
            {
                if (_logText != value)
                {
                    _logText = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _statusMessage = "Bereit";
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        private ObservableCollection<SoftwareItem> _installedSoftware = new();
        public ObservableCollection<SoftwareItem> InstalledSoftware
        {
            get => _installedSoftware;
            set
            {
                _installedSoftware = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilteredSoftware));
            }
        }

        private ObservableCollection<SoftwareItem> _installedUwpApps = new();
        public ObservableCollection<SoftwareItem> InstalledUwpApps
        {
            get => _installedUwpApps;
            set
            {
                _installedUwpApps = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilteredUwpApps));
            }
        }

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FilteredSoftware));
                    OnPropertyChanged(nameof(FilteredUwpApps));
                }
            }
        }

        private bool _cleanLeftovers = true;
        public bool CleanLeftovers
        {
            get => _cleanLeftovers;
            set
            {
                if (_cleanLeftovers != value)
                {
                    _cleanLeftovers = value;
                    OnPropertyChanged();
                }
            }
        }

        private SoftwareItem? _selectedSoftware;
        public SoftwareItem? SelectedSoftware
        {
            get => _selectedSoftware;
            set
            {
                if (_selectedSoftware != value)
                {
                    _selectedSoftware = value;
                    OnPropertyChanged();
                    if (value != null)
                    {
                        _selectedUwpApp = null;
                        OnPropertyChanged(nameof(SelectedUwpApp));
                    }
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private SoftwareItem? _selectedUwpApp;
        public SoftwareItem? SelectedUwpApp
        {
            get => _selectedUwpApp;
            set
            {
                if (_selectedUwpApp != value)
                {
                    _selectedUwpApp = value;
                    OnPropertyChanged();
                    if (value != null)
                    {
                        _selectedSoftware = null;
                        OnPropertyChanged(nameof(SelectedSoftware));
                    }
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public IEnumerable<SoftwareItem> FilteredSoftware
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SearchText))
                {
                    return _installedSoftware;
                }
                return _installedSoftware.Where(app =>
                    app.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    app.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IEnumerable<SoftwareItem> FilteredUwpApps
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SearchText))
                {
                    return _installedUwpApps;
                }
                return _installedUwpApps.Where(app =>
                    app.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    app.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        #endregion

        #region Commands

        public ICommand CheckUpdatesCommand { get; }
        public ICommand InstallUpdatesCommand { get; }
        public ICommand RepairSystemCommand { get; }
        public ICommand OptimizeNetworkCommand { get; }
        public ICommand CleanupSystemCommand { get; }
        public ICommand CheckDriversCommand { get; }
        public ICommand InstallDriversCommand { get; }
        public ICommand LoadAppsCommand { get; }
        public ICommand UninstallCommand { get; }
        public ICommand CancelCommand { get; }

        #endregion

        #region Methods

        private void Log(string message)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                LogText += $"{message}\n";
            }));
        }

        private bool IsInternetAvailable()
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = ping.Send("1.1.1.1", 2000);
                    return reply.Status == IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task ExecuteWithBusyStateAsync(string operationName, Func<CancellationToken, Task> action, bool requiresInternet = true)
        {
            if (requiresInternet && !IsInternetAvailable())
            {
                Log($"FEHLER: Keine Internetverbindung gefunden! [{operationName}] abgebrochen.\n");
                return;
            }

            IsBusy = true;
            StatusMessage = $"{operationName} läuft...";
            _cts = new CancellationTokenSource();

            try
            {
                await Task.Run(async () => await action(_cts.Token));
                StatusMessage = $"{operationName} erfolgreich abgeschlossen.";
            }
            catch (TaskCanceledException)
            {
                Log($"\n--- VORGANG ABGEBROCHEN: {operationName} ---");
                StatusMessage = "Abgebrochen";
            }
            catch (Exception ex)
            {
                Log($"\nKritischer Fehler bei {operationName}: {ex.Message}");
                StatusMessage = "Fehler aufgetreten";
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                IsBusy = false;
            }
        }

        private async Task LoadAppsAsync(CancellationToken ct)
        {
            Log("Lese installierte System-Programme aus (32-Bit & 64-Bit Scopes) & frage Windows Apps ab...");
            
            var classicTask = Task.Run(() => _uninstallerService.GetInstalledSoftware(), ct);
            var uwpTask = _uninstallerService.GetInstalledUwpAppsAsync(Log, ct);

            await Task.WhenAll(classicTask, uwpTask);
            
            if (ct.IsCancellationRequested) return;

            var classicApps = classicTask.Result;
            var uwpApps = uwpTask.Result;

            // Auf dem Dispatcher-Thread in die Collections schreiben
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _installedSoftware.Clear();
                foreach (var app in classicApps)
                {
                    _installedSoftware.Add(app);
                }

                _installedUwpApps.Clear();
                foreach (var app in uwpApps)
                {
                    _installedUwpApps.Add(app);
                }

                OnPropertyChanged(nameof(FilteredSoftware));
                OnPropertyChanged(nameof(FilteredUwpApps));
            });
            
            Log($"Erfolgreich {classicApps.Count} System-Programme und {uwpApps.Count} Windows Apps geladen.");
        }

        private async Task UninstallSelectedAppAsync(CancellationToken ct)
        {
            var app = SelectedSoftware ?? SelectedUwpApp;
            if (app == null)
            {
                Log("FEHLER: Keine Anwendung zum Deinstallieren ausgewählt.");
                return;
            }

            Log($"Bereite Deinstallation für '{app.Name}' vor...");
            
            // Führe Deinstallation im Hintergrund aus
            await _uninstallerService.UninstallSoftwareAsync(app, CleanLeftovers, Log, ct);
            
            if (ct.IsCancellationRequested) return;

            Log("Aktualisiere die Listen der installierten Anwendungen...");
            await LoadAppsAsync(ct);
        }

        private void CancelOperation()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                Log("\nAbbruch wird eingeleitet...");
                _cts.Cancel();
            }
        }

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

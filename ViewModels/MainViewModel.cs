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
        private readonly IStorageService _storageService;
        private readonly IPerformanceService _performanceService;
        private readonly IRecoveryService _recoveryService;
        private readonly IDeepRecoveryService _deepRecoveryService;
        private CancellationTokenSource? _cts;

        public MainViewModel(
            IUpdateService  updateService,
            IDriverService  driverService,
            IRepairService  repairService,
            ICleanupService cleanupService,
            IUninstallerService uninstallerService,
            IStorageService storageService,
            IPerformanceService performanceService,
            IRecoveryService recoveryService,
            IDeepRecoveryService deepRecoveryService)
        {
            _updateService  = updateService;
            _driverService  = driverService;
            _repairService  = repairService;
            _cleanupService = cleanupService;
            _uninstallerService = uninstallerService;
            _storageService = storageService;
            _performanceService = performanceService;
            _recoveryService = recoveryService;
            _deepRecoveryService = deepRecoveryService;

            // Update Center
            CheckUpdatesCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates suchen",       ct => _updateService.CheckUpdatesAsync(Log, ct)),   _ => !IsBusy);
            InstallUpdatesCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates installieren", ct => _updateService.InstallUpdatesAsync(Log, ct)), _ => !IsBusy);
            CheckDriversCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber suchen",       ct => _driverService.CheckDriversAsync(Log, ct)),   _ => !IsBusy);
            InstallDriversCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber installieren", ct => _driverService.InstallDriversAsync(Log, ct)), _ => !IsBusy);

            // System-Reparatur
            RepairSystemCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("System reparieren",   ct => _repairService.RepairSystemAsync(Log, ct)),    _ => !IsBusy);

            // Bereinigung & Optimierung
            CleanupSystemCommand  = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("System bereinigen",   async ct => {
                await _cleanupService.CleanupSystemAsync(Log, ct);
                await RefreshStorageInfoAsync();
            },  false), _ => !IsBusy);
            OptimizeNetworkCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Netzwerk optimieren", ct => _cleanupService.OptimizeNetworkAsync(Log, ct), false), _ => !IsBusy);

            // Uninstaller (Software & Apps Manager - Batch Support)
            LoadAppsCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Software & Apps laden", ct => LoadAppsAsync(ct), false), _ => !IsBusy);
            UninstallCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Deinstallation", ct => UninstallSelectedAppsAsync(ct), false), _ => !IsBusy && (InstalledSoftware.Any(x => x.IsSelected) || InstalledUwpApps.Any(x => x.IsSelected)));

            // System-Leistung (RAM-Booster & Autostart Manager)
            BoostRamCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("RAM optimieren", ct => _performanceService.BoostRamAsync(Log, ct), false), _ => !IsBusy);
            ToggleStartupCommand = new RelayCommand(async param => await ExecuteWithBusyStateAsync("Autostart ändern", async ct => {
                if (param is StartupItem item) {
                    await _performanceService.ToggleStartupItemAsync(item);
                    await LoadStartupItemsAsync(ct);
                }
            }, false), param => !IsBusy && param is StartupItem);

            // Daten-Wiederherstellung (Recovery-Center)
            ScanRecoveryCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Gelöschte Dateien suchen", ct => ScanRecoveryAsync(ct), false), _ => !IsBusy && !string.IsNullOrEmpty(SelectedDrive));
            RestoreCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Dateien wiederherstellen", ct => RestoreSelectedFilesAsync(ct), false), _ => !IsBusy && _recoverableFiles.Any(f => f.IsSelected));
            SelectFolderCommand = new RelayCommand(_ => SelectRecoveryFolder(), _ => !IsBusy);

            // Target recovery folder default path initialization
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloads = System.IO.Path.Combine(userProfile, "Downloads");
                if (System.IO.Directory.Exists(downloads))
                {
                    TargetRecoveryFolder = downloads;
                }
                else
                {
                    TargetRecoveryFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                }
            }
            catch
            {
                TargetRecoveryFolder = @"C:\SystemOptimierer_Recovery";
            }

            // Global
            CancelCommand  = new RelayCommand(_ => CancelOperation(), _ => IsBusy);

            Log("System Optimierer gestartet. Bereit für Befehle...\n");

            // Drives für Recovery-Dropdown initialisieren
            LoadAvailableDrives();

            // Apps & Autostart beim Start im Hintergrund laden und Speicherplatz-Anzeige refreshen
            _ = Task.Run(async () =>
            {
                await RefreshStorageInfoAsync();
                await Task.Delay(300);
                await ExecuteWithBusyStateAsync("System-Komponenten laden", async ct => {
                    await LoadAppsAsync(ct);
                    await LoadStartupItemsAsync(ct);
                }, false);
            });
        }

        #region Properties

        private string _driveCSpace = "C: Laden...";
        public string DriveCSpace
        {
            get => _driveCSpace;
            set
            {
                if (_driveCSpace != value)
                {
                    _driveCSpace = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _driveDSpace = "D: Laden...";
        public string DriveDSpace
        {
            get => _driveDSpace;
            set
            {
                if (_driveDSpace != value)
                {
                    _driveDSpace = value;
                    OnPropertyChanged();
                }
            }
        }

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

        // Startup Manager Properties
        private ObservableCollection<StartupItem> _startupItems = new();
        public ObservableCollection<StartupItem> StartupItems
        {
            get => _startupItems;
            set
            {
                _startupItems = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilteredStartupItems));
            }
        }

        private string _startupSearchText = string.Empty;
        public string StartupSearchText
        {
            get => _startupSearchText;
            set
            {
                if (_startupSearchText != value)
                {
                    _startupSearchText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FilteredStartupItems));
                }
            }
        }

        public IEnumerable<StartupItem> FilteredStartupItems
        {
            get
            {
                if (string.IsNullOrWhiteSpace(StartupSearchText))
                {
                    return _startupItems;
                }
                return _startupItems.Where(item =>
                    item.Name.Contains(StartupSearchText, StringComparison.OrdinalIgnoreCase) ||
                    item.Path.Contains(StartupSearchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Recovery-Center Properties
        private bool _isQuickScanMode = true;
        public bool IsQuickScanMode
        {
            get => _isQuickScanMode;
            set
            {
                if (_isQuickScanMode != value)
                {
                    _isQuickScanMode = value;
                    OnPropertyChanged();
                    if (value)
                    {
                        _isDeepScanMode = false;
                        OnPropertyChanged(nameof(IsDeepScanMode));
                        LoadAvailableDrives();
                    }
                }
            }
        }

        private bool _isDeepScanMode;
        public bool IsDeepScanMode
        {
            get => _isDeepScanMode;
            set
            {
                if (_isDeepScanMode != value)
                {
                    _isDeepScanMode = value;
                    OnPropertyChanged();
                    if (value)
                    {
                        _isQuickScanMode = false;
                        OnPropertyChanged(nameof(IsQuickScanMode));
                        LoadAvailableDrives();
                    }
                }
            }
        }

        private void LoadAvailableDrives()
        {
            try
            {
                AvailableDrives.Clear();
                if (IsQuickScanMode)
                {
                    var drives = _recoveryService.GetAvailableDrives();
                    foreach (var drive in drives)
                    {
                        AvailableDrives.Add(drive);
                    }
                }
                else
                {
                    var drives = _deepRecoveryService.GetPhysicalDrives();
                    foreach (var drive in drives)
                    {
                        AvailableDrives.Add(drive);
                    }
                }

                if (AvailableDrives.Count > 0)
                {
                    SelectedDrive = AvailableDrives[0];
                }
                else
                {
                    SelectedDrive = string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log($"[WARNUNG] Fehler beim Laden der verfügbaren Recovery-Laufwerke: {ex.Message}");
            }
        }

        private ObservableCollection<string> _availableDrives = new();
        public ObservableCollection<string> AvailableDrives
        {
            get => _availableDrives;
            set { _availableDrives = value; OnPropertyChanged(); }
        }

        private string _selectedDrive = string.Empty;
        public string SelectedDrive
        {
            get => _selectedDrive;
            set
            {
                if (_selectedDrive != value)
                {
                    _selectedDrive = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private bool _searchDocs = true;
        public bool SearchDocs
        {
            get => _searchDocs;
            set { _searchDocs = value; OnPropertyChanged(); }
        }

        private bool _searchImages = true;
        public bool SearchImages
        {
            get => _searchImages;
            set { _searchImages = value; OnPropertyChanged(); }
        }

        private bool _searchVideos = true;
        public bool SearchVideos
        {
            get => _searchVideos;
            set { _searchVideos = value; OnPropertyChanged(); }
        }

        private bool _searchMusic = true;
        public bool SearchMusic
        {
            get => _searchMusic;
            set { _searchMusic = value; OnPropertyChanged(); }
        }

        private ObservableCollection<RecoverableFile> _recoverableFiles = new();
        public ObservableCollection<RecoverableFile> RecoverableFiles
        {
            get => _recoverableFiles;
            set
            {
                _recoverableFiles = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilteredRecoverableFiles));
            }
        }

        private string _recoverySearchText = string.Empty;
        public string RecoverySearchText
        {
            get => _recoverySearchText;
            set
            {
                if (_recoverySearchText != value)
                {
                    _recoverySearchText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FilteredRecoverableFiles));
                }
            }
        }

        private bool _isSuppressingSelectionChanged;
        private bool _isAllFilesSelected;
        public bool IsAllFilesSelected
        {
            get => _isAllFilesSelected;
            set
            {
                if (_isAllFilesSelected != value)
                {
                    _isAllFilesSelected = value;
                    OnPropertyChanged();
                    if (RecoverableFiles != null && !_isSuppressingSelectionChanged)
                    {
                        _isSuppressingSelectionChanged = true;
                        foreach (var file in RecoverableFiles) { file.IsSelected = value; }
                        _isSuppressingSelectionChanged = false;
                    }
                }
            }
        }



        public IEnumerable<RecoverableFile> FilteredRecoverableFiles
        {
            get
            {
                if (string.IsNullOrWhiteSpace(RecoverySearchText))
                {
                    return _recoverableFiles;
                }
                return _recoverableFiles.Where(f =>
                    f.Name.Contains(RecoverySearchText, StringComparison.OrdinalIgnoreCase) ||
                    f.OriginalPath.Contains(RecoverySearchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        private string _targetRecoveryFolder = string.Empty;
        public string TargetRecoveryFolder
        {
            get => _targetRecoveryFolder;
            set
            {
                if (_targetRecoveryFolder != value)
                {
                    _targetRecoveryFolder = value;
                    OnPropertyChanged();
                }
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
        public ICommand BoostRamCommand { get; }
        public ICommand ToggleStartupCommand { get; }
        public ICommand ScanRecoveryCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand SelectFolderCommand { get; }
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
                    app.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(SoftwareItem.IsSelected))
                        {
                            CommandManager.InvalidateRequerySuggested();
                        }
                    };
                    _installedSoftware.Add(app);
                }

                _installedUwpApps.Clear();
                foreach (var app in uwpApps)
                {
                    app.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(SoftwareItem.IsSelected))
                        {
                            CommandManager.InvalidateRequerySuggested();
                        }
                    };
                    _installedUwpApps.Add(app);
                }

                OnPropertyChanged(nameof(FilteredSoftware));
                OnPropertyChanged(nameof(FilteredUwpApps));
            });
            
            Log($"Erfolgreich {classicApps.Count} System-Programme und {uwpApps.Count} Windows Apps geladen.");
        }

        private async Task LoadStartupItemsAsync(CancellationToken ct)
        {
            Log("Lese Windows-Autostart-Programme aus...");
            var items = await Task.Run(() => _performanceService.GetStartupItems(), ct);
            if (ct.IsCancellationRequested) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _startupItems.Clear();
                foreach (var item in items)
                {
                    _startupItems.Add(item);
                }
                OnPropertyChanged(nameof(FilteredStartupItems));
            });
            Log($"Erfolgreich {items.Count} Autostart-Programme geladen.");
        }

        private async Task UninstallSelectedAppsAsync(CancellationToken ct)
        {
            var appsToUninstall = _installedSoftware.Where(x => x.IsSelected)
                .Concat(_installedUwpApps.Where(x => x.IsSelected))
                .ToList();

            if (appsToUninstall.Count == 0)
            {
                Log("FEHLER: Keine Anwendungen zum Deinstallieren ausgewählt.");
                return;
            }

            Log($"Bereite Deinstallation für {appsToUninstall.Count} ausgewählte Anwendung(en) vor...");
            
            int currentIndex = 1;
            foreach (var app in appsToUninstall)
            {
                if (ct.IsCancellationRequested) break;

                Log($"\n[Batch-Schritt {currentIndex}/{appsToUninstall.Count}] Starte Deinstallation von '{app.Name}'...");
                await _uninstallerService.UninstallSoftwareAsync(app, CleanLeftovers, Log, ct);
                currentIndex++;
            }
            
            if (ct.IsCancellationRequested) return;

            Log("\nBatch-Deinstallation abgeschlossen. Aktualisiere die Listen und die Speicherplatz-Anzeige...");
            await LoadAppsAsync(ct);
            await RefreshStorageInfoAsync();
        }

        public async Task RefreshStorageInfoAsync()
        {
            try
            {
                var spaceC = await Task.Run(() => _storageService.GetDriveSpaceString("C"));
                var spaceD = await Task.Run(() => _storageService.GetDriveSpaceString("D"));
                
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    DriveCSpace = spaceC;
                    DriveDSpace = spaceD;
                });
            }
            catch (Exception ex)
            {
                Log($"[WARNUNG] Fehler beim Aktualisieren der Speicherplatz-Anzeige: {ex.Message}");
            }
        }

        // Recovery-Center Methods
        private async Task ScanRecoveryAsync(CancellationToken ct)
        {
            List<RecoverableFile> files;
            if (IsDeepScanMode)
            {
                files = await _deepRecoveryService.ScanPhysicalSectorsAsync(
                    SelectedDrive,
                    SearchDocs,
                    SearchImages,
                    SearchVideos,
                    SearchMusic,
                    Log,
                    ct
                );
            }
            else
            {
                Log($"Starte Dateisystem-Scan auf Laufwerk '{SelectedDrive}'...");
                files = await _recoveryService.ScanDeletedFilesAsync(
                    SelectedDrive,
                    SearchDocs,
                    SearchImages,
                    SearchVideos,
                    SearchMusic,
                    Log,
                    ct
                );
            }

            if (ct.IsCancellationRequested) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _recoverableFiles.Clear();
                _isAllFilesSelected = false;
                OnPropertyChanged(nameof(IsAllFilesSelected));
                foreach (var file in files)
                {
                    file.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(RecoverableFile.IsSelected))
                        {
                            if (!_isSuppressingSelectionChanged)
                            {
                                _isSuppressingSelectionChanged = true;
                                IsAllFilesSelected = RecoverableFiles.All(f => f.IsSelected);
                                _isSuppressingSelectionChanged = false;
                            }
                            CommandManager.InvalidateRequerySuggested();
                        }
                    };
                    _recoverableFiles.Add(file);
                }
                OnPropertyChanged(nameof(FilteredRecoverableFiles));
            });

            Log($"Scan beendet. {files.Count} wiederherstellbare Datei(en) gefunden.");
        }

        private async Task RestoreSelectedFilesAsync(CancellationToken ct)
        {
            var selected = _recoverableFiles.Where(f => f.IsSelected).ToList();
            if (selected.Count == 0)
            {
                Log("FEHLER: Keine Dateien zur Wiederherstellung ausgewählt.");
                return;
            }

            string targetDir = TargetRecoveryFolder;
            Log($"Starte Wiederherstellung von {selected.Count} Datei(en) nach '{targetDir}'...");

            await _recoveryService.RestoreFilesAsync(selected, targetDir, Log, ct);
            
            if (ct.IsCancellationRequested) return;

            Log("\nWiederherstellung erfolgreich abgeschlossen.");
        }

        private void SelectRecoveryFolder()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Wählen Sie das Zielverzeichnis für die Wiederherstellung",
                    InitialDirectory = TargetRecoveryFolder
                };
                if (dialog.ShowDialog() == true)
                {
                    TargetRecoveryFolder = dialog.FolderName;
                }
            }
            catch (Exception ex)
            {
                Log($"[Fehler] Ordner-Auswahl konnte nicht geöffnet werden: {ex.Message}");
            }
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

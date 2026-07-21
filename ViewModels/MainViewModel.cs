using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        private readonly IUpdateService _updateService;
        private readonly IDriverService _driverService;
        private readonly IRepairService _repairService;
        private readonly ICleanupService _cleanupService;
        private readonly IUninstallerService _uninstallerService;
        private readonly IStorageService _storageService;
        private readonly IPerformanceService _performanceService;
        private CancellationTokenSource? _cts;

        public ICollectionView SoftwareView { get; }
        public ICollectionView UwpView { get; }

        public MainViewModel(
            IUpdateService updateService,
            IDriverService driverService,
            IRepairService repairService,
            ICleanupService cleanupService,
            IUninstallerService uninstallerService,
            IStorageService storageService,
            IPerformanceService performanceService)
        {
            _updateService = updateService;
            _driverService = driverService;
            _repairService = repairService;
            _cleanupService = cleanupService;
            _uninstallerService = uninstallerService;
            _storageService = storageService;
            _performanceService = performanceService;

            SoftwareView = CollectionViewSource.GetDefaultView(_installedSoftware);
            SoftwareView.Filter = FilterSoftwarePredicate;
            UwpView = CollectionViewSource.GetDefaultView(_installedUwpApps);
            UwpView.Filter = FilterUwpAppsPredicate;

            // ============================================================
            // FIX #3: AsyncRelayCommand statt RelayCommand(async ...)
            // → Kein "async void" mehr, Exceptions werden sauber gefangen
            // ============================================================

            // Update Center
            CheckUpdatesCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Updates suchen", ct => CheckUpdatesWrapperAsync(ct)),
                _ => !IsBusy, OnCommandException);
            InstallUpdatesCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Updates installieren", ct => InstallUpdatesWrapperAsync(ct)),
                _ => !IsBusy, OnCommandException);
            CheckDriversCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Treiber suchen", ct => CheckDriversWrapperAsync(ct)),
                _ => !IsBusy, OnCommandException);
            InstallDriversCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Treiber installieren", ct => InstallDriversWrapperAsync(ct)),
                _ => !IsBusy, OnCommandException);

            // System-Reparatur
            RepairSystemCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("System reparieren", ct => _repairService.RepairSystemAsync(Log, ct)),
                _ => !IsBusy, OnCommandException);

            // Bereinigung & Optimierung
            CleanupSystemCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("System bereinigen", async ct =>
                {
                    await _cleanupService.CleanupSystemAsync(Log, ct);
                    await RefreshStorageInfoAsync();
                }, false),
                _ => !IsBusy, OnCommandException);
            OptimizeNetworkCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Netzwerk optimieren", ct => _cleanupService.OptimizeNetworkAsync(Log, ct), false),
                _ => !IsBusy, OnCommandException);

            // Uninstaller
            LoadAppsCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Software & Apps laden", ct => LoadAppsAsync(ct), false),
                _ => !IsBusy, OnCommandException);
            UninstallCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Deinstallation", ct => UninstallAppAsync(ct), false),
                _ => !IsBusy && (SelectedSoftware != null || SelectedUwpApp != null), OnCommandException);

            // System-Leistung
            BoostRamCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("RAM optimieren", ct => _performanceService.BoostRamAsync(Log, ct), false),
                _ => !IsBusy, OnCommandException);
            ToggleStartupCommand = new AsyncRelayCommand(
                async param =>
                {
                    if (param is StartupItem item)
                    {
                        await ExecuteWithBusyStateAsync("Autostart ändern", async ct =>
                        {
                            await _performanceService.ToggleStartupItemAsync(item);
                            await LoadStartupItemsAsync(ct);
                        }, false);
                    }
                },
                param => !IsBusy && param is StartupItem, OnCommandException);

            // Cancel bleibt synchron → RelayCommand ist hier korrekt
            CancelCommand = new RelayCommand(_ => CancelOperation(), _ => IsBusy);

            Log("System Optimierer gestartet. Bereit für Befehle...\n");

            _ = Task.Run(async () =>
            {
                await RefreshStorageInfoAsync();
                await Task.Delay(300);
                await ExecuteWithBusyStateAsync("System-Komponenten laden", async ct =>
                {
                    await LoadAppsAsync(ct);
                    await LoadStartupItemsAsync(ct);
                }, false);
            });
        }

        // ============================================================
        // FIX #3 (zugehörig): Zentraler Exception-Handler für Commands
        // ============================================================
        private void OnCommandException(Exception ex)
        {
            Log($"\n[FEHLER] Unbehandelter Fehler in einem Befehl: {ex.Message}");
            StatusMessage = "Fehler aufgetreten";
        }

        #region Properties

        public List<string> AvailableSortModes { get; } = new()
        {
            "Neueste zuerst",
            "Älteste zuerst",
            "Name (A-Z)",
            "Name (Z-A)",
            "Größe (Große zuerst)"
        };

        private string _softwareSortMode = "Neueste zuerst";
        public string SoftwareSortMode
        {
            get => _softwareSortMode;
            set { if (_softwareSortMode != value) { _softwareSortMode = value; OnPropertyChanged(); ApplySoftwareSorting(); } }
        }

        private string _uwpSortMode = "Neueste zuerst";
        public string UwpSortMode
        {
            get => _uwpSortMode;
            set { if (_uwpSortMode != value) { _uwpSortMode = value; OnPropertyChanged(); ApplyUwpSorting(); } }
        }

        private string _driveCSpace = "C: Laden...";
        public string DriveCSpace { get => _driveCSpace; set { if (_driveCSpace != value) { _driveCSpace = value; OnPropertyChanged(); } } }

        private string _driveDSpace = "D: Laden...";
        public string DriveDSpace { get => _driveDSpace; set { if (_driveDSpace != value) { _driveDSpace = value; OnPropertyChanged(); } } }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotBusy)); CommandManager.InvalidateRequerySuggested(); } }
        }
        public bool IsNotBusy => !_isBusy;

        private string _logText = string.Empty;
        public string LogText { get => _logText; set { if (_logText != value) { _logText = value; OnPropertyChanged(); } } }

        private string _statusMessage = "Bereit";
        public string StatusMessage { get => _statusMessage; set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } } }

        // ============================================================
        // FIX #1: LiveScanSpeed – fehlte komplett, XAML-Binding schlug fehl
        // ============================================================
        private string _liveScanSpeed = "BEREIT";
        public string LiveScanSpeed { get => _liveScanSpeed; set { if (_liveScanSpeed != value) { _liveScanSpeed = value; OnPropertyChanged(); } } }

        private ObservableCollection<SoftwareItem> _installedSoftware = new();
        public ObservableCollection<SoftwareItem> InstalledSoftware
        {
            get => _installedSoftware;
            set { _installedSoftware = value; OnPropertyChanged(); SoftwareView.Refresh(); }
        }

        private ObservableCollection<SoftwareItem> _installedUwpApps = new();
        public ObservableCollection<SoftwareItem> InstalledUwpApps
        {
            get => _installedUwpApps;
            set { _installedUwpApps = value; OnPropertyChanged(); UwpView.Refresh(); }
        }

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); SoftwareView.Refresh(); UwpView.Refresh(); } }
        }

        private bool _cleanLeftovers = true;
        public bool CleanLeftovers { get => _cleanLeftovers; set { if (_cleanLeftovers != value) { _cleanLeftovers = value; OnPropertyChanged(); } } }

        private SoftwareItem? _selectedSoftware;
        public SoftwareItem? SelectedSoftware
        {
            get => _selectedSoftware;
            set
            {
                if (_selectedSoftware != value)
                {
                    _selectedSoftware = value; OnPropertyChanged();
                    if (value != null) { _selectedUwpApp = null; OnPropertyChanged(nameof(SelectedUwpApp)); }
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
                    _selectedUwpApp = value; OnPropertyChanged();
                    if (value != null) { _selectedSoftware = null; OnPropertyChanged(nameof(SelectedSoftware)); }
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private ObservableCollection<StartupItem> _startupItems = new();
        public ObservableCollection<StartupItem> StartupItems
        {
            get => _startupItems;
            set { _startupItems = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredStartupItems)); }
        }

        private string _startupSearchText = string.Empty;
        public string StartupSearchText
        {
            get => _startupSearchText;
            set { if (_startupSearchText != value) { _startupSearchText = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredStartupItems)); } }
        }

        public IEnumerable<StartupItem> FilteredStartupItems
        {
            get
            {
                if (string.IsNullOrWhiteSpace(StartupSearchText)) return _startupItems;
                return _startupItems.Where(item =>
                    item.Name.Contains(StartupSearchText, StringComparison.OrdinalIgnoreCase) ||
                    item.Path.Contains(StartupSearchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        private string _currentUpdateActivity = "Bereit";
        public string CurrentUpdateActivity { get => _currentUpdateActivity; set { if (_currentUpdateActivity != value) { _currentUpdateActivity = value; OnPropertyChanged(); } } }

        private int _updatesFoundCount;
        public int UpdatesFoundCount { get => _updatesFoundCount; set { if (_updatesFoundCount != value) { _updatesFoundCount = value; OnPropertyChanged(); } } }

        private int _driversFoundCount;
        public int DriversFoundCount { get => _driversFoundCount; set { if (_driversFoundCount != value) { _driversFoundCount = value; OnPropertyChanged(); } } }

        public ObservableCollection<UpdateItem> UpdatesCollection { get; } = new ObservableCollection<UpdateItem>();

        #endregion

        #region Methods & Predicates

        private bool FilterSoftwarePredicate(object obj)
        {
            if (obj is not SoftwareItem app) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return app.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   app.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        private bool FilterUwpAppsPredicate(object obj)
        {
            if (obj is not SoftwareItem app) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return app.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   app.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        private void ApplySoftwareSorting()
        {
            SoftwareView.SortDescriptions.Clear();
            switch (SoftwareSortMode)
            {
                case "Neueste zuerst": SoftwareView.SortDescriptions.Add(new SortDescription("InstallDateTime", ListSortDirection.Descending)); break;
                case "Älteste zuerst": SoftwareView.SortDescriptions.Add(new SortDescription("InstallDateTime", ListSortDirection.Ascending)); break;
                case "Name (A-Z)": SoftwareView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending)); break;
                case "Name (Z-A)": SoftwareView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending)); break;
                case "Größe (Große zuerst)": SoftwareView.SortDescriptions.Add(new SortDescription("EstimatedSizeKb", ListSortDirection.Descending)); break;
                default: SoftwareView.SortDescriptions.Add(new SortDescription("InstallDateTime", ListSortDirection.Descending)); break;
            }
        }

        private void ApplyUwpSorting()
        {
            UwpView.SortDescriptions.Clear();
            switch (UwpSortMode)
            {
                case "Neueste zuerst": UwpView.SortDescriptions.Add(new SortDescription("InstallDateTime", ListSortDirection.Descending)); break;
                case "Älteste zuerst": UwpView.SortDescriptions.Add(new SortDescription("InstallDateTime", ListSortDirection.Ascending)); break;
                case "Name (A-Z)": UwpView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending)); break;
                case "Name (Z-A)": UwpView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending)); break;
                default: UwpView.SortDescriptions.Add(new SortDescription("InstallDateTime", ListSortDirection.Descending)); break;
            }
        }

        private const int MaxLogLength = 60_000;
        private void Log(string message)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                string combined = LogText + message + "\n";
                if (combined.Length > MaxLogLength)
                {
                    int startSearch = Math.Max(0, combined.Length - (MaxLogLength / 2));
                    int cutAt = combined.IndexOf('\n', startSearch);
                    combined = cutAt > 0
                        ? "[... ältere Einträge wurden gekürzt ...]\n" + combined.Substring(cutAt + 1)
                        : message + "\n";
                }
                LogText = combined;
            }));
        }

        private async Task<bool> IsInternetAvailableAsync()
        {
            try { using var ping = new Ping(); var reply = await ping.SendPingAsync("1.1.1.1", 2000); return reply.Status == IPStatus.Success; }
            catch { return false; }
        }

        private async Task ExecuteWithBusyStateAsync(string operationName, Func<CancellationToken, Task> action, bool requiresInternet = true)
        {
            if (IsBusy) return;
            if (requiresInternet && !await IsInternetAvailableAsync())
            {
                Log($"FEHLER: Keine Internetverbindung gefunden! [{operationName}] abgebrochen.\n");
                return;
            }

            IsBusy = true;
            StatusMessage = $"{operationName} läuft...";
            LiveScanSpeed = "AKTIV";
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
                LiveScanSpeed = "BEREIT";
            }
        }

        private async Task LoadAppsAsync(CancellationToken ct)
        {
            Log("Lese installierte System-Programme & alle Windows Apps aus...");
            var classicTask = Task.Run(() => _uninstallerService.GetInstalledSoftware(), ct);
            var uwpTask = _uninstallerService.GetInstalledUwpAppsAsync(Log, ct);
            await Task.WhenAll(classicTask, uwpTask);
            if (ct.IsCancellationRequested) return;

            var classicApps = classicTask.Result;
            var uwpApps = uwpTask.Result;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _installedSoftware.Clear();
                foreach (var app in classicApps)
                {
                    app.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(SoftwareItem.IsSelected)) CommandManager.InvalidateRequerySuggested(); };
                    _installedSoftware.Add(app);
                }
                _installedUwpApps.Clear();
                foreach (var app in uwpApps)
                {
                    app.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(SoftwareItem.IsSelected)) CommandManager.InvalidateRequerySuggested(); };
                    _installedUwpApps.Add(app);
                }
                SoftwareView.Refresh();
                UwpView.Refresh();
                ApplySoftwareSorting();
                ApplyUwpSorting();
            });

            Log($"Erfolgreich {classicApps.Count} System-Programme und {uwpApps.Count} Windows Apps geladen.");
        }

        private async Task LoadStartupItemsAsync(CancellationToken ct)
        {
            var items = await Task.Run(() => _performanceService.GetStartupItems(), ct);
            if (ct.IsCancellationRequested) return;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _startupItems.Clear();
                foreach (var item in items) _startupItems.Add(item);
                OnPropertyChanged(nameof(FilteredStartupItems));
            });
        }

        private async Task UninstallAppAsync(CancellationToken ct)
        {
            SoftwareItem? app = SelectedSoftware ?? SelectedUwpApp;
            if (app == null) return;
            Log($"Bereite Deinstallation für '{app.Name}' vor...");
            await _uninstallerService.UninstallSoftwareAsync(app, CleanLeftovers, Log, ct);
            if (ct.IsCancellationRequested) return;
            Log("\nDeinstallation abgeschlossen. Aktualisiere Listen...");
            await LoadAppsAsync(ct);
            await RefreshStorageInfoAsync();
        }

        public async Task RefreshStorageInfoAsync()
        {
            try
            {
                var spaceC = await Task.Run(() => _storageService.GetDriveSpaceString("C"));
                var spaceD = await Task.Run(() => _storageService.GetDriveSpaceString("D"));
                await Application.Current.Dispatcher.InvokeAsync(() => { DriveCSpace = spaceC; DriveDSpace = spaceD; });
            }
            catch { }
        }

        private void ParseWingetUpgradeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("---") || line.Contains("Name") || line.Contains("ID ") || line.Contains("Version")) return;

            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length >= 4)
            {
                string name = parts[0]; string id = parts[1]; string currentVer = parts[2]; string availableVer = parts[3];
                if (!string.IsNullOrEmpty(id) && availableVer.Any(char.IsDigit))
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!UpdatesCollection.Any(x => x.UpdateId == id))
                        {
                            UpdatesCollection.Add(new UpdateItem { Name = name, UpdateId = id, CurrentVersion = currentVer, AvailableVersion = availableVer, Type = "Software-Update", Status = "Ausstehend" });
                            UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update");
                        }
                    }));
                }
            }
        }

        private void ParseWingetInstallLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                string lowerLine = line.ToLowerInvariant();
                foreach (var item in UpdatesCollection.Where(x => x.Type == "Software-Update").ToList())
                {
                    if (lowerLine.Contains(item.UpdateId.ToLowerInvariant()) || lowerLine.Contains(item.Name.ToLowerInvariant()))
                    {
                        if (lowerLine.Contains("successfully") || lowerLine.Contains("erfolgreich") || lowerLine.Contains("installed") || lowerLine.Contains("installiert")) item.Status = "Erfolgreich";
                        else if (lowerLine.Contains("failed") || lowerLine.Contains("fehler") || lowerLine.Contains("fehlgeschlagen")) item.Status = "Fehlgeschlagen";
                        else if (lowerLine.Contains("download") || lowerLine.Contains("herunterladen")) item.Status = "Wird heruntergeladen...";
                        else item.Status = "Wird installiert...";
                    }
                }
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
            }));
        }

        // ============================================================
        // FIX #2: Treiber-Anzahl aus dem DriverService-Log parsen
        // ============================================================
        private void ParseDriverCount(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // DriverService loggt: "GESAMT: 5 Treiber-Updates verfügbar (Pflicht: 2, Optional: 3)"
            if (line.Contains("GESAMT:", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(line, @"GESAMT:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int count))
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => { DriversFoundCount = count; }));
                }
            }
        }

        private async Task CheckUpdatesWrapperAsync(CancellationToken ct)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var toRemove = UpdatesCollection.Where(x => x.Type == "Software-Update").ToList();
                foreach (var item in toRemove) UpdatesCollection.Remove(item);
                UpdatesFoundCount = 0;
                CurrentUpdateActivity = "Lade Quellen...";
            });

            await _updateService.CheckUpdatesAsync(msg => { Log(msg); ParseWingetUpgradeLine(msg); }, ct);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = $"Suche beendet. {UpdatesFoundCount} Update(s) gefunden.";
            });
        }

        private async Task InstallUpdatesWrapperAsync(CancellationToken ct)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { CurrentUpdateActivity = "Starte Installation..."; });
            await _updateService.InstallUpdatesAsync(msg => { Log(msg); ParseWingetInstallLine(msg); }, ct);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = "Installation beendet.";
                foreach (var item in UpdatesCollection.Where(x => x.Type == "Software-Update").ToList())
                {
                    if (item.Status != "Fehlgeschlagen") item.Status = "Erfolgreich";
                }
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
            });

            await Task.Delay(1800, ct);
            if (ct.IsCancellationRequested) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var done = UpdatesCollection.Where(x => x.Type == "Software-Update" && x.Status == "Erfolgreich").ToList();
                foreach (var item in done) UpdatesCollection.Remove(item);
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
            });
        }

        // ============================================================
        // FIX #2 (zugehörig): DriversFoundCount wird jetzt geparst
        // ============================================================
        private async Task CheckDriversWrapperAsync(CancellationToken ct)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { DriversFoundCount = 0; });
            await _driverService.CheckDriversAsync(msg => { Log(msg); ParseDriverCount(msg); }, ct);
        }

        private async Task InstallDriversWrapperAsync(CancellationToken ct)
        {
            await _driverService.InstallDriversAsync(msg => Log(msg), ct);
        }

        private void CancelOperation()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                Log("\nAbbruch eingeleitet...");
                _cts.Cancel();
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
        public ICommand CancelCommand { get; }
        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
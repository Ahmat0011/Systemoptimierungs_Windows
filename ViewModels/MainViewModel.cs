using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

            // Bereich "Deinstallieren"
            LoadAppsCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Programmlisten laden", ct => LoadAppsAsync(ct), false),
                _ => !IsBusy, OnCommandException);

            // Multi-Select – Button aktiv wenn mindestens 1 Programm angekreuzt ist
            UninstallCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Deinstallation", ct => UninstallSelectedAppsAsync(ct), false),
                _ => !IsBusy && (_installedSoftware.Any(a => a.IsSelected) || _installedUwpApps.Any(a => a.IsSelected) || SelectedSoftware != null || SelectedUwpApp != null), OnCommandException);

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

            CancelCommand = new RelayCommand(_ => CancelOperation(), _ => IsBusy);

            Log("System Optimierer gestartet. Bereit für Befehle...\n");
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await RefreshStorageInfoAsync();
                await Task.Delay(300);
                await ExecuteWithBusyStateAsync("System-Komponenten laden", async ct =>
                {
                    await LoadAppsAsync(ct);
                    await LoadStartupItemsAsync(ct);
                }, false);
            }
            catch (Exception ex)
            {
                Log($"[FEHLER] Beim Starten: {ex.Message}");
            }
        }

        /// <summary>
        /// Wird beim Schließen des Fensters aufgerufen – bricht laufende Operationen sauber ab.
        /// </summary>
        public void RequestShutdown()
        {
            try
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                    _cts.Cancel();
            }
            catch { }
        }

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

        private string _liveScanSpeed = "BEREIT";
        public string LiveScanSpeed { get => _liveScanSpeed; set { if (_liveScanSpeed != value) { _liveScanSpeed = value; OnPropertyChanged(); } } }

        // Read-only Collections (kein Setter mehr – verhindert latenten CollectionView-Bug)
        private readonly ObservableCollection<SoftwareItem> _installedSoftware = new();
        public ObservableCollection<SoftwareItem> InstalledSoftware => _installedSoftware;

        private readonly ObservableCollection<SoftwareItem> _installedUwpApps = new();
        public ObservableCollection<SoftwareItem> InstalledUwpApps => _installedUwpApps;

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

        private readonly ObservableCollection<StartupItem> _startupItems = new();
        public ObservableCollection<StartupItem> StartupItems => _startupItems;

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

        #region Dispatcher-Helfer (null-safe, kein Crash beim Beenden)

        private static void InvokeOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        private static Task InvokeOnUiAsync(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return Task.CompletedTask;
            if (dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
            return dispatcher.InvokeAsync(action).Task;
        }

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
            if (SoftwareView is not ListCollectionView lcv) return;
            lcv.CustomSort = new SoftwareItemComparer(SoftwareSortMode);
        }

        private void ApplyUwpSorting()
        {
            if (UwpView is not ListCollectionView lcv) return;
            lcv.CustomSort = new SoftwareItemComparer(UwpSortMode);
        }

        private const int MaxLogLength = 60_000;
        private void Log(string message)
        {
            InvokeOnUi(() =>
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
            });
        }

        /// <summary>
        /// Mehrstufiger Internet-Check (ICMP + TCP-Fallback).
        /// Funktioniert auch in Firmennetzen, wo ICMP-Pings blockiert werden.
        /// </summary>
        private static async Task<bool> IsInternetAvailableAsync()
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync("1.1.1.1", 1500);
                if (reply.Status == IPStatus.Success) return true;
            }
            catch { }

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync("8.8.8.8", 1500);
                if (reply.Status == IPStatus.Success) return true;
            }
            catch { }

            foreach (string host in new[] { "1.1.1.1", "8.8.8.8", "www.microsoft.com" })
            {
                try
                {
                    using var tcp = new TcpClient();
                    using var cts = new CancellationTokenSource(2000);
                    await tcp.ConnectAsync(host, 443, cts.Token);
                    return true;
                }
                catch { }
            }
            return false;
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
                await Task.Run(() => action(_cts.Token), CancellationToken.None);
                StatusMessage = $"{operationName} erfolgreich abgeschlossen.";
            }
            catch (OperationCanceledException)
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

            await InvokeOnUiAsync(() =>
            {
                _installedSoftware.Clear();
                foreach (var app in classicApps)
                {
                    app.PropertyChanged += OnSoftwareItemPropertyChanged;
                    _installedSoftware.Add(app);
                }

                _installedUwpApps.Clear();
                foreach (var app in uwpApps)
                {
                    app.PropertyChanged += OnSoftwareItemPropertyChanged;
                    _installedUwpApps.Add(app);
                }

                SoftwareView.Refresh();
                UwpView.Refresh();
                ApplySoftwareSorting();
                ApplyUwpSorting();
            });

            Log($"Erfolgreich {classicApps.Count} System-Programme und {uwpApps.Count} Windows Apps geladen.");
        }

        private void OnSoftwareItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SoftwareItem.IsSelected))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task LoadStartupItemsAsync(CancellationToken ct)
        {
            var items = await Task.Run(() => _performanceService.GetStartupItems(), ct);
            if (ct.IsCancellationRequested) return;

            await InvokeOnUiAsync(() =>
            {
                _startupItems.Clear();
                foreach (var item in items) _startupItems.Add(item);
                OnPropertyChanged(nameof(FilteredStartupItems));
            });
        }

        private async Task UninstallSelectedAppsAsync(CancellationToken ct)
        {
            var selectedClassic = _installedSoftware.Where(a => a.IsSelected).ToList();
            var selectedUwp = _installedUwpApps.Where(a => a.IsSelected).ToList();
            var allSelected = selectedClassic.Concat(selectedUwp).ToList();

            if (allSelected.Count == 0)
            {
                SoftwareItem? single = SelectedSoftware ?? SelectedUwpApp;
                if (single != null) allSelected.Add(single);
            }

            if (allSelected.Count == 0)
            {
                Log("Keine Programme zum Deinstallieren ausgewählt.");
                return;
            }

            Log($"Starte automatische Deinstallation für {allSelected.Count} ausgewählte(s) Element(e)...\n");

            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < allSelected.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                var app = allSelected[i];
                Log($"[{i + 1}/{allSelected.Count}] Bereite Deinstallation für '{app.Name}' vor...");

                int logStartLength = LogText.Length;

                await _uninstallerService.UninstallSoftwareAsync(app, CleanLeftovers, Log, ct);

                if (ct.IsCancellationRequested) break;

                string newLogPart = LogText.Length > logStartLength
                    ? LogText.Substring(logStartLength)
                    : string.Empty;
                bool wasSuccess = newLogPart.Contains("[ERFOLG]", StringComparison.OrdinalIgnoreCase)
                                  && !newLogPart.Contains("[WARNUNG]", StringComparison.OrdinalIgnoreCase)
                                  && !newLogPart.Contains("[FEHLER]", StringComparison.OrdinalIgnoreCase);

                if (wasSuccess)
                {
                    successCount++;
                    await InvokeOnUiAsync(() =>
                    {
                        if (app.IsUwp) _installedUwpApps.Remove(app);
                        else _installedSoftware.Remove(app);
                    });
                }
                else
                {
                    failCount++;
                    await InvokeOnUiAsync(() => { app.IsSelected = false; });
                }
            }

            Log($"\n=== Deinstallations-Durchgang beendet: {successCount} erfolgreich, {failCount} fehlgeschlagen ===\n");

            Log("Aktualisiere Programmlisten...\n");
            await LoadAppsAsync(ct);
            await RefreshStorageInfoAsync();
        }

        public async Task RefreshStorageInfoAsync()
        {
            try
            {
                var spaceC = await Task.Run(() => _storageService.GetDriveSpaceString("C"));
                var spaceD = await Task.Run(() => _storageService.GetDriveSpaceString("D"));
                await InvokeOnUiAsync(() => { DriveCSpace = spaceC; DriveDSpace = spaceD; });
            }
            catch { }
        }

        private void ParseWingetUpgradeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("---") || line.Contains('─') || line.Contains('━') ||
                line.Contains("Name") || line.Contains("ID ") || line.Contains("Version")) return;

            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length >= 4)
            {
                string name = parts[0]; string id = parts[1]; string currentVer = parts[2]; string availableVer = parts[3];
                if (!string.IsNullOrEmpty(id) && availableVer.Any(char.IsDigit))
                {
                    InvokeOnUi(() =>
                    {
                        if (!UpdatesCollection.Any(x => x.UpdateId == id))
                        {
                            UpdatesCollection.Add(new UpdateItem
                            {
                                Name = name,
                                UpdateId = id,
                                CurrentVersion = currentVer,
                                AvailableVersion = availableVer,
                                Type = "Software-Update",
                                Status = "Ausstehend",
                                Detail = "Bereit zum Installieren"
                            });
                            UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update");
                        }
                    });
                }
            }
        }

        private void ParseWingetInstallLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            InvokeOnUi(() =>
            {
                string lowerLine = line.ToLowerInvariant();
                foreach (var item in UpdatesCollection.Where(x => x.Type == "Software-Update").ToList())
                {
                    if (lowerLine.Contains(item.UpdateId.ToLowerInvariant()) || lowerLine.Contains(item.Name.ToLowerInvariant()))
                    {
                        if (lowerLine.Contains("successfully") || lowerLine.Contains("erfolgreich") || lowerLine.Contains("installed") || lowerLine.Contains("installiert"))
                        {
                            item.Status = "Erfolgreich";
                            item.Detail = $"v{item.AvailableVersion} installiert";
                        }
                        else if (lowerLine.Contains("failed") || lowerLine.Contains("fehler") || lowerLine.Contains("fehlgeschlagen"))
                        {
                            item.Status = "Fehlgeschlagen";
                            item.Detail = "Installation fehlgeschlagen";
                        }
                        else if (lowerLine.Contains("download") || lowerLine.Contains("herunterladen"))
                        {
                            item.Status = "Herunterladen...";
                            item.Detail = "Download läuft...";
                        }
                        else if (lowerLine.Contains("hash") || lowerLine.Contains("überprüf"))
                        {
                            item.Status = "Prüfe Integrität...";
                            item.Detail = "Hash wird geprüft...";
                        }
                        else
                        {
                            item.Status = "Wird installiert...";
                            item.Detail = "Installationsroutine aktiv...";
                        }
                    }
                }
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
            });
        }

        private void ParseDriverLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("GESAMT:", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(line, @"GESAMT:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int count))
                {
                    InvokeOnUi(() => { DriversFoundCount = count; });
                }
            }

            if (line.Contains("[PFLICHT]") || line.Contains("[OPTIONAL]"))
            {
                bool isMandatory = line.Contains("[PFLICHT]");
                string title = line.Replace("[PFLICHT]", "").Replace("[OPTIONAL]", "").Trim();
                if (title.StartsWith("Vorgemerkt:", StringComparison.OrdinalIgnoreCase))
                    title = title.Substring("Vorgemerkt:".Length).Trim();
                if (title.StartsWith("Gefunden:", StringComparison.OrdinalIgnoreCase))
                    return;

                if (!string.IsNullOrWhiteSpace(title))
                {
                    InvokeOnUi(() =>
                    {
                        if (!UpdatesCollection.Any(x => x.Name.Equals(title, StringComparison.OrdinalIgnoreCase)))
                        {
                            UpdatesCollection.Add(new UpdateItem
                            {
                                Name = title,
                                Type = "Treiber-Update",
                                CurrentVersion = "Installiert",
                                AvailableVersion = "WHQL-Update",
                                Status = isMandatory ? "Pflicht-Treiber" : "Optionaler Treiber",
                                Detail = isMandatory ? "Wichtiges System-Update" : "Optionales Hardware-Update",
                                UpdateId = title
                            });
                            DriversFoundCount = UpdatesCollection.Count(x => x.Type == "Treiber-Update");
                        }
                    });
                }
            }
        }

        private async Task CheckUpdatesWrapperAsync(CancellationToken ct)
        {
            await InvokeOnUiAsync(() =>
            {
                var toRemove = UpdatesCollection.Where(x => x.Type == "Software-Update").ToList();
                foreach (var item in toRemove) UpdatesCollection.Remove(item);
                UpdatesFoundCount = 0;
                CurrentUpdateActivity = "Lade Quellen & prüfe Updates...";
            });

            await _updateService.CheckUpdatesAsync(msg => { Log(msg); ParseWingetUpgradeLine(msg); }, ct);

            await InvokeOnUiAsync(() =>
            {
                CurrentUpdateActivity = $"Suche beendet. {UpdatesFoundCount} Update(s) gefunden.";
            });
        }

        private async Task InstallUpdatesWrapperAsync(CancellationToken ct)
        {
            var softwareUpdates = UpdatesCollection.Where(x => x.Type == "Software-Update").ToList();

            if (softwareUpdates.Count > 0)
            {
                Log($"\n--- STARTE GEZIELTE INSTALLATION VON {softwareUpdates.Count} SOFTWARE-UPDATES ---");

                int successCount = 0;
                int failCount = 0;

                for (int i = 0; i < softwareUpdates.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var item = softwareUpdates[i];
                    int currentIndex = i + 1;
                    int totalCount = softwareUpdates.Count;

                    await InvokeOnUiAsync(() =>
                    {
                        item.Status = "Herunterladen...";
                        item.Detail = "Wird vorbereitet...";
                        CurrentUpdateActivity = $"[{currentIndex}/{totalCount}] {item.Name}: Wird vorbereitet...";
                    });

                    Log($"\n[{currentIndex}/{totalCount}] Starte Update für '{item.Name}' (ID: {item.UpdateId})...");

                    Action<string> onProgressLog = line =>
                    {
                        Log(line);
                        if (string.IsNullOrWhiteSpace(line)) return;
                        string lower = line.ToLowerInvariant();
                        InvokeOnUi(() =>
                        {
                            if (lower.Contains("download") || lower.Contains("herunterladen") || lower.Contains(" mb ") || lower.Contains("%"))
                            {
                                item.Status = "Herunterladen...";
                                var match = Regex.Match(line, @"(\d+\s*(?:MB|KB|GB|%)\s*(?:/\s*\d+\s*(?:MB|KB|GB))?|\d+%)", RegexOptions.IgnoreCase);
                                item.Detail = match.Success ? $"Download: {match.Value}" : "Download läuft...";
                                CurrentUpdateActivity = $"[{currentIndex}/{totalCount}] {item.Name}: {item.Detail}";
                            }
                            else if (lower.Contains("hash") || lower.Contains("überprüf") || lower.Contains("verif"))
                            {
                                item.Status = "Prüfe Integrität...";
                                item.Detail = "Hash wird geprüft...";
                                CurrentUpdateActivity = $"[{currentIndex}/{totalCount}] {item.Name}: Prüfe Integrität...";
                            }
                            else if (lower.Contains("install") || lower.Contains("paket") || lower.Contains("start"))
                            {
                                item.Status = "Wird installiert...";
                                item.Detail = "Installationsroutine aktiv...";
                                CurrentUpdateActivity = $"[{currentIndex}/{totalCount}] {item.Name}: Wird installiert...";
                            }
                        });
                    };

                    int exitCode = await _updateService.InstallSingleUpdateAsync(item.UpdateId, onProgressLog, ct);

                    if (ct.IsCancellationRequested) break;

                    bool isSuccess = (exitCode == 0 || exitCode == 1641 || exitCode == 3010);
                    if (isSuccess)
                    {
                        successCount++;
                        await InvokeOnUiAsync(() =>
                        {
                            item.Status = "Erfolgreich";
                            item.Detail = string.IsNullOrEmpty(item.AvailableVersion) ? "Aktualisiert" : $"v{item.AvailableVersion} installiert";
                        });
                        Log($"[ERFOLG] '{item.Name}' erfolgreich aktualisiert (Exit-Code: {exitCode}).");
                    }
                    else
                    {
                        failCount++;
                        await InvokeOnUiAsync(() =>
                        {
                            item.Status = "Fehlgeschlagen";
                            item.Detail = $"Fehler (Code: {exitCode})";
                        });
                        Log($"[WARNUNG] Update für '{item.Name}' fehlgeschlagen mit Exit-Code: {exitCode}.");
                    }

                    await InvokeOnUiAsync(() =>
                    {
                        UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
                    });
                }

                await InvokeOnUiAsync(() =>
                {
                    CurrentUpdateActivity = $"Software-Updates abgeschlossen ({successCount} erfolgreich, {failCount} fehlgeschlagen).";
                });
            }
            else
            {
                await InvokeOnUiAsync(() => { CurrentUpdateActivity = "Starte Deep-Install aller Pakete..."; });
                await _updateService.InstallUpdatesAsync(msg => { Log(msg); ParseWingetInstallLine(msg); }, ct);
                await InvokeOnUiAsync(() =>
                {
                    CurrentUpdateActivity = "Installation beendet.";
                    foreach (var item in UpdatesCollection.Where(x => x.Type == "Software-Update").ToList())
                    {
                        if (item.Status != "Fehlgeschlagen")
                        {
                            item.Status = "Erfolgreich";
                            item.Detail = "Aktualisiert";
                        }
                    }
                    UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
                });
            }

            await Task.Delay(2500, ct);
            if (ct.IsCancellationRequested) return;

            await InvokeOnUiAsync(() =>
            {
                var done = UpdatesCollection.Where(x => x.Type == "Software-Update" && x.Status == "Erfolgreich").ToList();
                foreach (var item in done) UpdatesCollection.Remove(item);
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
            });
        }

        private async Task CheckDriversWrapperAsync(CancellationToken ct)
        {
            await InvokeOnUiAsync(() =>
            {
                var toRemove = UpdatesCollection.Where(x => x.Type == "Treiber-Update").ToList();
                foreach (var item in toRemove) UpdatesCollection.Remove(item);
                DriversFoundCount = 0;
                CurrentUpdateActivity = "Suche nach offiziellen Microsoft-Treibern...";
            });

            await _driverService.CheckDriversAsync(msg =>
            {
                Log(msg);
                ParseDriverLine(msg);
            }, ct);

            await InvokeOnUiAsync(() =>
            {
                CurrentUpdateActivity = $"Treibersuche beendet. {DriversFoundCount} Treiber-Update(s) gefunden.";
            });
        }

        private async Task InstallDriversWrapperAsync(CancellationToken ct)
        {
            await InvokeOnUiAsync(() =>
            {
                CurrentUpdateActivity = "Installiere Treiber-Updates...";
                foreach (var item in UpdatesCollection.Where(x => x.Type == "Treiber-Update"))
                {
                    item.Status = "Wird installiert...";
                    item.Detail = "Download & Installation...";
                }
            });

            await _driverService.InstallDriversAsync(msg =>
            {
                Log(msg);
                if (msg.Contains("Ergebnis-Code", StringComparison.OrdinalIgnoreCase))
                {
                    InvokeOnUi(() =>
                    {
                        foreach (var item in UpdatesCollection.Where(x => x.Type == "Treiber-Update"))
                        {
                            item.Status = "Erfolgreich";
                            item.Detail = "Treiber aktualisiert";
                        }
                        DriversFoundCount = 0;
                    });
                }
            }, ct);

            await InvokeOnUiAsync(() =>
            {
                CurrentUpdateActivity = "Treiber-Installation abgeschlossen.";
            });

            await Task.Delay(2500, ct);
            if (ct.IsCancellationRequested) return;

            await InvokeOnUiAsync(() =>
            {
                var done = UpdatesCollection.Where(x => x.Type == "Treiber-Update" && x.Status == "Erfolgreich").ToList();
                foreach (var item in done) UpdatesCollection.Remove(item);
                DriversFoundCount = UpdatesCollection.Count(x => x.Type == "Treiber-Update" && x.Status != "Erfolgreich");
            });
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

    /// <summary>
    /// Custom-Sortierer für SoftwareItem.
    /// Setzt Einträge OHNE Datum (InstallDateTime == null) IMMER ans Ende,
    /// unabhängig von Sortierrichtung. So zeigt "Neueste zuerst" wirklich die
    /// neuesten Programme oben – nicht mehr "Unbekannt" ganz oben.
    /// </summary>
    internal class SoftwareItemComparer : IComparer
    {
        private readonly string _mode;

        public SoftwareItemComparer(string mode)
        {
            _mode = mode;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not SoftwareItem a || y is not SoftwareItem b) return 0;

            int result = 0;

            switch (_mode)
            {
                case "Neueste zuerst":
                    result = CompareNullableDates(a.InstallDateTime, b.InstallDateTime, descending: true);
                    if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    break;

                case "Älteste zuerst":
                    result = CompareNullableDates(a.InstallDateTime, b.InstallDateTime, descending: false);
                    if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    break;

                case "Name (A-Z)":
                    result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    break;

                case "Name (Z-A)":
                    result = string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
                    break;

                case "Größe (Große zuerst)":
                    result = a.EstimatedSizeKb.CompareTo(b.EstimatedSizeKb);
                    if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    else result = -result;
                    break;
            }

            return result;
        }

        private static int CompareNullableDates(DateTime? a, DateTime? b, bool descending)
        {
            if (!a.HasValue && !b.HasValue) return 0;
            if (!a.HasValue) return 1;
            if (!b.HasValue) return -1;

            int cmp = DateTime.Compare(a.Value, b.Value);
            return descending ? -cmp : cmp;
        }
    }
}
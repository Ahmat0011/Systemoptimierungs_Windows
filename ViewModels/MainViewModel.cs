
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
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
        private CancellationTokenSource? _cts;

        public MainViewModel(
            IUpdateService  updateService,
            IDriverService  driverService,
            IRepairService  repairService,
            ICleanupService cleanupService,
            IUninstallerService uninstallerService,
            IStorageService storageService,
            IPerformanceService performanceService)
        {
            _updateService  = updateService;
            _driverService  = driverService;
            _repairService  = repairService;
            _cleanupService = cleanupService;
            _uninstallerService = uninstallerService;
            _storageService = storageService;
            _performanceService = performanceService;

            // Gefilterte Sichten (ICollectionView): filtert in-place, ohne dass die
            // DataGrids bei jedem Tastendruck alle Zeilen-Container neu aufbauen.
            FilteredSoftware = CollectionViewSource.GetDefaultView(_installedSoftware);
            FilteredSoftware.Filter = o => MatchesSoftwareSearch(o as SoftwareItem, SearchText);
            FilteredUwpApps = CollectionViewSource.GetDefaultView(_installedUwpApps);
            FilteredUwpApps.Filter = o => MatchesSoftwareSearch(o as SoftwareItem, SearchText);
            FilteredStartupItems = CollectionViewSource.GetDefaultView(_startupItems);
            FilteredStartupItems.Filter = o => MatchesStartupSearch(o as StartupItem, StartupSearchText);

            // Log-Batching: Zeilen werden gepuffert und gebündelt (10x/s) in die UI
            // geschrieben statt pro Zeile (verhindert Dispatcher-Flut & UI-Freeze).
            _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _logFlushTimer.Tick += FlushLogBuffer;
            _logFlushTimer.Start();

            // Zentrale Fehlerbehandlung für alle asynchronen Commands
            Action<Exception> onCommandError = ex => Log($"\nKritischer Fehler: {ex.Message}");

            // Update Center
            CheckUpdatesCommand   = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("Updates suchen",       ct => CheckUpdatesWrapperAsync(ct)),   _ => !IsBusy, onCommandError);
            InstallUpdatesCommand = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("Updates installieren", ct => InstallUpdatesWrapperAsync(ct)), _ => !IsBusy, onCommandError);
            CheckDriversCommand   = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("Treiber suchen",       ct => CheckDriversWrapperAsync(ct)),   _ => !IsBusy, onCommandError);
            InstallDriversCommand = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("Treiber installieren", ct => InstallDriversWrapperAsync(ct)), _ => !IsBusy, onCommandError);

            // System-Reparatur
            RepairSystemCommand   = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("System reparieren",   ct => _repairService.RepairSystemAsync(Log, ct)),    _ => !IsBusy, onCommandError);

            // Bereinigung & Optimierung
            CleanupSystemCommand  = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("System bereinigen",   async ct => {
                await _cleanupService.CleanupSystemAsync(Log, ct);
                await RefreshStorageInfoAsync();
            },  false), _ => !IsBusy, onCommandError);
            OptimizeNetworkCommand = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("Netzwerk optimieren", ct => _cleanupService.OptimizeNetworkAsync(Log, ct), false), _ => !IsBusy, onCommandError);

            // Uninstaller (Software & Apps Manager - Batch Support)
            LoadAppsCommand = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("Software & Apps laden", ct => LoadAppsAsync(ct), false), _ => !IsBusy, onCommandError);
            UninstallCommand = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("Deinstallation", ct => UninstallAppAsync(ct), false), _ => !IsBusy && (SelectedSoftware != null || SelectedUwpApp != null), onCommandError);

            // System-Leistung (RAM-Booster & Autostart Manager)
            BoostRamCommand = new AsyncRelayCommand(_ => ExecuteWithBusyStateAsync("RAM optimieren", ct => _performanceService.BoostRamAsync(Log, ct), false), _ => !IsBusy, onCommandError);
            ToggleStartupCommand = new AsyncRelayCommand(param => ExecuteWithBusyStateAsync("Autostart ändern", async ct => {
                if (param is StartupItem item) {
                    await _performanceService.ToggleStartupItemAsync(item);
                    await LoadStartupItemsAsync(ct);
                }
            }, false), param => !IsBusy && param is StartupItem, onCommandError);

            // Global
            CancelCommand  = new RelayCommand(_ => CancelOperation(), _ => IsBusy);

            Log("System Optimierer gestartet. Bereit für Befehle...\n");

            // Initiales Laden auf dem UI-Thread starten (kein Task.Run!):
            // IsBusy/CommandManager sind thread-affin – die schweren Arbeiten
            // laufen innerhalb von ExecuteWithBusyStateAsync ohnehin über Task.Run.
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await RefreshStorageInfoAsync();
                await Task.Delay(300);
                await ExecuteWithBusyStateAsync("System-Komponenten laden", async ct => {
                    await LoadAppsAsync(ct);
                    await LoadStartupItemsAsync(ct);
                }, false);
            }
            catch (Exception ex)
            {
                Log($"[WARNUNG] Fehler beim initialen Laden: {ex.Message}");
            }
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

        private readonly ObservableCollection<SoftwareItem> _installedSoftware = new();
        public ObservableCollection<SoftwareItem> InstalledSoftware => _installedSoftware;

        private readonly ObservableCollection<SoftwareItem> _installedUwpApps = new();
        public ObservableCollection<SoftwareItem> InstalledUwpApps => _installedUwpApps;

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
                    FilteredSoftware.Refresh();
                    FilteredUwpApps.Refresh();
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

        // Gefilterte Sichten – im Konstruktor initialisiert, filtern in-place.
        public ICollectionView FilteredSoftware { get; }
        public ICollectionView FilteredUwpApps { get; }
        public ICollectionView FilteredStartupItems { get; }

        private static bool MatchesSoftwareSearch(SoftwareItem? app, string search)
        {
            if (app == null) return false;
            if (string.IsNullOrWhiteSpace(search)) return true;
            return app.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   app.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesStartupSearch(StartupItem? item, string search)
        {
            if (item == null) return false;
            if (string.IsNullOrWhiteSpace(search)) return true;
            return item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   item.Path.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        // Startup Manager Properties
        private readonly ObservableCollection<StartupItem> _startupItems = new();
        public ObservableCollection<StartupItem> StartupItems => _startupItems;

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
                    FilteredStartupItems.Refresh();
                }
            }
        }



        private string _currentUpdateActivity = "Bereit";
        public string CurrentUpdateActivity
        {
            get => _currentUpdateActivity;
            set
            {
                if (_currentUpdateActivity != value)
                {
                    _currentUpdateActivity = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _updatesFoundCount;
        public int UpdatesFoundCount
        {
            get => _updatesFoundCount;
            set
            {
                if (_updatesFoundCount != value)
                {
                    _updatesFoundCount = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _driversFoundCount;
        public int DriversFoundCount
        {
            get => _driversFoundCount;
            set
            {
                if (_driversFoundCount != value)
                {
                    _driversFoundCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<UpdateItem> UpdatesCollection { get; } = new ObservableCollection<UpdateItem>();

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

        #region Methods

        private const int MaxLogLength = 60_000;

        private readonly StringBuilder _logBuffer = new();
        private readonly object _logLock = new();
        private readonly DispatcherTimer _logFlushTimer;

        /// <summary>
        /// Thread-sicher: puffert die Zeile nur. Der DispatcherTimer schreibt die
        /// gesammelten Zeilen gebündelt in LogText (max. 10 UI-Updates pro Sekunde
        /// statt eines Dispatcher-Aufrufs pro Zeile).
        /// </summary>
        private void Log(string message)
        {
            lock (_logLock)
            {
                _logBuffer.Append(message).Append('\n');
            }
        }

        private void FlushLogBuffer(object? sender, EventArgs e)
        {
            string pending;
            lock (_logLock)
            {
                if (_logBuffer.Length == 0) return;
                pending = _logBuffer.ToString();
                _logBuffer.Clear();
            }

            string combined = LogText + pending;
            if (combined.Length > MaxLogLength)
            {
                // Nur den jüngsten Teil behalten, damit die TextBox performant bleibt
                int startSearch = Math.Max(0, combined.Length - (MaxLogLength / 2));
                int cutAt = combined.IndexOf('\n', startSearch);
                combined = cutAt > 0
                    ? "[... ältere Einträge wurden gekürzt ...]\n" + combined.Substring(cutAt + 1)
                    : pending;
            }
            LogText = combined;
        }

        private async Task<bool> IsInternetAvailableAsync()
        {
            // Schnell-Ausstieg: gar kein Netzwerk-Adapter aktiv
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;

            // 1. Versuch: Ping (schnell, aber ICMP kann von Firewalls geblockt sein)
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync("1.1.1.1", 2000);
                if (reply.Status == IPStatus.Success) return true;
            }
            catch { /* weiter mit TCP-Fallback */ }

            // 2. Versuch: TCP-Verbindung auf Port 443 (funktioniert auch ohne ICMP)
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("www.microsoft.com", 443, cts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task ExecuteWithBusyStateAsync(string operationName, Func<CancellationToken, Task> action, bool requiresInternet = true)
        {
            if (requiresInternet && !await IsInternetAvailableAsync())
            {
                Log($"FEHLER: Keine Internetverbindung gefunden! [{operationName}] abgebrochen.\n");
                return;
            }

            IsBusy = true;
            StatusMessage = $"{operationName} läuft...";

            // Lokale Referenz: verhindert Race-Condition zwischen Dispose (hier)
            // und Cancel (CancelOperation) auf dem geteilten _cts-Feld.
            var cts = new CancellationTokenSource();
            _cts = cts;

            try
            {
                await Task.Run(() => action(cts.Token));
                StatusMessage = $"{operationName} erfolgreich abgeschlossen.";
            }
            catch (OperationCanceledException) // Basisklasse fängt auch TaskCanceledException
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
                _cts = null;   // Erst Feld freigeben, dann lokale Instanz entsorgen
                cts.Dispose();
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
            });
            Log($"Erfolgreich {items.Count} Autostart-Programme geladen.");
        }

        private async Task UninstallAppAsync(CancellationToken ct)
        {
            SoftwareItem? app = SelectedSoftware ?? SelectedUwpApp;

            if (app == null)
            {
                Log("FEHLER: Keine Anwendung zum Deinstallieren ausgewählt.");
                return;
            }

            Log($"Bereite Deinstallation für '{app.Name}' vor...");
            
            await _uninstallerService.UninstallSoftwareAsync(app, CleanLeftovers, Log, ct);
            
            if (ct.IsCancellationRequested) return;

            Log("\nDeinstallation abgeschlossen. Aktualisiere die Listen und die Speicherplatz-Anzeige...");
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



        private void ParseWingetUpgradeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("---") || line.Contains("Name") || line.Contains("ID ") || line.Contains("Version") || line.Contains("Source") || line.Contains("Quelle")) return;
            if (line.Contains("Keine installierten Pakete") || line.Contains("No upgrade available") || line.Contains("keine Aktualisierung") || line.Contains("No packages found")) return;
            if (line.Contains("%") || line.Contains("Downloading") || line.Contains("Herunterladen") || line.Contains("[") || line.Contains("]")) return;

            var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length >= 4)
            {
                string name = parts[0];
                string id = parts[1];
                string currentVer = parts[2];
                string availableVer = parts[3];

                if (!string.IsNullOrEmpty(id) && id.Contains(".") && (currentVer.Any(char.IsDigit) || currentVer == "<" || availableVer.Any(char.IsDigit)))
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
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
                                Status = "Ausstehend"
                            });
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
                foreach (var item in UpdatesCollection.Where(x => x.Type == "Software-Update").ToList())
                {
                    if (line.Contains(item.UpdateId) || line.Contains(item.Name))
                    {
                        if (line.Contains("Successfully installed") || line.Contains("Erfolgreich installiert") || line.Contains("installiert"))
                        {
                            item.Status = "Erfolgreich";
                        }
                        else if (line.Contains("failed") || line.Contains("Fehler") || line.Contains("fehlgeschlagen"))
                        {
                            item.Status = "Fehlgeschlagen";
                        }
                        else if (line.Contains("Downloading") || line.Contains("Herunterladen") || line.Contains("Download"))
                        {
                            item.Status = "Wird heruntergeladen...";
                        }
                        else
                        {
                            item.Status = "Wird installiert...";
                        }
                    }
                }
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
            }));
        }

        private void MarkDriversInstalling()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var item in UpdatesCollection.Where(x => x.Type.Contains("Treiber-Update")))
                {
                    item.Status = "Wird installiert...";
                }
            }));
        }

        private void MarkDriversCompleted()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var item in UpdatesCollection.Where(x => x.Type.Contains("Treiber-Update")))
                {
                    item.Status = "Erfolgreich";
                }
                DriversFoundCount = 0;
            }));
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

            Action<string> wrapperLog = msg =>
            {
                Log(msg);
                if (string.IsNullOrEmpty(msg)) return;

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (msg.Contains("[1/3]")) CurrentUpdateActivity = "Winget-Quellen aktualisieren...";
                    else if (msg.Contains("[2/3]")) CurrentUpdateActivity = "Machine-Scope scannen...";
                    else if (msg.Contains("[3/3]")) CurrentUpdateActivity = "User-Scope scannen...";
                }));

                ParseWingetUpgradeLine(msg);
            };

            await _updateService.CheckUpdatesAsync(wrapperLog, ct);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = $"Suche beendet. {UpdatesFoundCount} Update(s) gefunden.";
            });
        }

        private async Task InstallUpdatesWrapperAsync(CancellationToken ct)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = "Starte Installation...";
            });

            Action<string> wrapperLog = msg =>
            {
                Log(msg);
                if (string.IsNullOrEmpty(msg)) return;

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (msg.Contains("[1/3]")) CurrentUpdateActivity = "Winget-Quellen aktualisieren...";
                    else if (msg.Contains("[2/3]")) CurrentUpdateActivity = "Installiere Machine-Scope Updates...";
                    else if (msg.Contains("[3/3]")) CurrentUpdateActivity = "Installiere User-Scope/Store Updates...";
                }));

                ParseWingetInstallLine(msg);
            };

            await _updateService.InstallUpdatesAsync(wrapperLog, ct);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = "Installation beendet.";
                foreach (var item in UpdatesCollection.Where(x => x.Type == "Software-Update" && x.Status == "Ausstehend"))
                {
                    item.Status = "Erfolgreich";
                }
                UpdatesFoundCount = 0;
            });
        }

        private async Task CheckDriversWrapperAsync(CancellationToken ct)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var toRemove = UpdatesCollection.Where(x => x.Type.Contains("Treiber-Update")).ToList();
                foreach (var item in toRemove) UpdatesCollection.Remove(item);

                DriversFoundCount = 0;
                CurrentUpdateActivity = "Suche nach Hardware-Treibern...";
            });

            Action<string> wrapperLog = msg =>
            {
                Log(msg);
                if (string.IsNullOrEmpty(msg)) return;

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (msg.Contains("Stelle sicher")) CurrentUpdateActivity = "Überprüfe Windows Update Dienst...";
                    else if (msg.Contains("Verbinde mit")) CurrentUpdateActivity = "Verbinde mit Microsoft Update Servern...";
                    else if (msg.Contains("[PFLICHT-TREIBER]")) CurrentUpdateActivity = "Suche Pflicht-Treiber...";
                    else if (msg.Contains("[OPTIONALE TREIBER]")) CurrentUpdateActivity = "Suche optionale Treiber...";

                    if (msg.Contains("[PFLICHT]") || msg.Contains("[OPTIONAL]"))
                    {
                        string title = msg.Replace("[PFLICHT]", "").Replace("[OPTIONAL]", "").Trim();
                        string type = msg.Contains("[PFLICHT]") ? "Treiber-Update (Pflicht)" : "Treiber-Update (Optional)";
                        string status = msg.Contains("[PFLICHT]") ? "Pflicht-Treiber" : "Optionaler Treiber";

                        if (!UpdatesCollection.Any(x => x.Name == title))
                        {
                            UpdatesCollection.Add(new UpdateItem
                            {
                                Name = title,
                                Type = type,
                                CurrentVersion = "Installiert",
                                AvailableVersion = "Update verfügbar",
                                Status = status
                            });
                        }
                        DriversFoundCount = UpdatesCollection.Count(x => x.Type.Contains("Treiber-Update"));
                    }
                }));
            };

            await _driverService.CheckDriversAsync(wrapperLog, ct);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = $"Treibersuche beendet. {DriversFoundCount} Treiber verfügbar.";
            });
        }

        private async Task InstallDriversWrapperAsync(CancellationToken ct)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = "Treiber werden heruntergeladen...";
                MarkDriversInstalling();
            });

            Action<string> wrapperLog = msg =>
            {
                Log(msg);
                if (string.IsNullOrEmpty(msg)) return;

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (msg.Contains("heruntergeladen")) CurrentUpdateActivity = "Herunterladen der Updates...";
                    else if (msg.Contains("Installiere Treiber")) CurrentUpdateActivity = "Installiere Treiber (Bildschirm flackert eventuell)...";
                    else if (msg.Contains("Installation abgeschlossen"))
                    {
                        CurrentUpdateActivity = "Treiberinstallation abgeschlossen.";
                        MarkDriversCompleted();
                    }
                }));
            };

            await _driverService.InstallDriversAsync(wrapperLog, ct);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentUpdateActivity = "Treiberinstallation abgeschlossen.";
                MarkDriversCompleted();
            });
        }

        private void CancelOperation()
        {
            // Lokale Kopie gegen Race mit dem finally-Block in ExecuteWithBusyStateAsync
            var cts = _cts;
            if (cts == null) return;

            try
            {
                if (!cts.IsCancellationRequested)
                {
                    Log("\nAbbruch wird eingeleitet...");
                    cts.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // Operation ist gerade regulär beendet worden – nichts zu tun
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

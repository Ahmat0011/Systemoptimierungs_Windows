
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

            // Update Center
            CheckUpdatesCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates suchen",       ct => CheckUpdatesWrapperAsync(ct)),   _ => !IsBusy);
            InstallUpdatesCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates installieren", ct => InstallUpdatesWrapperAsync(ct)), _ => !IsBusy);
            CheckDriversCommand   = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber suchen",       ct => CheckDriversWrapperAsync(ct)),   _ => !IsBusy);
            InstallDriversCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber installieren", ct => InstallDriversWrapperAsync(ct)), _ => !IsBusy);

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

            // Global
            CancelCommand  = new RelayCommand(_ => CancelOperation(), _ => IsBusy);

            Log("System Optimierer gestartet. Bereit für Befehle...\n");

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

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
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
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IUpdateService _updateService;
        private readonly IDriverService _driverService;
        private readonly IRepairService _repairService;
        private readonly ICleanupService _cleanupService;
        private readonly IUninstallerService _uninstallerService;
        private readonly IStorageService _storageService;
        private readonly IPerformanceService _performanceService;
        private readonly IInstalledAppsWatcher _appsWatcher;
        private CancellationTokenSource? _cts;

        // Lebensdauer der App: beendet Hintergrund-Aktualisierungen beim Schließen
        private readonly CancellationTokenSource _lifetimeCts = new();

        // Live-Aktualisierung der Programmlisten (serialisiert und zusammengefasst)
        private readonly CoalescingRefresh _desktopAppsRefresh;
        private readonly CoalescingRefresh _storeAppsRefresh;
        private volatile bool _storeAppsLoaded;
        private volatile int _storeAppsFingerprint;
        private DateTime _lastActivationRefreshUtc = DateTime.MinValue;
        private const int MaxLiveLogEntries = 10;

        // Die Suche wird entprellt: Bei jedem Tastendruck über 10.000+ Einträge zu filtern ruckelt sonst.
        private const int SearchDebounceMs = 180;
        private readonly DispatcherTimer _searchDebounceTimer;

        // Während einer Massenauswahl ("Alle auswählen") wird die teure Statusauswertung nur EINMAL ausgeführt.
        private int _selectionBatchDepth;
        private bool _selectionChangedInBatch;
        private bool _disposed;

        public ICollectionView SoftwareView { get; }
        public ICollectionView UwpView { get; }

        public MainViewModel(
            IUpdateService updateService,
            IDriverService driverService,
            IRepairService repairService,
            ICleanupService cleanupService,
            IUninstallerService uninstallerService,
            IStorageService storageService,
            IPerformanceService performanceService,
            IInstalledAppsWatcher appsWatcher)
        {
            _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
            _driverService = driverService ?? throw new ArgumentNullException(nameof(driverService));
            _repairService = repairService ?? throw new ArgumentNullException(nameof(repairService));
            _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
            _uninstallerService = uninstallerService ?? throw new ArgumentNullException(nameof(uninstallerService));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _performanceService = performanceService ?? throw new ArgumentNullException(nameof(performanceService));
            _appsWatcher = appsWatcher ?? throw new ArgumentNullException(nameof(appsWatcher));

            SoftwareView = CollectionViewSource.GetDefaultView(_installedSoftware);
            SoftwareView.Filter = FilterSoftwarePredicate;

            UwpView = CollectionViewSource.GetDefaultView(_installedUwpApps);
            UwpView.Filter = FilterUwpAppsPredicate;

            // Sortierung von Anfang an setzen, damit live hinzugefügte Programme direkt richtig einsortiert werden
            ApplySoftwareSorting();
            ApplyUwpSorting();

            _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
            };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer.Stop();
                SoftwareView.Refresh();
                UwpView.Refresh();
            };

            _desktopAppsRefresh = new CoalescingRefresh(LoadDesktopAppsCoreAsync);
            _storeAppsRefresh = new CoalescingRefresh(LoadStoreAppsCoreAsync);
            _appsWatcher.DesktopAppsChanged += (_, _) => RunBackgroundRefresh(RefreshDesktopAppsAsync);
            _appsWatcher.StoreAppsChanged += (_, _) => RunBackgroundRefresh(() => RefreshStoreAppsAsync(force: false));

            // Update Center
            CheckUpdatesCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Updates suchen", (ct, progress) => CheckUpdatesWrapperAsync(ct, progress)),
                _ => !IsBusy, OnCommandException);
            InstallUpdatesCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Updates installieren", (ct, progress) => InstallUpdatesWrapperAsync(ct, progress)),
                _ => !IsBusy, OnCommandException);
            CheckDriversCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Treiber suchen", (ct, progress) => CheckDriversWrapperAsync(ct, progress)),
                _ => !IsBusy, OnCommandException);
            InstallDriversCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Treiber installieren", (ct, progress) => InstallDriversWrapperAsync(ct, progress)),
                _ => !IsBusy, OnCommandException);

            // System-Reparatur
            RepairSystemCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("System reparieren", (ct, progress) => _repairService.RepairSystemAsync(Log, progress, ct)),
                _ => !IsBusy, OnCommandException);

            // Bereinigung & Optimierung
            CleanupSystemCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("System bereinigen", async (ct, progress) =>
                {
                    await _cleanupService.CleanupSystemAsync(Log, progress, ct);
                    await RefreshStorageInfoAsync();
                }, false),
                _ => !IsBusy, OnCommandException);
            OptimizeNetworkCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("Netzwerk optimieren", ct => _cleanupService.OptimizeNetworkAsync(Log, ct), false),
                _ => !IsBusy, OnCommandException);

            // Bereich "Deinstallieren"
            RefreshAppsCommand = new AsyncRelayCommand(
                _ => LoadAppsAsync(_lifetimeCts.Token),
                null, OnCommandException);

            UninstallCommand = new AsyncRelayCommand(
                param => ExecuteWithBusyStateAsync("Deinstallation", ct => UninstallSelectedAppsAsync(param as string, ct), false),
                param => !IsBusy && HasSelectedApps(param as string), OnCommandException);

            // System-Leistung
            BoostRamCommand = new AsyncRelayCommand(
                _ => ExecuteWithBusyStateAsync("RAM optimieren", (ct, progress) => _performanceService.BoostRamAsync(Log, progress, ct), false),
                _ => !IsBusy, OnCommandException);
            ToggleStartupCommand = new AsyncRelayCommand(
                async param =>
                {
                    if (param is StartupItem item)
                    {
                        await ExecuteWithBusyStateAsync("Autostart ändern", async ct =>
                        {
                            await _performanceService.ToggleStartupItemAsync(item);
                            Log(item.IsEnabled
                                ? $"[ERFOLG] Autostart aktiviert: {item.Name}"
                                : $"[ERFOLG] Autostart deaktiviert: {item.Name}");
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
                _appsWatcher.Start();
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

        public void RequestShutdown()
        {
            try { _searchDebounceTimer.Stop(); } catch { }

            try
            {
                if (_cts != null && !_cts.IsCancellationRequested)
                    _cts.Cancel();
            }
            catch { }

            try { _lifetimeCts.Cancel(); } catch { }
            try { _appsWatcher.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RequestShutdown();

            try { _cts?.Dispose(); } catch { }
            try { _lifetimeCts.Dispose(); } catch { }
            try { _desktopAppsRefresh.Dispose(); } catch { }
            try { _storeAppsRefresh.Dispose(); } catch { }

            GC.SuppressFinalize(this);
        }

        public void OnWindowActivated()
        {
            if (_disposed || !_storeAppsLoaded) return;
            var now = DateTime.UtcNow;
            if (now - _lastActivationRefreshUtc < TimeSpan.FromSeconds(3)) return;
            _lastActivationRefreshUtc = now;

            RunBackgroundRefresh(RefreshDesktopAppsAsync);
            RunBackgroundRefresh(() => RefreshStoreAppsAsync(force: false));
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
            set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsNotBusy));
                    OnPropertyChanged(nameof(IsStatusBarIndeterminate));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }
        public bool IsNotBusy => !_isBusy;

        private string _logText = string.Empty;
        public string LogText { get => _logText; set { if (_logText != value) { _logText = value; OnPropertyChanged(); } } }

        private string _statusMessage = "Bereit";
        public string StatusMessage { get => _statusMessage; set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } } }

        private string _liveScanSpeed = "BEREIT";
        public string LiveScanSpeed { get => _liveScanSpeed; set { if (_liveScanSpeed != value) { _liveScanSpeed = value; OnPropertyChanged(); } } }

        private readonly ObservableCollection<SoftwareItem> _installedSoftware = new();
        public ObservableCollection<SoftwareItem> InstalledSoftware => _installedSoftware;

        private readonly ObservableCollection<SoftwareItem> _installedUwpApps = new();
        public ObservableCollection<SoftwareItem> InstalledUwpApps => _installedUwpApps;

        private string _softwareSearchText = string.Empty;
        public string SoftwareSearchText
        {
            get => _softwareSearchText;
            set
            {
                if (_softwareSearchText == value) return;
                _softwareSearchText = value;
                OnPropertyChanged();
                ScheduleViewRefresh();
            }
        }

        private string _uwpSearchText = string.Empty;
        public string UwpSearchText
        {
            get => _uwpSearchText;
            set
            {
                if (_uwpSearchText == value) return;
                _uwpSearchText = value;
                OnPropertyChanged();
                ScheduleViewRefresh();
            }
        }

        private void ScheduleViewRefresh()
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private bool _cleanLeftovers = true;
        public bool CleanLeftovers { get => _cleanLeftovers; set { if (_cleanLeftovers != value) { _cleanLeftovers = value; OnPropertyChanged(); } } }

        private string _selectedSoftwareStatusText = "Keine Auswahl";
        public string SelectedSoftwareStatusText
        {
            get => _selectedSoftwareStatusText;
            set { if (_selectedSoftwareStatusText != value) { _selectedSoftwareStatusText = value; OnPropertyChanged(); } }
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
                    UpdateSelectedSoftwareStatus();
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
                    UpdateSelectedSoftwareStatus();
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
                if (string.IsNullOrWhiteSpace(StartupSearchText)) return _startupItems;
                return _startupItems.Where(item =>
                    (item.Name?.Contains(StartupSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (item.Path?.Contains(StartupSearchText, StringComparison.OrdinalIgnoreCase) ?? false));
            }
        }

        private string _currentUpdateActivity = "Bereit";
        public string CurrentUpdateActivity { get => _currentUpdateActivity; set { if (_currentUpdateActivity != value) { _currentUpdateActivity = value; OnPropertyChanged(); } } }

        private int _updatesFoundCount;
        public int UpdatesFoundCount { get => _updatesFoundCount; set { if (_updatesFoundCount != value) { _updatesFoundCount = value; OnPropertyChanged(); } } }

        private int _driversFoundCount;
        public int DriversFoundCount { get => _driversFoundCount; set { if (_driversFoundCount != value) { _driversFoundCount = value; OnPropertyChanged(); } } }

        public ObservableCollection<UpdateItem> UpdatesCollection { get; } = new();

        #endregion

        #region Fortschrittsanzeige

        private bool _isProgressVisible;
        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            private set { if (_isProgressVisible != value) { _isProgressVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsStatusBarIndeterminate)); } }
        }

        private bool _isProgressIndeterminate;
        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            private set { if (_isProgressIndeterminate != value) { _isProgressIndeterminate = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsStatusBarIndeterminate)); } }
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            private set { if (Math.Abs(_progressValue - value) > 0.001) { _progressValue = value; OnPropertyChanged(); } }
        }

        private string _progressPercentText = "0 %";
        public string ProgressPercentText { get => _progressPercentText; private set { if (_progressPercentText != value) { _progressPercentText = value; OnPropertyChanged(); } } }

        private string _progressStep = string.Empty;
        public string ProgressStep { get => _progressStep; private set { if (_progressStep != value) { _progressStep = value; OnPropertyChanged(); } } }

        private string _progressDetail = string.Empty;
        public string ProgressDetail { get => _progressDetail; private set { if (_progressDetail != value) { _progressDetail = value; OnPropertyChanged(); } } }

        public bool IsStatusBarIndeterminate => IsBusy && (!IsProgressVisible || IsProgressIndeterminate);

        private int _progressSession;
        private bool _progressFinished;

        private void ResetProgress(int session)
        {
            _progressSession = session;
            _progressFinished = false;
            IsProgressVisible = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            ProgressPercentText = "0 %";
            ProgressStep = string.Empty;
            ProgressDetail = string.Empty;
        }

        private void ApplyProgress(int session, ProgressInfo info)
        {
            if (session != _progressSession || _progressFinished) return;

            IsProgressVisible = true;
            IsProgressIndeterminate = info.IsIndeterminate;
            double safePercent = double.IsFinite(info.Percent) ? Math.Clamp(info.Percent, 0, 100) : 0;
            ProgressValue = Math.Max(ProgressValue, safePercent);
            ProgressPercentText = $"{Math.Floor(ProgressValue):0} %";
            ProgressStep = info.Step;
            if (info.Detail != null) ProgressDetail = info.Detail;
        }

        private void FinishProgress(int session, string step, string detail, bool completed)
        {
            if (session != _progressSession) return;
            _progressFinished = true;
            if (!IsProgressVisible) return;

            IsProgressIndeterminate = false;
            if (completed)
            {
                ProgressValue = 100;
                ProgressPercentText = "100 %";
            }
            ProgressStep = step;
            ProgressDetail = detail;
            _ = HideProgressLaterAsync(session);
        }

        private async Task HideProgressLaterAsync(int session)
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (session == _progressSession && !IsBusy) ResetProgress(session);
        }

        #endregion

        #region Dispatcher-Helfer

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

        private static Task<T> InvokeOnUiAsync<T>(Func<T> func, T fallback)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return Task.FromResult(fallback);
            if (dispatcher.CheckAccess()) return Task.FromResult(func());
            return dispatcher.InvokeAsync(func).Task;
        }

        #endregion

        #region Methods & Predicates

        public void UpdateSelectedSoftwareStatus()
        {
            int classicCount = 0, uwpCount = 0;
            SoftwareItem? firstSelected = null;

            foreach (var app in _installedSoftware)
            {
                if (!app.IsSelected) continue;
                classicCount++;
                firstSelected ??= app;
            }

            foreach (var app in _installedUwpApps)
            {
                if (!app.IsSelected) continue;
                uwpCount++;
                firstSelected ??= app;
            }

            int count = classicCount + uwpCount;

            if (count == 0)
            {
                if (SelectedSoftware != null)
                {
                    SelectedSoftwareStatusText = $"1 Programm ausgewählt: {SelectedSoftware.Name}";
                }
                else if (SelectedUwpApp != null)
                {
                    SelectedSoftwareStatusText = $"1 App ausgewählt: {SelectedUwpApp.Name}";
                }
                else
                {
                    SelectedSoftwareStatusText = "Keine Auswahl";
                }
            }
            else if (count == 1)
            {
                SelectedSoftwareStatusText = $"1 Programm ausgewählt: {firstSelected?.Name}";
            }
            else
            {
                SelectedSoftwareStatusText = $"{count} Programme ausgewählt";
            }

            CommandManager.InvalidateRequerySuggested();
        }

        public void SetSelectionForVisibleSoftware(bool selected) => RunSelectionBatch(() =>
        {
            foreach (var item in SoftwareView)
            {
                if (item is SoftwareItem app) app.IsSelected = selected;
            }
        });

        public void SetSelectionForVisibleUwp(bool selected) => RunSelectionBatch(() =>
        {
            foreach (var item in UwpView)
            {
                if (item is SoftwareItem app) app.IsSelected = selected;
            }
        });

        private void RunSelectionBatch(Action action)
        {
            _selectionBatchDepth++;
            try
            {
                action();
            }
            finally
            {
                _selectionBatchDepth--;
                if (_selectionBatchDepth == 0 && _selectionChangedInBatch)
                {
                    _selectionChangedInBatch = false;
                    UpdateSelectedSoftwareStatus();
                }
            }
        }

        private bool FilterSoftwarePredicate(object obj)
        {
            if (obj is not SoftwareItem app) return false;
            if (string.IsNullOrWhiteSpace(SoftwareSearchText)) return true;
            return app.Name.Contains(SoftwareSearchText, StringComparison.OrdinalIgnoreCase) ||
                   app.Publisher.Contains(SoftwareSearchText, StringComparison.OrdinalIgnoreCase);
        }

        private bool FilterUwpAppsPredicate(object obj)
        {
            if (obj is not SoftwareItem app) return false;
            if (string.IsNullOrWhiteSpace(UwpSearchText)) return true;
            return app.Name.Contains(UwpSearchText, StringComparison.OrdinalIgnoreCase) ||
                   app.Publisher.Contains(UwpSearchText, StringComparison.OrdinalIgnoreCase);
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
        private readonly ConcurrentQueue<string> _pendingLog = new();
        private int _logFlushScheduled;

        private void Log(string message)
        {
            _pendingLog.Enqueue(message);
            if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 1) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Interlocked.Exchange(ref _logFlushScheduled, 0);
                return;
            }
            dispatcher.BeginInvoke(FlushLog, DispatcherPriority.Background);
        }

        private void FlushLog()
        {
            Interlocked.Exchange(ref _logFlushScheduled, 0);
            if (_pendingLog.IsEmpty) return;

            var builder = new StringBuilder(LogText);
            while (_pendingLog.TryDequeue(out var message))
                builder.Append(message).Append('\n');

            string combined = builder.ToString();
            if (combined.Length > MaxLogLength)
            {
                int startSearch = Math.Max(0, combined.Length - (MaxLogLength / 2));
                int cutAt = combined.IndexOf('\n', startSearch);
                combined = cutAt > 0
                    ? string.Concat("[... ältere Einträge wurden gekürzt ...]\n", combined.AsSpan(cutAt + 1))
                    : combined.Substring(combined.Length - (MaxLogLength / 2));
            }
            LogText = combined;
        }

        private static readonly object InternetCheckGate = new();
        private static DateTime _internetCheckedUtc = DateTime.MinValue;
        private static bool _internetAvailable;

        private static async Task<bool> IsInternetAvailableAsync()
        {
            TimeSpan maxAge = _internetAvailable
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(10);

            lock (InternetCheckGate)
            {
                if (DateTime.UtcNow - _internetCheckedUtc < maxAge) return _internetAvailable;
            }

            bool available = await ProbeInternetAsync();

            lock (InternetCheckGate)
            {
                _internetAvailable = available;
                _internetCheckedUtc = DateTime.UtcNow;
            }
            return available;
        }

        private static async Task<bool> ProbeInternetAsync()
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

        private Task ExecuteWithBusyStateAsync(string operationName, Func<CancellationToken, Task> action, bool requiresInternet = true)
            => ExecuteWithBusyStateAsync(operationName, (ct, _) => action(ct), requiresInternet);

        private async Task ExecuteWithBusyStateAsync(string operationName, Func<CancellationToken, IProgress<ProgressInfo>, Task> action, bool requiresInternet = true)
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
            var token = _cts.Token;

            int session = _progressSession + 1;
            ResetProgress(session);
            var progress = new Progress<ProgressInfo>(info => ApplyProgress(session, info));
            try
            {
                await Task.Run(() => action(token, progress), CancellationToken.None);

                if (token.IsCancellationRequested)
                {
                    Log($"\n--- VORGANG ABGEBROCHEN: {operationName} ---");
                    StatusMessage = "Abgebrochen";
                    FinishProgress(session, $"Abgebrochen: {operationName}", "Der Vorgang wurde abgebrochen.", completed: false);
                }
                else
                {
                    StatusMessage = $"{operationName} erfolgreich abgeschlossen.";
                    FinishProgress(session, $"Abgeschlossen: {operationName}", "Alle Schritte wurden ausgeführt – Details im Protokoll.", completed: true);
                }
            }
            catch (OperationCanceledException)
            {
                Log($"\n--- VORGANG ABGEBROCHEN: {operationName} ---");
                StatusMessage = "Abgebrochen";
                FinishProgress(session, $"Abgebrochen: {operationName}", "Der Vorgang wurde abgebrochen.", completed: false);
            }
            catch (Exception ex)
            {
                Log($"\nKritischer Fehler bei {operationName}: {ex.Message}");
                StatusMessage = "Fehler aufgetreten";
                FinishProgress(session, $"Fehler: {operationName}", ex.Message, completed: false);
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
            await Task.WhenAll(RefreshDesktopAppsAsync(), RefreshStoreAppsAsync(force: true));
            if (ct.IsCancellationRequested) return;

            var (classicCount, uwpCount) = await InvokeOnUiAsync(
                () => (_installedSoftware.Count, _installedUwpApps.Count), (0, 0));
            Log($"Erfolgreich {classicCount} System-Programme und {uwpCount} Windows Apps geladen.");
        }

        private Task RefreshDesktopAppsAsync() => _desktopAppsRefresh.RunAsync(_lifetimeCts.Token);

        private Task RefreshStoreAppsAsync(bool force)
        {
            if (!force && _storeAppsLoaded && _uninstallerService.GetStoreAppsFingerprint() == _storeAppsFingerprint)
                return Task.CompletedTask;
            return _storeAppsRefresh.RunAsync(_lifetimeCts.Token);
        }

        private void RunBackgroundRefresh(Func<Task> refresh)
        {
            _ = Task.Run(async () =>
            {
                try { await refresh(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log($"[FEHLER] Programmliste konnte nicht aktualisiert werden: {ex.Message}"); }
            });
        }

        private async Task LoadDesktopAppsCoreAsync(CancellationToken ct)
        {
            var apps = await Task.Run(() => _uninstallerService.GetInstalledSoftware(), ct);
            ct.ThrowIfCancellationRequested();

            await InvokeOnUiAsync(() =>
            {
                ApplyAppList(_installedSoftware, apps);
            });
        }

        private async Task LoadStoreAppsCoreAsync(CancellationToken ct)
        {
            int fingerprint = _uninstallerService.GetStoreAppsFingerprint();
            var apps = await _uninstallerService.GetInstalledUwpAppsAsync(
                msg => { if (msg.Contains("[FEHLER]", StringComparison.Ordinal)) Log(msg); }, ct);
            ct.ThrowIfCancellationRequested();

            bool applied = await InvokeOnUiAsync(() =>
            {
                if (apps.Count == 0 && _installedUwpApps.Count > 0) return false;
                ApplyAppList(_installedUwpApps, apps);
                return true;
            }, false);

            if (!applied)
            {
                Log("[WARNUNG] Windows Apps konnten nicht gelesen werden – die Liste bleibt unverändert.");
                return;
            }

            _storeAppsFingerprint = fingerprint;
            _storeAppsLoaded = true;
        }

        /// <summary>
        /// Gleicht eine angezeigte Liste mit einem frischen Scan ab:
        /// Neue Programme erscheinen sofort in der DataGrid-Ansicht, entfernte verschwinden.
        /// </summary>
        private void ApplyAppList(ObservableCollection<SoftwareItem> target, IReadOnlyList<SoftwareItem> fresh)
        {
            bool isInitialLoad = target.Count == 0;

            var freshById = new Dictionary<string, SoftwareItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in fresh) freshById.TryAdd(app.Identity, app);

            var added = new List<string>();
            var removed = new List<string>();
            var changed = new List<string>();

            for (int i = target.Count - 1; i >= 0; i--)
            {
                var existing = target[i];
                if (!freshById.Remove(existing.Identity, out var current))
                {
                    removed.Add(existing.Name);
                    ReplaceOrRemoveAppAt(target, i, null);
                }
                else if (!existing.HasSameDisplayData(current))
                {
                    changed.Add(current.Name);
                    ReplaceOrRemoveAppAt(target, i, current);
                }
            }

            foreach (var app in fresh)
            {
                if (!freshById.Remove(app.Identity, out var toAdd)) continue;
                toAdd.PropertyChanged += OnSoftwareItemPropertyChanged;
                target.Add(toAdd);
                added.Add(toAdd.Name);
            }

            if (added.Count == 0 && removed.Count == 0 && changed.Count == 0) return;

            UpdateSelectedSoftwareStatus();
            if (!isInitialLoad) LogLiveChanges(added, removed, changed);
        }

        private void ReplaceOrRemoveAppAt(ObservableCollection<SoftwareItem> target, int index, SoftwareItem? replacement)
        {
            var existing = target[index];
            bool wasSelectedSoftware = ReferenceEquals(_selectedSoftware, existing);
            bool wasSelectedUwp = ReferenceEquals(_selectedUwpApp, existing);

            existing.PropertyChanged -= OnSoftwareItemPropertyChanged;
            if (replacement == null)
            {
                target.RemoveAt(index);
            }
            else
            {
                replacement.IsSelected = existing.IsSelected;
                replacement.PropertyChanged += OnSoftwareItemPropertyChanged;
                target[index] = replacement;
            }

            if (wasSelectedSoftware) SelectedSoftware = replacement;
            if (wasSelectedUwp) SelectedUwpApp = replacement;
        }

        private void LogLiveChanges(List<string> added, List<string> removed, List<string> changed)
        {
            var updated = added.Intersect(removed, StringComparer.OrdinalIgnoreCase).Concat(changed)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var name in added.Except(updated, StringComparer.OrdinalIgnoreCase).Take(MaxLiveLogEntries))
                Log($"[Live] Neu installiert erkannt: {name}");
            foreach (var name in removed.Except(updated, StringComparer.OrdinalIgnoreCase).Take(MaxLiveLogEntries))
                Log($"[Live] Deinstallation erkannt: {name}");
            foreach (var name in updated.Take(MaxLiveLogEntries))
                Log($"[Live] Aktualisierung erkannt: {name}");
        }

        private void OnSoftwareItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SoftwareItem.IsSelected)) return;

            if (_selectionBatchDepth > 0)
            {
                _selectionChangedInBatch = true;
                return;
            }

            UpdateSelectedSoftwareStatus();
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

        private static bool IsUwpScope(string? scope)
            => string.Equals(scope, "uwp", StringComparison.OrdinalIgnoreCase);

        private bool HasSelectedApps(string? scope)
        {
            if (scope == null)
            {
                return _installedSoftware.Any(a => a.IsSelected)
                    || _installedUwpApps.Any(a => a.IsSelected)
                    || SelectedSoftware != null
                    || SelectedUwpApp != null;
            }

            return IsUwpScope(scope)
                ? _installedUwpApps.Any(a => a.IsSelected) || SelectedUwpApp != null
                : _installedSoftware.Any(a => a.IsSelected) || SelectedSoftware != null;
        }

        private async Task UninstallSelectedAppsAsync(string? scope, CancellationToken ct)
        {
            bool uwpScope = IsUwpScope(scope);

            var allSelected = await InvokeOnUiAsync(() =>
            {
                IEnumerable<SoftwareItem> source = scope == null
                    ? _installedSoftware.Concat(_installedUwpApps)
                    : uwpScope ? _installedUwpApps : _installedSoftware;

                var list = source.Where(a => a.IsSelected).ToList();
                if (list.Count == 0)
                {
                    SoftwareItem? single = scope == null
                        ? SelectedSoftware ?? SelectedUwpApp
                        : uwpScope ? SelectedUwpApp : SelectedSoftware;
                    if (single != null) list.Add(single);
                }
                return list;
            }, new List<SoftwareItem>());

            if (allSelected.Count == 0)
            {
                await InvokeOnUiAsync(() => SelectedSoftwareStatusText = "Keine Auswahl");
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
                int currentIdx = i + 1;
                int total = allSelected.Count;

                await InvokeOnUiAsync(() =>
                {
                    SelectedSoftwareStatusText = $"[{currentIdx}/{total}] Deinstalliere '{app.Name}'...";
                    StatusMessage = $"Deinstalliere '{app.Name}'...";
                });

                Log($"[{currentIdx}/{total}] Bereite Deinstallation für '{app.Name}' vor...");

                bool removed = await _uninstallerService.UninstallSoftwareAsync(app, CleanLeftovers, Log, ct);

                if (ct.IsCancellationRequested) break;

                if (removed) successCount++;
                else failCount++;

                await InvokeOnUiAsync(() =>
                {
                    var target = app.IsUwp ? _installedUwpApps : _installedSoftware;
                    for (int idx = 0; idx < target.Count; idx++)
                    {
                        if (!target[idx].Identity.Equals(app.Identity, StringComparison.OrdinalIgnoreCase)) continue;
                        if (removed) ReplaceOrRemoveAppAt(target, idx, null);
                        else target[idx].IsSelected = false;
                        break;
                    }
                });
            }

            Log($"\n=== Deinstallations-Durchgang beendet: {successCount} erfolgreich, {failCount} fehlgeschlagen ===\n");

            await Task.WhenAll(RefreshDesktopAppsAsync(), RefreshStoreAppsAsync(force: false));

            await InvokeOnUiAsync(() =>
            {
                SelectedSoftware = null;
                SelectedUwpApp = null;
                UpdateSelectedSoftwareStatus();
                bool anyChecked = _installedSoftware.Any(a => a.IsSelected) || _installedUwpApps.Any(a => a.IsSelected);
                if (successCount > 0 && !anyChecked)
                    SelectedSoftwareStatusText = $"{successCount} Programm(e) erfolgreich entfernt.";
            });

            await RefreshStorageInfoAsync();
        }

        public async Task RefreshStorageInfoAsync()
        {
            try
            {
                var taskC = Task.Run(() => _storageService.GetDriveSpaceString("C"));
                var taskD = Task.Run(() => _storageService.GetDriveSpaceString("D"));
                await Task.WhenAll(taskC, taskD);
                await InvokeOnUiAsync(() => { DriveCSpace = taskC.Result; DriveDSpace = taskD.Result; });
            }
            catch { }
        }

        private void ParseWingetUpgradeLine(WingetUpgradeTableParser parser, string line)
        {
            if (!parser.TryParse(line, out var entry) || entry == null) return;

            InvokeOnUi(() =>
            {
                if (UpdatesCollection.Any(x => x.UpdateId.Equals(entry.Id, StringComparison.OrdinalIgnoreCase))) return;

                UpdatesCollection.Add(new UpdateItem
                {
                    Name = entry.Name,
                    UpdateId = entry.Id,
                    CurrentVersion = entry.CurrentVersion,
                    AvailableVersion = entry.AvailableVersion,
                    Type = "Software-Update",
                    Status = "Ausstehend",
                    Detail = "Bereit zum Installieren"
                });
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update");
            });
        }

        private void ParseWingetInstallLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            string lowerLine = line.ToLowerInvariant();
            string? status;
            string? detail;

            if (lowerLine.Contains("successfully") || lowerLine.Contains("erfolgreich") ||
                lowerLine.Contains("installed") || lowerLine.Contains("installiert"))
            {
                status = "Erfolgreich";
                detail = null;
            }
            else if (lowerLine.Contains("failed") || lowerLine.Contains("fehler") || lowerLine.Contains("fehlgeschlagen"))
            {
                status = "Fehlgeschlagen";
                detail = "Installation fehlgeschlagen";
            }
            else if (lowerLine.Contains("download") || lowerLine.Contains("herunterladen"))
            {
                status = "Herunterladen...";
                detail = "Download läuft...";
            }
            else if (lowerLine.Contains("hash") || lowerLine.Contains("überprüf"))
            {
                status = "Prüfe Integrität...";
                detail = "Hash wird geprüft...";
            }
            else
            {
                status = "Wird installiert...";
                detail = "Installationsroutine aktiv...";
            }

            InvokeOnUi(() =>
            {
                foreach (var item in UpdatesCollection)
                {
                    if (item.Type != "Software-Update") continue;
                    if (!IsMentioned(line, item.UpdateId) && !IsMentioned(line, item.Name)) continue;

                    item.Status = status;
                    item.Detail = detail ?? (string.IsNullOrEmpty(item.AvailableVersion)
                        ? "Aktualisiert"
                        : $"v{item.AvailableVersion} installiert");
                }
                UpdatesFoundCount = UpdatesCollection.Count(x => x.Type == "Software-Update" && x.Status != "Erfolgreich");
            });
        }

        private static bool IsMentioned(string line, string value)
            => !string.IsNullOrEmpty(value) && line.Contains(value, StringComparison.OrdinalIgnoreCase);

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

        private async Task CheckUpdatesWrapperAsync(CancellationToken ct, IProgress<ProgressInfo> progress)
        {
            await InvokeOnUiAsync(() =>
            {
                var toRemove = UpdatesCollection.Where(x => x.Type == "Software-Update").ToList();
                foreach (var item in toRemove) UpdatesCollection.Remove(item);
                UpdatesFoundCount = 0;
                CurrentUpdateActivity = "Lade Quellen & prüfe Updates...";
            });

            var parser = new WingetUpgradeTableParser();
            await _updateService.CheckUpdatesAsync(msg => { Log(msg); ParseWingetUpgradeLine(parser, msg); }, progress, ct);

            await InvokeOnUiAsync(() =>
            {
                CurrentUpdateActivity = $"Suche beendet. {UpdatesFoundCount} Update(s) gefunden.";
            });
        }

        private async Task InstallUpdatesWrapperAsync(CancellationToken ct, IProgress<ProgressInfo> progress)
        {
            var softwareUpdates = await InvokeOnUiAsync(
                () => UpdatesCollection.Where(x => x.Type == "Software-Update").ToList(),
                new List<UpdateItem>());

            if (softwareUpdates.Count > 0)
            {
                Log($"\n--- STARTE GEZIELTE INSTALLATION VON {softwareUpdates.Count} SOFTWARE-UPDATES ---");

                int successCount = 0;
                int failCount = 0;
                var steps = new StepProgress(progress, Enumerable.Repeat(1.0, softwareUpdates.Count).ToArray());

                for (int i = 0; i < softwareUpdates.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var item = softwareUpdates[i];
                    int currentIndex = i + 1;
                    int totalCount = softwareUpdates.Count;
                    steps.Begin(i, item.Name, "Wird vorbereitet...");

                    await InvokeOnUiAsync(() =>
                    {
                        item.Status = "Herunterladen...";
                        item.Detail = "Wird vorbereitet...";
                        CurrentUpdateActivity = $"[{currentIndex}/{totalCount}] {item.Name}: Wird vorbereitet...";
                    });

                    Log($"\n[{currentIndex}/{totalCount}] Starte Update für '{item.Name}' (ID: {item.UpdateId})...");

                    Action<string> onProgressLog = line =>
                    {
                        if (ConsoleProgressParser.TryParse(line, out double? fraction, out string? barDetail))
                        {
                            string detail = barDetail ?? (fraction.HasValue ? $"Download: {fraction.Value:P0}" : "Download läuft...");
                            steps.Report((fraction ?? 0) * 0.7, detail);
                            InvokeOnUi(() =>
                            {
                                item.Status = "Herunterladen...";
                                item.Detail = detail;
                                CurrentUpdateActivity = $"[{currentIndex}/{totalCount}] {item.Name}: {detail}";
                            });
                            return;
                        }

                        Log(line);
                        if (string.IsNullOrWhiteSpace(line)) return;
                        string lower = line.ToLowerInvariant();
                        if (lower.Contains("install") || lower.Contains("paket") || lower.Contains("start"))
                            steps.Report(0.8, "Wird installiert...");
                        InvokeOnUi(() =>
                        {
                            if (lower.Contains("download") || lower.Contains("herunterladen") || lower.Contains(" mb ") || lower.Contains('%'))
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
                    steps.Report(1, "Fertig");

                    bool isSuccess = exitCode == 0 || exitCode == 1641 || exitCode == 3010;
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
                await _updateService.InstallUpdatesAsync(msg =>
                {
                    if (ConsoleProgressParser.TryParse(msg, out _, out _)) return;
                    Log(msg);
                    ParseWingetInstallLine(msg);
                }, progress, ct);
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

        private async Task CheckDriversWrapperAsync(CancellationToken ct, IProgress<ProgressInfo> progress)
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
            }, progress, ct);

            await InvokeOnUiAsync(() =>
            {
                CurrentUpdateActivity = $"Treibersuche beendet. {DriversFoundCount} Treiber-Update(s) gefunden.";
            });
        }

        private async Task InstallDriversWrapperAsync(CancellationToken ct, IProgress<ProgressInfo> progress)
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
                ApplyDriverInstallResult(msg);
            }, progress, ct);

            await InvokeOnUiAsync(() =>
            {
                foreach (var item in UpdatesCollection.Where(x => x.Type == "Treiber-Update" && x.Status == "Wird installiert..."))
                {
                    item.Status = "Ausstehend";
                    item.Detail = "Kein Installationsergebnis";
                }
                DriversFoundCount = UpdatesCollection.Count(x => x.Type == "Treiber-Update" && x.Status != "Erfolgreich");

                int ok = UpdatesCollection.Count(x => x.Type == "Treiber-Update" && x.Status == "Erfolgreich");
                int failed = UpdatesCollection.Count(x => x.Type == "Treiber-Update" && x.Status == "Fehlgeschlagen");
                CurrentUpdateActivity = $"Treiber-Installation abgeschlossen ({ok} erfolgreich, {failed} fehlgeschlagen).";
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

        private void ApplyDriverInstallResult(string line)
        {
            const string successPrefix = "[ERFOLG] Treiber installiert: ";
            const string failPrefix = "[FEHLER] Treiber nicht installiert (";

            string text = line.Trim();
            string title;
            bool success;
            string detail;

            if (text.StartsWith(successPrefix, StringComparison.Ordinal))
            {
                title = text.Substring(successPrefix.Length).Trim();
                success = true;
                detail = "Treiber aktualisiert";
            }
            else if (text.StartsWith(failPrefix, StringComparison.Ordinal))
            {
                int end = text.IndexOf("): ", failPrefix.Length, StringComparison.Ordinal);
                if (end < 0) return;
                title = text.Substring(end + 3).Trim();
                success = false;
                detail = text.Substring(failPrefix.Length, end - failPrefix.Length);
            }
            else
            {
                return;
            }

            InvokeOnUi(() =>
            {
                var item = UpdatesCollection.FirstOrDefault(x =>
                    x.Type == "Treiber-Update" && x.Name.Equals(title, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    item = new UpdateItem
                    {
                        Name = title,
                        UpdateId = title,
                        Type = "Treiber-Update",
                        CurrentVersion = "Installiert",
                        AvailableVersion = "WHQL-Update"
                    };
                    UpdatesCollection.Add(item);
                }
                item.Status = success ? "Erfolgreich" : "Fehlgeschlagen";
                item.Detail = detail;
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
        public ICommand RefreshAppsCommand { get; }
        public ICommand UninstallCommand { get; }
        public ICommand BoostRamCommand { get; }
        public ICommand ToggleStartupCommand { get; }
        public ICommand CancelCommand { get; }

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class SoftwareItemComparer : IComparer
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

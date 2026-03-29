using System;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SystemOptimierer.Core;
using SystemOptimierer.Services;

namespace SystemOptimierer.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IOptimizerService _optimizerService;
        private CancellationTokenSource _cts;

        public MainViewModel(IOptimizerService optimizerService)
        {
            _optimizerService = optimizerService;
            
            // Initialisiere Commands
            CheckUpdatesCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates suchen", ct => _optimizerService.CheckUpdatesAsync(Log, ct)), _ => !IsBusy);
            InstallUpdatesCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Updates installieren", ct => _optimizerService.InstallUpdatesAsync(Log, ct)), _ => !IsBusy);
            RepairSystemCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("System reparieren", ct => _optimizerService.RepairSystemAsync(Log, ct)), _ => !IsBusy);
            OptimizeNetworkCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Netzwerk optimieren", ct => _optimizerService.OptimizeNetworkAsync(Log, ct), false), _ => !IsBusy);
            CleanupSystemCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("System bereinigen", ct => _optimizerService.CleanupSystemAsync(Log, ct), false), _ => !IsBusy);
            CheckDriversCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber suchen", ct => _optimizerService.CheckDriversAsync(Log, ct)), _ => !IsBusy);
            InstallDriversCommand = new RelayCommand(async _ => await ExecuteWithBusyStateAsync("Treiber installieren", ct => _optimizerService.InstallDriversAsync(Log, ct)), _ => !IsBusy);
            CancelCommand = new RelayCommand(_ => CancelOperation(), _ => IsBusy);
            RestartCommand = new RelayCommand(_ => RestartPC(), _ => !IsBusy);

            Log("Programm gestartet. Bereit für Befehle...\n");
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

        #endregion

        #region Commands

        public ICommand CheckUpdatesCommand { get; }
        public ICommand InstallUpdatesCommand { get; }
        public ICommand RepairSystemCommand { get; }
        public ICommand OptimizeNetworkCommand { get; }
        public ICommand CleanupSystemCommand { get; }
        public ICommand CheckDriversCommand { get; }
        public ICommand InstallDriversCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand RestartCommand { get; }

        #endregion

        #region Methods

        private void Log(string message)
        {
            // Füge neue Nachricht hinzu und halte Log nicht unendlich lang
            Application.Current.Dispatcher.Invoke(() =>
            {
                LogText += $"{message}\n";
            });
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
                await action(_cts.Token);
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

        private void CancelOperation()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                Log("\nAbbruch wird eingeleitet...");
                _cts.Cancel();
            }
        }

        private void RestartPC()
        {
            MessageBoxResult result = MessageBox.Show(
                "Möchtest du den PC jetzt wirklich neu starten? Bitte speichere vorher alle offenen Dateien.", 
                "Neustart bestätigen", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                Log("\n--- PC WIRD NEU GESTARTET ---");
                System.Diagnostics.Process.Start("shutdown", "/r /t 0");
            }
            else
            {
                Log("Neustart abgebrochen.\n");
            }
        }

        #endregion

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

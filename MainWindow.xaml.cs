using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SystemOptimierer.Services;
using SystemOptimierer.ViewModels;

namespace SystemOptimierer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.Closing += Window_Closing;

            // Dependency Injection (Manuelles Wiring für dieses Projekt)
            var commandService = new CommandExecutionService();
            var powerShellService = new PowerShellService(commandService);
            var updateService = new UpdateService(commandService);
            var driverService = new DriverService(powerShellService);
            var repairService = new RepairService(commandService);
            var cleanupService = new CleanupService(commandService, powerShellService);
            var uninstallerService = new UninstallerService(commandService, powerShellService);
            var storageService = new StorageService();
            var performanceService = new PerformanceService();
            DataContext = new MainViewModel(updateService, driverService, repairService, cleanupService, uninstallerService, storageService, performanceService);
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.RequestShutdown();
            }
        }

        // Title Bar Drag Logic
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                this.MaxHeight = double.PositiveInfinity;
                this.MaxWidth = double.PositiveInfinity;
            }
            else
            {
                this.MaxHeight = SystemParameters.WorkArea.Height;
                this.MaxWidth = SystemParameters.WorkArea.Width;
                this.WindowState = WindowState.Maximized;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TxtLog_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtLog.ScrollToEnd();

            string log = TxtLog.Text ?? string.Empty;
            int totalLines = log.Split('\n').Length;
            int successCount = CountOccurrences(log, "[ERFOLG]") + CountOccurrences(log, "✓");
            int warningCount = CountOccurrences(log, "[WARNUNG]") + CountOccurrences(log, "⚠");
            int errorCount = CountOccurrences(log, "[FEHLER]") + CountOccurrences(log, "FEHLER:") + CountOccurrences(log, "✕");

            LogEntryCount.Text = totalLines.ToString();
            LogSuccessCount.Text = successCount.ToString();
            LogWarningCount.Text = warningCount.ToString();
            LogErrorCount.Text = errorCount.ToString();
        }

        private static int CountOccurrences(string source, string pattern)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(pattern)) return 0;
            int count = 0, index = 0;
            while ((index = source.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                count++;
                index += pattern.Length;
            }
            return count;
        }

        private void SelectAllSoftware_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not CheckBox cb) return;

            bool shouldSelect = cb.IsChecked == true;
            foreach (var app in vm.InstalledSoftware.Where(a => vm.SoftwareView.Contains(a)))
            {
                app.IsSelected = shouldSelect;
            }
        }

        private void SelectAllUwp_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not CheckBox cb) return;

            bool shouldSelect = cb.IsChecked == true;
            foreach (var app in vm.InstalledUwpApps.Where(a => vm.UwpView.Contains(a)))
            {
                app.IsSelected = shouldSelect;
            }
        }
    }
}

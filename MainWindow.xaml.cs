using System;
using System.Windows;
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

            // Dependency Injection (Manuelles Wiring für dieses Projekt)
            var commandService    = new CommandExecutionService();
            var powerShellService = new PowerShellService(commandService);

            var updateService      = new UpdateService(commandService);
            var driverService      = new DriverService(powerShellService);
            var repairService      = new RepairService(commandService);
            var cleanupService     = new CleanupService(commandService, powerShellService);
            var uninstallerService = new UninstallerService(commandService, powerShellService);
            var storageService     = new StorageService();
            var performanceService = new PerformanceService();
            DataContext = new MainViewModel(updateService, driverService, repairService, cleanupService, uninstallerService, storageService, performanceService);
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

        // Auto-Scroll Feature für die Konsolen-TextBox
        private void TxtLog_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            TxtLog.ScrollToEnd();
        }
    }
}

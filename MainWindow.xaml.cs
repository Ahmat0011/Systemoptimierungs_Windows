using System.Windows;
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

            var updateService  = new UpdateService(commandService);
            var driverService  = new DriverService(powerShellService);
            var repairService  = new RepairService(commandService);
            var cleanupService = new CleanupService(commandService, powerShellService);

            DataContext = new MainViewModel(updateService, driverService, repairService, cleanupService);
        }

        // Auto-Scroll Feature für die Konsolen-TextBox
        private void TxtLog_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            TxtLog.ScrollToEnd();
        }
    }
}

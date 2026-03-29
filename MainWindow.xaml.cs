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
            
            // Dependency Injection (Manuelles Wiringup für dieses kleine Projekt)
            var commandService = new CommandExecutionService();
            var powerShellService = new PowerShellService(commandService);
            var optimizerService = new OptimizerService(commandService, powerShellService);
            
            DataContext = new MainViewModel(optimizerService);
        }
        
        // Auto-Scroll Feature für die TextBox beibehalten
        private void TxtLog_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            TxtLog.ScrollToEnd();
        }
    }
}
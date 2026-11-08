using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SystemOptimierer.Services;
using SystemOptimierer.ViewModels;

namespace SystemOptimierer
{
    public partial class MainWindow : Window
    {
        private const uint MonitorDefaultToNearest = 0x00000002;

        // Zwischenstand der Log-Statistik: TextChanged feuert bei jeder Sammelaktualisierung des
        // Protokolls. Statt den kompletten Text (bis 60.000 Zeichen) jedes Mal erneut zu durchsuchen,
        // wird nur der seit der letzten Auswertung hinzugekommene Teil gezählt.
        private int _logScanOffset;
        private int _lastPrefixHash;
        private int _logEntryCount;
        private int _logSuccessCount;
        private int _logWarningCount;
        private int _logErrorCount;
        private bool _isClosing;

        public MainWindow()
        {
            InitializeComponent();
            Closing += Window_Closing;
            Closed += Window_Closed;
            Activated += Window_Activated;
            StateChanged += Window_StateChanged;

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
            var appsWatcher = new InstalledAppsWatcher();
            DataContext = new MainViewModel(
                updateService,
                driverService,
                repairService,
                cleanupService,
                uninstallerService,
                storageService,
                performanceService,
                appsWatcher);
        }

        private void Window_Activated(object? sender, EventArgs e)
        {
            if (_isClosing) return;
            if (DataContext is MainViewModel vm)
            {
                vm.OnWindowActivated();
            }
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                ApplyCurrentMonitorMaxBounds();
                BtnMaximize.Content = "\uE923"; // Restore-Symbol
            }
            else if (WindowState == WindowState.Normal)
            {
                MaxHeight = double.PositiveInfinity;
                MaxWidth = double.PositiveInfinity;
                BtnMaximize.Content = "\uE922"; // Maximize-Symbol
            }
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            if (DataContext is MainViewModel vm)
            {
                vm.RequestShutdown();
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        // Title Bar Drag Logic
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            // Doppelklick auf die Titelleiste: maximieren bzw. wiederherstellen (Standardverhalten).
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (WindowState == WindowState.Maximized) return;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Maus wurde zwischen MouseDown und DragMove losgelassen – kein Grund zum Absturz.
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                ApplyCurrentMonitorMaxBounds();
                WindowState = WindowState.Maximized;
            }
        }

        /// <summary>
        /// Begrenzt die Maximalgröße auf den Arbeitsbereich des Monitors, auf dem das Fenster gerade steht.
        /// </summary>
        private void ApplyCurrentMonitorMaxBounds()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            IntPtr monitor = handle != IntPtr.Zero ? MonitorFromWindow(handle, MonitorDefaultToNearest) : IntPtr.Zero;

            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return;

            // Arbeitsbereich liegt in Gerätepixeln vor; MaxWidth/MaxHeight erwartet DIP.
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;

            MaxWidth = (info.rcWork.Right - info.rcWork.Left) / scaleX;
            MaxHeight = (info.rcWork.Bottom - info.rcWork.Top) / scaleY;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TxtLog_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtLog.ScrollToEnd();
            UpdateLogStatistics(TxtLog.Text ?? string.Empty);
        }

        private void UpdateLogStatistics(string log)
        {
            int currentPrefixHash = GetSampleHash(log, _logScanOffset);

            // Wenn der Text gekürzt oder zurückgesetzt wurde, einmal komplett neu auswerten.
            if (log.Length < _logScanOffset || currentPrefixHash != _lastPrefixHash)
            {
                _logScanOffset = 0;
                _logEntryCount = 0;
                _logSuccessCount = 0;
                _logWarningCount = 0;
                _logErrorCount = 0;
            }

            if (log.Length > _logScanOffset)
            {
                ReadOnlySpan<char> delta = log.AsSpan(_logScanOffset);

                int newLines = 0;
                foreach (char c in delta)
                {
                    if (c == '\n') newLines++;
                }

                _logEntryCount += newLines;
                _logSuccessCount += CountOccurrences(delta, "[ERFOLG]") + CountOccurrences(delta, "✓");
                _logWarningCount += CountOccurrences(delta, "[WARNUNG]") + CountOccurrences(delta, "⚠");
                _logErrorCount += CountOccurrences(delta, "[FEHLER]") + CountOccurrences(delta, "FEHLER:") + CountOccurrences(delta, "✕");

                _logScanOffset = log.Length;
                _lastPrefixHash = GetSampleHash(log, _logScanOffset);
            }

            LogEntryCount.Text = _logEntryCount.ToString(CultureInfo.InvariantCulture);
            LogSuccessCount.Text = _logSuccessCount.ToString(CultureInfo.InvariantCulture);
            LogWarningCount.Text = _logWarningCount.ToString(CultureInfo.InvariantCulture);
            LogErrorCount.Text = _logErrorCount.ToString(CultureInfo.InvariantCulture);
        }

        private static int GetSampleHash(string text, int upToLength)
        {
            if (upToLength <= 0 || upToLength > text.Length) return 0;
            int sampleLength = Math.Min(64, upToLength);
            return string.GetHashCode(text.AsSpan(upToLength - sampleLength, sampleLength), StringComparison.Ordinal);
        }

        private static int CountOccurrences(ReadOnlySpan<char> source, string pattern)
        {
            if (source.IsEmpty || string.IsNullOrEmpty(pattern)) return 0;

            int count = 0, index = 0;
            while (index <= source.Length - pattern.Length)
            {
                int found = source[index..].IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                count++;
                index += found + pattern.Length;
            }
            return count;
        }

        private void SelectAllSoftware_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not CheckBox cb) return;

            // Sammelauswahl im ViewModel: aktualisiert die Statusanzeige nur einmal statt pro Zeile.
            vm.SetSelectionForVisibleSoftware(cb.IsChecked == true);
        }

        private void SelectAllUwp_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not CheckBox cb) return;

            vm.SetSelectionForVisibleUwp(cb.IsChecked == true);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public Rect rcMonitor;
            public Rect rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
    }
}

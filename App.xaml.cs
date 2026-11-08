using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SystemOptimierer;

/// <summary>
/// Interaction logic for App.xaml.
/// Registriert globale Exception-Handler und verhindert Mehrfachstarts per Mutex.
/// </summary>
public partial class App : Application
{
    private const long MaxCrashLogBytes = 512 * 1024;
    private static readonly object LogLock = new();
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\SystemOptimierer_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "SystemOptimierer wird bereits ausgeführt.",
                "SystemOptimierer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch
        {
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI-Thread", e.Exception);
        MessageBox.Show(
            $"Ein unerwarteter Fehler ist aufgetreten:\n\n{e.Exception.Message}\n\n" +
            "Die Anwendung läuft weiter. Details wurden in crash.log gespeichert.",
            "SystemOptimierer – Fehler",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Hintergrund-Task", e.Exception);
        e.SetObserved();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain", e.ExceptionObject as Exception);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            lock (LogLock)
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SystemOptimierer");
                Directory.CreateDirectory(logDir);

                string logPath = Path.Combine(logDir, "crash.log");
                RotateIfTooLarge(logPath);

                File.AppendAllText(
                    logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] " +
                    $"(Version {typeof(App).Assembly.GetName().Version}, {Environment.OSVersion})" +
                    Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private static void RotateIfTooLarge(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length <= MaxCrashLogBytes) return;

            string content = File.ReadAllText(logPath);
            int keepFrom = Math.Max(0, content.Length - (int)(MaxCrashLogBytes / 2));
            int lineStart = content.IndexOf('\n', keepFrom);
            string tail = lineStart >= 0 && lineStart + 1 < content.Length
                ? content[(lineStart + 1)..]
                : content[keepFrom..];

            File.WriteAllText(
                logPath,
                "[... ältere Einträge wurden gekürzt ...]" + Environment.NewLine + tail);
        }
        catch
        {
        }
    }
}

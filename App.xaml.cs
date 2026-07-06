using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SystemOptimierer;

/// <summary>
/// Interaction logic for App.xaml.
/// Registriert globale Exception-Handler, damit unbehandelte Fehler die App
/// nicht mehr sofort beenden, sondern geloggt und (wo möglich) abgefangen werden.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Fehler auf dem UI-Thread (Dispatcher)
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Fehler in nicht awaiteten / vergessenen Tasks
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Letzte Instanz: Fehler auf beliebigen Threads (nicht abfangbar, nur loggen)
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
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
        e.Handled = true; // App absturzsicher weiterlaufen lassen
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Hintergrund-Task", e.Exception);
        e.SetObserved(); // verhindert Prozessabbruch durch unbeobachtete Task-Fehler
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain", e.ExceptionObject as Exception);
    }

    /// <summary>
    /// Schreibt Absturzdetails nach %LocalAppData%\SystemOptimierer\crash.log.
    /// Darf selbst niemals werfen.
    /// </summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemOptimierer");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging darf nie einen weiteren Fehler auslösen
        }
    }
}

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class CommandExecutionService : ICommandExecutionService
    {
        private const int ProcessTerminationTimeoutMs = 5000;

        public async Task RunCommandAsync(
            string command,
            string arguments,
            Action<string> onOutput,
            Action<string> onError,
            CancellationToken cancellationToken)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process())
                {
                    process.StartInfo = processInfo;

                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null) onOutput?.Invoke(args.Data);
                    };
                    
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null) onError?.Invoke(args.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    try
                    {
                        await process.WaitForExitAsync(cancellationToken);

                        // Wait synchronously so that all remaining OutputDataReceived / ErrorDataReceived
                        // events are fully processed before we leave the using block.
                        process.WaitForExit();
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            try { process.Kill(entireProcessTree: true); } catch { }
                            // Give the OS a moment to terminate the process and drain the streams
                            // before CancelOutputRead is called in the finally block.
                            process.WaitForExit(ProcessTerminationTimeoutMs);
                        }
                        onError?.Invoke("Vorgang wurde abgebrochen.");
                    }
                    finally
                    {
                        // Stop the async read pumps.  Must be called before Dispose.
                        // Do NOT call StandardOutput.Close() / StandardError.Close() here –
                        // touching those streams after BeginOutputReadLine() can throw
                        // InvalidOperationException and is redundant (Dispose handles it).
                        try { process.CancelOutputRead(); } catch { }
                        try { process.CancelErrorRead(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Kritischer Fehler beim Ausführen: {ex.Message}");
            }
        }
    }
}

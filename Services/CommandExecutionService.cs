using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class CommandExecutionService : ICommandExecutionService
    {
        public async Task RunCommandAsync(string command, string arguments, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
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

                    // Wait for the process to exit or the cancellation token to be triggered
                    try
                    {
                        await process.WaitForExitAsync(cancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                        // Cleanly kill the process tree if cancellation was requested
                        if (!process.HasExited)
                        {
                            process.Kill(true); // .NET core and later supports killing process tree
                            onError?.Invoke("Vorgang wurde abgebrochen.");
                        }
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

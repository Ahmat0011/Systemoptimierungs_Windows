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

                    try
                    {
                        await process.WaitForExitAsync(cancellationToken);
                        
                        // Mandatory: Wait synchronously for remaining event handlers to finish processing the redirected streams
                        process.WaitForExit();
                    }
                    catch (TaskCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                            onError?.Invoke("Vorgang wurde abgebrochen.");
                        }
                    }
                    finally
                    {
                        // Clean up process streams
                        try { process.CancelOutputRead(); } catch {}
                        try { process.CancelErrorRead(); } catch {}
                        
                        try { process.StandardOutput.Close(); } catch {}
                        try { process.StandardError.Close(); } catch {}
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

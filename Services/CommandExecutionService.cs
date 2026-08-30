using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class CommandExecutionService : ICommandExecutionService
    {
        private const int ProcessTerminationTimeoutMs = 5000;

        public async Task<int> RunCommandAsync(
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
                        process.WaitForExit(ProcessTerminationTimeoutMs);
                        return process.ExitCode;
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            try { process.Kill(entireProcessTree: true); } catch { }
                            process.WaitForExit(ProcessTerminationTimeoutMs);
                        }
                        onError?.Invoke("Vorgang wurde abgebrochen.");
                        return -1;
                    }
                    finally
                    {
                        try { process.CancelOutputRead(); } catch { }
                        try { process.CancelErrorRead(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Kritischer Fehler beim Ausführen: {ex.Message}");
                return -1;
            }
        }
    }
}

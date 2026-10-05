using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemOptimierer.Core;

namespace SystemOptimierer.Services
{
    public class CommandExecutionService : ICommandExecutionService
    {
        private const int ProcessTerminationTimeoutMs = 5000;
        private const int ProcessTreePollIntervalMs = 500;
        private static readonly TimeSpan ProcessTreeGracePeriod = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Konsolenprogramme (cmd, DISM, ipconfig, net ...) schreiben umgeleitete Ausgaben in der
        /// OEM-Codepage (Deutsch: 850). Ohne passende Dekodierung werden Umlaute im Log zerstört.
        /// </summary>
        private static readonly Lazy<Encoding> OemEncoding = new(() =>
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(GetOEMCP());
            }
            catch
            {
                return Encoding.UTF8;
            }
        });

        [DllImport("kernel32.dll")]
        private static extern int GetOEMCP();

        public async Task<int> RunCommandAsync(
            string command,
            string arguments,
            Action<string> onOutput,
            Action<string> onError,
            CancellationToken cancellationToken,
            Encoding? outputEncoding = null)
        {
            try
            {
                var encoding = outputEncoding ?? OemEncoding.Value;
                var processInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = encoding,
                    StandardErrorEncoding = encoding,
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

        public async Task<int> RunCommandAndWaitForProcessTreeAsync(
            string command,
            string arguments,
            Action<string> onOutput,
            Action<string> onError,
            Func<bool>? isFinished,
            CancellationToken cancellationToken)
        {
            try
            {
                // Keine Umleitung von stdout/stderr: Kindprozesse würden sonst die Pipes erben und
                // das Warten auf den Hauptprozess unkontrolliert verlängern. GUI-Dialoge des
                // Deinstallers werden unabhängig von CreateNoWindow angezeigt.
                var processInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = processInfo };
                process.Start();
                using var job = ProcessJob.TryAttach(process);

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                    int exitCode = process.ExitCode;

                    if (job != null && job.ActiveProcessCount > 0)
                    {
                        // Kurzlebige Hilfsprozesse (z. B. conhost) enden meist sofort – erst danach melden.
                        await Task.Delay(ProcessTreePollIntervalMs * 2, cancellationToken);
                        if (job.ActiveProcessCount > 0)
                        {
                            onOutput?.Invoke("[Status] Warte auf Abschluss der vom Deinstaller gestarteten Prozesse...");
                            await WaitForJobAsync(job, isFinished, cancellationToken);
                        }
                    }
                    return exitCode;
                }
                catch (OperationCanceledException)
                {
                    job?.Terminate();
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        process.WaitForExit(ProcessTerminationTimeoutMs);
                    }
                    onError?.Invoke("Vorgang wurde abgebrochen.");
                    return -1;
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Kritischer Fehler beim Ausführen: {ex.Message}");
                return -1;
            }
        }

        private static async Task WaitForJobAsync(ProcessJob job, Func<bool>? isFinished, CancellationToken cancellationToken)
        {
            DateTime? finishedSince = null;
            while (job.ActiveProcessCount > 0)
            {
                if (isFinished != null && SafeInvoke(isFinished))
                {
                    finishedSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - finishedSince.Value >= ProcessTreeGracePeriod) return;
                }
                await Task.Delay(ProcessTreePollIntervalMs, cancellationToken);
            }
        }

        private static bool SafeInvoke(Func<bool> predicate)
        {
            try { return predicate(); } catch { return false; }
        }
    }
}

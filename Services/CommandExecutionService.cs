using System;
using System.Diagnostics;
using System.IO;
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

        public Task<int> RunCommandAsync(
            string command,
            string arguments,
            Action<string> onOutput,
            Action<string> onError,
            CancellationToken cancellationToken,
            Encoding? outputEncoding = null)
        {
            return ExecuteCoreAsync(
                command,
                arguments,
                standardInput: null,
                onOutput,
                onError,
                outputEncoding,
                cancellationToken);
        }

        public Task<int> RunCommandWithInputAsync(
            string command,
            string arguments,
            string standardInput,
            Action<string> onOutput,
            Action<string> onError,
            CancellationToken cancellationToken,
            Encoding? outputEncoding = null)
        {
            return ExecuteCoreAsync(
                command,
                arguments,
                standardInput,
                onOutput,
                onError,
                outputEncoding,
                cancellationToken);
        }

        private static async Task<int> ExecuteCoreAsync(
            string command,
            string arguments,
            string? standardInput,
            Action<string> onOutput,
            Action<string> onError,
            Encoding? outputEncoding,
            CancellationToken cancellationToken)
        {
            try
            {
                var encoding = outputEncoding ?? OemEncoding.Value;
                bool hasInput = standardInput != null;

                var processInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardInput = hasInput,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = encoding,
                    StandardErrorEncoding = encoding,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (hasInput)
                {
                    processInfo.StandardInputEncoding = new UTF8Encoding(false);
                }

                using var process = new Process { StartInfo = processInfo };

                var outputCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var errorCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data is null)
                    {
                        outputCompleted.TrySetResult(true);
                    }
                    else
                    {
                        try { onOutput?.Invoke(args.Data); } catch { }
                    }
                };

                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data is null)
                    {
                        errorCompleted.TrySetResult(true);
                    }
                    else
                    {
                        try { onError?.Invoke(args.Data); } catch { }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!string.IsNullOrEmpty(standardInput))
                {
                    try
                    {
                        await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                        process.StandardInput.Close();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        // Prozess hat sich bereits beendet, bevor der gesamte Input geschrieben wurde.
                    }
                }
                else if (hasInput)
                {
                    try { process.StandardInput.Close(); } catch { }
                }

                try
                {
                    await process.WaitForExitAsync(cancellationToken);

                    // Warten, bis auch die letzten asynchronen Ausgabezeilen (EOF) aus den Pipes gelesen wurden.
                    await Task.WhenAny(
                        Task.WhenAll(outputCompleted.Task, errorCompleted.Task),
                        Task.Delay(ProcessTerminationTimeoutMs, CancellationToken.None));

                    return process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        try { process.WaitForExit(ProcessTerminationTimeoutMs); } catch { }
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
                        try { process.WaitForExit(ProcessTerminationTimeoutMs); } catch { }
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
                else
                {
                    finishedSince = null;
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

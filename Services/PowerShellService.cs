using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SystemOptimierer.Services
{
    public class PowerShellService : IPowerShellService
    {
        private readonly ICommandExecutionService _commandService;

        public PowerShellService(ICommandExecutionService commandService)
        {
            _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        }

        public async Task RunScriptAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(script)) return;

            try
            {
                // In-Memory Scriptausführung via EncodedCommand:
                // Keine temporären .ps1 Dateien im Temp-Verzeichnis (verhindert Heuristik-Alarme von Antivirus bei Skript-Droppern)
                // und zuverlässige Ausführung ohne stdin-Piping-Verzögerungen oder EOF-Probleme.
                string optimizedScript =
                    "$ProgressPreference = 'SilentlyContinue'\r\n" +
                    "try { " +
                    "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                    "[Console]::InputEncoding = [System.Text.Encoding]::UTF8; " +
                    "$OutputEncoding = [System.Text.Encoding]::UTF8 " +
                    "} catch { }\r\n" +
                    script.Trim() + "\r\n";

                string base64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(optimizedScript));
                string arguments = $"-NoProfile -NoLogo -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {base64}";

                await _commandService.RunCommandAsync("powershell.exe", arguments, onOutput, onError, cancellationToken, Encoding.UTF8);
            }
            catch (OperationCanceledException)
            {
                // Ein Abbruch ist kein Fehler und soll nicht als "kritischer Fehler" im Log stehen.
                onError?.Invoke("Vorgang wurde abgebrochen.");
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Kritischer Fehler beim Ausführen des PowerShell-Skripts: {ex.Message}");
            }
        }
    }
}

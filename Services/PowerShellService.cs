using System;
using System.IO;
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
            _commandService = commandService;
        }

        public async Task RunScriptAsync(string script, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
        {
            string tempScriptPath = string.Empty;
            try
            {
                tempScriptPath = Path.Combine(Path.GetTempPath(), $"script_{Guid.NewGuid()}.ps1");

                // OPT 1: UTF-8 MIT BOM – Windows PowerShell 5.1 liest ohne BOM als ANSI (Umlaute-Kaputt)
                // OPT 2: ProgressPreference voranstellen – macht PS-Skripte deutlich schneller
                // OPT 4: Ausgabe als UTF-8 erzwingen – sonst OEM-Codepage (Zeichen wie ™ oder Emojis gehen verloren)
                string optimizedScript =
                    "$ProgressPreference = 'SilentlyContinue'\n" +
                    "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }\n" +
                    script;
                await File.WriteAllTextAsync(tempScriptPath, optimizedScript, new UTF8Encoding(true), cancellationToken);

                // OPT 3: -NonInteractive verhindert, dass PS auf User-Eingabe wartet (unsichtbarer Hänger)
                string arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScriptPath}\"";
                await _commandService.RunCommandAsync("powershell.exe", arguments, onOutput, onError, cancellationToken,
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Kritischer Fehler beim Ausführen des PowerShell-Skripts: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempScriptPath) && File.Exists(tempScriptPath))
                {
                    try { File.Delete(tempScriptPath); } catch { }
                }
            }
        }
    }
}

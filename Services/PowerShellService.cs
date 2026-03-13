using System;
using System.IO;
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
                // Skript temporär speichern
                tempScriptPath = Path.Combine(Path.GetTempPath(), $"script_{Guid.NewGuid()}.ps1");
                await File.WriteAllTextAsync(tempScriptPath, script, cancellationToken);

                // Skript sicher über den Command Service ausführen
                string arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScriptPath}\"";
                await _commandService.RunCommandAsync("powershell.exe", arguments, onOutput, onError, cancellationToken);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Kritischer Fehler beim Ausführen des PowerShell-Skripts: {ex.Message}");
            }
            finally
            {
                // Aufräumen: Temporäres Skript löschen, falls es noch existiert
                if (!string.IsNullOrEmpty(tempScriptPath) && File.Exists(tempScriptPath))
                {
                    try
                    {
                        File.Delete(tempScriptPath);
                    }
                    catch
                    {
                        // Ignore deletion errors in finally block
                    }
                }
            }
        }
    }
}

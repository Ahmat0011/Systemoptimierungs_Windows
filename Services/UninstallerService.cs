using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public class UninstallerService : IUninstallerService
    {
        private readonly ICommandExecutionService _cmd;
        private readonly IPowerShellService _ps;

        private static readonly string[] ProtectedKeywords = {
            "microsoft", "windows", "intel", "amd", "nvidia", "system",
            "drivers", "framework", "runtime", "dotnet", "visual studio",
            "directx", "office", "defender", "update"
        };

        public UninstallerService(ICommandExecutionService commandService, IPowerShellService powerShellService)
        {
            _cmd = commandService;
            _ps = powerShellService;
        }

        public List<SoftwareItem> GetInstalledSoftware()
        {
            var apps = new List<SoftwareItem>();

            ScanRegistryKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
            ScanRegistryKey(Registry.LocalMachine, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", apps);
            ScanRegistryKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);

            return apps
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        public async Task<List<SoftwareItem>> GetInstalledUwpAppsAsync(Action<string> log, CancellationToken ct)
        {
            var uwpApps = new List<SoftwareItem>();

            // Kompaktes JSON-Streaming pro Zeile verhindert Sonderzeichen-Abstürze vollständig!
            string script = @"
Get-AppxPackage -AllUsers | Where-Object { -not $_.IsFramework -and $_.NonRemovable -ne $true } | ForEach-Object {
    $date = 'Unbekannt'
    if ($_.InstallLocation -and (Test-Path $_.InstallLocation)) {
        $date = (Get-Item $_.InstallLocation).CreationTime.ToString('yyyyMMdd')
    }
    $obj = [PSCustomObject]@{
        Name = if ($_.DisplayName) { $_.DisplayName } else { $_.Name }
        Version = $_.Version
        Publisher = if ($_.PublisherId) { $_.PublisherId } else { 'Microsoft Store' }
        PackageFullName = $_.PackageFullName
        InstallDate = $date
    }
    $obj | ConvertTo-Json -Compress
}
";

            log("Führe erweiterten PowerShell-Scan für alle Windows-Apps durch...");

            try
            {
                await _ps.RunScriptAsync(script,
                    onOutput: line =>
                    {
                        if (string.IsNullOrWhiteSpace(line) || !line.Trim().StartsWith("{")) return;

                        try
                        {
                            using (var doc = JsonDocument.Parse(line.Trim()))
                            {
                                var root = doc.RootElement;
                                string displayName = root.GetProperty("Name").GetString() ?? string.Empty;
                                string version = root.GetProperty("Version").GetString() ?? string.Empty;
                                string publisher = root.GetProperty("Publisher").GetString() ?? string.Empty;
                                string packageFullName = root.GetProperty("PackageFullName").GetString() ?? string.Empty;
                                string rawDate = root.GetProperty("InstallDate").GetString() ?? "Unbekannt";

                                DateTime? installDateTime = null;
                                string displayDate = "Unbekannt";

                                if (rawDate != "Unbekannt" && DateTime.TryParseExact(rawDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime pDate))
                                {
                                    installDateTime = pDate;
                                    displayDate = pDate.ToString("dd.MM.yyyy");
                                }

                                lock (uwpApps)
                                {
                                    if (!uwpApps.Any(a => a.PackageFullName.Equals(packageFullName, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        uwpApps.Add(new SoftwareItem
                                        {
                                            Name = displayName,
                                            Publisher = publisher,
                                            DisplayVersion = version,
                                            UninstallString = string.Empty,
                                            InstallDate = displayDate,
                                            InstallDateTime = installDateTime,
                                            InstallDateDisplay = displayDate,
                                            EstimatedSize = "Unbekannt",
                                            EstimatedSizeKb = 0,
                                            IsUwp = true,
                                            PackageFullName = packageFullName
                                        });
                                    }
                                }
                            }
                        }
                        catch { }
                    },
                    onError: err => { },
                    cancellationToken: ct
                );
            }
            catch (Exception ex)
            {
                log($"[FEHLER] Beim Auslesen der Windows Apps: {ex.Message}");
            }

            return uwpApps;
        }

        private string GetFreshUninstallString(string registryPath)
        {
            if (string.IsNullOrEmpty(registryPath)) return string.Empty;

            RegistryKey? rootKey = null;
            string subKeyPath = string.Empty;

            if (registryPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.LocalMachine;
                subKeyPath = registryPath.Substring("HKEY_LOCAL_MACHINE\\".Length);
            }
            else if (registryPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            {
                rootKey = Registry.CurrentUser;
                subKeyPath = registryPath.Substring("HKEY_CURRENT_USER\\".Length);
            }

            if (rootKey != null && !string.IsNullOrEmpty(subKeyPath))
            {
                using (RegistryKey? key = rootKey.OpenSubKey(subKeyPath))
                {
                    string un = key?.GetValue("UninstallString")?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(un))
                    {
                        un = key?.GetValue("QuietUninstallString")?.ToString() ?? string.Empty;
                    }
                    return un;
                }
            }
            return string.Empty;
        }

        public async Task UninstallSoftwareAsync(SoftwareItem item, bool cleanLeftovers, Action<string> log, CancellationToken ct)
        {
            log($"--- DEINSTALLATION VON {item.Name.ToUpper()} GESTARTET ---");

            if (item.IsUwp)
            {
                log($"[Aktion] Führe PowerShell-Deinstallation aus...");
                string removeScript = $@"
$PackageName = ""{item.PackageFullName}""
$AppName = ""{item.Name}""
Remove-AppxPackage -Package $PackageName -AllUsers -ErrorAction SilentlyContinue
Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq $AppName -or $_.PackageName -eq $PackageName }} | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue
";
                try
                {
                    await _ps.RunScriptAsync(removeScript, _ => { }, _ => { }, ct);
                    log("[Status] UWP-App Deinstallationsbefehl ausgeführt.");
                }
                catch (Exception ex) { log($"[FEHLER]: {ex.Message}"); }
            }
            else
            {
                string freshUninstallString = GetFreshUninstallString(item.RegistryPath);
                if (string.IsNullOrEmpty(freshUninstallString))
                {
                    log($"FEHLER: Deinstallations-Eintrag besitzt keine gültige Ausführungslogik.\n");
                    return;
                }

                item.UninstallString = freshUninstallString;
                var (command, arguments) = ParseUninstallString(item.UninstallString);

                try
                {
                    await _cmd.RunCommandAsync(command, arguments, log, log, ct);
                    log("[Status] Deinstallationsbefehl ausgeführt.");
                }
                catch (Exception ex) { log($"[FEHLER]: {ex.Message}"); }
            }

            if (ct.IsCancellationRequested) return;

            if (cleanLeftovers)
            {
                log("\n--- STARTE DATENMÜLL- BEREINIGUNG ---");
                try { CleanLeftoverFiles(item.Name, item.Publisher, log); } catch { }
                try { CleanShortcutsAndIconCache(item, log); } catch { }
                if (!item.IsUwp) { try { CleanLeftoverRegistry(item, log); } catch { } }
            }
        }

        private void ScanRegistryKey(RegistryKey rootKey, string subKeyPath, List<SoftwareItem> apps)
        {
            try
            {
                using (RegistryKey? key = rootKey.OpenSubKey(subKeyPath))
                {
                    if (key == null) return;
                    foreach (string subkeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey? subkey = key.OpenSubKey(subkeyName))
                            {
                                if (subkey == null) continue;

                                // Sicherheitsfilter behalten!
                                var systemComponent = subkey.GetValue("SystemComponent");
                                if (systemComponent != null && Convert.ToInt32(systemComponent) == 1)
                                    continue;

                                var parentKeyName = subkey.GetValue("ParentKeyName");
                                if (parentKeyName != null && !string.IsNullOrEmpty(parentKeyName.ToString()))
                                    continue;

                                string? displayName = subkey.GetValue("DisplayName")?.ToString();
                                if (string.IsNullOrWhiteSpace(displayName)) continue;

                                // Weiche auf QuietUninstallString aus, falls UninstallString leer ist
                                string uninstallString = subkey.GetValue("UninstallString")?.ToString() ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(uninstallString))
                                {
                                    uninstallString = subkey.GetValue("QuietUninstallString")?.ToString() ?? string.Empty;
                                }
                                // Wir brechen NICHT mehr per 'continue' ab, damit ALLE Apps gelistet werden!

                                if (displayName.Contains("Security Update") ||
                                    displayName.Contains("Update for Microsoft") ||
                                    displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string publisher = subkey.GetValue("Publisher")?.ToString() ?? "Unbekannt";
                                string displayVersion = subkey.GetValue("DisplayVersion")?.ToString() ?? "Unbekannt";
                                string installDateStr = subkey.GetValue("InstallDate")?.ToString() ?? string.Empty;

                                DateTime? installDateTime = null;
                                string installDateDisplay = "Unbekannt";

                                if (!string.IsNullOrEmpty(installDateStr) && installDateStr.Length == 8 &&
                                    DateTime.TryParseExact(installDateStr, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime pDate))
                                {
                                    installDateTime = pDate;
                                    installDateDisplay = pDate.ToString("dd.MM.yyyy");
                                }
                                else if (!string.IsNullOrEmpty(installDateStr))
                                {
                                    installDateDisplay = installDateStr;
                                }

                                long estimatedSizeKb = 0;
                                var sizeVal = subkey.GetValue("EstimatedSize");
                                if (sizeVal != null) { try { estimatedSizeKb = Convert.ToInt64(sizeVal); } catch { } }

                                string estimatedSizeStr = "Unbekannt";
                                if (estimatedSizeKb > 0)
                                {
                                    double sizeMb = estimatedSizeKb / 1024.0;
                                    estimatedSizeStr = sizeMb >= 1024 ? $"{sizeMb / 1024.0:F2} GB" : $"{sizeMb:F1} MB";
                                }

                                apps.Add(new SoftwareItem
                                {
                                    Name = displayName,
                                    Publisher = publisher,
                                    DisplayVersion = displayVersion,
                                    UninstallString = uninstallString,
                                    InstallDate = installDateDisplay,
                                    InstallDateTime = installDateTime,
                                    InstallDateDisplay = installDateDisplay,
                                    EstimatedSize = estimatedSizeStr,
                                    EstimatedSizeKb = estimatedSizeKb,
                                    RegistryPath = $"{rootKey.Name}\\{subKeyPath}\\{subkeyName}",
                                    RegistryKey = subkeyName,
                                    IsUwp = false
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private (string Command, string Arguments) ParseUninstallString(string uninstallString)
        {
            uninstallString = uninstallString.Trim();
            string command; string arguments = string.Empty;
            if (uninstallString.StartsWith("\""))
            {
                int nextQuote = uninstallString.IndexOf("\"", 1);
                if (nextQuote > 0)
                {
                    command = uninstallString.Substring(1, nextQuote - 1);
                    arguments = uninstallString.Substring(nextQuote + 1).Trim();
                }
                else { command = uninstallString.Replace("\"", ""); }
            }
            else
            {
                int firstSpace = uninstallString.IndexOf(" ");
                if (firstSpace > 0)
                {
                    command = uninstallString.Substring(0, firstSpace);
                    arguments = uninstallString.Substring(firstSpace + 1).Trim();
                }
                else { command = uninstallString; }
            }
            if (command.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || command.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Contains("/I", StringComparison.OrdinalIgnoreCase)) arguments = arguments.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase);
                if (!arguments.Contains("/qn", StringComparison.OrdinalIgnoreCase)) arguments += " /qn /norestart";
            }
            return (command, arguments);
        }

        private void CleanLeftoverFiles(string appName, string publisher, Action<string> log)
        {
            var searchDirs = new List<string> {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };
            foreach (var baseDir in searchDirs.Where(d => !string.IsNullOrEmpty(d)).Distinct())
            {
                try
                {
                    if (!Directory.Exists(baseDir)) continue;
                    foreach (var dir in Directory.GetDirectories(baseDir))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (dirName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Delete(dir, true);
                            log($"[Dateibereinigung] Ordner entfernt: {dirName}");
                        }
                    }
                }
                catch { }
            }
        }

        private void CleanLeftoverRegistry(SoftwareItem app, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(app.Name)) return;
            var softwareRoots = new List<(RegistryKey Root, string Path)> {
                (Registry.LocalMachine, @"SOFTWARE"), (Registry.LocalMachine, @"SOFTWARE\Wow6432Node"), (Registry.CurrentUser, @"SOFTWARE")
            };
            foreach (var root in softwareRoots)
            {
                try
                {
                    using (RegistryKey? key = root.Root.OpenSubKey(root.Path, true))
                    {
                        if (key == null) continue;
                        foreach (string subkeyName in key.GetSubKeyNames())
                        {
                            if (subkeyName.Equals(app.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                key.DeleteSubKeyTree(subkeyName, false);
                                log($"[Registrybereinigung] Schlüssel entfernt: {subkeyName}");
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void CleanShortcutsAndIconCache(SoftwareItem item, Action<string> log)
        {
            var searchPaths = new List<string> { @"C:\Users\Public\Desktop" };
            try { searchPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)); searchPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs)); } catch { }
            foreach (var dir in searchPaths.Where(Directory.Exists).Distinct())
            {
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".lnk" || ext == ".url")
                        {
                            if (Path.GetFileNameWithoutExtension(file).Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(file);
                                log($"[Shortcutbereinigung] Verknüpfung gelöscht: {Path.GetFileName(file)}");
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }
}
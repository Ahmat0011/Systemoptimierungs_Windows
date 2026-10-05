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
            ScanRegistryKey(Registry.LocalMachine, AppRegistryPaths.Uninstall, apps);
            ScanRegistryKey(Registry.LocalMachine, AppRegistryPaths.UninstallWow6432, apps);
            ScanRegistryKey(Registry.CurrentUser, AppRegistryPaths.Uninstall, apps);
            return apps
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        public async Task<List<SoftwareItem>> GetInstalledUwpAppsAsync(Action<string> log, CancellationToken ct)
        {
            var uwpApps = new List<SoftwareItem>();
            string script = @"
Get-AppxPackage -AllUsers | Where-Object { -not $_.IsFramework -and $_.NonRemovable -ne $true } | ForEach-Object {
    $date = 'Unbekannt'
    if ($_.InstallLocation -and (Test-Path $_.InstallLocation)) {
        $date = (Get-Item $_.InstallLocation).CreationTime.ToString('yyyyMMdd')
    }
    $obj = [PSCustomObject]@{
        Name            = if ($_.Name) { $_.Name } else { $_.PackageFullName }
        Version         = $_.Version
        Publisher       = if ($_.Publisher) { $_.Publisher } else { 'Microsoft Store' }
        PackageFullName = $_.PackageFullName
        InstallDate     = $date
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
                            using var doc = JsonDocument.Parse(line.Trim());
                            var root = doc.RootElement;
                            string displayName = root.GetProperty("Name").GetString() ?? string.Empty;
                            string version = root.GetProperty("Version").GetString() ?? string.Empty;
                            string publisher = root.GetProperty("Publisher").GetString() ?? string.Empty;
                            string packageFullName = root.GetProperty("PackageFullName").GetString() ?? string.Empty;
                            string rawDate = root.GetProperty("InstallDate").GetString() ?? "Unbekannt";

                            DateTime? installDateTime = null;
                            string displayDate = "Unbekannt";
                            if (rawDate != "Unbekannt" && DateTime.TryParseExact(rawDate, "yyyyMMdd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out DateTime pDate))
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

        /// <summary>
        /// Öffnet einen als "HKEY_LOCAL_MACHINE\..." bzw. "HKEY_CURRENT_USER\..." gespeicherten Registry-Pfad.
        /// </summary>
        private static RegistryKey? OpenRegistryPath(string registryPath)
        {
            var (root, subKeyPath) = SplitRegistryPath(registryPath);
            if (root == null || string.IsNullOrEmpty(subKeyPath)) return null;
            try { return root.OpenSubKey(subKeyPath); }
            catch { return null; }
        }

        private static (RegistryKey? Root, string SubKeyPath) SplitRegistryPath(string registryPath)
        {
            if (string.IsNullOrEmpty(registryPath)) return (null, string.Empty);
            const string hklm = "HKEY_LOCAL_MACHINE\\";
            const string hkcu = "HKEY_CURRENT_USER\\";
            if (registryPath.StartsWith(hklm, StringComparison.OrdinalIgnoreCase))
                return (Registry.LocalMachine, registryPath.Substring(hklm.Length));
            if (registryPath.StartsWith(hkcu, StringComparison.OrdinalIgnoreCase))
                return (Registry.CurrentUser, registryPath.Substring(hkcu.Length));
            return (null, string.Empty);
        }

        /// <summary>
        /// Liest den aktuellen Deinstallationsbefehl. QuietUninstallString wird bevorzugt,
        /// weil er vom Hersteller explizit für die unbeaufsichtigte Deinstallation vorgesehen ist.
        /// </summary>
        private static string GetFreshUninstallString(string registryPath)
        {
            using RegistryKey? key = OpenRegistryPath(registryPath);
            if (key == null) return string.Empty;
            string quiet = key.GetValue("QuietUninstallString")?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(quiet)) return quiet;
            return key.GetValue("UninstallString")?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Prüft, ob der Uninstall-Eintrag eines Desktop-Programms noch in der Registry existiert.
        /// </summary>
        private bool IsStillInstalled(SoftwareItem item)
        {
            if (item.IsUwp) return true; // Store-Apps werden per PowerShell verifiziert
            if (string.IsNullOrEmpty(item.RegistryPath)) return true;
            using RegistryKey? key = OpenRegistryPath(item.RegistryPath);
            return key != null;
        }

        public int GetStoreAppsFingerprint()
        {
            var hash = new HashCode();
            AddSubKeyNamesToHash(Registry.CurrentUser, AppRegistryPaths.StorePackagesCurrentUser, ref hash);
            AddSubKeyNamesToHash(Registry.LocalMachine, AppRegistryPaths.StorePackagesAllUsers, ref hash);
            return hash.ToHashCode();
        }

        private static void AddSubKeyNamesToHash(RegistryKey root, string path, ref HashCode hash)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(path);
                if (key == null) return;
                string[] names = key.GetSubKeyNames();
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                hash.Add(names.Length);
                foreach (string name in names) hash.Add(name, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        public async Task<bool> UninstallSoftwareAsync(SoftwareItem item, bool cleanLeftovers, Action<string> log, CancellationToken ct)
        {
            string matchedKeyword = ProtectedKeywords
                .FirstOrDefault(kw => item.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            if (!string.IsNullOrEmpty(matchedKeyword))
            {
                log($"[SICHERHEIT] '{item.Name}' enthält das geschützte Schlüsselwort '{matchedKeyword}'.");
                log($"[SICHERHEIT] Deinstallation wurde ZUM SCHUTZ DES SYSTEMS BLOCKIERT.\n");
                return false;
            }

            log($"--- DEINSTALLATION VON {item.Name.ToUpper()} GESTARTET ---");

            bool removed = item.IsUwp
                ? await UninstallStoreAppAsync(item, log, ct)
                : await UninstallDesktopAppAsync(item, cleanLeftovers, log, ct);

            if (ct.IsCancellationRequested) return false;

            if (!removed)
            {
                log($"[WARNUNG] '{item.Name}' ist weiterhin installiert. Es wurden keine Dateien oder Registry-Einträge gelöscht.\n");
                return false;
            }

            // Reste erst NACH bestätigter Deinstallation löschen – nie bei einem noch installierten Programm.
            if (cleanLeftovers)
            {
                log("\n--- STARTE DATENMÜLL-BEREINIGUNG ---");
                try { CleanLeftoverFiles(item.Name, item.Publisher, log); } catch { }
                try { CleanShortcutsAndIconCache(item, log); } catch { }
                if (!item.IsUwp) { try { CleanLeftoverRegistry(item, log); } catch { } }
            }

            log($"[ERFOLG] '{item.Name}' wurde erfolgreich deinstalliert.\n");
            return true;
        }

        private async Task<bool> UninstallStoreAppAsync(SoftwareItem item, Action<string> log, CancellationToken ct)
        {
            log("[Aktion] Führe PowerShell-Deinstallation aus...");
            string removeScript = $@"
$PackageName = '{EscapePowerShellLiteral(item.PackageFullName)}'
$AppName     = '{EscapePowerShellLiteral(item.Name)}'
try {{
    Remove-AppxPackage -Package $PackageName -AllUsers -ErrorAction Stop
}} catch {{
    try {{ Remove-AppxPackage -Package $PackageName -ErrorAction Stop }}
    catch {{ Write-Output ('[FEHLER] ' + $_.Exception.Message) }}
}}
Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
    Where-Object {{ $_.DisplayName -eq $AppName -or $_.PackageName -eq $PackageName }} |
    Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null
if (Get-AppxPackage -AllUsers | Where-Object {{ $_.PackageFullName -eq $PackageName }}) {{
    Write-Output 'STATUS:STILL_INSTALLED'
}} else {{
    Write-Output 'STATUS:REMOVED'
}}
";
            bool removed = false;
            await _ps.RunScriptAsync(removeScript,
                onOutput: line =>
                {
                    string trimmed = line.Trim();
                    if (trimmed == "STATUS:REMOVED") removed = true;
                    else if (trimmed.StartsWith("[FEHLER]", StringComparison.Ordinal)) log(trimmed);
                },
                onError: err => { if (!string.IsNullOrWhiteSpace(err)) log($"[FEHLER] {err.Trim()}"); },
                cancellationToken: ct);

            if (!ct.IsCancellationRequested)
                log(removed ? "[Status] Windows-App wurde entfernt." : "[Status] Windows-App ist weiterhin registriert.");
            return removed;
        }

        private async Task<bool> UninstallDesktopAppAsync(SoftwareItem item, bool cleanLeftovers, Action<string> log, CancellationToken ct)
        {
            if (!IsStillInstalled(item))
            {
                log("[Hinweis] Der Deinstallations-Eintrag existiert nicht mehr – das Programm wurde bereits entfernt.");
                return true;
            }

            string uninstallString = GetFreshUninstallString(item.RegistryPath);
            if (string.IsNullOrWhiteSpace(uninstallString)) uninstallString = item.UninstallString;
            if (string.IsNullOrWhiteSpace(uninstallString))
                return RemoveOrphanedEntry(item, "Der Eintrag enthält keinen Deinstallationsbefehl.", cleanLeftovers, log);

            var (command, arguments) = ParseUninstallString(uninstallString);

            if (Path.IsPathRooted(command) && !File.Exists(command))
                return RemoveOrphanedEntry(item, $"Das Deinstallationsprogramm wurde nicht gefunden: {command}", cleanLeftovers, log);

            log($"[Aktion] Starte: {Path.GetFileName(command)} {arguments}");

            int exitCode = await _cmd.RunCommandAndWaitForProcessTreeAsync(
                command, arguments, log, log,
                isFinished: () => !IsStillInstalled(item),
                ct);
            if (ct.IsCancellationRequested) return false;

            log($"[Status] Deinstaller beendet mit Exit-Code: {exitCode}");

            // 1602 = vom Benutzer abgebrochen: kein Nachwarten nötig
            if (exitCode == 1602 && IsStillInstalled(item))
            {
                log("[Hinweis] Die Deinstallation wurde im Deinstallationsassistenten abgebrochen.");
                return false;
            }

            // Manche Deinstaller entfernen ihren Registry-Eintrag erst kurz nach dem Beenden.
            bool removed = await WaitUntilRemovedAsync(item, TimeSpan.FromSeconds(10), ct);
            if (removed || ct.IsCancellationRequested) return removed;

            // 0 = Erfolg, 1641 = MSI Neustart eingeleitet, 3010 = MSI Neustart erforderlich
            bool exitCodeSuccess = exitCode == 0 || exitCode == 1641 || exitCode == 3010;
            if (exitCodeSuccess)
                log("[Hinweis] Der Deinstaller meldet Erfolg, der Eintrag ist aber noch vorhanden (evtl. ist ein Neustart erforderlich).");
            else
                log("[Hinweis] Die Deinstallation wurde abgebrochen oder ist fehlgeschlagen.");
            return false;
        }

        private async Task<bool> WaitUntilRemovedAsync(SoftwareItem item, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (!IsStillInstalled(item)) return true;
                if (DateTime.UtcNow >= deadline || ct.IsCancellationRequested) return false;
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { return false; }
            }
        }

        /// <summary>
        /// Verwaister Eintrag (Programm existiert nicht mehr, nur die Registry verweist noch darauf):
        /// Nur mit aktivierter Bereinigung wird genau DIESER Eintrag entfernt.
        /// </summary>
        private bool RemoveOrphanedEntry(SoftwareItem item, string reason, bool cleanLeftovers, Action<string> log)
        {
            log($"[Hinweis] {reason}");
            if (!cleanLeftovers)
            {
                log("[Hinweis] Aktiviere \"Restliche Dateien und leere Ordner automatisch löschen\", um den verwaisten Eintrag zu entfernen.");
                return false;
            }

            var (root, subKeyPath) = SplitRegistryPath(item.RegistryPath);
            int lastSeparator = subKeyPath.LastIndexOf('\\');
            if (root == null || lastSeparator <= 0) return false;

            try
            {
                using RegistryKey? parent = root.OpenSubKey(subKeyPath.Substring(0, lastSeparator), writable: true);
                parent?.DeleteSubKeyTree(subKeyPath.Substring(lastSeparator + 1), throwOnMissingSubKey: false);
                log($"[Registry] Verwaister Deinstallations-Eintrag entfernt: {item.RegistryKey}");
            }
            catch (Exception ex)
            {
                log($"[FEHLER] Verwaister Eintrag konnte nicht entfernt werden: {ex.Message}");
            }
            return !IsStillInstalled(item);
        }

        private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

        private void ScanRegistryKey(RegistryKey rootKey, string subKeyPath, List<SoftwareItem> apps)
        {
            try
            {
                using RegistryKey? key = rootKey.OpenSubKey(subKeyPath);
                if (key == null) return;
                foreach (string subkeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? subkey = key.OpenSubKey(subkeyName);
                        if (subkey == null) continue;
                        var systemComponent = subkey.GetValue("SystemComponent");
                        if (systemComponent != null && Convert.ToInt32(systemComponent) == 1) continue;
                        var parentKeyName = subkey.GetValue("ParentKeyName");
                        if (parentKeyName != null && !string.IsNullOrEmpty(parentKeyName.ToString())) continue;
                        string? displayName = subkey.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;
                        string uninstallString = subkey.GetValue("UninstallString")?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(uninstallString))
                            uninstallString = subkey.GetValue("QuietUninstallString")?.ToString() ?? string.Empty;
                        if (displayName.Contains("Security Update") ||
                            displayName.Contains("Update for Microsoft") ||
                            displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) continue;
                        string publisher = subkey.GetValue("Publisher")?.ToString() ?? "Unbekannt";
                        string displayVersion = subkey.GetValue("DisplayVersion")?.ToString() ?? "Unbekannt";
                        string installDateStr = subkey.GetValue("InstallDate")?.ToString() ?? string.Empty;
                        DateTime? installDateTime = null;
                        string installDateDisplay = "Unbekannt";
                        if (!string.IsNullOrEmpty(installDateStr) && installDateStr.Length == 8 &&
                            DateTime.TryParseExact(installDateStr, "yyyyMMdd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out DateTime pDate))
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
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Erkennt MSI, InnoSetup, NSIS und fügt passende Silent-Parameter hinzu.
        /// </summary>
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

            string cmdLower = command.ToLowerInvariant();
            string argsLower = arguments.ToLowerInvariant();

            // MSI (msiexec.exe)
            if (cmdLower.EndsWith("msiexec.exe") || cmdLower.EndsWith("msiexec"))
            {
                arguments = System.Text.RegularExpressions.Regex.Replace(
                    arguments,
                    @"(?i)/I\s*(?=\{|$|\s)",
                    "/X ");
                if (!argsLower.Contains("/qn") && !argsLower.Contains("/quiet"))
                    arguments += " /qn /norestart";
            }
            // InnoSetup (unins000.exe, oft Setup-Dateien)
            else if (argsLower.Contains("inno setup") ||
                     cmdLower.Contains("unins") ||
                     argsLower.Contains("/verysilent") ||
                     argsLower.Contains("/silent"))
            {
                if (!argsLower.Contains("/verysilent"))
                    arguments += " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
            }
            // NSIS (uninstall.exe mit typischen Namen)
            else if (cmdLower.EndsWith("uninstall.exe") || cmdLower.EndsWith("uninst.exe"))
            {
                if (!argsLower.Contains("/s") && !argsLower.Contains("/silent"))
                    arguments += " /S";
            }
            // InstallShield
            else if (argsLower.Contains("installshield"))
            {
                if (!argsLower.Contains("/s") && !argsLower.Contains("/silent"))
                    arguments += " /s /sms";
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

                    // 1. Direkter Programmordner: baseDir\AppName
                    foreach (var dir in Directory.GetDirectories(baseDir))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (dirName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                Directory.Delete(dir, true);
                                log($"[Dateibereinigung] Ordner entfernt: {dirName}");
                            }
                            catch { }
                        }
                    }

                    // 2. Hersteller-Unterordner: baseDir\Publisher\AppName
                    if (!string.IsNullOrWhiteSpace(publisher) &&
                        !publisher.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase) &&
                        !publisher.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                    {
                        string pubDir = Path.Combine(baseDir, publisher);
                        if (Directory.Exists(pubDir))
                        {
                            string subAppDir = Path.Combine(pubDir, appName);
                            if (Directory.Exists(subAppDir))
                            {
                                try
                                {
                                    Directory.Delete(subAppDir, true);
                                    log($"[Dateibereinigung] Hersteller-Ordner bereinigt: {publisher}\\{appName}");
                                }
                                catch { }
                            }

                            try
                            {
                                if (!Directory.EnumerateFileSystemEntries(pubDir).Any())
                                {
                                    Directory.Delete(pubDir, false);
                                    log($"[Dateibereinigung] Leeren Hersteller-Ordner entfernt: {publisher}");
                                }
                            }
                            catch { }
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
                (Registry.LocalMachine, @"SOFTWARE"),
                (Registry.LocalMachine, @"SOFTWARE\Wow6432Node"),
                (Registry.CurrentUser, @"SOFTWARE")
            };
            foreach (var root in softwareRoots)
            {
                try
                {
                    using RegistryKey? key = root.Root.OpenSubKey(root.Path, true);
                    if (key == null) continue;
                    foreach (string subkeyName in key.GetSubKeyNames())
                    {
                        if (subkeyName.Equals(app.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                key.DeleteSubKeyTree(subkeyName, false);
                                log($"[Registrybereinigung] Schlüssel entfernt: {subkeyName}");
                            }
                            catch { }
                        }
                        else if (!string.IsNullOrWhiteSpace(app.Publisher) &&
                                 !app.Publisher.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase) &&
                                 !app.Publisher.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                                 subkeyName.Equals(app.Publisher, StringComparison.OrdinalIgnoreCase))
                        {
                            using RegistryKey? pubKey = key.OpenSubKey(subkeyName, true);
                            if (pubKey != null)
                            {
                                foreach (string pubSubName in pubKey.GetSubKeyNames())
                                {
                                    if (pubSubName.Equals(app.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            pubKey.DeleteSubKeyTree(pubSubName, false);
                                            log($"[Registrybereinigung] Schlüssel entfernt: {subkeyName}\\{pubSubName}");
                                        }
                                        catch { }
                                    }
                                }
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
            try
            {
                searchPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                searchPaths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            }
            catch { }
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
                                try
                                {
                                    File.Delete(file);
                                    log($"[Shortcutbereinigung] Verknüpfung gelöscht: {Path.GetFileName(file)}");
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }
}
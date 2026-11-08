using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        /// <summary>
        /// Schlüsselwörter, die als eigenständige Wörter oder Phrasen auf kritische System-,
        /// Treiber- oder Laufzeitkomponenten hinweisen. Wortgrenzen verhindern Fehlalarme
        /// (z. B. "amd" in "StreamDeck" oder "office" in "LibreOffice").
        /// </summary>
        private static readonly (string Label, Regex Pattern)[] ProtectedPatterns =
        {
            ("microsoft", new Regex(@"\bmicrosoft\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("windows", new Regex(@"\bwindows\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("intel", new Regex(@"\bintel\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("amd", new Regex(@"\bamd\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("nvidia", new Regex(@"\bnvidia\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("drivers", new Regex(@"\bdrivers?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("framework", new Regex(@"\b\.?net\s+framework\b|\bframework\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("runtime", new Regex(@"\bruntime\b|\bredistributable\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("dotnet", new Regex(@"\bdotnet\b|\b\.net\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("visual studio", new Regex(@"\bvisual\s+studio\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("directx", new Regex(@"\bdirectx\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("defender", new Regex(@"\bdefender\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
        };

        /// <summary>
        /// Ordner, die bei der Reste-Bereinigung niemals gelöscht werden dürfen – selbst wenn ein
        /// Programm denselben Namen trägt. Verhindert, dass ein unglücklicher Programmname einen
        /// Systemordner trifft.
        /// </summary>
        private static readonly HashSet<string> ProtectedFolderNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "WindowsApps", "Windows Defender", "Windows NT", "Common Files",
            "Microsoft", "Microsoft Shared", "Microsoft.NET", "Internet Explorer",
            "System32", "SysWOW64", "Drivers", "System", "Desktop", "Documents",
            "Downloads", "Pictures", "Music", "Videos", "Favorites", "Start Menu", "Templates",
            "Programs", "Startup", "AppData", "Local", "Roaming", "LocalLow", "Package Cache"
        };

        /// <summary>Registry-Zweige, die nie als Programmrest gelöscht werden dürfen.</summary>
        private static readonly HashSet<string> ProtectedRegistryKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Classes", "Clients", "Microsoft", "Policies", "RegisteredApplications",
            "WOW6432Node", "Windows", "Windows NT", "System", "Setup", "ODBC", "Control Panel"
        };

        private static readonly string[] ExecutableExtensions = { ".exe", ".com", ".bat", ".cmd" };

        public UninstallerService(ICommandExecutionService commandService, IPowerShellService powerShellService)
        {
            _cmd = commandService ?? throw new ArgumentNullException(nameof(commandService));
            _ps = powerShellService ?? throw new ArgumentNullException(nameof(powerShellService));
        }

        public List<SoftwareItem> GetInstalledSoftware()
        {
            var apps = new List<SoftwareItem>();
            ScanRegistryKey(Registry.LocalMachine, AppRegistryPaths.Uninstall, apps);
            ScanRegistryKey(Registry.LocalMachine, AppRegistryPaths.UninstallWow6432, apps);
            ScanRegistryKey(Registry.CurrentUser, AppRegistryPaths.Uninstall, apps);

            // Bei Namensgleichheit (z. B. Schatten-Eintrag in WOW6432Node oder HKCU) den vollständigsten Eintrag bevorzugen
            return apps
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(a => !string.IsNullOrWhiteSpace(a.UninstallString))
                    .ThenByDescending(a => a.EstimatedSizeKb)
                    .First())
                .ToList();
        }

        public async Task<List<SoftwareItem>> GetInstalledUwpAppsAsync(Action<string> log, CancellationToken ct)
        {
            var uwpApps = new List<SoftwareItem>();
            // Bereits gesehene Pakete: verhindert doppelte Einträge in O(1) statt O(n) pro Paket.
            var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string script = @"
$pkgs = @()
try {
    $pkgs = @(Get-AppxPackage -AllUsers -ErrorAction Stop)
} catch {
    $pkgs = @(Get-AppxPackage -ErrorAction SilentlyContinue)
}
$pkgs | Where-Object { -not $_.IsFramework -and $_.NonRemovable -ne $true } | ForEach-Object {
    $date = 'Unbekannt'
    if ($_.InstallLocation) {
        # Direkter .NET-Aufruf statt Test-Path + Get-Item (zwei Cmdlet-Aufrufe pro Paket weniger)
        try { $date = [System.IO.Directory]::GetCreationTimeUtc($_.InstallLocation).ToString('yyyyMMdd', [System.Globalization.CultureInfo]::InvariantCulture) } catch { }
    }
    $obj = [PSCustomObject]@{
        Name            = if ($_.Name) { $_.Name } else { $_.PackageFullName }
        Version         = [string]$_.Version
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
                await _ps.RunScriptAsync(
                    script,
                    onOutput: line =>
                    {
                        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) return;
                        try
                        {
                            using var doc = JsonDocument.Parse(line.Trim());
                            var root = doc.RootElement;
                            string displayName = GetJsonString(root, "Name");
                            string version = GetJsonString(root, "Version");
                            string publisher = GetJsonString(root, "Publisher");
                            string packageFullName = GetJsonString(root, "PackageFullName");
                            string rawDate = GetJsonString(root, "InstallDate");

                            if (string.IsNullOrWhiteSpace(packageFullName)) return;

                            DateTime? installDateTime = null;
                            string displayDate = "Unbekannt";
                            if (rawDate.Length > 0 && !rawDate.Equals("Unbekannt", StringComparison.Ordinal) &&
                                DateTime.TryParseExact(rawDate, "yyyyMMdd",
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out DateTime parsedDate))
                            {
                                installDateTime = parsedDate;
                                displayDate = parsedDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                            }

                            lock (uwpApps)
                            {
                                if (!seenPackages.Add(packageFullName)) return;

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
                        catch
                        {
                        }
                    },
                    onError: err => { if (!string.IsNullOrWhiteSpace(err)) log($"[PS-ERR] {err.Trim()}"); },
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                log($"[FEHLER] Beim Auslesen der Windows Apps: {ex.Message}");
            }
            return uwpApps;
        }

        /// <summary>Liest eine Zeichenfolge aus dem JSON-Objekt, ohne bei fehlenden Feldern zu werfen.</summary>
        private static string GetJsonString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>
        /// Öffnet einen als "HKEY_LOCAL_MACHINE\..." bzw. "HKEY_CURRENT_USER\..." gespeicherten Registry-Pfad.
        /// </summary>
        private static RegistryKey? OpenRegistryPath(string registryPath)
        {
            var (root, subKeyPath) = SplitRegistryPath(registryPath);
            if (root == null || string.IsNullOrEmpty(subKeyPath)) return null;
            try { return root.OpenSubKey(subKeyPath, writable: false); }
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
        private static bool IsStillInstalled(SoftwareItem item)
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
                using RegistryKey? key = root.OpenSubKey(path, writable: false);
                if (key == null) return;
                string[] names = key.GetSubKeyNames();
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                hash.Add(names.Length);
                foreach (string name in names) hash.Add(name, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
            }
        }

        public async Task<bool> UninstallSoftwareAsync(SoftwareItem item, bool cleanLeftovers, Action<string> log, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(log);

            string matchedKeyword = GetMatchedProtectedKeyword(item.Name);
            if (!string.IsNullOrEmpty(matchedKeyword))
            {
                log($"[SICHERHEIT] '{item.Name}' enthält das geschützte Schlüsselwort '{matchedKeyword}'.");
                log("[SICHERHEIT] Deinstallation wurde ZUM SCHUTZ DES SYSTEMS BLOCKIERT.\n");
                return false;
            }

            log($"--- DEINSTALLATION VON {item.Name.ToUpperInvariant()} GESTARTET ---");

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
                if (!item.IsUwp)
                {
                    try { CleanLeftoverRegistry(item, log); } catch { }
                }
            }

            log($"[ERFOLG] '{item.Name}' wurde erfolgreich deinstalliert.\n");
            return true;
        }

        private static string GetMatchedProtectedKeyword(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return string.Empty;
            foreach (var (label, pattern) in ProtectedPatterns)
            {
                if (pattern.IsMatch(appName)) return label;
            }
            return string.Empty;
        }

        private async Task<bool> UninstallStoreAppAsync(SoftwareItem item, Action<string> log, CancellationToken ct)
        {
            log("[Aktion] Führe PowerShell-Deinstallation aus...");
            string removeScript = $@"
$PackageName = '{EscapePowerShellLiteral(item.PackageFullName)}'
$AppName     = '{EscapePowerShellLiteral(item.Name)}'

Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
    Where-Object {{ $_.DisplayName -eq $AppName -or $_.PackageName -eq $PackageName }} |
    Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null

try {{
    Remove-AppxPackage -Package $PackageName -AllUsers -ErrorAction Stop
}} catch {{
    try {{
        Remove-AppxPackage -Package $PackageName -ErrorAction Stop
    }} catch {{
        Write-Output ('[FEHLER] ' + $_.Exception.Message)
    }}
}}

$stillInstalled = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object {{ $_.PackageFullName -eq $PackageName }})
if ($stillInstalled.Count -gt 0) {{
    Write-Output 'STATUS:STILL_INSTALLED'
}} else {{
    Write-Output 'STATUS:REMOVED'
}}
";
            bool removed = false;
            await _ps.RunScriptAsync(
                removeScript,
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

            uninstallString = Environment.ExpandEnvironmentVariables(uninstallString);
            var (command, arguments) = ParseUninstallString(uninstallString);

            if (Path.IsPathRooted(command) && !File.Exists(command))
                return RemoveOrphanedEntry(item, $"Das Deinstallationsprogramm wurde nicht gefunden: {command}", cleanLeftovers, log);

            log($"[Aktion] Starte: {Path.GetFileName(command)} {arguments}");

            int exitCode = await _cmd.RunCommandAndWaitForProcessTreeAsync(
                command,
                arguments,
                log,
                log,
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

        private static async Task<bool> WaitUntilRemovedAsync(SoftwareItem item, TimeSpan timeout, CancellationToken ct)
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
        private static bool RemoveOrphanedEntry(SoftwareItem item, string reason, bool cleanLeftovers, Action<string> log)
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

        private static string EscapePowerShellLiteral(string value) => (value ?? string.Empty).Replace("'", "''");

        private static void ScanRegistryKey(RegistryKey rootKey, string subKeyPath, List<SoftwareItem> apps)
        {
            try
            {
                using RegistryKey? key = rootKey.OpenSubKey(subKeyPath, writable: false);
                if (key == null) return;
                foreach (string subkeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? subkey = key.OpenSubKey(subkeyName, writable: false);
                        if (subkey == null) continue;

                        var systemComponent = subkey.GetValue("SystemComponent");
                        if (systemComponent != null && Convert.ToInt32(systemComponent, CultureInfo.InvariantCulture) == 1) continue;

                        var parentKeyName = subkey.GetValue("ParentKeyName");
                        if (parentKeyName != null && !string.IsNullOrEmpty(parentKeyName.ToString())) continue;

                        string? displayName = subkey.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        string uninstallString = subkey.GetValue("UninstallString")?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(uninstallString))
                            uninstallString = subkey.GetValue("QuietUninstallString")?.ToString() ?? string.Empty;

                        if (displayName.Contains("Security Update", StringComparison.OrdinalIgnoreCase) ||
                            displayName.Contains("Update for Microsoft", StringComparison.OrdinalIgnoreCase) ||
                            displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string publisher = subkey.GetValue("Publisher")?.ToString() ?? "Unbekannt";
                        string displayVersion = subkey.GetValue("DisplayVersion")?.ToString() ?? "Unbekannt";
                        string installDateStr = subkey.GetValue("InstallDate")?.ToString()?.Trim() ?? string.Empty;

                        DateTime? installDateTime = null;
                        string installDateDisplay = "Unbekannt";
                        if (!string.IsNullOrEmpty(installDateStr) && installDateStr.Length == 8 &&
                            DateTime.TryParseExact(installDateStr, "yyyyMMdd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out DateTime pDate))
                        {
                            installDateTime = pDate;
                            installDateDisplay = pDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                        }
                        else if (!string.IsNullOrEmpty(installDateStr))
                        {
                            installDateDisplay = installDateStr;
                        }

                        long estimatedSizeKb = 0;
                        var sizeVal = subkey.GetValue("EstimatedSize");
                        if (sizeVal != null)
                        {
                            try { estimatedSizeKb = Convert.ToInt64(sizeVal, CultureInfo.InvariantCulture); } catch { }
                        }

                        string estimatedSizeStr = "Unbekannt";
                        if (estimatedSizeKb > 0)
                        {
                            double sizeMb = estimatedSizeKb / 1024.0;
                            estimatedSizeStr = sizeMb >= 1024 ? $"{sizeMb / 1024.0:F2} GB" : $"{sizeMb:F1} MB";
                        }

                        apps.Add(new SoftwareItem
                        {
                            Name = displayName.Trim(),
                            Publisher = publisher.Trim(),
                            DisplayVersion = displayVersion.Trim(),
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
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Zerlegt einen Deinstallationsbefehl in Programm und Argumente. Berücksichtigt
        /// Anführungszeichen und – entscheidend – nicht angeführte Pfade mit Leerzeichen
        /// (z. B. <c>C:\Program Files\App\uninstall.exe /S</c>).
        /// </summary>
        private static (string Command, string Arguments) ParseUninstallString(string uninstallString)
        {
            string text = uninstallString.Trim();
            string command;
            string arguments;

            if (text.StartsWith('"'))
            {
                int closingQuote = text.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    command = text[1..closingQuote];
                    arguments = text[(closingQuote + 1)..].Trim();
                }
                else
                {
                    command = text.Replace("\"", string.Empty);
                    arguments = string.Empty;
                }
            }
            else
            {
                int executableEnd = FindExecutableEnd(text);
                if (executableEnd > 0)
                {
                    command = text[..executableEnd].Trim();
                    arguments = text[executableEnd..].Trim();
                }
                else
                {
                    int firstSpace = text.IndexOf(' ');
                    command = firstSpace > 0 ? text[..firstSpace] : text;
                    arguments = firstSpace > 0 ? text[(firstSpace + 1)..].Trim() : string.Empty;
                }
            }

            string cmdLower = command.ToLowerInvariant();
            string argsLower = arguments.ToLowerInvariant();

            // MSI (msiexec.exe)
            if (cmdLower.EndsWith("msiexec.exe", StringComparison.Ordinal) || cmdLower.EndsWith("msiexec", StringComparison.Ordinal))
            {
                arguments = Regex.Replace(
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
            else if (cmdLower.EndsWith("uninstall.exe", StringComparison.Ordinal) || cmdLower.EndsWith("uninst.exe", StringComparison.Ordinal))
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

            return (command, arguments.Trim());
        }

        /// <summary>
        /// Liefert die Position direkt hinter einer ausführbaren Datei in einem nicht angeführten
        /// Pfad oder -1, wenn keine gefunden wurde.
        /// </summary>
        private static int FindExecutableEnd(string text)
        {
            foreach (string extension in ExecutableExtensions)
            {
                int index = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    int end = index + extension.Length;
                    if (end == text.Length || text[end] == ' ' || text[end] == '\t')
                        return end;
                    index = text.IndexOf(extension, index + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            return -1;
        }

        /// <summary>
        /// Entfernt übrig gebliebene Programmordner. In Benutzerprofilen (AppData) werden nur noch
        /// LEERE Ordner gelöscht: Dort liegen Einstellungen, Spielstände und Caches, die ein Nutzer
        /// nach einer Neuinstallation oft weiterverwenden möchte.
        /// </summary>
        private static void CleanLeftoverFiles(string appName, string publisher, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(appName)) return;

            string name = appName.Trim();
            if (name.Length < 3 || ProtectedFolderNames.Contains(name)) return;

            var searchDirs = new (string Path, bool MayDeleteNonEmpty)[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), false),
                (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), false)
            };

            foreach (var (baseDir, mayDeleteNonEmpty) in searchDirs)
            {
                if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;

                // 1. Direkter Programmordner: baseDir\AppName
                TryRemoveAppFolder(baseDir, name, mayDeleteNonEmpty, log);

                // 2. Hersteller-Unterordner: baseDir\Publisher\AppName
                if (!IsUsablePublisher(publisher)) continue;

                string cleanPublisher = publisher.Trim();
                if (ProtectedFolderNames.Contains(cleanPublisher)) continue;

                string publisherDir = Path.Combine(baseDir, cleanPublisher);
                if (!IsInsideDirectory(baseDir, publisherDir) || !Directory.Exists(publisherDir)) continue;

                TryRemoveAppFolder(publisherDir, name, mayDeleteNonEmpty, log);
                TryRemoveEmptyDirectory(publisherDir, cleanPublisher, log);
            }
        }

        private static bool IsUsablePublisher(string publisher) =>
            !string.IsNullOrWhiteSpace(publisher) &&
            !publisher.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase) &&
            !publisher.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            !publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);

        private static void TryRemoveAppFolder(string parentDir, string appName, bool mayDeleteNonEmpty, Action<string> log)
        {
            string target = Path.Combine(parentDir, appName);

            // Sicherheitsnetz: Der Zielpfad muss wirklich UNTER dem Basisordner liegen
            // (kein "..\" oder absolute Pfade aus dem Programmnamen).
            if (!IsInsideDirectory(parentDir, target) || !Directory.Exists(target)) return;

            if (!mayDeleteNonEmpty)
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        log($"[Dateibereinigung] Benutzerdaten bleiben erhalten: {target}");
                        return;
                    }
                }
                catch
                {
                    return;
                }
            }

            try
            {
                Directory.Delete(target, recursive: true);
                log($"[Dateibereinigung] Ordner entfernt: {target}");
            }
            catch (Exception ex)
            {
                log($"[Dateibereinigung] Ordner konnte nicht entfernt werden ({ex.Message}): {target}");
            }
        }

        private static void TryRemoveEmptyDirectory(string directory, string displayName, Action<string> log)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                    log($"[Dateibereinigung] Leeren Hersteller-Ordner entfernt: {displayName}");
                }
            }
            catch
            {
            }
        }

        /// <summary>Prüft, ob <paramref name="candidate"/> echt unterhalb von <paramref name="parentDir"/> liegt.</summary>
        private static bool IsInsideDirectory(string parentDir, string candidate)
        {
            try
            {
                string parent = Path.GetFullPath(parentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(candidate);
                return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CleanLeftoverRegistry(SoftwareItem app, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(app.Name)) return;
            string cleanName = app.Name.Trim();
            if (cleanName.Length < 3 || ProtectedRegistryKeys.Contains(cleanName)) return;

            var softwareRoots = new (RegistryKey Root, string Path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE"),
                (Registry.LocalMachine, @"SOFTWARE\Wow6432Node"),
                (Registry.CurrentUser, @"SOFTWARE")
            };

            foreach (var root in softwareRoots)
            {
                try
                {
                    using RegistryKey? key = root.Root.OpenSubKey(root.Path, writable: true);
                    if (key == null) continue;
                    foreach (string subkeyName in key.GetSubKeyNames())
                    {
                        if (ProtectedRegistryKeys.Contains(subkeyName)) continue;

                        if (subkeyName.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                key.DeleteSubKeyTree(subkeyName, throwOnMissingSubKey: false);
                                log($"[Registrybereinigung] Schlüssel entfernt: {subkeyName}");
                            }
                            catch
                            {
                            }
                        }
                        else if (IsUsablePublisher(app.Publisher) &&
                                 subkeyName.Equals(app.Publisher.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            using RegistryKey? pubKey = key.OpenSubKey(subkeyName, writable: true);
                            if (pubKey != null)
                            {
                                foreach (string pubSubName in pubKey.GetSubKeyNames())
                                {
                                    if (pubSubName.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            pubKey.DeleteSubKeyTree(pubSubName, throwOnMissingSubKey: false);
                                            log($"[Registrybereinigung] Schlüssel entfernt: {subkeyName}\\{pubSubName}");
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static void CleanShortcutsAndIconCache(SoftwareItem item, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) return;
            string targetName = item.Name.Trim();

            var searchPaths = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };

            foreach (var dir in searchPaths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", enumOptions))
                    {
                        string ext = Path.GetExtension(file);
                        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Path.GetFileNameWithoutExtension(file).Equals(targetName, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    File.Delete(file);
                                    log($"[Shortcutbereinigung] Verknüpfung gelöscht: {Path.GetFileName(file)}");
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }
}

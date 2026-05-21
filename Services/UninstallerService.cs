using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            
            // HKLM 64-bit
            ScanRegistryKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
            
            // HKLM 32-bit (Wow6432Node)
            ScanRegistryKey(Registry.LocalMachine, @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall", apps);
            
            // HKCU
            ScanRegistryKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
            
            // Filter and sort
            return apps
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(a => a.Name)
                .ToList();
        }

        public async Task<List<SoftwareItem>> GetInstalledUwpAppsAsync(Action<string> log, CancellationToken ct)
        {
            var uwpApps = new List<SoftwareItem>();
            
            // Script to query Store/UWP packages
            string script = @"
Get-AppxPackage -AllUsers | Where-Object { -not $_.IsFramework -and $_.NonRemovable -ne $true -and $_.SignatureKind -eq 'Store' } | ForEach-Object {
    $displayName = $_.DisplayName
    if ([string]::IsNullOrEmpty($displayName)) {
        $displayName = $_.Name
    }
    $publisher = $_.PublisherId
    if ([string]::IsNullOrEmpty($publisher)) {
        $publisher = 'Microsoft Store'
    }
    Write-Output ""$($displayName)##$($_.Version)##$($publisher)##$($_.PackageFullName)""
}
";

            log("Führe PowerShell-Abfrage für Windows Store Apps aus...");
            
            try
            {
                await _ps.RunScriptAsync(script, 
                    onOutput: line =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        
                        var parts = line.Split("##", StringSplitOptions.None);
                        if (parts.Length >= 4)
                        {
                            var displayName = parts[0].Trim();
                            var version = parts[1].Trim();
                            var publisher = parts[2].Trim();
                            var packageFullName = parts[3].Trim();
                            
                            lock (uwpApps)
                            {
                                // Filter duplicate package full names
                                if (!uwpApps.Any(a => a.PackageFullName.Equals(packageFullName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    uwpApps.Add(new SoftwareItem
                                    {
                                        Name = displayName,
                                        Publisher = publisher,
                                        DisplayVersion = version,
                                        UninstallString = string.Empty,
                                        InstallDate = "Unbekannt",
                                        EstimatedSize = "Unbekannt",
                                        IsUwp = true,
                                        PackageFullName = packageFullName
                                    });
                                }
                            }
                        }
                    },
                    onError: err =>
                    {
                        // Simply log error in background
                        log($"[PowerShell Info] {err}");
                    },
                    cancellationToken: ct
                );
            }
            catch (Exception ex)
            {
                log($"[FEHLER] Beim Auslesen der Windows Apps: {ex.Message}");
            }
            
            return uwpApps.OrderBy(a => a.Name).ToList();
        }

        public async Task UninstallSoftwareAsync(SoftwareItem item, bool cleanLeftovers, Action<string> log, CancellationToken ct)
        {
            log($"--- DEINSTALLATION VON {item.Name.ToUpper()} GESTARTET ---");
            log($"App-Typ:   {(item.IsUwp ? "Windows Store App (UWP)" : "Klassisches System-Programm")}");
            log($"Publisher: {item.Publisher}");
            log($"Version:   {item.DisplayVersion}");
            log($"Größe:     {item.EstimatedSize}");
            
            if (item.IsUwp)
            {
                log($"PackageFullName: {item.PackageFullName}");
                log($"[Aktion] Führe PowerShell-Deinstallation aus...");
                
                string removeScript = $"Remove-AppxPackage -PackageFullName \"{item.PackageFullName}\" -AllUsers";
                try
                {
                    bool success = true;
                    await _ps.RunScriptAsync(removeScript, 
                        onOutput: outLine => {
                            if (!string.IsNullOrWhiteSpace(outLine)) log($"[UWP Output] {outLine}");
                        }, 
                        onError: errLine => {
                            if (!string.IsNullOrWhiteSpace(errLine))
                            {
                                log($"[UWP Fehler] {errLine}");
                                success = false;
                            }
                        }, 
                        cancellationToken: ct);
                    
                    if (success)
                    {
                        log("[Status] UWP-App Deinstallationsbefehl erfolgreich ausgeführt.");
                    }
                    else
                    {
                        log("[WARNUNG] Bei der UWP-App Deinstallation traten Fehler auf oder sie ist nicht für alle Benutzer entfernbar.");
                    }
                }
                catch (Exception ex)
                {
                    log($"[FEHLER] Bei der Ausführung der UWP-Deinstallation: {ex.Message}");
                }
            }
            else
            {
                log($"UninstallPfad:   {item.UninstallString}");
                var (command, arguments) = ParseUninstallString(item.UninstallString);
                
                if (string.IsNullOrEmpty(command))
                {
                    log("FEHLER: Deinstallationsbefehl konnte nicht analysiert werden.\n");
                    return;
                }
                
                log($"[Aktion] Führe Deinstallationsbefehl aus: \"{command}\" {arguments}");
                
                try
                {
                    await _cmd.RunCommandAsync(command, arguments, log, log, ct);
                    log("[Status] Deinstallationsbefehl erfolgreich ausgeführt.");
                }
                catch (Exception ex)
                {
                    log($"[FEHLER] Bei der Ausführung des Deinstallationsbefehls: {ex.Message}");
                }
            }
            
            if (ct.IsCancellationRequested) return;
            
            if (cleanLeftovers)
            {
                log("\n--- STARTE DATENMÜLL- UND REGISTRY-BEREINIGUNG ---");
                try
                {
                    CleanLeftoverFiles(item.Name, item.Publisher, log);
                }
                catch (Exception ex)
                {
                    log($"[Fehler] Bei der Datei-Reste-Bereinigung: {ex.Message}");
                }
                
                if (!item.IsUwp)
                {
                    try
                    {
                        CleanLeftoverRegistry(item, log);
                    }
                    catch (Exception ex)
                    {
                        log($"[Fehler] Bei der Registry-Reste-Bereinigung: {ex.Message}");
                    }
                }
                log("--- BEREINIGUNG DER RESTE ABGESCHLOSSEN ---");
            }
            
            log($"--- DEINSTALLATION BEENDET ---\n");
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
                                
                                var systemComponent = subkey.GetValue("SystemComponent");
                                if (systemComponent != null && Convert.ToInt32(systemComponent) == 1)
                                    continue;
                                
                                var parentKeyName = subkey.GetValue("ParentKeyName");
                                if (parentKeyName != null && !string.IsNullOrEmpty(parentKeyName.ToString()))
                                    continue;
                                    
                                string? displayName = subkey.GetValue("DisplayName")?.ToString();
                                if (string.IsNullOrWhiteSpace(displayName)) continue;
                                
                                string? uninstallString = subkey.GetValue("UninstallString")?.ToString();
                                if (string.IsNullOrWhiteSpace(uninstallString)) continue;

                                if (displayName.Contains("Security Update") || 
                                    displayName.Contains("Update for Microsoft") || 
                                    displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                
                                string publisher = subkey.GetValue("Publisher")?.ToString() ?? "Unbekannt";
                                string displayVersion = subkey.GetValue("DisplayVersion")?.ToString() ?? "Unbekannt";
                                string installDateStr = subkey.GetValue("InstallDate")?.ToString() ?? "Unbekannt";
                                
                                long estimatedSizeKb = 0;
                                var sizeVal = subkey.GetValue("EstimatedSize");
                                if (sizeVal != null)
                                {
                                    try
                                    {
                                        estimatedSizeKb = Convert.ToInt64(sizeVal);
                                    }
                                    catch {}
                                }
                                
                                string estimatedSizeStr = "Unbekannt";
                                if (estimatedSizeKb > 0)
                                {
                                    double sizeMb = estimatedSizeKb / 1024.0;
                                    if (sizeMb >= 1024)
                                    {
                                        estimatedSizeStr = $"{sizeMb / 1024.0:F2} GB";
                                    }
                                    else
                                    {
                                        estimatedSizeStr = $"{sizeMb:F1} MB";
                                    }
                                }
                                
                                apps.Add(new SoftwareItem
                                {
                                    Name = displayName,
                                    Publisher = publisher,
                                    DisplayVersion = displayVersion,
                                    UninstallString = uninstallString,
                                    InstallDate = installDateStr,
                                    EstimatedSize = estimatedSizeStr,
                                    RegistryPath = $"{rootKey.Name}\\{subKeyPath}\\{subkeyName}",
                                    IsUwp = false,
                                    PackageFullName = string.Empty
                                });
                            }
                        }
                        catch
                        {
                            // Ignoriere Einzelfehler beim Lesen von Subkeys
                        }
                    }
                }
            }
            catch
            {
                // Ignoriere Fehler beim Öffnen des Hauptpfads
            }
        }

        private (string Command, string Arguments) ParseUninstallString(string uninstallString)
        {
            uninstallString = uninstallString.Trim();
            if (string.IsNullOrEmpty(uninstallString))
                return (string.Empty, string.Empty);
                
            string command;
            string arguments = string.Empty;
            
            if (uninstallString.StartsWith("\""))
            {
                int nextQuote = uninstallString.IndexOf("\"", 1);
                if (nextQuote > 0)
                {
                    command = uninstallString.Substring(1, nextQuote - 1);
                    arguments = uninstallString.Substring(nextQuote + 1).Trim();
                }
                else
                {
                    command = uninstallString.Replace("\"", "");
                }
            }
            else
            {
                int firstSpace = uninstallString.IndexOf(" ");
                if (firstSpace > 0)
                {
                    command = uninstallString.Substring(0, firstSpace);
                    arguments = uninstallString.Substring(firstSpace + 1).Trim();
                }
                else
                {
                    command = uninstallString;
                }
            }
            
            if (command.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || command.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                if (arguments.Contains("/I", StringComparison.OrdinalIgnoreCase))
                {
                    arguments = arguments.Replace("/I", "/X", StringComparison.OrdinalIgnoreCase);
                }
                
                if (!arguments.Contains("/qn", StringComparison.OrdinalIgnoreCase) && !arguments.Contains("/qb", StringComparison.OrdinalIgnoreCase))
                {
                    arguments += " /qn /norestart";
                }
            }
            
            return (command, arguments);
        }

        private void CleanLeftoverFiles(string appName, string publisher, Action<string> log)
        {
            log("[Dateibereinigung] Suche nach verwaisten AppData/ProgramFiles Ordnern...");
            
            var searchDirs = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // Roaming
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), // Local
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) // C:\ProgramData
            };
            
            searchDirs = searchDirs.Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList();
            
            var appKeywords = ExtractKeywords(appName);
            var publisherKeywords = ExtractKeywords(publisher);
            
            if (appKeywords.Count == 0) return;
            
            foreach (var baseDir in searchDirs)
            {
                try
                {
                    if (!Directory.Exists(baseDir)) continue;
                    
                    foreach (var dir in Directory.GetDirectories(baseDir))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(dirName)) continue;
                        
                        bool isMatch = false;
                        
                        if (dirName.Equals(appName, StringComparison.OrdinalIgnoreCase) || 
                            (appName.Length > 3 && dirName.Contains(appName, StringComparison.OrdinalIgnoreCase)))
                        {
                            isMatch = true;
                        }
                        else
                        {
                            foreach (var keyword in appKeywords)
                            {
                                if (keyword.Length > 2 && dirName.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                    break;
                                }
                            }
                        }
                        
                        if (isMatch)
                        {
                            log($"[Dateibereinigung] Verwaister Ordner gefunden: {dir}");
                            try
                            {
                                Directory.Delete(dir, true);
                                log($"[Dateibereinigung] Ordner gelöscht: {dirName}");
                            }
                            catch (Exception ex)
                            {
                                log($"[Dateibereinigung] Fehler beim Löschen von {dirName}: {ex.Message}");
                            }
                        }
                        else if (publisherKeywords.Count > 0)
                        {
                            bool matchesPublisher = false;
                            foreach (var pubKeyword in publisherKeywords)
                            {
                                if (pubKeyword.Length > 2 && dirName.Equals(pubKeyword, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchesPublisher = true;
                                    break;
                                }
                            }
                            
                            if (matchesPublisher)
                            {
                                try
                                {
                                    foreach (var subDir in Directory.GetDirectories(dir))
                                    {
                                        string subDirName = Path.GetFileName(subDir);
                                        bool subMatch = false;
                                        
                                        if (subDirName.Equals(appName, StringComparison.OrdinalIgnoreCase) || 
                                            (appName.Length > 3 && subDirName.Contains(appName, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            subMatch = true;
                                        }
                                        else
                                        {
                                            foreach (var keyword in appKeywords)
                                            {
                                                if (keyword.Length > 2 && subDirName.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    subMatch = true;
                                                    break;
                                                }
                                            }
                                        }
                                        
                                        if (subMatch)
                                        {
                                            log($"[Dateibereinigung] Verwaister Unterordner in Hersteller-Ordner gefunden: {subDir}");
                                            try
                                            {
                                                Directory.Delete(subDir, true);
                                                log($"[Dateibereinigung] Unterordner gelöscht: {subDirName}");
                                                
                                                if (Directory.GetDirectories(dir).Length == 0 && 
                                                    Directory.GetFiles(dir).Length == 0)
                                                {
                                                    log($"[Dateibereinigung] Leerer Hersteller-Ordner wird entfernt: {dir}");
                                                    Directory.Delete(dir, false);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                log($"[Dateibereinigung] Fehler beim Löschen von {subDirName}: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                                catch {}
                            }
                        }
                    }
                }
                catch
                {
                    // Ignoriere Zugriffsfehler auf Ordnern
                }
            }
        }

        private void CleanLeftoverRegistry(SoftwareItem app, Action<string> log)
        {
            log("[Registrybereinigung] Suche nach verbliebenen Registrierungsschlüsseln...");
            
            if (!string.IsNullOrEmpty(app.RegistryPath))
            {
                try
                {
                    string fullPath = app.RegistryPath;
                    RegistryKey? rootKey = null;
                    string subKeyPath = string.Empty;
                    
                    if (fullPath.StartsWith("HKEY_LOCAL_MACHINE\\"))
                    {
                        rootKey = Registry.LocalMachine;
                        subKeyPath = fullPath.Substring("HKEY_LOCAL_MACHINE\\".Length);
                    }
                    else if (fullPath.StartsWith("HKEY_CURRENT_USER\\"))
                    {
                        rootKey = Registry.CurrentUser;
                        subKeyPath = fullPath.Substring("HKEY_CURRENT_USER\\".Length);
                    }
                    
                    if (rootKey != null && !string.IsNullOrEmpty(subKeyPath))
                    {
                        using (RegistryKey? check = rootKey.OpenSubKey(subKeyPath))
                        {
                            if (check != null)
                            {
                                log($"[Registrybereinigung] Verwaister Deinstallationsschlüssel gefunden: {fullPath}");
                                
                                int lastSlash = subKeyPath.LastIndexOf('\\');
                                if (lastSlash > 0)
                                {
                                    string parentPath = subKeyPath.Substring(0, lastSlash);
                                    string keyToDelete = subKeyPath.Substring(lastSlash + 1);
                                    using (RegistryKey? parentKey = rootKey.OpenSubKey(parentPath, true))
                                    {
                                        parentKey?.DeleteSubKeyTree(keyToDelete, false);
                                        log($"[Registrybereinigung] Deinstallationsschlüssel erfolgreich entfernt.");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log($"[Registrybereinigung] Fehler beim Entfernen des Deinstallationsschlüssels: {ex.Message}");
                }
            }
            
            var appKeywords = ExtractKeywords(app.Name);
            var publisherKeywords = ExtractKeywords(app.Publisher);
            
            if (appKeywords.Count == 0) return;
            
            var softwareRoots = new List<(RegistryKey Root, string Path)>
            {
                (Registry.LocalMachine, @"SOFTWARE"),
                (Registry.LocalMachine, @"SOFTWARE\Wow6432Node"),
                (Registry.CurrentUser, @"SOFTWARE")
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
                            if (IsProtected(subkeyName)) continue;
                            
                            bool isMatch = false;
                            
                            if (subkeyName.Equals(app.Name, StringComparison.OrdinalIgnoreCase) || 
                                (app.Name.Length > 3 && subkeyName.Contains(app.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                isMatch = true;
                            }
                            else
                            {
                                foreach (var keyword in appKeywords)
                                {
                                    if (keyword.Length > 2 && subkeyName.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isMatch = true;
                                        break;
                                    }
                                }
                            }
                            
                            if (isMatch)
                            {
                                log($"[Registrybereinigung] Verwaister Schlüssel gefunden: {root.Root.Name}\\{root.Path}\\{subkeyName}");
                                try
                                {
                                    key.DeleteSubKeyTree(subkeyName, false);
                                    log($"[Registrybereinigung] Registry-Schlüssel erfolgreich entfernt.");
                                }
                                catch (Exception ex)
                                {
                                    log($"[Registrybereinigung] Fehler beim Löschen: {ex.Message}");
                                }
                            }
                            else if (publisherKeywords.Count > 0)
                            {
                                bool matchesPublisher = false;
                                foreach (var pubKeyword in publisherKeywords)
                                {
                                    if (pubKeyword.Length > 2 && subkeyName.Equals(pubKeyword, StringComparison.OrdinalIgnoreCase))
                                    {
                                        matchesPublisher = true;
                                        break;
                                    }
                                }
                                
                                if (matchesPublisher)
                                {
                                    try
                                    {
                                        using (RegistryKey? pubKey = key.OpenSubKey(subkeyName, true))
                                        {
                                            if (pubKey != null)
                                            {
                                                foreach (string childKeyName in pubKey.GetSubKeyNames())
                                                {
                                                    bool childMatch = false;
                                                    if (childKeyName.Equals(app.Name, StringComparison.OrdinalIgnoreCase) || 
                                                        (app.Name.Length > 3 && childKeyName.Contains(app.Name, StringComparison.OrdinalIgnoreCase)))
                                                    {
                                                        childMatch = true;
                                                    }
                                                    else
                                                    {
                                                        foreach (var keyword in appKeywords)
                                                        {
                                                            if (keyword.Length > 2 && childKeyName.Equals(keyword, StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                childMatch = true;
                                                                break;
                                                            }
                                                        }
                                                    }
                                                    
                                                    if (childMatch)
                                                    {
                                                        log($"[Registrybereinigung] Verwaister Schlüssel im Hersteller-Pfad gefunden: {root.Root.Name}\\{root.Path}\\{subkeyName}\\{childKeyName}");
                                                        try
                                                        {
                                                            pubKey.DeleteSubKeyTree(childKeyName, false);
                                                            log($"[Registrybereinigung] Schlüssel erfolgreich entfernt.");
                                                            
                                                            if (pubKey.SubKeyCount == 0 && pubKey.ValueCount == 0)
                                                            {
                                                                log($"[Registrybereinigung] Leerer Hersteller-Schlüssel wird entfernt: {subkeyName}");
                                                                key.DeleteSubKeyTree(subkeyName, false);
                                                            }
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            log($"[Registrybereinigung] Fehler beim Löschen: {ex.Message}");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch {}
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignoriere Zugriffsfehler auf Registry-Stämmen
                }
            }
        }

        private List<string> ExtractKeywords(string text)
        {
            var keywords = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return keywords;
            
            var parts = text.Split(new[] { ' ', '-', ',', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                string cleaned = part.Trim().ToLowerInvariant();
                
                if (cleaned.Length <= 2) continue;
                if (double.TryParse(cleaned, out _)) continue;
                if (cleaned == "corporation" || cleaned == "software" || cleaned == "limited" || 
                    cleaned == "gmbh" || cleaned == "inc" || cleaned == "llc" || cleaned == "co") 
                    continue;
                
                if (IsProtected(cleaned)) continue;
                
                keywords.Add(cleaned);
            }
            return keywords.Distinct().ToList();
        }

        private bool IsProtected(string name)
        {
            string lower = name.ToLowerInvariant();
            foreach (var p in ProtectedKeywords)
            {
                if (lower.Contains(p)) return true;
            }
            return false;
        }
    }
}

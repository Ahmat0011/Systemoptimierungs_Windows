using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SystemOptimierer.Core;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public class PerformanceService : IPerformanceService
    {
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        // ============================================================
        // Systemprozesse & Sicherheitssoftware ausschließen:
        // 1. PID 0 (Idle), PID 4 (System) und die eigene App.
        // 2. Kritische Windows-Dienste (lsass, csrss, dwm, audiodg -> Schutz vor Manipulation/Rucklern).
        // 3. Antivirus-Prozesse (Bitdefender, Windows Defender etc.) -> Deren Self-Defense
        //    wertet OpenProcess-Aufrufe sonst als Manipulationsversuch.
        // ============================================================
        private static readonly HashSet<int> ExcludedPids = new() { 0, 4, Environment.ProcessId };

        private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SystemOptimierer", "System", "Idle", "Registry", "Memory Compression",
            "csrss", "lsass", "smss", "services", "wininit", "winlogon", "fontdrvhost", "dwminit", "dwm", "audiodg",
            "bdservicehost", "bdagent", "bdntwrk", "bdredline", "bduserhost", "bdvpnservice", "bdvpnapp",
            "MsMpEng", "NisSrv", "MpDefenderCoreService", "SecurityHealthService", "smartscreen", "SenseCncProxy", "SenseIR"
        };

        // ============================================================
        // Autostart-Orte. "Run32" = 32-Bit-Programme auf 64-Bit-Windows (WOW6432Node) –
        // dort tragen sich viele Programme ein, die bisher gar nicht angezeigt wurden.
        // StartupApproved = der Ort, an dem Task-Manager/Einstellungen Autostarts deaktivieren.
        // ============================================================
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string BackupKey = @"SOFTWARE\SystemOptimierer\StartupBackup";
        private const string BackupKey32 = @"SOFTWARE\SystemOptimierer\StartupBackup32";
        private const string ApprovedRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ApprovedRunKey32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

        private sealed record StartupLocation(string Label, RegistryKey Root, string RunPath, string BackupPath, string ApprovedPath);

        private static readonly StartupLocation[] StartupLocations =
        {
            new("HKCU", Registry.CurrentUser, RunKey, BackupKey, ApprovedRunKey),
            new("HKLM", Registry.LocalMachine, RunKey, BackupKey, ApprovedRunKey),
            new("HKLM (32-Bit)", Registry.LocalMachine, RunKey32, BackupKey32, ApprovedRunKey32),
        };

        public async Task BoostRamAsync(Action<string> log, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(log);

            log("--- SYSTEM ARBEITSSPEICHER (RAM) OPTIMIERUNG GESTARTET ---");
            log("Scanne aktive Prozesse und minimiere ungenutzte Working-Sets...");

            var memoryBefore = GetMemoryStatus();
            var workingSetBefore = new Dictionary<int, long>();
            int successCount = 0;
            int failCount = 0;
            int skippedCount = 0;

            var steps = new StepProgress(progress, 1);
            steps.Begin(0, "Arbeitsspeicher optimieren", "Prozesse werden ermittelt...");

            // Prozess-Iteration auf Hintergrund-Thread ausführen, damit die UI nicht blockiert
            await Task.Run(() =>
            {
                var processes = Process.GetProcesses();
                for (int i = 0; i < processes.Length; i++)
                {
                    var proc = processes[i];
                    try
                    {
                        if (ct.IsCancellationRequested) continue;

                        if (i % 10 == 0 && processes.Length > 0)
                        {
                            steps.Report(i / (double)processes.Length * 0.9, $"Prozess {i + 1} von {processes.Length}");
                        }

                        // System- und Sicherheits-Prozesse (z. B. Bitdefender) strikt meiden
                        if (ExcludedPids.Contains(proc.Id) || ExcludedProcessNames.Contains(proc.ProcessName))
                        {
                            skippedCount++;
                            continue;
                        }

                        long workingSet = proc.WorkingSet64;
                        if (workingSet > 0) workingSetBefore[proc.Id] = workingSet;

                        IntPtr hProcess = OpenProcess(
                            PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, proc.Id);

                        if (hProcess != IntPtr.Zero)
                        {
                            try
                            {
                                if (EmptyWorkingSet(hProcess))
                                    successCount++;
                                else
                                    failCount++;
                            }
                            finally
                            {
                                CloseHandle(hProcess);
                            }
                        }
                        else
                        {
                            failCount++;
                        }
                    }
                    catch
                    {
                        failCount++;
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }, ct);

            if (ct.IsCancellationRequested) return;

            steps.Report(0.95, "Speichermanager wird aktualisiert...");
            log("Speichermanager wird aktualisiert...");
            await Task.Delay(500, ct);

            long finalMem = await Task.Run(() =>
            {
                long sum = 0;
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (!ExcludedPids.Contains(proc.Id) && workingSetBefore.ContainsKey(proc.Id))
                            sum += proc.WorkingSet64;
                    }
                    catch
                    {
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
                return sum;
            }, ct);

            long initialMem = 0;
            foreach (long value in workingSetBefore.Values) initialMem += value;

            double freedMb = Math.Max(0, initialMem - finalMem) / (1024.0 * 1024.0);
            var memoryAfter = GetMemoryStatus();

            steps.Complete("Optimierung abgeschlossen");
            log("[Ergebnis] RAM-Optimierung abgeschlossen.");
            log($"[Status] {successCount} Prozesse optimiert, {failCount} übersprungen (geschützt), {skippedCount} Prozesse ausgelassen.");
            log($"[Speicher] Working-Sets um ca. {freedMb:F1} MB verkleinert.");
            if (memoryBefore.HasValue && memoryAfter.HasValue)
            {
                var before = memoryBefore.Value;
                var after = memoryAfter.Value;
                log($"[Speicher] Freier Arbeitsspeicher: vorher {ToGb(before.AvailPhys):F2} GB, jetzt {ToGb(after.AvailPhys):F2} GB " +
                    $"von {ToGb(after.TotalPhys):F2} GB (Auslastung {before.MemoryLoad} % -> {after.MemoryLoad} %).\n");
            }
            else
            {
                log(string.Empty);
            }
        }

        private static MemoryStatusEx? GetMemoryStatus()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? status : null;
        }

        private static double ToGb(ulong bytes) => bytes / (1024.0 * 1024.0 * 1024.0);

        public List<StartupItem> GetStartupItems()
        {
            var items = new List<StartupItem>();
            var seenInLocation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var location in StartupLocations)
            {
                seenInLocation.Clear();

                // Aktive Einträge (können aber per Task-Manager deaktiviert sein)
                foreach (var (name, value) in ReadValues(location.Root, location.RunPath))
                {
                    seenInLocation.Add(name);
                    bool disabledByWindows = IsDisabledInStartupApproved(location, name);
                    items.Add(new StartupItem
                    {
                        Name = name,
                        Path = value,
                        RegistryKeyPath = location.Label,
                        IsEnabled = !disabledByWindows,
                        IsDisabledByWindows = disabledByWindows
                    });
                }

                // Von dieser App deaktivierte Einträge (Sicherung) – nur aufnehmen,
                // wenn sich das Programm nicht inzwischen selbst wieder in "Run" eingetragen hat.
                foreach (var (name, value) in ReadValues(location.Root, location.BackupPath))
                {
                    if (!seenInLocation.Add(name)) continue;

                    items.Add(new StartupItem
                    {
                        Name = name,
                        Path = value,
                        RegistryKeyPath = location.Label,
                        IsEnabled = false
                    });
                }
            }

            return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task ToggleStartupItemAsync(StartupItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            await Task.Run(() =>
            {
                var location = StartupLocations.FirstOrDefault(l => l.Label == item.RegistryKeyPath)
                    ?? throw new InvalidOperationException($"Unbekannter Autostart-Bereich '{item.RegistryKeyPath}'.");

                try
                {
                    if (item.IsDisabledByWindows)
                    {
                        // Wurde per Task-Manager deaktiviert: Sperre in StartupApproved entfernen
                        ClearStartupApproved(location, item.Name);
                        item.IsDisabledByWindows = false;
                        item.IsEnabled = true;
                        return;
                    }

                    string sourcePath = item.IsEnabled ? location.RunPath : location.BackupPath;
                    string targetPath = item.IsEnabled ? location.BackupPath : location.RunPath;

                    using var srcKey = location.Root.OpenSubKey(sourcePath, writable: true);
                    using var dstKey = location.Root.CreateSubKey(targetPath, writable: true);

                    if (srcKey == null || dstKey == null)
                        throw new UnauthorizedAccessException("Registry-Zugriff verweigert (Schreibrechte erforderlich).");

                    object value = srcKey.GetValue(item.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                        ?? throw new InvalidOperationException("Der Registry-Wert konnte nicht gelesen werden.");

                    dstKey.SetValue(item.Name, value, srcKey.GetValueKind(item.Name));
                    srcKey.DeleteValue(item.Name, throwOnMissingValue: false);

                    // Beim Aktivieren eine eventuelle Task-Manager-Sperre mit entfernen,
                    // sonst stünde der Eintrag zwar wieder in "Run", würde aber nicht starten.
                    if (!item.IsEnabled)
                    {
                        ClearStartupApproved(location, item.Name);
                    }

                    item.IsEnabled = !item.IsEnabled;
                    item.IsDisabledByWindows = false;
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"Fehler beim Ändern des Autostarts für '{item.Name}': {ex.Message}", ex);
                }
            });
        }

        private static List<(string Name, string Value)> ReadValues(RegistryKey root, string path)
        {
            var result = new List<(string, string)>();
            try
            {
                using var key = root.OpenSubKey(path, writable: false);
                if (key == null) return result;

                foreach (string valueName in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(valueName)) continue; // "(Standard)" ist kein Autostart
                    string value = key.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
                    result.Add((valueName, value));
                }
            }
            catch
            {
            }
            return result;
        }

        /// <summary>
        /// StartupApproved-Werte: Erstes Byte gerade (z. B. 0x02) = aktiviert, ungerade (z. B. 0x03) = deaktiviert.
        /// </summary>
        private static bool IsDisabledInStartupApproved(StartupLocation location, string name)
        {
            try
            {
                using var key = location.Root.OpenSubKey(location.ApprovedPath, writable: false);
                return key?.GetValue(name) is byte[] data && data.Length > 0 && (data[0] & 0x01) == 0x01;
            }
            catch
            {
                return false;
            }
        }

        private static void ClearStartupApproved(StartupLocation location, string name)
        {
            using var key = location.Root.OpenSubKey(location.ApprovedPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}

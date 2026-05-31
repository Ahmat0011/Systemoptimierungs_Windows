using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SystemOptimierer.Models;

namespace SystemOptimierer.Services
{
    public class PerformanceService : IPerformanceService
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_SET_QUOTA = 0x0100;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;

        public async Task BoostRamAsync(Action<string> log, CancellationToken ct)
        {
            log("--- SYSTEM ARBEITSSPEICHER (RAM) OPTIMIERUNG GESTARTET ---");
            log("Scanne aktive Prozesse und minimiere ungenutzte Working-Sets...");

            long initialMem = 0;
            long finalMem = 0;
            int successCount = 0;
            int failCount = 0;

            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    initialMem += proc.WorkingSet64;

                    IntPtr hProcess = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, proc.Id);
                    if (hProcess == IntPtr.Zero)
                    {
                        hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, proc.Id);
                    }

                    if (hProcess != IntPtr.Zero)
                    {
                        if (EmptyWorkingSet(hProcess))
                        {
                            successCount++;
                        }
                        else
                        {
                            failCount++;
                        }
                        CloseHandle(hProcess);
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
            }

            log("Speichermanager wird aktualisiert...");
            await Task.Delay(500, ct);

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    finalMem += proc.WorkingSet64;
                }
                catch {}
            }

            long freedBytes = initialMem - finalMem;
            double freedMb = freedBytes / (1024.0 * 1024.0);
            if (freedMb < 0) freedMb = 0;

            log($"[Ergebnis] RAM-Optimierung abgeschlossen.");
            log($"[Status] {successCount} Prozesse erfolgreich optimiert, {failCount} Prozesse übersprungen (geschützt/inaktiv).");
            log($"[Speicher] Ca. {freedMb:F1} MB RAM freigegeben.\n");
        }

        public List<StartupItem> GetStartupItems()
        {
            var items = new List<StartupItem>();

            // HKCU
            ReadRunKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true, "HKCU", items);
            ReadRunKey(Registry.CurrentUser, @"SOFTWARE\SystemOptimierer\StartupBackup", false, "HKCU", items);

            // HKLM
            ReadRunKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true, "HKLM", items);
            ReadRunKey(Registry.LocalMachine, @"SOFTWARE\SystemOptimierer\StartupBackup", false, "HKLM", items);

            return items.OrderBy(i => i.Name).ToList();
        }

        public async Task ToggleStartupItemAsync(StartupItem item)
        {
            await Task.Run(() =>
            {
                RegistryKey rootKey = item.RegistryKeyPath == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                string sourcePath = item.IsEnabled 
                    ? @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" 
                    : @"SOFTWARE\SystemOptimierer\StartupBackup";
                string targetPath = item.IsEnabled 
                    ? @"SOFTWARE\SystemOptimierer\StartupBackup" 
                    : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

                try
                {
                    using (var srcKey = rootKey.OpenSubKey(sourcePath, true))
                    using (var dstKey = rootKey.CreateSubKey(targetPath, true))
                    {
                        if (srcKey != null && dstKey != null)
                        {
                            object? value = srcKey.GetValue(item.Name);
                            if (value != null)
                            {
                                dstKey.SetValue(item.Name, value, srcKey.GetValueKind(item.Name));
                                srcKey.DeleteValue(item.Name, false);
                                item.IsEnabled = !item.IsEnabled;
                            }
                            else
                            {
                                throw new InvalidOperationException("Der Registry-Wert konnte nicht gelesen werden.");
                            }
                        }
                        else
                        {
                            throw new UnauthorizedAccessException("Registry-Zugriff verweigert (Schreibrechte erforderlich).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Fehler beim Ändern des Autostarts für '{item.Name}': {ex.Message}");
                }
            });
        }

        private void ReadRunKey(RegistryKey rootKey, string subKeyPath, bool isEnabled, string keyPathLabel, List<StartupItem> items)
        {
            try
            {
                using (var key = rootKey.OpenSubKey(subKeyPath))
                {
                    if (key == null) return;
                    foreach (string valueName in key.GetValueNames())
                    {
                        string value = key.GetValue(valueName)?.ToString() ?? string.Empty;
                        items.Add(new StartupItem
                        {
                            Name = valueName,
                            Path = value,
                            RegistryKeyPath = keyPathLabel,
                            IsEnabled = isEnabled
                        });
                    }
                }
            }
            catch
            {
                // Ignoriere fehlende Rechte / nicht vorhandene Registry Pfade
            }
        }
    }
}

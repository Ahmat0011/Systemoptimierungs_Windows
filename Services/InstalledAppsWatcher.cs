using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SystemOptimierer.Services
{
    /// <summary>
    /// Überwacht die Uninstall- und Store-Paket-Zweige der Registry per RegNotifyChangeKeyValue.
    /// Änderungen werden entprellt (Installer schreiben viele Werte nacheinander), damit pro
    /// Installation/Deinstallation nur eine Aktualisierung ausgelöst wird.
    /// </summary>
    public sealed class InstalledAppsWatcher : IInstalledAppsWatcher
    {
        private const uint RegNotifyChangeName = 0x1;
        private const uint RegNotifyChangeLastSet = 0x4;

        private static readonly TimeSpan DesktopDebounce = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan StoreDebounce = TimeSpan.FromMilliseconds(3000);

        private readonly ManualResetEvent _stopSignal = new(false);
        private readonly object _sync = new();
        private Thread? _thread;
        private Timer? _desktopTimer;
        private Timer? _storeTimer;
        private bool _disposed;

        public event EventHandler? DesktopAppsChanged;
        public event EventHandler? StoreAppsChanged;

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _thread != null) return;

                _desktopTimer = new Timer(_ => Raise(DesktopAppsChanged));
                _storeTimer = new Timer(_ => Raise(StoreAppsChanged));

                // Eigener Thread: bei asynchronem RegNotifyChangeKeyValue muss der registrierende Thread weiterleben.
                _thread = new Thread(WatchLoop) { IsBackground = true, Name = "InstalledAppsWatcher" };
                _thread.Start();
            }
        }

        private void Raise(EventHandler? handler)
        {
            if (_disposed) return;
            try { handler?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private sealed class WatchedKey : IDisposable
        {
            public required RegistryKey Key { get; init; }
            public required bool WatchSubtree { get; init; }
            public required uint Filter { get; init; }
            public required bool IsStore { get; init; }
            public AutoResetEvent Signal { get; } = new(false);

            public bool Arm() =>
                RegNotifyChangeKeyValue(Key.Handle, WatchSubtree, Filter, Signal.SafeWaitHandle, true) == 0;

            public void Dispose()
            {
                Key.Dispose();
                Signal.Dispose();
            }
        }

        private void WatchLoop()
        {
            var watched = OpenWatchedKeys();
            try
            {
                watched.RemoveAll(w =>
                {
                    if (w.Arm()) return false;
                    w.Dispose();
                    return true;
                });
                if (watched.Count == 0) return;

                var handles = watched.Select(w => (WaitHandle)w.Signal).Append(_stopSignal).ToArray();
                while (true)
                {
                    int index = WaitHandle.WaitAny(handles);
                    if (index < 0 || index >= watched.Count) break; // Stop-Signal

                    var fired = watched[index];
                    // Zuerst neu registrieren, damit keine direkt folgende Änderung verloren geht.
                    // Schlägt das fehl (z. B. Schlüssel gelöscht), bleibt dieser Schlüssel einfach stumm.
                    fired.Arm();

                    if (fired.IsStore) _storeTimer?.Change(StoreDebounce, Timeout.InfiniteTimeSpan);
                    else _desktopTimer?.Change(DesktopDebounce, Timeout.InfiniteTimeSpan);
                }
            }
            catch (ObjectDisposedException) { }
            finally
            {
                foreach (var w in watched) w.Dispose();
            }
        }

        private static List<WatchedKey> OpenWatchedKeys()
        {
            var result = new List<WatchedKey>();
            const uint desktopFilter = RegNotifyChangeName | RegNotifyChangeLastSet;

            // Desktop-Programme: gesamter Unterbaum (neue/gelöschte Einträge und geänderte Werte wie DisplayVersion)
            TryAdd(result, RegistryHive.LocalMachine, RegistryView.Registry64, AppRegistryPaths.Uninstall, true, desktopFilter, false);
            if (Environment.Is64BitOperatingSystem)
                TryAdd(result, RegistryHive.LocalMachine, RegistryView.Registry32, AppRegistryPaths.Uninstall, true, desktopFilter, false);
            TryAdd(result, RegistryHive.CurrentUser, RegistryView.Default, AppRegistryPaths.Uninstall, true, desktopFilter, false);

            // Store-Apps: nur hinzugefügte/entfernte Paket-Schlüssel direkt unterhalb
            TryAdd(result, RegistryHive.CurrentUser, RegistryView.Default, AppRegistryPaths.StorePackagesCurrentUser, false, RegNotifyChangeName, true);
            TryAdd(result, RegistryHive.LocalMachine, RegistryView.Registry64, AppRegistryPaths.StorePackagesAllUsers, false, RegNotifyChangeName, true);

            return result;
        }

        private static void TryAdd(List<WatchedKey> list, RegistryHive hive, RegistryView view, string path,
            bool watchSubtree, uint filter, bool isStore)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                var key = baseKey.OpenSubKey(path, writable: false);
                if (key == null) return;
                list.Add(new WatchedKey { Key = key, WatchSubtree = watchSubtree, Filter = filter, IsStore = isStore });
            }
            catch { }
        }

        public void Dispose()
        {
            Thread? thread;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                thread = _thread;
            }

            try { _stopSignal.Set(); } catch { }
            bool stopped = thread == null || thread.Join(TimeSpan.FromSeconds(2));

            _desktopTimer?.Dispose();
            _storeTimer?.Dispose();
            if (stopped) _stopSignal.Dispose();
        }

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree,
            uint dwNotifyFilter,
            SafeWaitHandle hEvent,
            [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);
    }
}

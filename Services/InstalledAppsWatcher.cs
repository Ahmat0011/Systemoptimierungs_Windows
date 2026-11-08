using System;
using System.Collections.Generic;
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
        private int _disposed;

        public event EventHandler? DesktopAppsChanged;
        public event EventHandler? StoreAppsChanged;

        public void Start()
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;

                _desktopTimer = new Timer(_ => Raise(DesktopAppsChanged), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _storeTimer = new Timer(_ => Raise(StoreAppsChanged), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                // Eigener Thread: bei asynchronem RegNotifyChangeKeyValue muss der registrierende Thread weiterleben.
                _thread = new Thread(WatchLoop)
                {
                    IsBackground = true,
                    Name = "InstalledAppsWatcher"
                };
                _thread.Start();
            }
        }

        private void Raise(EventHandler? handler)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                handler?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
            }
        }

        private sealed class WatchedKey : IDisposable
        {
            public required RegistryKey Key { get; init; }
            public required bool WatchSubtree { get; init; }
            public required uint Filter { get; init; }
            public required bool IsStore { get; init; }
            public AutoResetEvent Signal { get; } = new(false);

            public bool Arm()
            {
                try
                {
                    return RegNotifyChangeKeyValue(Key.Handle, WatchSubtree, Filter, Signal.SafeWaitHandle, true) == 0;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            public void Dispose()
            {
                try { Key.Dispose(); } catch { }
                try { Signal.Dispose(); } catch { }
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

                // _stopSignal auf Index 0: WaitHandle.WaitAny gibt bei gleichzeitig signalisierten
                // Handles immer den niedrigsten Index zurück -> Stop hat sofort Vorrang.
                var handles = new WaitHandle[watched.Count + 1];
                handles[0] = _stopSignal;
                for (int i = 0; i < watched.Count; i++)
                {
                    handles[i + 1] = watched[i].Signal;
                }

                while (Volatile.Read(ref _disposed) == 0)
                {
                    int index = WaitHandle.WaitAny(handles);
                    if (index <= 0 || index > watched.Count || Volatile.Read(ref _disposed) != 0)
                        break; // Stop-Signal oder Timeout/Abbruch

                    var fired = watched[index - 1];
                    // Zuerst neu registrieren, damit keine direkt folgende Änderung verloren geht.
                    // Schlägt das fehl (z. B. Schlüssel gelöscht), bleibt dieser Schlüssel einfach stumm.
                    fired.Arm();

                    try
                    {
                        if (fired.IsStore)
                            _storeTimer?.Change(StoreDebounce, Timeout.InfiniteTimeSpan);
                        else
                            _desktopTimer?.Change(DesktopDebounce, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                foreach (var w in watched)
                {
                    w.Dispose();
                }
            }
        }

        private static List<WatchedKey> OpenWatchedKeys()
        {
            var result = new List<WatchedKey>(5);
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

        private static void TryAdd(
            List<WatchedKey> list,
            RegistryHive hive,
            RegistryView view,
            string path,
            bool watchSubtree,
            uint filter,
            bool isStore)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                var key = baseKey.OpenSubKey(path, writable: false);
                if (key == null) return;
                list.Add(new WatchedKey
                {
                    Key = key,
                    WatchSubtree = watchSubtree,
                    Filter = filter,
                    IsStore = isStore
                });
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Thread? thread;
            Timer? desktopTimer;
            Timer? storeTimer;

            lock (_sync)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                thread = _thread;
                desktopTimer = _desktopTimer;
                storeTimer = _storeTimer;
                _thread = null;
                _desktopTimer = null;
                _storeTimer = null;
            }

            try { _stopSignal.Set(); } catch { }
            bool stopped = thread == null || thread.Join(TimeSpan.FromSeconds(2));

            try { desktopTimer?.Dispose(); } catch { }
            try { storeTimer?.Dispose(); } catch { }
            if (stopped)
            {
                try { _stopSignal.Dispose(); } catch { }
            }
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

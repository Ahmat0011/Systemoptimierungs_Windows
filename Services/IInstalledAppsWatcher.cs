using System;

namespace SystemOptimierer.Services
{
    /// <summary>
    /// Meldet, sobald Programme oder Store-Apps installiert, aktualisiert oder deinstalliert wurden –
    /// unabhängig davon, ob die Änderung aus dieser App oder von außen (Setup, winget, Store) kam.
    /// </summary>
    public interface IInstalledAppsWatcher : IDisposable
    {
        /// <summary>Klassische Desktop-Programme (Uninstall-Registry) haben sich geändert. Wird auf einem Hintergrund-Thread ausgelöst.</summary>
        event EventHandler? DesktopAppsChanged;

        /// <summary>Store-/UWP-Pakete haben sich geändert. Wird auf einem Hintergrund-Thread ausgelöst.</summary>
        event EventHandler? StoreAppsChanged;

        void Start();
    }
}

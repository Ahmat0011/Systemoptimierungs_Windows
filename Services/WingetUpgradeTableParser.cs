using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SystemOptimierer.Services
{
    public sealed record WingetUpgradeEntry(string Name, string Id, string CurrentVersion, string AvailableVersion);

    /// <summary>
    /// Liest die Tabelle von "winget upgrade" zeilenweise anhand der Spaltenpositionen der Kopfzeile.
    /// Ein Aufteilen an Leerzeichen ist unzuverlässig: Die längste Spalte wird nur durch EIN
    /// Leerzeichen getrennt, Versionen können "&lt; 1.2" lauten und Namen Wörter wie "Version" enthalten.
    /// Pro winget-Aufruf eine eigene Instanz verwenden (zustandsbehaftet).
    /// </summary>
    public sealed class WingetUpgradeTableParser
    {
        // Kopfzeile in allen Sprachen: <Name> ID Version <Verfügbar> [<Quelle>]
        private static readonly Regex HeaderRegex = new(
            @"(?<name>\S+)\s+(?<id>[Ii][Dd])\s+(?<version>Version)\s+(?<available>\S+)(?:\s+(?<source>\S+))?\s*$",
            RegexOptions.Compiled);

        private int _idStart, _versionStart, _availableStart, _sourceStart = -1;
        private bool _hasHeader;
        private bool _inTable;

        public bool TryParse(string line, out WingetUpgradeEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                _inTable = false;
                return false;
            }

            string trimmed = line.TrimEnd();

            var header = HeaderRegex.Match(trimmed);
            if (header.Success)
            {
                // Positionen relativ zum Beginn der Namensspalte (eventuelle Spinner-Reste davor ignorieren)
                int offset = header.Groups["name"].Index;
                _idStart = header.Groups["id"].Index - offset;
                _versionStart = header.Groups["version"].Index - offset;
                _availableStart = header.Groups["available"].Index - offset;
                _sourceStart = header.Groups["source"].Success ? header.Groups["source"].Index - offset : -1;
                _hasHeader = true;
                _inTable = false;
                return false;
            }

            if (!_hasHeader) return false;

            if (trimmed.Trim().Length > 10 && trimmed.Trim().All(c => c == '-' || c == '─' || c == '━'))
            {
                _inTable = true;
                return false;
            }

            if (!_inTable) return false;

            string id = Column(trimmed, _idStart, _versionStart);
            string current = Column(trimmed, _versionStart, _availableStart);
            string available = Column(trimmed, _availableStart, _sourceStart);
            string name = Column(trimmed, 0, _idStart);

            // Zusammenfassungszeile ("7 Aktualisierungen verfügbar.") o. Ä. beendet die Tabelle
            if (id.Length == 0 || id.Any(char.IsWhiteSpace) || !available.Any(char.IsDigit))
            {
                _inTable = false;
                return false;
            }

            // Von winget abgeschnittene IDs ("Foo.Ba…") wären für "winget upgrade --id --exact" ungültig
            if (id.EndsWith('…')) return false;

            entry = new WingetUpgradeEntry(name, id, current, available);
            return true;
        }

        private static string Column(string line, int start, int end)
        {
            if (start >= line.Length) return string.Empty;
            int length = (end < 0 || end > line.Length ? line.Length : end) - start;
            return length <= 0 ? string.Empty : line.Substring(start, length).Trim();
        }
    }
}

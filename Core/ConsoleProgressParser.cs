using System;
using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SystemOptimierer.Core
{
    /// <summary>
    /// Erkennt Fortschrittszeilen von Konsolenprogrammen und leitet sie an die Fortschrittsanzeige
    /// weiter, statt sie ins Log zu schreiben:
    ///  - DISM:   "[=========          45.3%          ]"
    ///  - sfc:    "Überprüfung 45 % abgeschlossen." / "Verification 45% complete."
    ///  - winget: "  ██████████▒▒▒▒▒▒  12.0 MB / 40.0 MB"  bzw.  "  ████▒▒▒▒  45%"
    /// Alle anderen Zeilen gehen unverändert ins Log.
    /// </summary>
    public sealed class ConsoleProgressParser
    {
        private static readonly Regex DismProgress = new(
            @"^\s*\[[=\s]*(?<p>\d{1,3}(?:[.,]\d+)?)\s*%[=\s]*\]\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SfcProgress = new(
            @"^\s*\S+\s+(?<p>\d{1,3}(?:[.,]\d+)?)\s*%\s+\S+\.?\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Percent = new(
            @"(?<p>\d{1,3}(?:[.,]\d+)?)\s*%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Size = new(
            @"(?<a>\d+(?:[.,]\d+)?)\s*(?<au>B|KB|MB|GB|TB)\s*/\s*(?<b>\d+(?:[.,]\d+)?)\s*(?<bu>B|KB|MB|GB|TB)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // winget zeichnet seinen Fortschrittsbalken mit diesen Blockzeichen (inkl. ░).
        private static readonly SearchValues<char> WingetBarChars = SearchValues.Create("█▒▓░");

        private readonly Action<string> _log;
        private readonly Action<double?, string?> _onProgress;

        /// <param name="log">Ziel für alle normalen Zeilen.</param>
        /// <param name="onProgress">Fortschritt 0.0–1.0 (null = nicht bestimmbar) und optionaler Detailtext.</param>
        public ConsoleProgressParser(Action<string> log, Action<double?, string?> onProgress)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _onProgress = onProgress ?? throw new ArgumentNullException(nameof(onProgress));
        }

        public void Write(string line)
        {
            if (TryParse(line, out double? fraction, out string? detail))
                _onProgress(fraction, detail);
            else
                _log(line);
        }

        public static bool TryParse(string line, out double? fraction, out string? detail)
        {
            fraction = null;
            detail = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            var match = DismProgress.Match(line);
            if (!match.Success) match = SfcProgress.Match(line);
            if (match.Success)
            {
                double pct = ParseNumber(match.Groups["p"].Value);
                if (pct is >= 0 and <= 100)
                {
                    fraction = Math.Clamp(pct / 100.0, 0.0, 1.0);
                    return true;
                }
            }

            // winget-Balken bestehen aus Blockzeichen
            if (line.AsSpan().IndexOfAny(WingetBarChars) < 0) return false;

            var size = Size.Match(line);
            if (size.Success)
            {
                double done = ToMegabytes(ParseNumber(size.Groups["a"].Value), size.Groups["au"].Value);
                double total = ToMegabytes(ParseNumber(size.Groups["b"].Value), size.Groups["bu"].Value);
                if (total > 0)
                {
                    fraction = Math.Clamp(done / total, 0.0, 1.0);
                }
                detail = $"Download: {size.Value}";
                return true;
            }

            var percent = Percent.Match(line);
            if (percent.Success)
            {
                double pct = ParseNumber(percent.Groups["p"].Value);
                if (pct is >= 0 and <= 100)
                {
                    fraction = Math.Clamp(pct / 100.0, 0.0, 1.0);
                }
            }
            return true;
        }

        private static double ParseNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;

            int lastComma = value.LastIndexOf(',');
            int lastDot = value.LastIndexOf('.');
            int decimalSeparatorIndex = Math.Max(lastComma, lastDot);

            string normalized;
            if (decimalSeparatorIndex >= 0)
            {
                // Wenn sowohl Punkt als auch Komma vorkommen, ist das letzte Zeichen das Dezimaltrennzeichen.
                // Kommt nur eines vor, behandeln wir es als Dezimaltrennzeichen (z. B. "45,3" oder "40.0").
                string integerPart = value[..decimalSeparatorIndex].Replace(".", "").Replace(",", "");
                string fractionalPart = value[(decimalSeparatorIndex + 1)..].Replace(".", "").Replace(",", "");
                normalized = $"{integerPart}.{fractionalPart}";
            }
            else
            {
                normalized = value;
            }

            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
                ? result
                : 0;
        }

        private static double ToMegabytes(double value, string unit) => unit.ToUpperInvariant() switch
        {
            "B" => value / (1024.0 * 1024.0),
            "KB" => value / 1024.0,
            "GB" => value * 1024.0,
            "TB" => value * 1024.0 * 1024.0,
            _ => value
        };
    }
}

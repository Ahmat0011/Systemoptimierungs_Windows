using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SystemOptimierer.Core
{
    /// <summary>
    /// Erkennt Fortschrittszeilen von Konsolenprogrammen und leitet sie an die Fortschrittsanzeige
    /// weiter, statt sie ins Log zu schreiben:
    ///  - DISM:   "[=========          45.3%          ]"
    ///  - sfc:    "Überprüfung 45 % abgeschlossen."
    ///  - winget: "  ██████████▒▒▒▒▒▒  12.0 MB / 40.0 MB"  bzw.  "  ████▒▒▒▒  45%"
    /// Alle anderen Zeilen gehen unverändert ins Log.
    /// </summary>
    public sealed class ConsoleProgressParser
    {
        private static readonly Regex DismProgress = new(@"^\s*\[[=\s]*(?<p>\d{1,3}(?:[.,]\d+)?)\s*%[=\s]*\]\s*$", RegexOptions.Compiled);
        private static readonly Regex SfcProgress = new(@"^\s*\S+\s+(?<p>\d{1,3})\s*%\s+\S+\.?\s*$", RegexOptions.Compiled);
        private static readonly Regex Percent = new(@"(?<p>\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);
        private static readonly Regex Size = new(@"(?<a>[\d.,]+)\s*(?<au>KB|MB|GB)\s*/\s*(?<b>[\d.,]+)\s*(?<bu>KB|MB|GB)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly Action<string> _log;
        private readonly Action<double?, string?> _onProgress;

        /// <param name="log">Ziel für alle normalen Zeilen.</param>
        /// <param name="onProgress">Fortschritt 0.0–1.0 (null = nicht bestimmbar) und optionaler Detailtext.</param>
        public ConsoleProgressParser(Action<string> log, Action<double?, string?> onProgress)
        {
            _log = log;
            _onProgress = onProgress;
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
                fraction = ParseNumber(match.Groups["p"].Value) / 100.0;
                return true;
            }

            // winget-Balken bestehen aus Blockzeichen
            if (line.IndexOfAny(new[] { '█', '▒', '▓' }) < 0) return false;

            var size = Size.Match(line);
            if (size.Success)
            {
                double done = ToMegabytes(ParseNumber(size.Groups["a"].Value), size.Groups["au"].Value);
                double total = ToMegabytes(ParseNumber(size.Groups["b"].Value), size.Groups["bu"].Value);
                if (total > 0) fraction = done / total;
                detail = $"Download: {size.Value}";
                return true;
            }

            var percent = Percent.Match(line);
            if (percent.Success) fraction = ParseNumber(percent.Groups["p"].Value) / 100.0;
            return true;
        }

        private static double ParseNumber(string value)
        {
            // "1.234,5" / "1,234.5" / "45,3" / "45.3" robust auswerten
            string normalized = value.Replace(',', '.');
            int lastDot = normalized.LastIndexOf('.');
            if (lastDot >= 0) normalized = normalized[..lastDot].Replace(".", "") + normalized[lastDot..];
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0;
        }

        private static double ToMegabytes(double value, string unit) => unit.ToUpperInvariant() switch
        {
            "KB" => value / 1024.0,
            "GB" => value * 1024.0,
            _ => value
        };
    }
}

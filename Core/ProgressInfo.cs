using System;

namespace SystemOptimierer.Core
{
    /// <summary>
    /// Fortschrittsmeldung für die Fortschrittsanzeige.
    /// </summary>
    /// <param name="Percent">Gesamtfortschritt 0–100.</param>
    /// <param name="Step">Überschrift, z. B. "Schritt 3 von 8: Papierkorb leeren".</param>
    /// <param name="Detail">Aktueller Vorgang unter dem Balken, z. B. "Komponentenspeicher wird bereinigt".</param>
    /// <param name="IsIndeterminate">Der aktuelle Schritt ist nicht messbar (z. B. Serversuche) – Balken animieren.</param>
    public sealed record ProgressInfo(double Percent, string Step, string? Detail = null, bool IsIndeterminate = false);

    /// <summary>
    /// Rechnet den Fortschritt einzelner Schritte in einen Gesamtfortschritt um.
    /// Lange Schritte (z. B. DISM) bekommen ein höheres Gewicht, damit der Balken realistisch wandert.
    /// </summary>
    public sealed class StepProgress
    {
        private readonly IProgress<ProgressInfo>? _progress;
        private readonly double[] _weights;
        // Laufende Summe der Gewichte VOR jedem Schritt: vermeidet eine LINQ-Summe pro Fortschrittsmeldung
        // (bei DISM/winget kommen hunderte Meldungen pro Vorgang).
        private readonly double[] _weightPrefix;
        private readonly double _totalWeight;
        private int _index;
        private string _title = string.Empty;

        public StepProgress(IProgress<ProgressInfo>? progress, params double[] weights)
        {
            if (weights == null || weights.Length == 0)
                throw new ArgumentException("Mindestens ein Schritt erforderlich.", nameof(weights));

            _progress = progress;
            _weights = new double[weights.Length];
            _weightPrefix = new double[weights.Length];

            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                double cleanWeight = double.IsFinite(weights[i]) ? Math.Max(0, weights[i]) : 0;
                _weights[i] = cleanWeight;
                _weightPrefix[i] = sum;
                sum += cleanWeight;
            }

            _totalWeight = sum;
            if (_totalWeight <= 0)
                throw new ArgumentException("Die Summe der Schritt-Gewichte muss größer als 0 sein.", nameof(weights));
        }

        public int StepCount => _weights.Length;

        /// <summary>Beginnt Schritt <paramref name="index"/> (0-basiert).</summary>
        public void Begin(int index, string title, string? detail = null)
        {
            _index = Math.Clamp(index, 0, _weights.Length - 1);
            _title = title ?? string.Empty;
            Report(0, detail);
        }

        /// <summary>Meldet den Fortschritt innerhalb des aktuellen Schritts (0.0–1.0).</summary>
        public void Report(double fraction, string? detail = null)
            => _progress?.Report(new ProgressInfo(OverallPercent(fraction), StepText, detail));

        /// <summary>Der aktuelle Schritt läuft, sein Fortschritt ist aber nicht messbar (animierter Balken).</summary>
        public void ReportIndeterminate(string? detail = null)
            => _progress?.Report(new ProgressInfo(OverallPercent(0), StepText, detail, IsIndeterminate: true));

        /// <summary>Schließt den gesamten Fortschritt sauber bei 100 % ab.</summary>
        public void Complete(string? detail = null)
            => _progress?.Report(new ProgressInfo(100.0, StepText, detail));

        private double OverallPercent(double fraction)
        {
            double safeFraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0.0, 1.0) : 0.0;
            return (_weightPrefix[_index] + _weights[_index] * safeFraction) / _totalWeight * 100.0;
        }

        private string StepText => _weights.Length > 1
            ? $"Schritt {_index + 1} von {_weights.Length}: {_title}"
            : _title;
    }
}

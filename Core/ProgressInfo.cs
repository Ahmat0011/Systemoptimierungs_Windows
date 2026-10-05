using System;
using System.Linq;

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
        private readonly double _totalWeight;
        private int _index;
        private string _title = string.Empty;

        public StepProgress(IProgress<ProgressInfo>? progress, params double[] weights)
        {
            if (weights.Length == 0) throw new ArgumentException("Mindestens ein Schritt erforderlich.", nameof(weights));
            _progress = progress;
            _weights = weights;
            _totalWeight = weights.Sum();
        }

        public int StepCount => _weights.Length;

        /// <summary>Beginnt Schritt <paramref name="index"/> (0-basiert).</summary>
        public void Begin(int index, string title, string? detail = null)
        {
            _index = Math.Clamp(index, 0, _weights.Length - 1);
            _title = title;
            Report(0, detail);
        }

        /// <summary>Meldet den Fortschritt innerhalb des aktuellen Schritts (0.0–1.0).</summary>
        public void Report(double fraction, string? detail = null)
            => _progress?.Report(new ProgressInfo(OverallPercent(fraction), StepText, detail));

        /// <summary>Der aktuelle Schritt läuft, sein Fortschritt ist aber nicht messbar (animierter Balken).</summary>
        public void ReportIndeterminate(string? detail = null)
            => _progress?.Report(new ProgressInfo(OverallPercent(0), StepText, detail, IsIndeterminate: true));

        private double OverallPercent(double fraction)
            => (_weights.Take(_index).Sum() + _weights[_index] * Math.Clamp(fraction, 0, 1)) / _totalWeight * 100;

        private string StepText => _weights.Length > 1
            ? $"Schritt {_index + 1} von {_weights.Length}: {_title}"
            : _title;
    }
}

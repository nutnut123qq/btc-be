using Backend.Services.Models;

namespace Backend.Services;

public static class VolumeAnalyzer
{
    public static void Compute(IReadOnlyList<RecognizedCandle> candles, IReadOnlyList<KlineDto> allKlines, int lookback = 20)
    {
        if (candles.Count == 0) return;

        // Build a dictionary for fast lookup by OpenTimeMs
        var indexByTime = allKlines
            .Select((k, idx) => new { k.OpenTimeMs, Idx = idx })
            .ToDictionary(x => x.OpenTimeMs, x => x.Idx);

        foreach (var c in candles)
        {
            if (!indexByTime.TryGetValue(c.OpenTimeMs, out var idx)) continue;

            var start = Math.Max(0, idx - lookback);
            var count = idx - start; // exclude current for SMA
            if (count <= 0) continue;

            double sum = 0;
            for (int i = start; i < idx; i++)
                sum += (double)allKlines[i].Volume;

            var sma = sum / count;
            c.VolumeAnomalyRatio = sma > 0 ? (double)c.Volume / sma : 1.0;
        }
    }

    public static string Summarize(IReadOnlyList<RecognizedCandle> candles)
    {
        if (candles.Count == 0) return string.Empty;
        var last = candles[^1];
        var recent = candles.Skip(Math.Max(0, candles.Count - 5)).ToList();
        var avgRatio = recent.Count > 0 ? recent.Average(c => c.VolumeAnomalyRatio) : 1.0;

        return $"Latest volume anomaly: {last.VolumeAnomalyRatio:F2}x (avg last 5 bars: {avgRatio:F2}x). " +
               $"{(last.VolumeAnomalyRatio > 1.5 ? "Above-average volume." : last.VolumeAnomalyRatio < 0.5 ? "Below-average volume." : "Normal volume.")}";
    }
}

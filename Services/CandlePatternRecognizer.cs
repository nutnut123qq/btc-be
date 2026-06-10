using Backend.Services.Models;

namespace Backend.Services;

public static class CandlePatternRecognizer
{
    private const double Epsilon = 0.0000001;

    public static PatternRecognitionResult Recognize(IReadOnlyList<KlineDto> klines, int tailCount = 30)
    {
        if (klines.Count == 0)
            return new PatternRecognitionResult();

        var tail = klines.Count <= tailCount ? klines : klines.Skip(klines.Count - tailCount).ToList();
        var candles = tail.Select(MapToRecognized).ToList();

        // Volume anomaly first (needs full klines for SMA lookback)
        VolumeAnalyzer.Compute(candles, klines, lookback: 20);

        // Trend detection on broader context for pattern classification
        var trend = DetectTrend(klines);

        // Single candle patterns
        for (int i = 0; i < candles.Count; i++)
        {
            candles[i].SinglePattern = RecognizeSingle(candles[i], i > 0 ? candles[i - 1] : null, trend);
        }

        // Multi-candle patterns
        var multiPatterns = new List<RecognizedMultiPattern>();
        for (int i = 0; i < candles.Count - 1; i++)
        {
            var d = RecognizeDouble(candles, i, trend);
            if (d != MultiCandlePattern.None)
                multiPatterns.Add(new RecognizedMultiPattern
                {
                    Pattern = d,
                    StartTimeMs = candles[i].OpenTimeMs,
                    EndTimeMs = candles[i + 1].OpenTimeMs
                });
        }
        for (int i = 0; i < candles.Count - 2; i++)
        {
            var t = RecognizeTriple(candles, i, trend);
            if (t != MultiCandlePattern.None)
                multiPatterns.Add(new RecognizedMultiPattern
                {
                    Pattern = t,
                    StartTimeMs = candles[i].OpenTimeMs,
                    EndTimeMs = candles[i + 2].OpenTimeMs
                });
        }

        var summary = BuildSummary(candles, multiPatterns);

        return new PatternRecognitionResult
        {
            Candles = candles,
            MultiPatterns = multiPatterns.DistinctBy(x => new { x.Pattern, x.StartTimeMs }).ToList(),
            SummaryText = summary
        };
    }

    private static RecognizedCandle MapToRecognized(KlineDto k)
    {
        return new RecognizedCandle
        {
            OpenTimeMs = k.OpenTimeMs,
            Open = k.Open,
            High = k.High,
            Low = k.Low,
            Close = k.Close,
            Volume = k.Volume
        };
    }

    private static TrendDirection DetectTrend(IReadOnlyList<KlineDto> klines)
    {
        if (klines.Count < 6) return TrendDirection.Sideways;
        var recent = klines.Skip(Math.Max(0, klines.Count - 6)).ToList();
        var up = 0;
        var down = 0;
        for (int i = 1; i < recent.Count; i++)
        {
            if (recent[i].Close > recent[i - 1].Close) up++;
            else if (recent[i].Close < recent[i - 1].Close) down++;
        }
        if (up >= 4 && down <= 1) return TrendDirection.Uptrend;
        if (down >= 4 && up <= 1) return TrendDirection.Downtrend;
        return TrendDirection.Sideways;
    }

    private static SingleCandlePattern RecognizeSingle(RecognizedCandle c, RecognizedCandle? prev, TrendDirection trend)
    {
        var range = (double)c.Range;
        if (range <= Epsilon) return SingleCandlePattern.Doji;

        var body = (double)c.BodySize;
        var upper = (double)c.UpperShadow;
        var lower = (double)c.LowerShadow;
        var bodyRatio = body / range;
        var upperRatio = upper / range;
        var lowerRatio = lower / range;

        // Doji variants
        if (bodyRatio < 0.015)
        {
            if (lowerRatio > 0.6 && upperRatio < 0.1) return SingleCandlePattern.DragonflyDoji;
            if (upperRatio > 0.6 && lowerRatio < 0.1) return SingleCandlePattern.GravestoneDoji;
            return SingleCandlePattern.Doji;
        }

        // Marubozu
        if (bodyRatio > 0.95)
        {
            return c.IsGreen ? SingleCandlePattern.BullishMarubozu : SingleCandlePattern.BearishMarubozu;
        }

        // Spinning Top
        if (bodyRatio < 0.3 && upperRatio > 0.25 && lowerRatio > 0.25)
        {
            return SingleCandlePattern.SpinningTop;
        }

        // Hammer / Hanging Man: small body at top, long lower shadow
        if (lower >= body * 2.0)
        {
            var bodyTop = Math.Max(c.Open, c.Close);
            var bodyTopRatio = (double)((bodyTop - c.Low) / c.Range);
            if (bodyTopRatio < 0.35 && upperRatio < 0.15)
            {
                if (trend == TrendDirection.Downtrend) return SingleCandlePattern.Hammer;
                if (trend == TrendDirection.Uptrend) return SingleCandlePattern.HangingMan;
            }
        }

        // Inverted Hammer / Shooting Star: small body at bottom, long upper shadow
        if (upper >= body * 2.0)
        {
            var bodyBottom = Math.Min(c.Open, c.Close);
            var bodyBottomRatio = (double)((c.High - bodyBottom) / c.Range);
            if (bodyBottomRatio < 0.35 && lowerRatio < 0.15)
            {
                if (trend == TrendDirection.Downtrend) return SingleCandlePattern.InvertedHammer;
                if (trend == TrendDirection.Uptrend) return SingleCandlePattern.ShootingStar;
            }
        }

        return SingleCandlePattern.None;
    }

    private static MultiCandlePattern RecognizeDouble(IReadOnlyList<RecognizedCandle> c, int i, TrendDirection trend)
    {
        if (i + 1 >= c.Count) return MultiCandlePattern.None;
        var c1 = c[i];
        var c2 = c[i + 1];
        var body1 = (double)c1.BodySize;
        var body2 = (double)c2.BodySize;
        if (body1 <= Epsilon || body2 <= Epsilon) return MultiCandlePattern.None;

        // Engulfing
        if (c1.IsRed && c2.IsGreen)
        {
            if (c2.Open <= c1.Close && c2.Close >= c1.Open && body2 > body1)
                return MultiCandlePattern.BullishEngulfing;
        }
        if (c1.IsGreen && c2.IsRed)
        {
            if (c2.Open >= c1.Close && c2.Close <= c1.Open && body2 > body1)
                return MultiCandlePattern.BearishEngulfing;
        }

        // Piercing Line
        if (c1.IsRed && c2.IsGreen && trend == TrendDirection.Downtrend)
        {
            var mid1 = (double)((c1.Open + c1.Close) / 2m);
            if (c2.Open < c1.Close && c2.Close > (decimal)mid1 && c2.Close < c1.Open)
                return MultiCandlePattern.PiercingLine;
        }

        // Dark Cloud Cover
        if (c1.IsGreen && c2.IsRed && trend == TrendDirection.Uptrend)
        {
            var mid1 = (double)((c1.Open + c1.Close) / 2m);
            if (c2.Open > c1.Close && c2.Close < (decimal)mid1 && c2.Close > c1.Open)
                return MultiCandlePattern.DarkCloudCover;
        }

        // Harami
        if (c1.IsRed && c2.IsGreen && body2 < body1 * 0.6)
        {
            if (c2.Open <= c1.Open && c2.Close >= c1.Close)
                return MultiCandlePattern.BullishHarami;
        }
        if (c1.IsGreen && c2.IsRed && body2 < body1 * 0.6)
        {
            if (c2.Open >= c1.Open && c2.Close <= c1.Close)
                return MultiCandlePattern.BearishHarami;
        }

        // Tweezer
        if (trend == TrendDirection.Downtrend && Math.Abs((double)(c1.Low - c2.Low)) / (double)c1.Range < 0.08)
            return MultiCandlePattern.TweezerBottoms;
        if (trend == TrendDirection.Uptrend && Math.Abs((double)(c1.High - c2.High)) / (double)c1.Range < 0.08)
            return MultiCandlePattern.TweezerTops;

        return MultiCandlePattern.None;
    }

    private static MultiCandlePattern RecognizeTriple(IReadOnlyList<RecognizedCandle> c, int i, TrendDirection trend)
    {
        if (i + 2 >= c.Count) return MultiCandlePattern.None;
        var c1 = c[i];
        var c2 = c[i + 1];
        var c3 = c[i + 2];
        var body1 = (double)c1.BodySize;
        var body2 = (double)c2.BodySize;
        var body3 = (double)c3.BodySize;
        var range1 = (double)c1.Range;
        var range2 = (double)c2.Range;
        if (range1 <= Epsilon || range2 <= Epsilon) return MultiCandlePattern.None;

        // Morning Star
        if (c1.IsRed && c3.IsGreen && trend == TrendDirection.Downtrend)
        {
            if (body1 > range1 * 0.5 && body2 < range2 * 0.3 && body3 > body1 * 0.5)
            {
                var mid1 = (c1.Open + c1.Close) / 2m;
                if (c3.Close > mid1)
                    return MultiCandlePattern.MorningStar;
            }
        }

        // Evening Star
        if (c1.IsGreen && c3.IsRed && trend == TrendDirection.Uptrend)
        {
            if (body1 > range1 * 0.5 && body2 < range2 * 0.3 && body3 > body1 * 0.5)
            {
                var mid1 = (c1.Open + c1.Close) / 2m;
                if (c3.Close < mid1)
                    return MultiCandlePattern.EveningStar;
            }
        }

        // Three White Soldiers
        if (c1.IsGreen && c2.IsGreen && c3.IsGreen)
        {
            var b1 = (double)c1.BodySize;
            var b2 = (double)c2.BodySize;
            var b3 = (double)c3.BodySize;
            if (b1 > range1 * 0.5 && b2 > range1 * 0.5 && b3 > range1 * 0.5 &&
                c2.Open > c1.Open && c2.Close > c1.Close &&
                c3.Open > c2.Open && c3.Close > c2.Close &&
                (double)c1.UpperShadow < b1 * 0.15 && (double)c2.UpperShadow < b2 * 0.15 && (double)c3.UpperShadow < b3 * 0.15)
                return MultiCandlePattern.ThreeWhiteSoldiers;
        }

        // Three Black Crows
        if (c1.IsRed && c2.IsRed && c3.IsRed)
        {
            var b1 = (double)c1.BodySize;
            var b2 = (double)c2.BodySize;
            var b3 = (double)c3.BodySize;
            if (b1 > range1 * 0.5 && b2 > range1 * 0.5 && b3 > range1 * 0.5 &&
                c2.Open < c1.Open && c2.Close < c1.Close &&
                c3.Open < c2.Open && c3.Close < c2.Close &&
                (double)c1.LowerShadow < b1 * 0.15 && (double)c2.LowerShadow < b2 * 0.15 && (double)c3.LowerShadow < b3 * 0.15)
                return MultiCandlePattern.ThreeBlackCrows;
        }

        // Three Inside Up
        if (c1.IsRed && c2.IsGreen && c3.IsGreen && trend == TrendDirection.Downtrend)
        {
            if (body2 < body1 * 0.6 && c2.Open >= c1.Close && c2.Close <= c1.Open && c3.Close > c1.High)
                return MultiCandlePattern.ThreeInsideUp;
        }

        // Three Inside Down
        if (c1.IsGreen && c2.IsRed && c3.IsRed && trend == TrendDirection.Uptrend)
        {
            if (body2 < body1 * 0.6 && c2.Open <= c1.Close && c2.Close >= c1.Open && c3.Close < c1.Low)
                return MultiCandlePattern.ThreeInsideDown;
        }

        return MultiCandlePattern.None;
    }

    private static string BuildSummary(IReadOnlyList<RecognizedCandle> candles, IReadOnlyList<RecognizedMultiPattern> multiPatterns)
    {
        var singles = candles
            .Where(c => c.SinglePattern != SingleCandlePattern.None)
            .Select(c => $"{c.SinglePattern}@{DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTimeMs):HH:mm}")
            .ToList();

        var lines = new List<string>();
        if (singles.Count > 0)
            lines.Add($"Single-candle patterns ({singles.Count}): {string.Join(", ", singles)}.");
        if (multiPatterns.Count > 0)
            lines.Add($"Multi-candle patterns: {string.Join(", ", multiPatterns.Select(x => x.Pattern).Distinct())}.");

        var volSpikes = candles
            .Where(c => c.VolumeAnomalyRatio > 1.5)
            .Select(c => $"{c.VolumeAnomalyRatio:F1}x@{DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTimeMs):HH:mm}")
            .ToList();
        if (volSpikes.Count > 0)
            lines.Add($"Volume spikes: {string.Join(", ", volSpikes)}.");

        return lines.Count > 0 ? string.Join("\n", lines) : "No significant candlestick patterns detected in recent bars.";
    }
}

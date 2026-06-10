using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class CandlePatternIndexer : ICandlePatternIndexer
{
    private readonly AppDbContext _db;
    private readonly IBinanceKlinesService _klines;

    public CandlePatternIndexer(AppDbContext db, IBinanceKlinesService klines)
    {
        _db = db;
        _klines = klines;
    }

    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count == 0) return 0;

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        // Map OpenTimeMs -> index trong klines để tính trend tại đúng thởi điểm pattern
        var timeToIndex = klines
            .Select((k, i) => new { k.OpenTimeMs, Index = i })
            .ToDictionary(x => x.OpenTimeMs, x => x.Index);

        var toAdd = new List<CandlePattern>();

        // Single patterns
        foreach (var candle in result.Candles)
        {
            if (candle.SinglePattern == SingleCandlePattern.None) continue;

            var patternType = candle.SinglePattern.ToString();
            if (await ExistsAsync(symbol, timeframe, candle.OpenTimeMs, patternType, cancellationToken)) continue;

            var trend = timeToIndex.TryGetValue(candle.OpenTimeMs, out var idx)
                ? DetectTrendAt(klines, idx)
                : TrendDirection.Sideways;

            toAdd.Add(new CandlePattern
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeMs = candle.OpenTimeMs,
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume,
                PatternType = patternType,
                PatternCategory = "Single",
                TrendDirection = trend.ToString(),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        // Multi patterns
        foreach (var mp in result.MultiPatterns)
        {
            var patternType = mp.Pattern.ToString();
            if (await ExistsAsync(symbol, timeframe, mp.StartTimeMs, patternType, cancellationToken)) continue;

            var startCandle = result.Candles.FirstOrDefault(c => c.OpenTimeMs == mp.StartTimeMs);
            if (startCandle is null) continue;

            // Trend tính tại nến kết thúc pattern (EndTimeMs)
            var trend = timeToIndex.TryGetValue(mp.EndTimeMs, out var endIdx)
                ? DetectTrendAt(klines, endIdx)
                : TrendDirection.Sideways;

            toAdd.Add(new CandlePattern
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeMs = mp.StartTimeMs,
                Open = startCandle.Open,
                High = startCandle.High,
                Low = startCandle.Low,
                Close = startCandle.Close,
                Volume = startCandle.Volume,
                PatternType = patternType,
                PatternCategory = mp.Pattern.ToString().StartsWith("Three") ? "Triple" : "Double",
                TrendDirection = trend.ToString(),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        if (toAdd.Count > 0)
        {
            _db.CandlePatterns.AddRange(toAdd);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return toAdd.Count;
    }

    public async Task<int> BuildFullAsync(
        string symbol,
        string timeframe,
        int lookbackBars,
        CancellationToken cancellationToken = default)
    {
        var klines = await _klines.GetKlinesAsync(
            symbol: symbol,
            interval: timeframe,
            limit: Math.Clamp(lookbackBars, 10, 5_000),
            cancellationToken: cancellationToken);

        return await IndexAsync(symbol, timeframe, klines, cancellationToken);
    }

    private async Task<bool> ExistsAsync(
        string symbol,
        string timeframe,
        long openTimeMs,
        string patternType,
        CancellationToken cancellationToken)
    {
        return await _db.CandlePatterns.AnyAsync(
            x => x.Symbol == symbol &&
                 x.Timeframe == timeframe &&
                 x.OpenTimeMs == openTimeMs &&
                 x.PatternType == patternType,
            cancellationToken);
    }

    /// <summary>
    /// Tính xu hướng tại thởi điểm của nến thứ <paramref name="endIndex"/>,
    /// dựa trên tối đa 6 nến kết thúc tại index đó.
    /// </summary>
    private static TrendDirection DetectTrendAt(IReadOnlyList<KlineDto> klines, int endIndex)
    {
        int start = Math.Max(0, endIndex - 5);
        int comparisons = endIndex - start;
        if (comparisons < 3) return TrendDirection.Sideways;

        int up = 0;
        int down = 0;
        for (int i = start + 1; i <= endIndex; i++)
        {
            if (klines[i].Close > klines[i - 1].Close) up++;
            else if (klines[i].Close < klines[i - 1].Close) down++;
        }

        if (up >= 4 && down <= 1) return TrendDirection.Uptrend;
        if (down >= 4 && up <= 1) return TrendDirection.Downtrend;
        return TrendDirection.Sideways;
    }
}

using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public class CandlePatternIndexer : ICandlePatternIndexer
{
    private readonly AppDbContext _db;
    private readonly IBinanceKlinesService _klines;
    private readonly IndexingOptions _options;

    public CandlePatternIndexer(
        AppDbContext db,
        IBinanceKlinesService klines,
        IOptions<IndexingOptions> options)
    {
        _db = db;
        _klines = klines;
        _options = options.Value;
    }

    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count == 0) return 0;

        var startMs = klines[0].OpenTimeMs;
        var endMs = klines[^1].OpenTimeMs;
        var existingKeys = await LoadExistingKeysAsync(symbol, timeframe, startMs, endMs, cancellationToken);

        return await IndexAsync(symbol, timeframe, klines, existingKeys, cancellationToken);
    }

    /// <summary>
    /// Index với existing keys đã có sẵn. Dùng khi caller muốn cache keys giữa các chunk.
    /// </summary>
    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        HashSet<(long OpenTimeMs, string PatternType)> existingKeys,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count == 0) return 0;

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        var timeToIndex = klines
            .Select((k, i) => (k.OpenTimeMs, Index: i))
            .ToDictionary(x => x.OpenTimeMs, x => x.Index);

        var batchSize = Math.Max(100, _options.CandlePatternBatchSize);
        var batch = new List<CandlePattern>(batchSize);
        var totalAdded = 0;
        var now = DateTime.UtcNow;

        async Task FlushBatchAsync()
        {
            if (batch.Count == 0) return;
            _db.CandlePatterns.AddRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            totalAdded += batch.Count;
            batch.Clear();
            _db.ChangeTracker.Clear();
        }

        // Single patterns
        foreach (var candle in result.Candles)
        {
            if (candle.SinglePattern == SingleCandlePattern.None) continue;

            var patternType = candle.SinglePattern.ToString();
            if (existingKeys.Contains((candle.OpenTimeMs, patternType))) continue;

            var trend = timeToIndex.TryGetValue(candle.OpenTimeMs, out var idx)
                ? CandlePatternRecognizer.DetectTrendAt(klines, idx)
                : TrendDirection.Sideways;

            batch.Add(new CandlePattern
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
                CreatedAtUtc = now
            });

            if (batch.Count >= batchSize) await FlushBatchAsync();
        }

        // Multi patterns
        var candleByTime = result.Candles.ToDictionary(c => c.OpenTimeMs);
        foreach (var mp in result.MultiPatterns)
        {
            var patternType = mp.Pattern.ToString();
            if (existingKeys.Contains((mp.StartTimeMs, patternType))) continue;

            if (!candleByTime.TryGetValue(mp.StartTimeMs, out var startCandle)) continue;

            var trend = timeToIndex.TryGetValue(mp.StartTimeMs, out var startIdx)
                ? CandlePatternRecognizer.DetectTrendAt(klines, startIdx)
                : TrendDirection.Sideways;

            batch.Add(new CandlePattern
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
                CreatedAtUtc = now
            });

            if (batch.Count >= batchSize) await FlushBatchAsync();
        }

        await FlushBatchAsync();
        return totalAdded;
    }

    public async Task<int> BuildFullAsync(
        string symbol,
        string timeframe,
        int lookbackBars,
        CancellationToken cancellationToken = default)
    {
        var klines = await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderBy(k => k.OpenTimeMs)
            .Select(k => KlineMapper.ToDto(k))
            .ToListAsync(cancellationToken);

        var effectiveKlines = IndexingRangeHelper.ApplyLookback(klines, lookbackBars);
        return await IndexAsync(symbol, timeframe, effectiveKlines, cancellationToken);
    }

    private async Task<HashSet<(long OpenTimeMs, string PatternType)>> LoadExistingKeysAsync(
        string symbol,
        string timeframe,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        var keys = await _db.CandlePatterns
            .AsNoTracking()
            .Where(x =>
                x.Symbol == symbol &&
                x.Timeframe == timeframe &&
                x.OpenTimeMs >= startMs &&
                x.OpenTimeMs <= endMs)
            .Select(x => new { x.OpenTimeMs, x.PatternType })
            .ToListAsync(cancellationToken);

        return keys.Select(x => (x.OpenTimeMs, x.PatternType)).ToHashSet();
    }
}

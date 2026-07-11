using System.Text.Json;
using Backend.Data;
using Backend.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Tạo chuỗi pattern nến liên tiếp (sliding window 3-5 patterns) để phân tích context.
/// Hỗ trợ incremental indexing và batch insert.
/// </summary>
public class CandlePatternSequenceIndexer
{
    private readonly AppDbContext _db;
    private readonly ILogger<CandlePatternSequenceIndexer> _logger;
    private readonly IndexingOptions _options;

    private static readonly int[] WindowSizes = { 3, 4, 5 };
    private const int MaxWindowSize = 5;

    public CandlePatternSequenceIndexer(
        AppDbContext db,
        ILogger<CandlePatternSequenceIndexer> logger,
        IOptions<IndexingOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        int lookbackBars = 0,
        CancellationToken cancellationToken = default)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0)
        {
            _logger.LogWarning("Invalid timeframe {Timeframe} for pattern sequence indexing", timeframe);
            return 0;
        }

        // Xác định điểm bắt đầu incremental cho từng window size.
        var maxExistingEnds = await _db.PatternSequences
            .AsNoTracking()
            .Where(s => s.Symbol == symbol && s.Timeframe == timeframe)
            .GroupBy(s => s.WindowSize)
            .Select(g => new { WindowSize = g.Key, MaxEnd = (long?)g.Max(s => s.EndTimeMs) })
            .ToDictionaryAsync(x => x.WindowSize, x => x.MaxEnd, cancellationToken);

        long? minStartMs = null;
        long? maxEndMs = null;
        foreach (var ws in WindowSizes)
        {
            if (maxExistingEnds.TryGetValue(ws, out var maxEnd) && maxEnd.HasValue)
            {
                if (!maxEndMs.HasValue || maxEnd.Value > maxEndMs.Value)
                    maxEndMs = maxEnd.Value;

                var startForWs = maxEnd.Value - (ws - 1) * intervalMs;
                if (!minStartMs.HasValue || startForWs < minStartMs.Value)
                    minStartMs = startForWs;
            }
            else
            {
                minStartMs = null; // chưa có dữ liệu -> load toàn bộ
                break;
            }
        }

        // Áp dụng lookbackBars nếu có
        if (lookbackBars > 0 && maxEndMs.HasValue)
        {
            var lookbackStartMs = maxEndMs.Value - lookbackBars * intervalMs;
            if (!minStartMs.HasValue || lookbackStartMs > minStartMs.Value)
                minStartMs = lookbackStartMs;
        }

        // Nếu chưa có dữ liệu và có lookbackBars, chỉ load gần đây
        if (!minStartMs.HasValue && lookbackBars > 0)
        {
            var latestPatternTime = await _db.CandlePatterns
                .AsNoTracking()
                .Where(p => p.Symbol == symbol && p.Timeframe == timeframe)
                .Select(p => (long?)p.OpenTimeMs)
                .MaxAsync(cancellationToken);

            if (latestPatternTime.HasValue)
                minStartMs = Math.Max(0L, latestPatternTime.Value - lookbackBars * intervalMs);
        }

        var patternsQuery = _db.CandlePatterns
            .AsNoTracking()
            .Where(p => p.Symbol == symbol && p.Timeframe == timeframe)
            .OrderBy(p => p.OpenTimeMs);

        if (minStartMs.HasValue)
            patternsQuery = patternsQuery.Where(p => p.OpenTimeMs >= minStartMs.Value).OrderBy(p => p.OpenTimeMs);

        var patterns = await patternsQuery
            .Select(p => new PatternKey(p.OpenTimeMs, p.PatternType))
            .ToListAsync(cancellationToken);

        if (patterns.Count < MaxWindowSize)
        {
            _logger.LogWarning("Not enough patterns to build sequences for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        var startRangeMs = patterns[0].OpenTimeMs;
        var endRangeMs = patterns[^1].OpenTimeMs;
        var existingKeys = await LoadExistingKeysAsync(symbol, timeframe, startRangeMs, endRangeMs, cancellationToken);

        var batchSize = Math.Max(100, _options.PatternSequenceBatchSize);
        var batch = new List<PatternSequence>(batchSize);
        var totalAdded = 0;

        foreach (var ws in WindowSizes)
        {
            if (patterns.Count < ws) continue;

            for (int i = 0; i <= patterns.Count - ws; i++)
            {
                var startMs = patterns[i].OpenTimeMs;
                var endMs = patterns[i + ws - 1].OpenTimeMs;
                var key = ToKey(startMs, endMs, ws);
                if (existingKeys.Contains(key)) continue;

                var chain = new string[ws];
                for (int j = 0; j < ws; j++)
                    chain[j] = patterns[i + j].PatternType;

                batch.Add(new PatternSequence
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    StartTimeMs = startMs,
                    EndTimeMs = endMs,
                    WindowSize = ws,
                    PatternChainJson = JsonSerializer.Serialize(chain),
                    Count = 1
                });

                if (batch.Count >= batchSize)
                {
                    _db.PatternSequences.AddRange(batch);
                    await _db.SaveChangesAsync(cancellationToken);
                    totalAdded += batch.Count;
                    batch.Clear();
                    _db.ChangeTracker.Clear();
                }
            }
        }

        if (batch.Count > 0)
        {
            _db.PatternSequences.AddRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            totalAdded += batch.Count;
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation("Indexed {Count} pattern sequences for {Symbol} {Timeframe}", totalAdded, symbol, timeframe);
        return totalAdded;
    }

    private async Task<HashSet<string>> LoadExistingKeysAsync(
        string symbol,
        string timeframe,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        var keys = await _db.PatternSequences
            .AsNoTracking()
            .Where(s =>
                s.Symbol == symbol &&
                s.Timeframe == timeframe &&
                s.StartTimeMs >= startMs &&
                s.EndTimeMs <= endMs)
            .Select(s => new { s.StartTimeMs, s.EndTimeMs, s.WindowSize })
            .ToListAsync(cancellationToken);

        return keys
            .Select(e => ToKey(e.StartTimeMs, e.EndTimeMs, e.WindowSize))
            .ToHashSet();
    }

    private static string ToKey(long startMs, long endMs, int windowSize) => $"{startMs}_{endMs}_{windowSize}";

    private readonly record struct PatternKey(long OpenTimeMs, string PatternType);
}

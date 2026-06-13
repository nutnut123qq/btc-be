using System.Text.Json;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Tạo chuỗi pattern nến liên tiếp (sliding window 3-5 patterns) để phân tích context.
/// </summary>
public class CandlePatternSequenceIndexer
{
    private readonly AppDbContext _db;
    private readonly ILogger<CandlePatternSequenceIndexer> _logger;

    public CandlePatternSequenceIndexer(
        AppDbContext db,
        ILogger<CandlePatternSequenceIndexer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var patterns = await _db.CandlePatterns
            .AsNoTracking()
            .Where(p => p.Symbol == symbol && p.Timeframe == timeframe)
            .OrderBy(p => p.OpenTimeMs)
            .ToListAsync(cancellationToken);

        if (patterns.Count < 5)
        {
            _logger.LogWarning("Not enough patterns to build sequences for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        var existingKeys = await _db.PatternSequences
            .AsNoTracking()
            .Where(s => s.Symbol == symbol && s.Timeframe == timeframe)
            .Select(s => new { s.StartTimeMs, s.EndTimeMs, s.WindowSize })
            .ToListAsync(cancellationToken);
        var existingSet = new HashSet<string>(existingKeys.Select(e => $"{e.StartTimeMs}_{e.EndTimeMs}_{e.WindowSize}"));

        var windowSizes = new[] { 3, 4, 5 };
        var toAdd = new List<PatternSequence>();

        foreach (var ws in windowSizes)
        {
            for (int i = 0; i <= patterns.Count - ws; i++)
            {
                var window = patterns.Skip(i).Take(ws).ToList();
                var startMs = window[0].OpenTimeMs;
                var endMs = window[^1].OpenTimeMs;
                var key = $"{startMs}_{endMs}_{ws}";
                if (existingSet.Contains(key)) continue;

                var chain = window.Select(p => p.PatternType).ToList();
                toAdd.Add(new PatternSequence
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    StartTimeMs = startMs,
                    EndTimeMs = endMs,
                    WindowSize = ws,
                    PatternChainJson = JsonSerializer.Serialize(chain),
                    Count = 1
                });
            }
        }

        if (toAdd.Count > 0)
        {
            _db.PatternSequences.AddRange(toAdd);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Indexed {Count} pattern sequences for {Symbol} {Timeframe}", toAdd.Count, symbol, timeframe);
        }

        return toAdd.Count;
    }
}

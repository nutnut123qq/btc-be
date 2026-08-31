using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Backend.Services;

public class ConfluenceService : IConfluenceService
{
    private readonly IArchetypeService _archetypeService;
    private readonly IRegimeDetectionService _regimeDetectionService;
    private readonly ITransitionService _transitionService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ConfluenceService> _logger;

    public ConfluenceService(
        IArchetypeService archetypeService,
        IRegimeDetectionService regimeDetectionService,
        ITransitionService transitionService,
        AppDbContext dbContext,
        ILogger<ConfluenceService> logger)
    {
        _archetypeService = archetypeService;
        _regimeDetectionService = regimeDetectionService;
        _transitionService = transitionService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ConfluenceSnapshot> CalculateConfluenceAsync(string symbol, CancellationToken ct = default)
    {
        var timeframes = new[] { "15m", "1h", "4h", "1d" };
        var weights = new Dictionary<string, double>
        {
            { "1d", 0.40 },
            { "4h", 0.30 },
            { "1h", 0.20 },
            { "15m", 0.10 }
        };

        var timeframeAlignments = new List<ConfluenceTimeframeAlignmentDto>();
        double totalWeightedScore = 0;
        
        var tfScores = new Dictionary<string, double>();

        foreach (var tf in timeframes)
        {
            // Get Regime
            var regime = await _regimeDetectionService.GetCurrentRegimeAsync(symbol, tf, ct);
            
            // Window size 20 is supported by the indexed archetype pipeline.
            var match = await _archetypeService.MatchCurrentWindowAsync(symbol, tf, 20, ct);
            
            double regimeScore = 0;
            if (regime != null)
            {
                if (regime.RegimeType == "TrendingUp") regimeScore = 0.8;
                else if (regime.RegimeType == "TrendingDown") regimeScore = -0.8;
                else if (regime.RegimeType == "Breakout") regimeScore = regime.PlusDi > regime.MinusDi ? 0.5 : -0.5;
                else regimeScore = 0; // RangeBound, Compression
            }

            double archetypeScore = 0;
            var outcome = match?.Archetype?.BestOutcome;
            if (outcome != null)
            {
                archetypeScore = outcome.UpRate - outcome.DownRate;
            }

            // Combine scores (simple average between regime and archetype)
            double tfScore = (regimeScore + archetypeScore) / 2.0;
            
            // Cap between -1 and +1
            tfScore = Math.Clamp(tfScore, -1.0, 1.0);
            
            tfScores[tf] = tfScore;

            totalWeightedScore += tfScore * weights[tf];

            timeframeAlignments.Add(new ConfluenceTimeframeAlignmentDto
            {
                Timeframe = tf,
                DirectionalScore = tfScore,
                Direction = tfScore > 0 ? "Bullish" : tfScore < 0 ? "Bearish" : "Neutral",
                RegimeType = regime?.RegimeType ?? "Unknown",
                ArchetypeCode = match?.Archetype?.ArchetypeCode,
                Weight = weights[tf]
            });
        }

        // Calculate Normalized Confluence Score (0 - 100)
        // totalWeightedScore is between -1 and 1
        double confluenceScore = ((totalWeightedScore + 1.0) / 2.0) * 100.0;

        string overallDirection = "Neutral";
        if (confluenceScore >= 80) overallDirection = "StrongBullish";
        else if (confluenceScore >= 60) overallDirection = "Bullish";
        else if (confluenceScore <= 20) overallDirection = "StrongBearish";
        else if (confluenceScore <= 40) overallDirection = "Bearish";

        // Detect Conflict
        bool hasConflict = false;
        string? conflictDetails = null;

        double htScore = tfScores["4h"] * 0.5 + tfScores["1d"] * 0.5;
        double ltScore = tfScores["15m"] * 0.5 + tfScores["1h"] * 0.5;

        if (htScore > 0.3 && ltScore < -0.3)
        {
            hasConflict = true;
            conflictDetails = "Cảnh báo điều chỉnh ngắn hạn trong xu hướng tăng dài hạn"; // Short term correction in long term uptrend
        }
        else if (htScore < -0.3 && ltScore > 0.3)
        {
            hasConflict = true;
            conflictDetails = "Cảnh báo phục hồi ngắn hạn trong xu hướng giảm dài hạn"; // Short term bounce in long term downtrend
        }

        var snapshot = new ConfluenceSnapshot
        {
            Symbol = symbol,
            TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ConfluenceScore = Math.Round(confluenceScore, 2),
            OverallDirection = overallDirection,
            TimeframeAlignmentsJson = JsonSerializer.Serialize(timeframeAlignments, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            HasConflict = hasConflict,
            ConflictDetails = conflictDetails,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.ConfluenceSnapshots.Add(snapshot);
        await _dbContext.SaveChangesAsync(ct);

        return snapshot;
    }

    public async Task<ConfluenceSnapshot?> GetLatestConfluenceAsync(string symbol, CancellationToken ct = default)
    {
        return await _dbContext.ConfluenceSnapshots
            .Where(x => x.Symbol == symbol)
            .OrderByDescending(x => x.TimeMs)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<ConfluenceSnapshot>> GetConfluenceHistoryAsync(string symbol, int limit, CancellationToken ct = default)
    {
        return await _dbContext.ConfluenceSnapshots
            .Where(x => x.Symbol == symbol)
            .OrderByDescending(x => x.TimeMs)
            .Take(limit)
            .ToListAsync(ct);
    }
}

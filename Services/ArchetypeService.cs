using System.Text.Json;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class ArchetypeService : IArchetypeService
{
    private readonly AppDbContext _db;
    private readonly IWindowDatasetService _windowDataset;
    private readonly ILogger<ArchetypeService> _logger;

    public ArchetypeService(
        AppDbContext db,
        IWindowDatasetService windowDataset,
        ILogger<ArchetypeService> logger)
    {
        _db = db;
        _windowDataset = windowDataset;
        _logger = logger;
    }

    public async Task<(int Total, List<ArchetypeDto> Items)> GetArchetypesAsync(string symbol, string timeframe, int? windowSize, string sortBy, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.CandleArchetypes.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe);

        if (windowSize.HasValue)
        {
            query = query.Where(x => x.WindowSize == windowSize.Value);
        }

        var total = await query.CountAsync(ct);
        
        var archetypes = await query.ToListAsync(ct);
        var archetypeIds = archetypes.Select(x => x.Id).ToList();

        var outcomes = await _db.ArchetypeOutcomes.AsNoTracking()
            .Where(x => archetypeIds.Contains(x.ArchetypeId))
            .ToListAsync(ct);

        var outcomesByArchetype = outcomes.GroupBy(x => x.ArchetypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var items = new List<ArchetypeDto>();
        foreach (var arch in archetypes)
        {
            var archOutcomes = outcomesByArchetype.GetValueOrDefault(arch.Id, new List<ArchetypeOutcome>());
            var bestOutcome = archOutcomes.OrderByDescending(o => Math.Max(Math.Abs(o.UpRate - 0.333), Math.Abs(o.DownRate - 0.333))).FirstOrDefault();

            var dto = new ArchetypeDto
            {
                Id = arch.Id,
                ArchetypeCode = arch.ArchetypeCode,
                Symbol = arch.Symbol,
                Timeframe = arch.Timeframe,
                WindowSize = arch.WindowSize,
                MemberCount = arch.MemberCount,
                IntraClusterDistance = arch.IntraClusterDistance,
                RepresentativeOhlc = arch.RepresentativeOhlcJson != null ? JsonSerializer.Deserialize<object>(arch.RepresentativeOhlcJson) : null,
                BestOutcome = bestOutcome != null ? MapOutcome(bestOutcome) : null
            };
            items.Add(dto);
        }

        if (sortBy == "memberCount")
            items = items.OrderByDescending(x => x.MemberCount).ToList();
        else if (sortBy == "recentAccuracy")
            items = items.OrderByDescending(x => x.BestOutcome != null ? Math.Max(x.BestOutcome.RecentUpRate, x.BestOutcome.RecentDownRate) : 0).ToList();
        else
            items = items.OrderByDescending(x => x.BestOutcome != null ? Math.Max(x.BestOutcome.UpRate, x.BestOutcome.DownRate) : 0).ToList();

        items = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return (total, items);
    }

    public async Task<ArchetypeDetailDto?> GetArchetypeDetailAsync(long id, CancellationToken ct = default)
    {
        var arch = await _db.CandleArchetypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (arch == null) return null;

        var outcomes = await _db.ArchetypeOutcomes.AsNoTracking()
            .Where(x => x.ArchetypeId == id)
            .ToListAsync(ct);

        return new ArchetypeDetailDto
        {
            Id = arch.Id,
            ArchetypeCode = arch.ArchetypeCode,
            Symbol = arch.Symbol,
            Timeframe = arch.Timeframe,
            WindowSize = arch.WindowSize,
            MemberCount = arch.MemberCount,
            IntraClusterDistance = arch.IntraClusterDistance,
            RepresentativeOhlc = arch.RepresentativeOhlcJson != null ? JsonSerializer.Deserialize<object>(arch.RepresentativeOhlcJson) : null,
            Outcomes = outcomes.Select(MapOutcome).ToList()
        };
    }

    public async Task<ArchetypeMatchDto?> MatchCurrentWindowAsync(string symbol, string timeframe, int windowSize, CancellationToken ct = default)
    {
        var currentVectorResult = await _windowDataset.BuildLatestFeatureVectorAsync(symbol, timeframe, windowSize, ct);
        if (currentVectorResult == null) return null;

        var currentVec = currentVectorResult.Value.Vector;
        var currentNorm = (float)Math.Sqrt(currentVec.Sum(v => v * v));

        var archetypes = await _db.CandleArchetypes.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize)
            .ToListAsync(ct);

        if (archetypes.Count == 0) return null;

        var latestVersion = archetypes.Max(x => x.Version);
        archetypes = archetypes.Where(x => x.Version == latestVersion).ToList();

        CandleArchetype? bestArch = null;
        double bestSimilarity = -1;

        foreach (var arch in archetypes)
        {
            if (arch.CentroidVector.Length != currentVec.Length) continue;
            var sim = CosineSimilarity(currentVec, currentNorm, arch.CentroidVector, arch.CentroidNorm);
            if (sim > bestSimilarity)
            {
                bestSimilarity = sim;
                bestArch = arch;
            }
        }

        if (bestArch == null) return null;

        var outcomes = await _db.ArchetypeOutcomes.AsNoTracking()
            .Where(x => x.ArchetypeId == bestArch.Id)
            .ToListAsync(ct);

        var bestOutcome = outcomes.OrderByDescending(o => Math.Max(Math.Abs(o.UpRate - 0.333), Math.Abs(o.DownRate - 0.333))).FirstOrDefault();

        string confidence = bestSimilarity >= 0.85 ? "High" : (bestSimilarity >= 0.70 ? "Medium" : "Low");

        return new ArchetypeMatchDto
        {
            WindowSize = windowSize,
            Similarity = (float)bestSimilarity,
            ConfidenceLevel = confidence,
            Archetype = new ArchetypeDto
            {
                Id = bestArch.Id,
                ArchetypeCode = bestArch.ArchetypeCode,
                Symbol = bestArch.Symbol,
                Timeframe = bestArch.Timeframe,
                WindowSize = bestArch.WindowSize,
                MemberCount = bestArch.MemberCount,
                IntraClusterDistance = bestArch.IntraClusterDistance,
                RepresentativeOhlc = bestArch.RepresentativeOhlcJson != null ? JsonSerializer.Deserialize<object>(bestArch.RepresentativeOhlcJson) : null,
                BestOutcome = bestOutcome != null ? MapOutcome(bestOutcome) : null
            },
            Outcomes = outcomes.Select(MapOutcome).ToList()
        };
    }

    public async Task<(List<ArchetypeMatchDto> Matches, object WeightedSignal)> MatchMultiWindowAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        var windowSizes = new[] { 10, 15, 20, 25 };
        var matches = new List<ArchetypeMatchDto>();

        foreach (var ws in windowSizes)
        {
            var match = await MatchCurrentWindowAsync(symbol, timeframe, ws, ct);
            if (match != null)
            {
                matches.Add(match);
            }
        }

        double upVotes = 0;
        double downVotes = 0;
        double sidewaysVotes = 0;

        foreach (var match in matches)
        {
            if (match.Archetype?.BestOutcome != null)
            {
                var outcome = match.Archetype.BestOutcome;
                var memberCount = match.Archetype.MemberCount;
                var weight = match.Similarity * Math.Log10(memberCount + 1);

                if (outcome.UpRate > outcome.DownRate && outcome.UpRate > outcome.SidewaysRate)
                    upVotes += weight;
                else if (outcome.DownRate > outcome.UpRate && outcome.DownRate > outcome.SidewaysRate)
                    downVotes += weight;
                else
                    sidewaysVotes += weight;
            }
        }

        var totalVotes = upVotes + downVotes + sidewaysVotes;
        string direction = "Sideways";
        double confidence = 0;

        if (totalVotes > 0)
        {
            if (upVotes >= downVotes && upVotes >= sidewaysVotes)
            {
                direction = "Up";
                confidence = upVotes / totalVotes;
            }
            else if (downVotes >= upVotes && downVotes >= sidewaysVotes)
            {
                direction = "Down";
                confidence = downVotes / totalVotes;
            }
            else
            {
                direction = "Sideways";
                confidence = sidewaysVotes / totalVotes;
            }
        }

        var weightedSignal = new
        {
            direction = direction,
            confidence = confidence,
            upVotes = upVotes,
            downVotes = downVotes,
            sidewaysVotes = sidewaysVotes
        };

        return (matches, weightedSignal);
    }

    public async Task<(int Total, List<ArchetypeOccurrenceDto> Items)> GetOccurrencesAsync(long archetypeId, string horizon, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.ArchetypeOccurrences.AsNoTracking()
            .Where(x => x.ArchetypeId == archetypeId && x.Horizon == horizon)
            .OrderByDescending(x => x.WindowStartMs);

        var total = await query.CountAsync(ct);
        var occurrences = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = occurrences.Select(x => new ArchetypeOccurrenceDto
        {
            WindowStartMs = x.WindowStartMs,
            WindowEndMs = x.WindowEndMs,
            DistanceToCentroid = x.DistanceToCentroid,
            Label = x.Label,
            TargetReturn = x.TargetReturn
        }).ToList();

        return (total, items);
    }

    public async Task<List<ArchetypeRankingDto>> GetRankingsAsync(string symbol, string timeframe, int? windowSize, string? horizon, string sortBy, int top, CancellationToken ct = default)
    {
        var archQuery = _db.CandleArchetypes.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe);

        if (windowSize.HasValue)
            archQuery = archQuery.Where(x => x.WindowSize == windowSize.Value);

        var archetypes = await archQuery.ToListAsync(ct);
        var archIds = archetypes.Select(x => x.Id).ToList();

        var outQuery = _db.ArchetypeOutcomes.AsNoTracking()
            .Where(x => archIds.Contains(x.ArchetypeId));
            
        if (!string.IsNullOrEmpty(horizon))
            outQuery = outQuery.Where(x => x.Horizon == horizon);

        var outcomes = await outQuery.ToListAsync(ct);
        
        var outcomesByArch = outcomes.GroupBy(x => x.ArchetypeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => Math.Max(Math.Abs(o.UpRate - 0.333), Math.Abs(o.DownRate - 0.333))).FirstOrDefault());

        var rankings = new List<ArchetypeRankingDto>();

        foreach (var arch in archetypes)
        {
            var bestOutcome = outcomesByArch.GetValueOrDefault(arch.Id);
            if (bestOutcome == null) continue;

            var winRate = Math.Max(bestOutcome.UpRate, bestOutcome.DownRate);
            var dominantDir = bestOutcome.UpRate > bestOutcome.DownRate ? "Up" : "Down";
            
            var overallDirRate = dominantDir == "Up" ? bestOutcome.UpRate : bestOutcome.DownRate;
            var recentDirRate = dominantDir == "Up" ? bestOutcome.RecentUpRate : bestOutcome.RecentDownRate;

            string trend = "stable";
            if (recentDirRate > overallDirRate + 0.03) trend = "improving";
            else if (recentDirRate < overallDirRate - 0.03) trend = "declining";

            rankings.Add(new ArchetypeRankingDto
            {
                ArchetypeId = arch.Id,
                ArchetypeCode = arch.ArchetypeCode,
                WindowSize = arch.WindowSize,
                Timeframe = arch.Timeframe,
                MemberCount = arch.MemberCount,
                WinRate = winRate,
                DominantDirection = dominantDir,
                TotalSamples = bestOutcome.TotalSamples,
                RecentAccuracy = recentDirRate,
                AvgReturnPct = bestOutcome.AvgReturnPct,
                Trend = trend
            });
        }

        if (sortBy == "recentAccuracy")
            rankings = rankings.OrderByDescending(x => x.RecentAccuracy).ToList();
        else
            rankings = rankings.OrderByDescending(x => x.WinRate).ToList();

        var topRankings = rankings.Take(top).ToList();
        for (int i = 0; i < topRankings.Count; i++)
        {
            topRankings[i].Rank = i + 1;
        }

        return topRankings;
    }

    private static ArchetypeOutcomeDto MapOutcome(ArchetypeOutcome o)
    {
        return new ArchetypeOutcomeDto
        {
            Horizon = o.Horizon,
            TotalSamples = o.TotalSamples,
            UpRate = o.UpRate,
            DownRate = o.DownRate,
            SidewaysRate = o.SidewaysRate,
            AvgReturnPct = o.AvgReturnPct,
            MedianReturnPct = o.MedianReturnPct,
            MaxReturnPct = o.MaxReturnPct,
            MinReturnPct = o.MinReturnPct,
            StdDevReturnPct = o.StdDevReturnPct,
            RecentSamples = o.RecentSamples,
            RecentUpRate = o.RecentUpRate,
            RecentDownRate = o.RecentDownRate,
            RecentAvgReturnPct = o.RecentAvgReturnPct
        };
    }

    private static double CosineSimilarity(float[] a, float normA, float[] b, float normB)
    {
        if (normA <= 0 || normB <= 0) return 0;
        var n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (var i = 0; i < n; i++)
            dot += a[i] * b[i];
        return dot / (normA * normB);
    }
}

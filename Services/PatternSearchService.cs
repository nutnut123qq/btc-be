using Backend.Services.Models;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class PatternSearchService : IPatternSearchService
{
    private readonly IBinanceKlinesService _klines;
    private readonly AppDbContext _db;
    private readonly IWindowVectorIndexer _indexer;
    private readonly ILogger<PatternSearchService> _logger;

    public PatternSearchService(
        IBinanceKlinesService klines,
        AppDbContext db,
        IWindowVectorIndexer indexer,
        ILogger<PatternSearchService> logger)
    {
        _klines = klines;
        _db = db;
        _indexer = indexer;
        _logger = logger;
    }

    public async Task<PatternSearchResponse> SearchAsync(
        PatternSearchRequest request,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var lookback = Math.Clamp(request.LookbackBars, 100, 100_000);
        var windowSize = Math.Clamp(request.WindowSize, 5, 100);
        var topK = Math.Clamp(request.TopK, 1, 50);
        var minGapBars = request.MinGapBars.GetValueOrDefault(windowSize);
        var featureType = PatternVectorFeatureType.Normalize(request.FeatureType);
        if (string.IsNullOrEmpty(featureType))
        {
            throw new InvalidOperationException("Invalid featureType. Allowed: open/high/low/close/all/returns_shape.");
        }

        var rows = await _klines.GetKlinesAsync(
            symbol: request.Symbol,
            interval: request.Timeframe,
            limit: lookback,
            cancellationToken: cancellationToken);

        if (rows.Count < (windowSize * 2))
        {
            return new PatternSearchResponse
            {
                RequestId = requestId,
                Symbol = request.Symbol,
                Timeframe = request.Timeframe,
                FeatureType = featureType,
                WindowSize = windowSize,
                TopK = topK,
                ScannedWindows = 0,
                Items = []
            };
        }

        var current = rows.Skip(rows.Count - windowSize).Take(windowSize).ToList();
        var currentVec = WindowVectorIndexer.BuildVector(current, featureType);
        if (currentVec is null)
            throw new InvalidOperationException("Could not build vector from current window.");
        var currentEndTimeMs = current[current.Count - 1].OpenTimeMs;

        var expectedDim = PatternVectorFeatureType.VectorDim(featureType, windowSize);
        var dbCandidates = await _db.WindowVectors
            .Where(x =>
                x.Symbol == request.Symbol &&
                x.Timeframe == request.Timeframe &&
                x.FeatureType == featureType &&
                x.WindowSize == windowSize &&
                x.Version == WindowVectorIndexer.VectorVersion &&
                x.VectorDim == expectedDim &&
                x.EndTimeMs < currentEndTimeMs)
            .OrderByDescending(x => x.EndTimeMs)
            .Take(lookback)
            .ToListAsync(cancellationToken);

        if (dbCandidates.Count == 0)
        {
            await _indexer.BuildFullAsync(
                request.Symbol,
                request.Timeframe,
                featureType,
                lookback,
                windowSize,
                cancellationToken);
            dbCandidates = await _db.WindowVectors
                .Where(x =>
                    x.Symbol == request.Symbol &&
                    x.Timeframe == request.Timeframe &&
                    x.FeatureType == featureType &&
                    x.WindowSize == windowSize &&
                    x.Version == WindowVectorIndexer.VectorVersion &&
                    x.VectorDim == expectedDim &&
                    x.EndTimeMs < currentEndTimeMs)
                .OrderByDescending(x => x.EndTimeMs)
                .Take(lookback)
                .ToListAsync(cancellationToken);
        }

        var candidates = new List<_Candidate>();
        var fromVectorStore = dbCandidates.Count > 0;
        var startIndexByTimeMs = rows
            .Select((row, idx) => new { row.OpenTimeMs, Index = idx })
            .GroupBy(x => x.OpenTimeMs)
            .ToDictionary(g => g.Key, g => g.First().Index);
        var currentNorm = (float)Math.Sqrt(currentVec.Sum(v => v * v));
        if (dbCandidates.Count > 0)
        {
            foreach (var row in dbCandidates)
            {
                if (row.Vector.Length != currentVec.Length) continue;
                if (!startIndexByTimeMs.TryGetValue(row.StartTimeMs, out var startIndex)) continue;
                var similarity = CosineSimilarity(currentVec, currentNorm, row.Vector, row.VectorNorm);
                candidates.Add(new _Candidate(
                    StartIndex: startIndex,
                    StartTimeMs: row.StartTimeMs,
                    EndTimeMs: row.EndTimeMs,
                    Similarity: similarity));
            }
        }

        if (candidates.Count == 0)
        {
            // Fallback path when vector store is empty or no candidates matched current klines.
            for (var start = 0; start <= rows.Count - (windowSize * 2); start++)
            {
                var w = rows.Skip(start).Take(windowSize).ToList();
                var vec = WindowVectorIndexer.BuildVector(w, featureType);
                if (vec is null) continue;
                var norm = (float)Math.Sqrt(vec.Sum(v => v * v));
                var similarity = CosineSimilarity(currentVec, currentNorm, vec, norm);
                var startTime = w[0].OpenTimeMs;
                var endTime = w[^1].OpenTimeMs;
                candidates.Add(new _Candidate(start, startTime, endTime, similarity));
            }
            fromVectorStore = false;
        }

        var ranked = candidates
            .OrderByDescending(x => x.Similarity)
            .ThenByDescending(x => x.StartTimeMs)
            .ToList();

        var selected = new List<_Candidate>();
        foreach (var c in ranked)
        {
            if (selected.Count >= topK) break;
            if (selected.Any(s => Math.Abs(s.StartIndex - c.StartIndex) < minGapBars))
                continue;
            selected.Add(c);
        }

        var items = selected
            .Select((x, idx) => new PatternSearchItem
            {
                WindowId = $"w_{x.StartTimeMs}_{x.EndTimeMs}",
                Symbol = request.Symbol,
                Timeframe = request.Timeframe,
                FeatureType = featureType,
                StartTimeMs = x.StartTimeMs,
                EndTimeMs = x.EndTimeMs,
                Distance = 1.0 - x.Similarity,
                Similarity = x.Similarity,
                Rank = idx + 1
            })
            .ToList();

        var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogInformation(
            "pattern_search_done requestId={RequestId} symbol={Symbol} timeframe={Timeframe} featureType={FeatureType} scanned={Scanned} fromVectorStore={FromVectorStore} latencyMs={LatencyMs}",
            requestId,
            request.Symbol,
            request.Timeframe,
            featureType,
            candidates.Count,
            fromVectorStore,
            latencyMs);

        return new PatternSearchResponse
        {
            RequestId = requestId,
            Symbol = request.Symbol,
            Timeframe = request.Timeframe,
            FeatureType = featureType,
            WindowSize = windowSize,
            TopK = topK,
            ScannedWindows = candidates.Count,
            FromVectorStore = fromVectorStore,
            LatencyMs = latencyMs,
            Items = items
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

    private sealed record _Candidate(int StartIndex, long StartTimeMs, long EndTimeMs, double Similarity);
}

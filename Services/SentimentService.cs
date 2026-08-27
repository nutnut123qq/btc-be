using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Services;

public class SentimentService : ISentimentService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SentimentService> _logger;
    private static readonly TimeSpan LatestTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HistoryTtl = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> BullishKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bull", "bullish", "surge", "surging", "rally", "rallying", "gain", "gains", "gaining",
        "breakout", "ath", "high", "highest", "adoption", "inflow", "inflows", "accumulate",
        "accumulation", "etf", "approval", "approved", "upgrade", "partnership", "optimistic",
        "profit", "profitable", "growth", "jump", "jumped", "pump", "pumping", "outperform",
        "recovery", "rebound", "soar", "soaring", "institutional", "milestone", "record"
    };

    private static readonly HashSet<string> BearishKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bear", "bearish", "crash", "crashing", "plunge", "plunging", "dump", "dumping",
        "drop", "dropped", "dropping", "fall", "falling", "loss", "losses", "selloff",
        "sell-off", "liquidation", "liquidated", "outflow", "outflows", "hack", "hacked",
        "exploit", "fraud", "scam", "lawsuit", "sue", "sued", "sec", "ban", "banned",
        "investigation", "recession", "inflation", "hike", "default", "bankruptcy",
        "insolvent", "panic", "fear", "pessimistic", "struggle", "decline", "declining"
    };

    [ActivatorUtilitiesConstructor]
    public SentimentService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<SentimentService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public SentimentService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<SentimentService> logger)
        : this(db, httpClientFactory, new MemoryCache(new MemoryCacheOptions()), logger)
    {
    }

    public async Task<SentimentSnapshot> GetLatestSentimentAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        var cacheKey = $"sentiment:latest:{symbol}";
        if (_cache.TryGetValue(cacheKey, out SentimentSnapshot? cached) && cached != null)
        {
            return cached;
        }

        var snapshot = await _db.SentimentSnapshots
            .AsNoTracking()
            .Where(x => x.Symbol == symbol)
            .OrderByDescending(x => x.TimeMs)
            .FirstOrDefaultAsync(ct);

        if (snapshot == null)
        {
            snapshot = await CalculateAndSaveSnapshotAsync(symbol, ct);
        }

        _cache.Set(cacheKey, snapshot, LatestTtl);
        return snapshot;
    }

    public async Task<List<SentimentSnapshot>> GetSentimentHistoryAsync(string symbol, int limit, CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        var cacheKey = $"sentiment:history:{symbol}:{limit}";
        if (_cache.TryGetValue(cacheKey, out List<SentimentSnapshot>? cached) && cached != null)
        {
            return cached;
        }

        var list = await _db.SentimentSnapshots
            .AsNoTracking()
            .Where(x => x.Symbol == symbol)
            .OrderByDescending(x => x.TimeMs)
            .Take(limit)
            .ToListAsync(ct);

        _cache.Set(cacheKey, list, HistoryTtl);
        return list;
    }

    public async Task<SentimentSnapshot> CalculateAndSaveSnapshotAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        var nowUtc = DateTime.UtcNow;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1. Fetch Alternative.me Fear & Greed Index
        int fngScore = await FetchFearAndGreedAsync(ct);
        double fngNorm = (fngScore - 50.0) / 50.0; // [-1.0, +1.0]

        // 2. Compute News NLP Sentiment Score
        double newsSentimentScore = await ComputeNewsSentimentAsync(ct); // [-1.0, +1.0]

        // 3. Fetch Derivatives Metrics (Funding Rate & Taker Ratio)
        var (fundingRate, takerRatio, lsRatio) = await FetchLatestDerivativesMetricsAsync(symbol, ct);
        
        // Normalize Derivatives sentiment:
        // TakerRatio (around 1.0) -> S_taker in [-1, +1] via tanh
        double sTaker = Math.Tanh(2.0 * (takerRatio - 1.0));
        // FundingRate (typical 0.0001 per 8h, bull 0.0005) -> S_funding in [-1, +1]
        double sFunding = Math.Clamp(fundingRate / 0.0005, -1.0, 1.0);
        double derivSentimentScore = (0.5 * sTaker) + (0.5 * sFunding);

        // 4. Multi-Source Composite Macro Sentiment:
        // 40% News NLP + 30% Fear & Greed + 30% Derivatives
        double compositeScore = (0.40 * newsSentimentScore) + (0.30 * fngNorm) + (0.30 * derivSentimentScore);
        compositeScore = Math.Clamp(compositeScore, -1.0, 1.0);

        string label = compositeScore switch
        {
            <= -0.60 => "EXTREME_FEAR",
            <= -0.15 => "FEAR",
            <= 0.15 => "NEUTRAL",
            <= 0.60 => "GREED",
            _ => "EXTREME_GREED"
        };

        var snapshot = new SentimentSnapshot
        {
            Symbol = symbol,
            TimeMs = nowMs,
            FearGreedScore = fngScore,
            FundingRateZScore = fundingRate,
            LongShortRatio = lsRatio,
            NewsSentimentScore = Math.Round(newsSentimentScore, 4),
            AggregatedSentiment = Math.Round(compositeScore * 100.0, 2), // [-100, +100]
            SentimentLabel = label,
            CreatedAtUtc = nowUtc
        };

        _db.SentimentSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Calculated Sentiment for {Symbol}: FNG={FNG}, News={News:F2}, Deriv={Deriv:F2} => MacroComposite={Macro:F2} ({Label})",
            symbol, fngScore, newsSentimentScore, derivSentimentScore, compositeScore, label);

        return snapshot;
    }

    private async Task<int> FetchFearAndGreedAsync(CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var json = await http.GetStringAsync("https://api.alternative.me/fng/?limit=1", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.GetArrayLength() > 0)
            {
                var item = dataArr[0];
                if (item.TryGetProperty("value", out var valProp) && int.TryParse(valProp.GetString(), out int score))
                {
                    return score;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Alternative.me Fear & Greed index; defaulting to 50.");
        }

        return 50;
    }

    private async Task<double> ComputeNewsSentimentAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-2);
            var recentTitles = await _db.NewsArticles
                .AsNoTracking()
                .Where(a => (a.PublishedAt >= cutoff || a.FetchedAt >= cutoff))
                .OrderByDescending(a => a.PublishedAt ?? a.FetchedAt)
                .Take(40)
                .Select(a => a.Title)
                .ToListAsync(ct);

            if (recentTitles.Count == 0)
            {
                recentTitles = await _db.NewsArticles
                    .AsNoTracking()
                    .OrderByDescending(a => a.PublishedAt ?? a.FetchedAt)
                    .Take(40)
                    .Select(a => a.Title)
                    .ToListAsync(ct);
            }

            if (recentTitles.Count == 0)
                return 0.0;

            double totalScore = 0.0;
            int counted = 0;

            foreach (var title in recentTitles)
            {
                if (string.IsNullOrWhiteSpace(title)) continue;
                var words = Regex.Split(title.ToLowerInvariant(), @"\W+");
                int bullCount = words.Count(w => BullishKeywords.Contains(w));
                int bearCount = words.Count(w => BearishKeywords.Contains(w));

                if (bullCount + bearCount > 0)
                {
                    double s = (double)(bullCount - bearCount) / (bullCount + bearCount);
                    totalScore += s;
                    counted++;
                }
            }

            return counted > 0 ? Math.Clamp(totalScore / counted, -1.0, 1.0) : 0.0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error computing News NLP sentiment; defaulting to 0.0");
            return 0.0;
        }
    }

    private async Task<(double FundingRate, double TakerRatio, double LongShortRatio)> FetchLatestDerivativesMetricsAsync(
        string symbol,
        CancellationToken ct)
    {
        try
        {
            var fm = await _db.FuturesMetrics
                .AsNoTracking()
                .Where(x => x.Symbol == symbol)
                .OrderByDescending(x => x.OpenTimeMs)
                .FirstOrDefaultAsync(ct);

            if (fm != null)
            {
                double fr = fm.FundingRate ?? 0.0001;
                double taker = fm.TakerBuySellVolRatio ?? 1.0;
                double ls = fm.TopTraderLsSumRatio ?? fm.GlobalLsRatio ?? 1.0;
                return (fr, taker, ls);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching FuturesMetrics for {Symbol}", symbol);
        }

        return (0.0001, 1.0, 1.0);
    }

    private static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "BTCUSDT";
        symbol = symbol.Trim().ToUpperInvariant();
        if (!symbol.EndsWith("USDT") && !symbol.Contains('/'))
            symbol += "USDT";
        return symbol;
    }
}

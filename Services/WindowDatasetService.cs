using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class WindowDatasetService : IWindowDatasetService
{
    private readonly AppDbContext _db;
    private readonly ILogger<WindowDatasetService> _logger;

    private static readonly int[] WindowSizes = { 5, 10, 15, 20, 25 };
    private static readonly string[] Horizons = { "1h", "4h", "1d" };

    public WindowDatasetService(AppDbContext db, ILogger<WindowDatasetService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> BuildAllAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var windowSize in WindowSizes)
        {
            foreach (var horizon in Horizons)
            {
                try
                {
                    total += await BuildAsync(symbol, timeframe, windowSize, horizon, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to build window dataset for {Symbol} {Timeframe} ws={WindowSize} h={Horizon}",
                        symbol, timeframe, windowSize, horizon);
                }
            }
        }
        return total;
    }

    public async Task<int> BuildAsync(
        string symbol,
        string timeframe,
        int windowSize,
        string horizon,
        CancellationToken cancellationToken = default)
    {
        if (!WindowSizes.Contains(windowSize))
            throw new ArgumentException("windowSize must be one of: 5, 10, 15, 20, 25", nameof(windowSize));
        if (!Horizons.Contains(horizon))
            throw new ArgumentException("horizon must be one of: 1h, 4h, 1d", nameof(horizon));

        var intervalMs = ResolveIntervalMs(timeframe);

        var features = await _db.MlFeatureStores
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .OrderBy(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        var targets = await _db.PriceTargets
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        if (features.Count < windowSize + 10)
            return 0;

        var existingKeys = (await _db.WindowClassificationDatasets
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize && x.Horizon == horizon)
            .Select(x => x.WindowStartMs)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var samples = new List<WindowClassificationDataset>();
        var totalInserted = 0;
        const int maxBatch = 2000;

        for (int i = 0; i + windowSize <= features.Count; i++)
        {
            var windowBars = features.Skip(i).Take(windowSize).ToList();
            var startBar = windowBars.First();
            var endBar = windowBars.Last();

            // Ensure consecutive bars.
            if (endBar.OpenTimeMs - startBar.OpenTimeMs != (windowSize - 1) * intervalMs)
                continue;

            if (existingKeys.Contains(startBar.OpenTimeMs))
                continue;

            var vector = BuildFeatureVector(windowBars);
            if (vector == null)
                continue;

            if (!targets.TryGetValue(endBar.OpenTimeMs, out var target))
                continue;

            var (label, targetReturn) = ExtractLabelAndReturn(target, horizon);
            if (!label.HasValue)
                continue;

            var avgNullRatio = windowBars.Average(x => x.NullRatio);
            if (avgNullRatio > 0.25)
                continue;

            samples.Add(new WindowClassificationDataset
            {
                Symbol = symbol,
                Timeframe = timeframe,
                WindowSize = windowSize,
                Horizon = horizon,
                WindowStartMs = startBar.OpenTimeMs,
                WindowEndMs = endBar.OpenTimeMs,
                FeatureVector = vector,
                FeatureDim = vector.Length,
                Label = label.Value,
                TargetReturn = targetReturn,
                WindowNullRatio = avgNullRatio,
                CreatedAtUtc = DateTime.UtcNow,
            });

            if (samples.Count >= maxBatch)
            {
                _db.WindowClassificationDatasets.AddRange(samples);
                await _db.SaveChangesAsync(cancellationToken);
                totalInserted += samples.Count;
                samples.Clear();
            }
        }

        if (samples.Count > 0)
        {
            _db.WindowClassificationDatasets.AddRange(samples);
            await _db.SaveChangesAsync(cancellationToken);
            totalInserted += samples.Count;
        }

        _logger.LogInformation(
            "Window dataset built for {Symbol} {Timeframe} ws={WindowSize} h={Horizon}: {Count} samples",
            symbol, timeframe, windowSize, horizon, totalInserted);

        return totalInserted;
    }

    private static float[]? BuildFeatureVector(List<MlFeatureStore> windowBars)
    {
        var vector = new List<float>(windowBars.Count * 8);

        foreach (var bar in windowBars)
        {
            if (bar.ClosePctChange1 == null ||
                bar.BodyPct == null ||
                bar.HighLowRangePct == null ||
                bar.Rsi14 == null ||
                bar.MacdHistogramNorm == null ||
                bar.Ema12Dist == null ||
                bar.Ema26Dist == null ||
                bar.VolumeZscore == null)
            {
                return null;
            }

            vector.Add((float)bar.ClosePctChange1.Value);
            vector.Add((float)bar.BodyPct.Value);
            vector.Add((float)bar.HighLowRangePct.Value);
            vector.Add((float)bar.Rsi14.Value);
            vector.Add((float)bar.MacdHistogramNorm.Value);
            vector.Add((float)bar.Ema12Dist.Value);
            vector.Add((float)bar.Ema26Dist.Value);
            vector.Add((float)bar.VolumeZscore.Value);
        }

        return vector.ToArray();
    }

    private static (int? Label, double? Return) ExtractLabelAndReturn(PriceTarget target, string horizon)
    {
        return horizon switch
        {
            "1h" => (target.TargetDirection1h, target.TargetReturn1h),
            "4h" => (target.TargetDirection4h, target.TargetReturn4h),
            "1d" => (target.TargetDirection1d, target.TargetReturn1d),
            _ => (null, null)
        };
    }

    private static long ResolveIntervalMs(string interval) =>
        interval switch
        {
            "1m" => 60_000L,
            "5m" => 300_000L,
            "15m" => 900_000L,
            "30m" => 1_800_000L,
            "1h" => 3_600_000L,
            "4h" => 14_400_000L,
            "1d" => 86_400_000L,
            _ => 3_600_000L
        };
}

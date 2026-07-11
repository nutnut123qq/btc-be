using Backend.Data;
using Backend.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public class WindowDatasetService : IWindowDatasetService
{
    private readonly AppDbContext _db;
    private readonly ILogger<WindowDatasetService> _logger;
    private readonly IndexingOptions _options;

    private static readonly int[] WindowSizes = { 5, 10, 15, 20, 25 };
    private static readonly string[] Horizons = { "1h", "4h", "1d" };

    public WindowDatasetService(AppDbContext db, ILogger<WindowDatasetService> logger, IOptions<IndexingOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> BuildAllAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var horizon in Horizons)
        {
            total += await BuildHorizonAsync(symbol, timeframe, horizon, null, ct);
        }
        return total;
    }

    public async Task<int> BuildHorizonAsync(string symbol, string timeframe, string horizon, int? maxSamplesPerWindowSize = null, CancellationToken ct = default)
    {
        if (!Horizons.Contains(horizon))
            throw new ArgumentException("horizon must be one of: 1h, 4h, 1d", nameof(horizon));

        var total = 0;
        foreach (var windowSize in WindowSizes)
        {
            try
            {
                total += await BuildAsync(symbol, timeframe, windowSize, horizon, maxSamplesPerWindowSize, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to build window dataset for {Symbol} {Timeframe} ws={WindowSize} h={Horizon}",
                    symbol, timeframe, windowSize, horizon);
            }
        }
        return total;
    }

    public async Task<int> BuildAsync(
        string symbol,
        string timeframe,
        int windowSize,
        string horizon,
        int? maxSamples = null,
        CancellationToken cancellationToken = default)
    {
        if (!WindowSizes.Contains(windowSize))
            throw new ArgumentException("windowSize must be one of: 5, 10, 15, 20, 25", nameof(windowSize));
        if (!Horizons.Contains(horizon))
            throw new ArgumentException("horizon must be one of: 1h, 4h, 1d", nameof(horizon));

        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0) intervalMs = 3_600_000L; // default 1h for unknown interval

        // Incremental: chỉ load features/targets từ sau cửa sổ cuối cùng đã index.
        var maxExistingStart = await _db.WindowClassificationDatasets
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize && x.Horizon == horizon)
            .Select(x => (long?)x.WindowStartMs)
            .MaxAsync(cancellationToken) ?? 0L;

        var maxFeatureTime = await _db.MlFeatureStores
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .Select(x => (long?)x.OpenTimeMs)
            .MaxAsync(cancellationToken) ?? 0L;

        var maxTargetTime = await _db.PriceTargets
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .Select(x => (long?)x.OpenTimeMs)
            .MaxAsync(cancellationToken) ?? 0L;

        var maxDataTime = Math.Min(maxFeatureTime, maxTargetTime);
        var minFeatureTime = maxExistingStart > 0
            ? Math.Max(0L, maxExistingStart - (windowSize + 5) * intervalMs)
            : 0L;

        // Nếu đã có dữ liệu window, giới hạn load phần mới nhất để tránh scan toàn bộ bảng.
        if (maxExistingStart > 0 && maxDataTime > 0)
        {
            minFeatureTime = Math.Max(minFeatureTime, maxDataTime - (windowSize + 100) * intervalMs);
        }

        var features = await _db.MlFeatureStores
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.OpenTimeMs >= minFeatureTime)
            .OrderBy(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        var targets = await _db.PriceTargets
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.OpenTimeMs >= minFeatureTime)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        if (features.Count < windowSize + 10)
            return 0;

        var existingKeys = (await _db.WindowClassificationDatasets
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize && x.Horizon == horizon)
            .Select(x => x.WindowStartMs)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var batchSize = Math.Max(100, _options.WindowDatasetBatchSize);
        var samples = new List<WindowClassificationDataset>(batchSize);
        var totalInserted = 0;

        // Giới hạn số sample để tránh quá tải với timeframe nhỏ (1m, 5m).
        var stride = 1;
        if (maxSamples.HasValue && features.Count > windowSize)
        {
            var estimated = features.Count - windowSize + 1;
            stride = Math.Max(1, estimated / maxSamples.Value);
        }

        for (int i = 0; i + windowSize <= features.Count; i += stride)
        {
            var startBar = features[i];

            // Bỏ qua các cửa sổ đã index trong lần chạy trước.
            if (startBar.OpenTimeMs <= maxExistingStart)
                continue;

            var endBar = features[i + windowSize - 1];

            // Ensure consecutive bars.
            if (endBar.OpenTimeMs - startBar.OpenTimeMs != (windowSize - 1) * intervalMs)
                continue;

            if (existingKeys.Contains(startBar.OpenTimeMs))
                continue;

            var windowBars = new SliceView<MlFeatureStore>(features, i, windowSize);
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

            if (samples.Count >= batchSize)
            {
                _db.WindowClassificationDatasets.AddRange(samples);
                await _db.SaveChangesAsync(cancellationToken);
                totalInserted += samples.Count;
                samples.Clear();
                _db.ChangeTracker.Clear();
            }
        }

        if (samples.Count > 0)
        {
            _db.WindowClassificationDatasets.AddRange(samples);
            await _db.SaveChangesAsync(cancellationToken);
            totalInserted += samples.Count;
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation(
            "Window dataset built for {Symbol} {Timeframe} ws={WindowSize} h={Horizon}: {Count} samples",
            symbol, timeframe, windowSize, horizon, totalInserted);

        return totalInserted;
    }

    private static float[]? BuildFeatureVector(IReadOnlyList<MlFeatureStore> windowBars)
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
}

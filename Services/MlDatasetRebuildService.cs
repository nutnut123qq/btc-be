using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Kết quả rebuild ML datasets cho một timeframe.
/// </summary>
public class TimeframeMlRebuildResult
{
    public string Timeframe { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int MlFeatureStores { get; set; }
    public int PriceTargets { get; set; }
    public Dictionary<string, int> WindowClassificationDatasetsByHorizon { get; set; } = new();
    public int WindowClassificationDatasetsTotal => WindowClassificationDatasetsByHorizon.Values.Sum();
    public double DurationSeconds { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Kết quả tổng hợp của job rebuild ML datasets.
/// </summary>
public class MlDatasetRebuildResult
{
    public string Symbol { get; set; } = string.Empty;
    public List<string> Timeframes { get; set; } = new();
    public List<string> Horizons { get; set; } = new();
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int TotalMlFeatureStores { get; set; }
    public int TotalPriceTargets { get; set; }
    public int TotalWindowClassificationDatasets { get; set; }
    public Dictionary<string, TimeframeMlRebuildResult> PerTimeframe { get; set; } = new();
}

/// <summary>
/// Thông tin tiến trình rebuild ML datasets.
/// </summary>
public class MlDatasetRebuildProgress
{
    public string Timeframe { get; set; } = string.Empty;
    public int TimeframeIndex { get; set; }
    public int TotalTimeframes { get; set; }
    public string Stage { get; set; } = string.Empty;
    public int? SamplesProcessed { get; set; }
}

/// <summary>
/// Service rebuild toàn bộ ML datasets (MlFeatureStores, PriceTargets, WindowClassificationDatasets)
/// từ Klines và TechnicalIndicators đã có trong DB.
/// </summary>
public class MlDatasetRebuildService
{
    public static readonly string[] DefaultTimeframes = { "1m", "5m", "15m", "30m", "1h", "4h", "1d" };
    public static readonly string[] DefaultHorizons = { "1h", "4h", "1d" };

    private readonly AppDbContext _db;
    private readonly IMlDatasetService _mlDatasetService;
    private readonly IWindowDatasetService _windowDatasetService;
    private readonly ILogger<MlDatasetRebuildService> _logger;
    private readonly DataAuditCache? _auditCache;

    public MlDatasetRebuildService(
        AppDbContext db,
        IMlDatasetService mlDatasetService,
        IWindowDatasetService windowDatasetService,
        ILogger<MlDatasetRebuildService> logger,
        DataAuditCache? auditCache = null)
    {
        _db = db;
        _mlDatasetService = mlDatasetService;
        _windowDatasetService = windowDatasetService;
        _logger = logger;
        _auditCache = auditCache;
    }

    public async Task<MlDatasetRebuildResult> RebuildAsync(
        string symbol,
        IReadOnlyList<string> timeframes,
        IReadOnlyList<string> horizons,
        IProgress<MlDatasetRebuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MlDatasetRebuildResult
        {
            Symbol = symbol,
            Timeframes = timeframes.ToList(),
            Horizons = horizons.ToList(),
            StartedAtUtc = DateTime.UtcNow
        };

        // Tăng command timeout cho toàn bộ quá trình rebuild (xóa + insert hàng triệu row) khi dùng DB quan hệ.
        if (_db.Database.IsRelational())
        {
            _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
        }

        void Report(string tf, int index, string stage, int? samples = null)
        {
            progress?.Report(new MlDatasetRebuildProgress
            {
                Timeframe = tf,
                TimeframeIndex = index + 1,
                TotalTimeframes = timeframes.Count,
                Stage = stage,
                SamplesProcessed = samples
            });
        }

        for (int i = 0; i < timeframes.Count; i++)
        {
            var tf = timeframes[i];
            var tfResult = new TimeframeMlRebuildResult { Timeframe = tf };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("[MlDatasetRebuild] Bắt đầu rebuild {Symbol} {Timeframe}", symbol, tf);
                Report(tf, i, "deleting_old");

                await DeleteOldDataAsync(symbol, tf, horizons, cancellationToken);
                _auditCache?.Invalidate(symbol);

                Report(tf, i, "features_targets");
                _logger.LogInformation("[MlDatasetRebuild] Đang build features/targets {Symbol} {Timeframe}", symbol, tf);
                // ponytail: BuildAsync now caps bars-per-call (memory safety), so loop until it reports
                // 0 new rows — that fully catches up any gap (e.g. 1m's ~3M bars) in bounded chunks.
                while (await _mlDatasetService.BuildAsync(symbol, tf, cancellationToken) > 0
                    && !cancellationToken.IsCancellationRequested)
                {
                }

                tfResult.MlFeatureStores = await _db.MlFeatureStores
                    .CountAsync(x => x.Symbol == symbol && x.Timeframe == tf, cancellationToken);
                tfResult.PriceTargets = await _db.PriceTargets
                    .CountAsync(x => x.Symbol == symbol && x.Timeframe == tf, cancellationToken);

                Report(tf, i, "features_targets", tfResult.MlFeatureStores + tfResult.PriceTargets);

                const int maxSamplesPerWindowSize = 20_000;
                foreach (var horizon in horizons)
                {
                    Report(tf, i, $"window_dataset_{horizon}");
                    _logger.LogInformation("[MlDatasetRebuild] Đang build window dataset {Symbol} {Timeframe} h={Horizon}", symbol, tf, horizon);
                    var count = await _windowDatasetService.BuildHorizonAsync(symbol, tf, horizon, maxSamplesPerWindowSize, cancellationToken);
                    tfResult.WindowClassificationDatasetsByHorizon[horizon] = count;
                }

                tfResult.Status = "ok";
                _logger.LogInformation(
                    "[MlDatasetRebuild] Hoàn thành {Symbol} {Timeframe}: features={Features}, targets={Targets}, windows={Windows} trong {ElapsedMs}ms",
                    symbol, tf, tfResult.MlFeatureStores, tfResult.PriceTargets, tfResult.WindowClassificationDatasetsTotal, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                tfResult.Status = "error";
                tfResult.Error = ex.Message;
                _logger.LogError(ex, "[MlDatasetRebuild] Lỗi khi rebuild {Symbol} {Timeframe}", symbol, tf);
            }
            finally
            {
                sw.Stop();
                tfResult.DurationSeconds = sw.Elapsed.TotalSeconds;
                result.PerTimeframe[tf] = tfResult;
            }
        }

        result.CompletedAtUtc = DateTime.UtcNow;
        result.DurationSeconds = (result.CompletedAtUtc - result.StartedAtUtc).TotalSeconds;
        result.TotalMlFeatureStores = result.PerTimeframe.Values.Sum(r => r.MlFeatureStores);
        result.TotalPriceTargets = result.PerTimeframe.Values.Sum(r => r.PriceTargets);
        result.TotalWindowClassificationDatasets = result.PerTimeframe.Values.Sum(r => r.WindowClassificationDatasetsTotal);

        if (_db.Database.IsRelational())
        {
            _db.Database.SetCommandTimeout(null);
        }

        _auditCache?.Invalidate(symbol);
        return result;
    }

    private async Task DeleteOldDataAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<string> horizons,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MlDatasetRebuild] Đang xóa dữ liệu cũ {Symbol} {Timeframe}", symbol, timeframe);

        if (_db.Database.IsRelational())
        {
            // Tăng command timeout cho DELETE lớn (đặc biệt 1m/5m có hàng triệu row).
            _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

            await _db.MlFeatureStores
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ExecuteDeleteAsync(cancellationToken);

            await _db.PriceTargets
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ExecuteDeleteAsync(cancellationToken);

            if (horizons.Count == DefaultHorizons.Length)
            {
                await _db.WindowClassificationDatasets
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                foreach (var horizon in horizons)
                {
                    await _db.WindowClassificationDatasets
                        .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.Horizon == horizon)
                        .ExecuteDeleteAsync(cancellationToken);
                }
            }
        }
        else
        {
            // EF Core InMemory không hỗ trợ ExecuteDelete; xóa client-side.
            var features = await _db.MlFeatureStores
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken);
            _db.MlFeatureStores.RemoveRange(features);

            var targets = await _db.PriceTargets
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken);
            _db.PriceTargets.RemoveRange(targets);

            List<WindowClassificationDataset> windows;
            if (horizons.Count == DefaultHorizons.Length)
            {
                windows = await _db.WindowClassificationDatasets
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                    .ToListAsync(cancellationToken);
            }
            else
            {
                windows = await _db.WindowClassificationDatasets
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && horizons.Contains(x.Horizon))
                    .ToListAsync(cancellationToken);
            }
            _db.WindowClassificationDatasets.RemoveRange(windows);

            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();
        }
    }
}

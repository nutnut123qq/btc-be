using System.Text.Json;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Re-index toàn bộ dữ liệu phân tích (patterns, window vectors, volume stats,
/// technical indicators, pattern sequences) từ Klines đã backfill trong DB.
/// Không gọi Binance API để tránh rate limit.
/// Hỗ trợ chạy song song theo timeframe.
/// </summary>
public class FullReindexService
{
    private readonly AppDbContext _db;
    private readonly ICandlePatternIndexer _patternIndexer;
    private readonly IWindowVectorIndexer _vectorIndexer;
    private readonly CandleVolumeIndexer _volumeIndexer;
    private readonly TechnicalIndicatorIndexer _techIndexer;
    private readonly CandlePatternSequenceIndexer _sequenceIndexer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IndexingOptions _options;
    private readonly ILogger<FullReindexService> _logger;

    public static readonly string[] DefaultTimeframes = { "1m", "5m", "15m", "30m", "1h", "4h", "1d" };
    public static readonly string[] FeatureTypes = { "open", "high", "low", "close", "all", "returns_shape", "returns_log", "volume_norm", "volatility", "trend" };
    public static readonly int[] WindowSizes = { 10, 15, 25 };

    public FullReindexService(
        AppDbContext db,
        ICandlePatternIndexer patternIndexer,
        IWindowVectorIndexer vectorIndexer,
        CandleVolumeIndexer volumeIndexer,
        TechnicalIndicatorIndexer techIndexer,
        CandlePatternSequenceIndexer sequenceIndexer,
        IServiceScopeFactory scopeFactory,
        IOptions<IndexingOptions> options,
        ILogger<FullReindexService> logger)
    {
        _db = db;
        _patternIndexer = patternIndexer;
        _vectorIndexer = vectorIndexer;
        _volumeIndexer = volumeIndexer;
        _techIndexer = techIndexer;
        _sequenceIndexer = sequenceIndexer;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Re-index toàn bộ các bảng phân tích từ Klines trong DB.
    /// </summary>
    /// <param name="symbol">Symbol cần re-index.</param>
    /// <param name="timeframes">Danh sách khung thờ gian.</param>
    /// <param name="windowLookbackBars">Số nến gần nhất dùng cho window vectors (mặc định 5000).</param>
    public async Task<FullReindexResult> ReindexAsync(
        string symbol,
        IReadOnlyList<string> timeframes,
        int windowLookbackBars = 5000,
        IProgress<FullReindexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new FullReindexResult
        {
            Symbol = symbol,
            Timeframes = timeframes.ToList(),
            StartedAtUtc = DateTime.UtcNow
        };

        void Report(string tf, int index, string stage, int? rows = null)
        {
            progress?.Report(new FullReindexProgress
            {
                Timeframe = tf,
                TimeframeIndex = index + 1,
                TotalTimeframes = timeframes.Count,
                Stage = stage,
                RowsProcessed = rows
            });
        }

        if (_options.EnableParallelTimeframes && timeframes.Count > 1)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelTimeframes),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(timeframes, parallelOptions, async (tf, ct) =>
            {
                // Mỗi timeframe chạy trong scope riêng với DbContext riêng.
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<FullReindexService>();
                var tfResult = await service.ReindexTimeframeAsync(symbol, tf, windowLookbackBars, Report, progress, ct);
                lock (result.Results)
                {
                    result.Results[tf] = tfResult;
                }
            });
        }
        else
        {
            for (var tfIndex = 0; tfIndex < timeframes.Count; tfIndex++)
            {
                var tf = timeframes[tfIndex];
                var tfResult = await ReindexTimeframeAsync(symbol, tf, windowLookbackBars, Report, progress, cancellationToken);
                result.Results[tf] = tfResult;
            }
        }

        result.CompletedAtUtc = DateTime.UtcNow;
        result.DurationSeconds = (result.CompletedAtUtc - result.StartedAtUtc).TotalSeconds;

        // Tính totals
        result.Totals = new ReindexTotals
        {
            CandlePatterns = result.Results.Values.Sum(r => r.CandlePatterns),
            WindowVectors = result.Results.Values.Sum(r => r.WindowVectors),
            VolumeStats = result.Results.Values.Sum(r => r.VolumeStats),
            TechnicalIndicators = result.Results.Values.Sum(r => r.TechnicalIndicators),
            PatternSequences = result.Results.Values.Sum(r => r.PatternSequences)
        };
        result.TotalRowsIndexed = result.Totals.CandlePatterns
            + result.Totals.WindowVectors
            + result.Totals.VolumeStats
            + result.Totals.TechnicalIndicators
            + result.Totals.PatternSequences;

        return result;
    }

    internal async Task<TimeframeReindexResult> ReindexTimeframeAsync(
        string symbol,
        string timeframe,
        int windowLookbackBars,
        Action<string, int, string, int?> report,
        IProgress<FullReindexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tfResult = new TimeframeReindexResult { Timeframe = timeframe };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("[FullReindex] Bắt đầu xử lý {Symbol} {Timeframe}", symbol, timeframe);

            report(timeframe, 0, "loading", null);

            // 1. Load Klines từ DB (không gọi Binance)
            var klines = await LoadKlinesFromDbAsync(symbol, timeframe, cancellationToken);
            tfResult.Klines = klines.Count;
            tfResult.StartTimeMs = klines.FirstOrDefault()?.OpenTimeMs;
            tfResult.EndTimeMs = klines.LastOrDefault()?.OpenTimeMs;

            if (klines.Count == 0)
            {
                _logger.LogWarning("[FullReindex] Không có klines trong DB cho {Symbol} {Timeframe}", symbol, timeframe);
                tfResult.Status = "no_data";
                return tfResult;
            }

            var klineDtos = klines.Select(KlineMapper.ToDto).ToList();

            report(timeframe, 0, "cleanup", null);

            // 2. Xóa dữ liệu cũ cho symbol/timeframe để re-index sạch sẽ
            await CleanupOldDataAsync(symbol, timeframe, cancellationToken);

            // 3. Candle patterns (bulk insert, không kiểm tra duplicate vì đã xóa dữ liệu cũ)
            report(timeframe, 0, "patterns", null);
            _logger.LogInformation("[FullReindex] Đang index candle patterns {Symbol} {Timeframe} ({Klines} klines)", symbol, timeframe, klines.Count);
            tfResult.CandlePatterns = await _patternIndexer.IndexAsync(symbol, timeframe, klineDtos, cancellationToken);

            // 4. Window vectors: tất cả feature types × window sizes trong một lần gọi
            var windowKlines = klineDtos.Count > windowLookbackBars && windowLookbackBars > 0
                ? IndexingRangeHelper.ApplyLookback(klineDtos, windowLookbackBars)
                : klineDtos;
            tfResult.WindowLookbackBars = windowKlines.Count;
            report(timeframe, 0, "window_vectors", windowKlines.Count);
            _logger.LogInformation(
                "[FullReindex] Đang index window vectors {Symbol} {Timeframe} (lookback {Lookback}/{Total})",
                symbol, timeframe, windowKlines.Count, klineDtos.Count);
            tfResult.WindowVectors = await _vectorIndexer.BuildAllForTimeframeAsync(
                symbol, timeframe, windowKlines, FeatureTypes, WindowSizes, cancellationToken);

            // 5. Volume stats (batch insert)
            report(timeframe, 0, "volume_stats", null);
            _logger.LogInformation("[FullReindex] Đang index volume stats {Symbol} {Timeframe}", symbol, timeframe);
            tfResult.VolumeStats = await _volumeIndexer.IndexAsync(symbol, timeframe, klineDtos, cancellationToken);

            // 6. Technical indicators (dùng klines đã load, chia chunk nếu quá lớn)
            report(timeframe, 0, "technical_indicators", null);
            _logger.LogInformation("[FullReindex] Đang index technical indicators {Symbol} {Timeframe}", symbol, timeframe);
            tfResult.TechnicalIndicators = await BuildTechnicalIndicatorsChunkedAsync(symbol, timeframe, klineDtos, cancellationToken);

            // 7. Pattern sequences (dựa trên CandlePatterns vừa tạo, batch insert)
            report(timeframe, 0, "pattern_sequences", null);
            _logger.LogInformation("[FullReindex] Đang index pattern sequences {Symbol} {Timeframe}", symbol, timeframe);
            tfResult.PatternSequences = await _sequenceIndexer.IndexAsync(symbol, timeframe, 0, cancellationToken);

            tfResult.Status = "ok";
            _logger.LogInformation(
                "[FullReindex] Hoàn thành {Symbol} {Timeframe}: patterns={Patterns}, vectors={Vectors}, volume={Volume}, tech={Tech}, sequences={Sequences} trong {ElapsedMs}ms",
                symbol, timeframe, tfResult.CandlePatterns, tfResult.WindowVectors, tfResult.VolumeStats,
                tfResult.TechnicalIndicators, tfResult.PatternSequences, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            tfResult.Status = "error";
            tfResult.Error = ex.Message;
            _logger.LogError(ex, "[FullReindex] Lỗi khi xử lý {Symbol} {Timeframe}", symbol, timeframe);
        }
        finally
        {
            stopwatch.Stop();
            tfResult.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            ReleaseMemory();
        }

        return tfResult;
    }

    private async Task<int> BuildTechnicalIndicatorsChunkedAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken)
    {
        if (klines.Count <= _options.MaxInMemoryKlines)
        {
            return await _techIndexer.IndexAsync(symbol, timeframe, klines, cancellationToken);
        }

        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0)
        {
            _logger.LogWarning("[FullReindex] Invalid timeframe {Timeframe} for chunked technical indicators", timeframe);
            return 0;
        }

        var warmupBars = _options.TechnicalIndicatorWarmupBars;
        var chunks = IndexingRangeHelper.BuildChunks(
            klines[0].OpenTimeMs, klines[^1].OpenTimeMs, intervalMs, _options.MaxInMemoryKlines, warmupBars);

        var total = 0;
        foreach (var (chunkStartMs, chunkEndMs) in chunks)
        {
            var chunk = klines.Where(k => k.OpenTimeMs >= chunkStartMs && k.OpenTimeMs <= chunkEndMs).ToList();
            if (chunk.Count < 50) continue;

            var indexed = await _techIndexer.IndexAsync(symbol, timeframe, chunk, cancellationToken);
            total += indexed;
            _logger.LogInformation(
                "[FullReindex] Technical indicators chunk [{Start}..{End}] for {Symbol} {Timeframe}: {Indexed} rows",
                chunkStartMs, chunkEndMs, symbol, timeframe, indexed);
        }

        return total;
    }

    private async Task<List<Kline>> LoadKlinesFromDbAsync(string symbol, string timeframe, CancellationToken cancellationToken)
    {
        return await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderBy(k => k.OpenTimeMs)
            .ToListAsync(cancellationToken);
    }

    private async Task CleanupOldDataAsync(string symbol, string timeframe, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[FullReindex] Đang xóa dữ liệu cũ {Symbol} {Timeframe}", symbol, timeframe);

        if (_db.Database.IsRelational())
        {
            // Tăng command timeout để xóa hàng triệu row mà không bị Npgsql timeout.
            _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

            try
            {
                await _db.CandlePatterns
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                    .ExecuteDeleteAsync(cancellationToken);

                await _db.CandleVolumeStats
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                    .ExecuteDeleteAsync(cancellationToken);

                await _db.TechnicalIndicators
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                    .ExecuteDeleteAsync(cancellationToken);

                await _db.PatternSequences
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                    .ExecuteDeleteAsync(cancellationToken);

                await _db.WindowVectors
                    .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            finally
            {
                _db.Database.SetCommandTimeout(null);
            }
        }
        else
        {
            // EF Core InMemory không hỗ trợ ExecuteDelete; xóa client-side.
            var patterns = await _db.CandlePatterns
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken);
            _db.CandlePatterns.RemoveRange(patterns);

            var volume = await _db.CandleVolumeStats
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken);
            _db.CandleVolumeStats.RemoveRange(volume);

            var tech = await _db.TechnicalIndicators
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken);
            _db.TechnicalIndicators.RemoveRange(tech);

            var sequences = await _db.PatternSequences
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken);
            _db.PatternSequences.RemoveRange(sequences);

            var vectors = await _db.WindowVectors
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
                .ToListAsync(cancellationToken);
            _db.WindowVectors.RemoveRange(vectors);

            await _db.SaveChangesAsync(cancellationToken);
            _db.ChangeTracker.Clear();
        }
    }

    private void ReleaseMemory()
    {
        try
        {
            var before = GC.GetTotalMemory(false);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
            var after = GC.GetTotalMemory(false);
            _logger.LogDebug("[FullReindex] Memory released: {Before:N0} -> {After:N0} bytes", before, after);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "[FullReindex] GC.Collect failed");
        }
    }
}

public class FullReindexResult
{
    public string Symbol { get; set; } = string.Empty;
    public List<string> Timeframes { get; set; } = new();
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int TotalRowsIndexed { get; set; }
    public ReindexTotals Totals { get; set; } = new();
    public Dictionary<string, TimeframeReindexResult> Results { get; set; } = new();
}

public class ReindexTotals
{
    public int CandlePatterns { get; set; }
    public int WindowVectors { get; set; }
    public int VolumeStats { get; set; }
    public int TechnicalIndicators { get; set; }
    public int PatternSequences { get; set; }
}

public class TimeframeReindexResult
{
    public string Timeframe { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Klines { get; set; }
    public long? StartTimeMs { get; set; }
    public long? EndTimeMs { get; set; }
    public int CandlePatterns { get; set; }
    public int WindowVectors { get; set; }
    public Dictionary<string, int> WindowVectorsByFeature { get; set; } = new();
    public int VolumeStats { get; set; }
    public int TechnicalIndicators { get; set; }
    public int PatternSequences { get; set; }
    public int WindowLookbackBars { get; set; }
    public double DurationSeconds { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Progress report for a full re-index operation.
/// Moved here from the removed FullReindexJobQueue glue class.
/// </summary>
public class FullReindexProgress
{
    public string Timeframe { get; set; } = string.Empty;
    public int TimeframeIndex { get; set; }
    public int TotalTimeframes { get; set; }
    public string Stage { get; set; } = string.Empty;
    public int? RowsProcessed { get; set; }
}

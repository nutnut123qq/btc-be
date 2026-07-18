using System.Diagnostics;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Tự động rebuild các index (pattern, window vector, volume stats, technical indicators, pattern sequences) định kỳ
/// cho đầy đủ 7 khung thởi gian: 1m, 5m, 15m, 30m, 1h, 4h, 1d.
/// Dùng dữ liệu từ DB để incremental; hỗ trợ chạy song song các timeframe.
/// </summary>
public class IndexingBackgroundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IndexingBackgroundWorker> _logger;
    private readonly IndexingOptions _options;

    private static readonly string[] Timeframes = { "1m", "5m", "15m", "30m", "1h", "4h", "1d" };
    private static readonly string[] FeatureTypes = { "open", "high", "low", "close", "all", "returns_shape", "returns_log", "volume_norm", "volatility", "trend" };
    private static readonly int[] WindowSizes = { 10, 15, 25 };
    private const string Symbol = "BTCUSDT";

    public IndexingBackgroundWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<IndexingBackgroundWorker> logger,
        IOptions<IndexingOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay 30s để app khởi động xong
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Indexing background cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // shutdown
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting indexing cycle for {Symbol} across timeframes: {Timeframes}", Symbol, string.Join(", ", Timeframes));

        if (_options.EnableParallelTimeframes && Timeframes.Length > 1)
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelTimeframes),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(Timeframes, parallelOptions, async (tf, ct) =>
            {
                try
                {
                    await IndexTimeframeAsync(Symbol, tf, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index timeframe {Symbol} {Timeframe}", Symbol, tf);
                }
            });
        }
        else
        {
            foreach (var tf in Timeframes)
            {
                try
                {
                    await IndexTimeframeAsync(Symbol, tf, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index timeframe {Symbol} {Timeframe}", Symbol, tf);
                }
            }
        }

        // Market metrics (funding rate, open interest, liquidations) — không phụ thuộc timeframe chính
        await IndexMarketMetricsAsync(cancellationToken);

        _logger.LogInformation("Completed indexing cycle for {Symbol}", Symbol);
    }

    private async Task IndexTimeframeAsync(string symbol, string timeframe, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var patternIndexer = scope.ServiceProvider.GetRequiredService<ICandlePatternIndexer>();
        var vectorIndexer = scope.ServiceProvider.GetRequiredService<IWindowVectorIndexer>();
        var volumeIndexer = scope.ServiceProvider.GetRequiredService<CandleVolumeIndexer>();
        var techIndexer = scope.ServiceProvider.GetRequiredService<TechnicalIndicatorIndexer>();
        var patternSeqIndexer = scope.ServiceProvider.GetRequiredService<CandlePatternSequenceIndexer>();

        var stopwatch = Stopwatch.StartNew();

        // Load Klines từ DB một lần cho cả indexer trong timeframe.
        var allKlines = await LoadKlinesAsync(db, symbol, timeframe, cancellationToken);

        if (allKlines.Count == 0)
        {
            _logger.LogWarning("No klines in DB for {Symbol} {Timeframe}; skipping indexing", symbol, timeframe);
            return;
        }

        // Giới hạn số nến trong memory nếu vượt quá cấu hình.
        var isStreaming = _options.EnableStreamingIndexing && allKlines.Count > _options.MaxInMemoryKlines;
        var klines = isStreaming
            ? IndexingRangeHelper.ApplyLookback(allKlines, _options.MaxInMemoryKlines)
            : allKlines;

        if (isStreaming)
        {
            _logger.LogWarning(
                "Timeframe {Symbol} {Timeframe} has {Total} klines, limiting in-memory processing to {Limit} most recent bars",
                symbol, timeframe, allKlines.Count, klines.Count);
        }

        // 1. Candle patterns
        try
        {
            var indexed = await patternIndexer.IndexAsync(symbol, timeframe, klines, cancellationToken);
            _logger.LogInformation("Auto-indexed {Indexed} candle patterns for {Symbol} {Timeframe}", indexed, symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-index candle patterns for {Symbol} {Timeframe}", symbol, timeframe);
        }

        // 2. Window vectors cho tất cả feature types × window sizes trong MỘT lần gọi
        try
        {
            var vectorKlines = IndexingRangeHelper.ApplyLookback(klines, _options.WindowVectorLookbackBars);
            var upserted = await vectorIndexer.BuildAllForTimeframeAsync(
                symbol, timeframe, vectorKlines, FeatureTypes, WindowSizes, cancellationToken);
            _logger.LogInformation(
                "Auto-built {Upserted} window vectors for {Symbol} {Timeframe}",
                upserted, symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-build window vectors for {Symbol} {Timeframe}", symbol, timeframe);
        }

        // 3. Volume stats
        try
        {
            var volumeKlines = IndexingRangeHelper.ApplyLookback(klines, _options.VolumeStatsLookbackBars);
            var indexed = await volumeIndexer.IndexAsync(symbol, timeframe, volumeKlines, cancellationToken);
            _logger.LogInformation("Auto-indexed {Indexed} volume stats for {Symbol} {Timeframe}", indexed, symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-index volume stats for {Symbol} {Timeframe}", symbol, timeframe);
        }

        // 4. Technical indicators
        try
        {
            int indexed;
            if (isStreaming)
            {
                // Khi streaming, dùng DB incremental để đảm bảo các nến cũ hơn cũng được tính.
                indexed = await techIndexer.IndexAsync(symbol, timeframe, cancellationToken);
            }
            else
            {
                indexed = await techIndexer.IndexAsync(symbol, timeframe, klines, cancellationToken);
            }
            _logger.LogInformation("Auto-indexed {Indexed} technical indicators for {Symbol} {Timeframe}", indexed, symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-index technical indicators for {Symbol} {Timeframe}", symbol, timeframe);
        }

        // 5. Pattern sequences
        try
        {
            var seqLookback = isStreaming ? _options.PatternSequenceLookbackBars : 0;
            var indexed = await patternSeqIndexer.IndexAsync(symbol, timeframe, seqLookback, cancellationToken);
            _logger.LogInformation("Auto-indexed {Indexed} pattern sequences for {Symbol} {Timeframe}", indexed, symbol, timeframe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-index pattern sequences for {Symbol} {Timeframe}", symbol, timeframe);
        }

        stopwatch.Stop();
        ReleaseMemory();
        _logger.LogInformation(
            "Finished indexing {Symbol} {Timeframe} in {ElapsedMs}ms (processed {Bars} bars, streaming={Streaming})",
            symbol, timeframe, stopwatch.ElapsedMilliseconds, klines.Count, isStreaming);
    }

    private static async Task<IReadOnlyList<KlineDto>> LoadKlinesAsync(
        AppDbContext db,
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        return await db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderBy(k => k.OpenTimeMs)
            .Select(k => KlineMapper.ToDto(k))
            .ToListAsync(cancellationToken);
    }

    private void ReleaseMemory()
    {
        try
        {
            var before = GC.GetTotalMemory(false);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
            var after = GC.GetTotalMemory(false);
            _logger.LogDebug("Memory released: {Before:N0} -> {After:N0} bytes", before, after);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "GC.Collect failed");
        }
    }

    private async Task IndexMarketMetricsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var marketIndexer = scope.ServiceProvider.GetRequiredService<MarketMetricsIndexer>();

        try
        {
            await marketIndexer.IndexFundingRateAsync(Symbol, cancellationToken);
            await marketIndexer.IndexOpenInterestAsync(Symbol, "1h", cancellationToken);
            // Liquidations: endpoint /fapi/v1/forceOrders yêu cầu API key (401 Unauthorized) — tắt để tránh log spam.
            // await marketIndexer.IndexLiquidationsAsync(Symbol, cancellationToken);
            _logger.LogInformation("Auto-indexed market metrics for {Symbol}", Symbol);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-index market metrics for {Symbol}", Symbol);
        }
    }
}

using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Endpoint thủ công để chạy các indexer (dùng cho backfill hoặc debug).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Backend.Filters.AdminGuard]
public class IndexerController : ControllerBase
{
    private readonly TechnicalIndicatorIndexer _techIndexer;
    private readonly IMlDatasetService _mlDatasetService;
    private readonly FullReindexService _fullReindexService;
    private readonly MlDatasetRebuildService _mlDatasetRebuildService;
    private readonly ILogger<IndexerController> _logger;
    private readonly DataAuditCache? _auditCache;
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public IndexerController(
        TechnicalIndicatorIndexer techIndexer,
        IMlDatasetService mlDatasetService,
        FullReindexService fullReindexService,
        MlDatasetRebuildService mlDatasetRebuildService,
        ILogger<IndexerController> logger,
        DataAuditCache? auditCache = null,
        ProductionTimeframePolicy? timeframePolicy = null)
    {
        _techIndexer = techIndexer;
        _mlDatasetService = mlDatasetService;
        _fullReindexService = fullReindexService;
        _mlDatasetRebuildService = mlDatasetRebuildService;
        _logger = logger;
        _auditCache = auditCache;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
    }

    /// <summary>
    /// Rebuild all BTC derived technical tables, then rebuild downstream ML
    /// datasets from those corrected indicators. Raw Klines are never deleted.
    /// A literal confirmation token prevents accidental destructive calls.
    /// </summary>
    [HttpPost("rebuild-derived")]
    public async Task<IActionResult> RebuildDerived(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null,
        [FromQuery] string? confirm = null,
        CancellationToken cancellationToken = default)
    {
        var timeframes = string.IsNullOrWhiteSpace(timeframe)
            ? _timeframePolicy.Active
            : new[] { ProductionTimeframePolicy.Canonicalize(timeframe) };

        var inactive = timeframes.FirstOrDefault(tf => !_timeframePolicy.IsActive(tf));
        if (inactive is not null)
            return InactiveTimeframe(inactive);

        const string confirmation = "REBUILD_DERIVED";
        if (!string.Equals(confirm, confirmation, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                code = "CONFIRMATION_REQUIRED",
                message = $"Repeat with confirm={confirmation}. Raw Klines are preserved; derived technical and ML tables for the selected BTC timeframes are rebuilt.",
                symbol,
                timeframes
            });
        }

        var technical = await _fullReindexService.ReindexAsync(
            symbol, timeframes, cancellationToken: cancellationToken);
        var successful = technical.Results.Values
            .Where(result => result.Status == "ok")
            .Select(result => result.Timeframe)
            .ToArray();

        MlDatasetRebuildResult? ml = null;
        if (successful.Length > 0)
        {
            ml = await _mlDatasetRebuildService.RebuildAsync(
                symbol,
                successful,
                MlDatasetRebuildService.DefaultHorizons,
                cancellationToken: cancellationToken);
        }

        _auditCache?.Invalidate(symbol);
        var mlSucceeded = ml is not null
            && successful.All(tf => ml.PerTimeframe.TryGetValue(tf, out var result) && result.Status == "ok");
        return Ok(new
        {
            status = successful.Length == timeframes.Count && mlSucceeded ? "ok" : "partial",
            rawKlinesPreserved = true,
            technical,
            ml
        });
    }

    /// <summary>
    /// Chạy TechnicalIndicatorIndexer cho một hoặc nhiều timeframe.
    /// Mặc định chạy cho BTCUSDT trên các timeframe production được cấu hình.
    /// </summary>
    [HttpPost("technical-indicators")]
    public async Task<IActionResult> IndexTechnicalIndicators(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null,
        CancellationToken cancellationToken = default)
    {
        var timeframes = string.IsNullOrWhiteSpace(timeframe)
            ? _timeframePolicy.Active
            : new[] { ProductionTimeframePolicy.Canonicalize(timeframe) };

        var inactive = timeframes.FirstOrDefault(tf => !_timeframePolicy.IsActive(tf));
        if (inactive is not null)
            return InactiveTimeframe(inactive);

        var results = new Dictionary<string, object>();
        foreach (var tf in timeframes)
        {
            try
            {
                var indexed = await _techIndexer.IndexAsync(symbol, tf, cancellationToken);
                results[tf] = new { indexed, status = "ok" };
                _logger.LogInformation("Manually indexed {Indexed} technical indicators for {Symbol} {Timeframe}", indexed, symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index technical indicators for {Symbol} {Timeframe}", symbol, tf);
                results[tf] = new { indexed = 0, status = "error", error = ex.Message };
            }
        }

        _auditCache?.Invalidate(symbol);
        return Ok(new { symbol, timeframes = results.Keys, results });
    }

    /// <summary>
    /// Rebuild ML features and price targets for one or more timeframes.
    /// </summary>
    [HttpPost("ml-dataset")]
    public async Task<IActionResult> RebuildMlDataset(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null,
        CancellationToken cancellationToken = default)
    {
        var timeframes = string.IsNullOrWhiteSpace(timeframe)
            ? _timeframePolicy.Active
            : new[] { ProductionTimeframePolicy.Canonicalize(timeframe) };

        var inactive = timeframes.FirstOrDefault(tf => !_timeframePolicy.IsActive(tf));
        if (inactive is not null)
            return InactiveTimeframe(inactive);

        var results = new Dictionary<string, object>();
        foreach (var tf in timeframes)
        {
            try
            {
                var count = await _mlDatasetService.BuildAsync(symbol, tf, cancellationToken);
                results[tf] = new { count, status = "ok" };
                _logger.LogInformation("Manually rebuilt ML dataset for {Symbol} {Timeframe}: {Count} rows", symbol, tf, count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rebuild ML dataset for {Symbol} {Timeframe}", symbol, tf);
                results[tf] = new { count = 0, status = "error", error = ex.Message };
            }
        }

        _auditCache?.Invalidate(symbol);
        return Ok(new { symbol, timeframes = results.Keys, results });
    }

    private BadRequestObjectResult InactiveTimeframe(string timeframe) => BadRequest(new ApiErrorEnvelope
    {
        Code = "INACTIVE_TIMEFRAME",
        Message = _timeframePolicy.InactiveMessage(timeframe),
        Retryable = false,
        RequestId = HttpContext.TraceIdentifier
    });
}

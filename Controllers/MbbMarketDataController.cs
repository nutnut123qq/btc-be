using Backend.Data;
using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Controllers;

/// <summary>
/// MBB read endpoints (MBB-06A): candles, current conditions and data quality.
///
/// Same symbol-gate contract as <see cref="MbbPositionController"/>: MBB is a
/// known-but-DISABLED instrument in <see cref="ProductionSymbolPolicy"/> and the
/// global <see cref="ProductionSymbolScopeFilter"/> rejects any `symbol` argument,
/// so these actions take NO symbol input — the lane is locked to MBB server-side.
/// Serving MBB through the generic /api/market routes is impossible while the
/// filter is up (BinanceKlinesService would hit api.binance.com for an HOSE
/// symbol), which is exactly why these dedicated routes exist.
///
/// Read-only actions carry no AdminGuard, matching the convention for market
/// data GETs; mutations live on MbbAdminController.
///
/// EQX: fixed-MBB alias of /api/equities/{symbol}/* — same services, same DTO
/// projections (observations gain the additive `symbol` field).
/// </summary>
[ApiController]
[Route("api/mbb")]
public sealed class MbbMarketDataController : ControllerBase
{
    internal const int DefaultLimit = 500;
    internal const int MaxLimit = 2000;

    private readonly AppDbContext _db;
    private readonly MbbDataQualityService _quality;
    private readonly CurrentConditionsService _conditions;
    private readonly InstrumentDescriptor _instrument;

    // CurrentConditionsService is not DI-registered (same pattern as
    // ResearchCurrentConditionsController): it is built in place from the
    // "AIService" named HttpClient + evidence catalog, sharing the injected
    // IMemoryCache so conditions cache keys stay per symbol:timeframe.
    public MbbMarketDataController(
        AppDbContext db,
        MbbDataQualityService quality,
        IHttpClientFactory httpClientFactory,
        IResearchEvidenceCatalog evidenceCatalog,
        ILogger<CurrentConditionsService> conditionsLogger,
        IMemoryCache? cache = null,
        CurrentConditionsService? conditions = null,
        InstrumentCatalog? instruments = null)
    {
        _db = db;
        _quality = quality;
        _conditions = conditions
            ?? new CurrentConditionsService(httpClientFactory, evidenceCatalog, conditionsLogger, cache);
        _instrument = (instruments ?? InstrumentCatalog.Default).Get(MbbTechnicalRecomputeService.SupportedSymbol)
            ?? throw new InvalidOperationException("MBB is not registered in the instrument catalog.");
    }

    /// <summary>
    /// MBB candles from InstrumentCandles (VCI, adjusted price basis, HOSE
    /// session timestamps). Ascending open-time order like Binance klines.
    /// Binance-only fields on the response are null — never fabricated.
    /// </summary>
    [HttpGet("candles")]
    [ProducesResponseType(typeof(IReadOnlyList<MbbCandleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<MbbCandleDto>>> GetCandles(
        [FromQuery] string? timeframe,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] long? endTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTimeframe(timeframe, out var tf))
            return InvalidTimeframe(timeframe);
        if (limit < 1)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_LIMIT",
                Message = "limit must be >= 1.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier,
            });
        }
        limit = Math.Min(limit, MaxLimit);

        var query = _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == _instrument.Symbol && c.Timeframe == tf);
        if (endTimeMs.HasValue)
            query = query.Where(c => c.OpenTimeMs <= endTimeMs.Value);

        var rows = await query
            .OrderByDescending(c => c.OpenTimeMs)
            .Take(limit)
            .ToListAsync(cancellationToken);
        rows.Reverse(); // ascending open-time order, matching Binance klines
        return Ok(rows.Select(EquityLaneHttp.ToCandleDto).ToArray());
    }

    /// <summary>
    /// MBB current conditions — proxies the AI /api/current-conditions service
    /// pinned to symbol=MBB and joins verified MBB-scoped statistical evidence.
    /// There is no BTC fallback: missing MBB evidence reports
    /// "no-verified-evidence-bundle" honestly.
    /// </summary>
    [HttpGet("conditions")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetConditions(
        [FromQuery] string? timeframe,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTimeframe(timeframe, out var tf))
            return InvalidTimeframe(timeframe);

        var result = await _conditions.GetAsync(_instrument.Symbol, tf, cancellationToken);
        return result.Outcome switch
        {
            CurrentConditionsOutcome.Ok => Ok(result.Payload),
            CurrentConditionsOutcome.SourceUnavailable => StatusCode(
                StatusCodes.Status502BadGateway,
                new ApiErrorEnvelope
                {
                    Code = "CONDITIONS_SOURCE_UNAVAILABLE",
                    Message = "The AI current-conditions service is unavailable.",
                    Retryable = true,
                    RequestId = HttpContext.TraceIdentifier
                }),
            _ => StatusCode(
                StatusCodes.Status502BadGateway,
                new ApiErrorEnvelope
                {
                    Code = "CONDITIONS_PAYLOAD_INVALID",
                    Message = "The AI current-conditions payload failed schema validation.",
                    Retryable = false,
                    RequestId = HttpContext.TraceIdentifier
                })
        };
    }

    /// <summary>
    /// Forward observation journal (MBB-07): one row per finalized signal bar
    /// with the decision recorded live at that bar's availability time.
    /// wait/hold rows never carry fills; unresolved fills/outcomes are null,
    /// not fabricated. The full decision snapshot stays in the DB for
    /// provenance — the list projection is intentionally slim.
    /// </summary>
    [HttpGet("observations")]
    [AdminGuard] // decisions are position-derived: the journal reveals holdings (EQX review #4)
    [ProducesResponseType(typeof(IReadOnlyList<MbbObservationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<MbbObservationDto>>> GetObservations(
        [FromQuery] string? timeframe,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTimeframe(timeframe, out var tf))
            return InvalidTimeframe(timeframe);
        take = Math.Clamp(take, 1, 200);

        var rows = await _db.MbbSignalObservations.AsNoTracking()
            .Where(o => o.Symbol == _instrument.Symbol && o.Timeframe == tf)
            .OrderByDescending(o => o.SignalBarOpenTimeMs)
            .Take(take)
            .ToListAsync(cancellationToken);
        return Ok(rows.Select(EquityLaneHttp.ToObservationDto).ToArray());
    }

    /// <summary>
    /// Coverage / freshness / anomaly report over stored MBB candles (per
    /// timeframe: expected vs stored bars, gap ranges, unexpected session bars,
    /// stale flag). Read-only.
    /// </summary>
    [HttpGet("data-quality")]
    [ProducesResponseType(typeof(MbbCoverageReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<MbbCoverageReport>> GetDataQuality(CancellationToken cancellationToken)
    {
        return Ok(await _quality.BuildReportAsync(cancellationToken));
    }

    private bool TryResolveTimeframe(string? timeframe, out string resolved)
    {
        resolved = ProductionTimeframePolicy.Canonicalize(timeframe);
        return !string.IsNullOrWhiteSpace(resolved)
            && _instrument.ActiveTimeframes.Contains(resolved, StringComparer.Ordinal);
    }

    private BadRequestObjectResult InvalidTimeframe(string? timeframe) =>
        BadRequest(new ApiErrorEnvelope
        {
            Code = "INVALID_TIMEFRAME",
            Message = $"timeframe must be one of {string.Join(", ", _instrument.ActiveTimeframes)} for {_instrument.Symbol}; got '{timeframe}'.",
            Retryable = false,
            RequestId = HttpContext.TraceIdentifier,
        });
}

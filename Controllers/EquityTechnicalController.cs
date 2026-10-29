using Backend.Data;
using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Controllers;

/// <summary>
/// EQTECH-1 lane T: GET /api/equities/{symbol}/technical (equity-technical-v1) — the technical/candle-only payload an
/// AI in chat reads to analyse a TechnicalAnalysis equity (MBB, BID), plus the binding it needs to record a research
/// note. Public like GET conditions (market-only: no position, lots or cash). The symbol resolves through
/// <see cref="EquityScope"/> with capability technicalAnalysis (unknown ⇒ 404 UNKNOWN_SYMBOL, crypto or context-only ⇒
/// 400 UNSUPPORTED_CAPABILITY), so the BTC enabled-traffic gate is skipped. Rate-limited "expensive": one request calls
/// the AI conditions service once per active timeframe (shared 30 s cache) and rebuilds the research snapshot.
/// </summary>
[ApiController]
[Route("api/equities")]
[SkipProductionSymbolScope]
public sealed class EquityTechnicalController : ControllerBase
{
    private readonly EquityTechnicalService _technical;
    private readonly InstrumentCatalog _catalog;

    public EquityTechnicalController(
        AppDbContext db,
        VietnamMarketCalendar calendar,
        MbbDataQualityService quality,
        EquityBaseRatesService baseRates,
        EquityContextService context,
        EquityResearchSnapshotService snapshots,
        IHttpClientFactory httpClientFactory,
        IResearchEvidenceCatalog evidenceCatalog,
        ILogger<CurrentConditionsService> conditionsLogger,
        ILogger<EquityTechnicalService> logger,
        IMemoryCache? cache = null,
        InstrumentCatalog? catalog = null,
        TimeProvider? timeProvider = null)
    {
        _catalog = catalog ?? InstrumentCatalog.Default;
        // Same construction as EquitiesController: the shared IMemoryCache makes repeated calls hit the
        // current-conditions cache (key symbol:timeframe) that GET conditions and the research snapshot also use.
        var conditions = new CurrentConditionsService(httpClientFactory, evidenceCatalog, conditionsLogger, cache);
        _technical = new EquityTechnicalService(db, calendar, quality, baseRates, context, snapshots, conditions,
            timeProvider, logger);
    }

    /// <summary>
    /// Technical-only payload (equity-technical-v1): per catalog timeframe the last <paramref name="bars"/> finalized
    /// candles (default 100, clamped to 1..500), latest indicators with their formulas, candle patterns in the window,
    /// conditions and a data-quality summary; base rates as GET base-rates; relative strength; the forward-note
    /// calendar window; the research-note binding; the direction-scoring rule; limitations.
    /// </summary>
    [HttpGet("{symbol}/technical")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityTechnicalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityTechnicalResponse>> GetTechnical(
        string symbol, [FromQuery] int bars = EquityTechnicalContracts.DefaultBars,
        CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.TechnicalAnalysis);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _technical.BuildAsync(scope.Instrument!, bars, cancellationToken));
    }
}

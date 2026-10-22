using Backend.Data;
using Backend.Filters;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Controllers;

/// <summary>
/// Multi-symbol equity lane (EQX spec §2, P0 surfaces): list, candles, data quality,
/// conditions, observations, position, decision. Every action resolves its symbol through
/// <see cref="EquityScope"/> against catalog capabilities, so the BTC enabled-traffic gate is
/// skipped (<see cref="SkipProductionSymbolScopeAttribute"/>) — equities are Enabled=false by
/// design and crypto endpoints keep rejecting them. /api/mbb/* stays a fixed-MBB alias over
/// the same services.
///
/// Personal data (position, decision with its embedded position summary) is AdminGuard'd,
/// reads included. Market reads are public like other market-data GETs.
/// </summary>
[ApiController]
[Route("api/equities")]
[SkipProductionSymbolScope]
public sealed class EquitiesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MbbDataQualityService _quality;
    private readonly MbbDecisionService _decisions;
    private readonly CurrentConditionsService _conditions;
    private readonly EquityIngestionStatusStore _status;
    private readonly VietnamMarketCalendar _calendar;
    private readonly InstrumentCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly EquityBaseRatesService _baseRates;
    private readonly EquityContextService _context;
    private readonly EquityForecastJournalService _journal;

    public EquitiesController(
        AppDbContext db,
        MbbDataQualityService quality,
        MbbDecisionService decisions,
        IHttpClientFactory httpClientFactory,
        IResearchEvidenceCatalog evidenceCatalog,
        ILogger<CurrentConditionsService> conditionsLogger,
        EquityIngestionStatusStore status,
        VietnamMarketCalendar calendar,
        EquityBaseRatesService baseRates,
        EquityContextService context,
        EquityForecastJournalService journal,
        IMemoryCache? cache = null,
        CurrentConditionsService? conditions = null,
        InstrumentCatalog? catalog = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _quality = quality;
        _decisions = decisions;
        _conditions = conditions
            ?? new CurrentConditionsService(httpClientFactory, evidenceCatalog, conditionsLogger, cache);
        _status = status;
        _calendar = calendar;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _baseRates = baseRates;
        _context = context;
        _journal = journal;
    }

    /// <summary>
    /// Equity/index instruments with lane capabilities and per-symbol ingestion status.
    /// Status is data-plane (worker state when the worker runs in this process, else
    /// "disabled"); per-timeframe freshness is always derived from stored candles.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(EquityInstrumentListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EquityInstrumentListResponse>> List(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var nowMs = nowUtc.ToUnixTimeMilliseconds();
        var instruments = _catalog.Instruments.Where(EquityScope.IsEquityLaneAsset).ToArray();
        var symbols = instruments.Select(i => i.Symbol).ToArray();
        var stats = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => symbols.Contains(c.Symbol))
            .GroupBy(c => new { c.Symbol, c.Timeframe })
            .Select(g => new { g.Key.Symbol, g.Key.Timeframe, Count = g.LongCount(), Latest = g.Max(c => c.OpenTimeMs) })
            .ToListAsync(cancellationToken);

        var items = instruments.Select(i =>
        {
            var timeframes = i.ActiveTimeframes.Select(tf =>
            {
                var row = stats.FirstOrDefault(s => s.Symbol == i.Symbol && s.Timeframe == tf);
                var expected = _calendar.LastExpectedFinalizedOpenUtcMs(tf, nowMs);
                var latest = row?.Latest;
                return new EquityTimeframeFreshnessDto(tf, row?.Count ?? 0, latest, expected,
                    latest is not null && expected is not null && latest >= expected);
            }).ToArray();
            var state = _status.Get(i.Symbol);
            var ingestion = !_status.WorkerActive || state is null
                ? new EquityIngestionStatusDto(
                    _status.WorkerActive
                        ? (timeframes.All(t => t.UpToDate) ? EquityIngestionStatuses.UpToDate : EquityIngestionStatuses.PendingProvider)
                        : EquityIngestionStatuses.Disabled,
                    state?.SessionOpenTimeMs, state?.Attempts ?? 0, state?.MaxAttempts ?? 0,
                    state?.LastAttemptAtUtc, state?.LastCompletedAtUtc,
                    _status.WorkerActive ? state?.Detail : "EquityIngestionWorker is not running in this process (EquityIngestion:Enabled=false); data is refreshed only by admin sync.",
                    timeframes)
                : new EquityIngestionStatusDto(state.Status, state.SessionOpenTimeMs, state.Attempts,
                    state.MaxAttempts, state.LastAttemptAtUtc, state.LastCompletedAtUtc, state.Detail, timeframes);
            return new EquityInstrumentDto(
                i.Symbol, i.Label, i.Venue, i.AssetClass, i.Currency, i.PriceBasis, i.DataProvider,
                i.CrossCheckDataProvider, i.ActiveTimeframes, i.DefaultTimeframe,
                new EquityCapabilitiesDto(i.Capabilities.Ingestion,
                    EquityScope.Has(i, EquityCapability.TechnicalAnalysis),
                    EquityScope.Has(i, EquityCapability.Decision)),
                ingestion);
        }).ToArray();

        return Ok(new EquityInstrumentListResponse(
            EquityInstrumentListResponse.Version, nowUtc.UtcDateTime, _status.WorkerActive, items));
    }

    /// <summary>Stored candles (VCI; shape of /api/mbb/candles), ascending open time.</summary>
    [HttpGet("{symbol}/candles")]
    [ProducesResponseType(typeof(IReadOnlyList<MbbCandleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<MbbCandleDto>>> GetCandles(
        string symbol,
        [FromQuery] string? timeframe,
        [FromQuery] int limit = MbbMarketDataController.DefaultLimit,
        [FromQuery] long? endTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Ingestion);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        if (!EquityScope.TryResolveTimeframe(instrument, timeframe, out var tf))
            return EquityLaneHttp.InvalidTimeframe(this, instrument, timeframe);
        if (limit < 1)
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_LIMIT", "limit must be >= 1.");
        limit = Math.Min(limit, MbbMarketDataController.MaxLimit);

        var query = _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == instrument.Symbol && c.Timeframe == tf);
        if (endTimeMs.HasValue)
            query = query.Where(c => c.OpenTimeMs <= endTimeMs.Value);
        var rows = await query
            .OrderByDescending(c => c.OpenTimeMs)
            .Take(limit)
            .ToListAsync(cancellationToken);
        rows.Reverse();
        return Ok(rows.Select(EquityLaneHttp.ToCandleDto).ToArray());
    }

    /// <summary>Coverage / freshness / anomaly report (shape of /api/mbb/data-quality).</summary>
    [HttpGet("{symbol}/data-quality")]
    [ProducesResponseType(typeof(MbbCoverageReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MbbCoverageReport>> GetDataQuality(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Ingestion);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _quality.ForSymbol(scope.Instrument!.Symbol).BuildReportAsync(cancellationToken));
    }

    /// <summary>Current conditions — AI proxy pinned to the symbol; cache key symbol:timeframe.</summary>
    [HttpGet("{symbol}/conditions")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetConditions(
        string symbol, [FromQuery] string? timeframe, CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.TechnicalAnalysis);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        if (!EquityScope.TryResolveTimeframe(instrument, timeframe, out var tf))
            return EquityLaneHttp.InvalidTimeframe(this, instrument, timeframe);

        var result = await _conditions.GetAsync(instrument.Symbol, tf, cancellationToken);
        return result.Outcome switch
        {
            CurrentConditionsOutcome.Ok => Ok(result.Payload),
            CurrentConditionsOutcome.SourceUnavailable => EquityLaneHttp.Error(this,
                StatusCodes.Status502BadGateway, "CONDITIONS_SOURCE_UNAVAILABLE",
                "The AI current-conditions service is unavailable.", retryable: true),
            _ => EquityLaneHttp.Error(this, StatusCodes.Status502BadGateway, "CONDITIONS_PAYLOAD_INVALID",
                "The AI current-conditions payload failed schema validation."),
        };
    }

    /// <summary>
    /// Forward observation journal for one Decision symbol (legacy MBB rows included). AdminGuard'd:
    /// the recorded decision is position-derived (wait/no-position vs hold/buy-more/sell… reveals holdings).
    /// </summary>
    [HttpGet("{symbol}/observations")]
    [AdminGuard]
    [ProducesResponseType(typeof(IReadOnlyList<MbbObservationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<MbbObservationDto>>> GetObservations(
        string symbol, [FromQuery] string? timeframe, [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        if (!EquityScope.TryResolveTimeframe(instrument, timeframe, out var tf))
            return EquityLaneHttp.InvalidTimeframe(this, instrument, timeframe);
        take = Math.Clamp(take, 1, 200);

        var rows = await _db.MbbSignalObservations.AsNoTracking()
            .Where(o => o.Symbol == instrument.Symbol && o.Timeframe == tf)
            .OrderByDescending(o => o.SignalBarOpenTimeMs)
            .Take(take)
            .ToListAsync(cancellationToken);
        return Ok(rows.Select(EquityLaneHttp.ToObservationDto).ToArray());
    }

    /// <summary>Current position (lots with derived T+2 settlement + cash). AdminGuard'd.</summary>
    [HttpGet("{symbol}/position")]
    [AdminGuard]
    [ProducesResponseType(typeof(MbbPositionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MbbPositionDto>> GetPosition(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _decisions.GetPositionAsync(scope.Instrument!.Symbol, cancellationToken));
    }

    /// <summary>Full-replacement write of the position (lots + cash). Idempotent. AdminGuard'd.</summary>
    [HttpPut("{symbol}/position")]
    [AdminGuard]
    [ProducesResponseType(typeof(MbbPositionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MbbPositionDto>> PutPosition(
        string symbol, [FromBody] UpdateMbbPositionRequest body, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        try
        {
            return Ok(await _decisions.ReplacePositionAsync(body, scope.Instrument!.Symbol, cancellationToken));
        }
        catch (EquityCashValidationException ex)
        {
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, ex.Code, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_POSITION_INPUT", ex.Message);
        }
    }

    /// <summary>
    /// Set the ONE shared brokerage cash pool used by every equity symbol (EQX v2 A3). Per-symbol buying
    /// power is the declared buyBudgetVnd (PUT position); the sum of budgets must stay within this pool.
    /// Lowering the pool below the budgets is allowed and makes decisions report budget-exceeds-cash;
    /// availableCashVnd null resets the pool to unknown (cash-unknown). AdminGuard'd (personal data).
    /// </summary>
    [HttpPut("cash")]
    [AdminGuard]
    [ProducesResponseType(typeof(EquityCashDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EquityCashDto>> PutCash(
        [FromBody] UpdateEquityCashRequest body, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _decisions.SetSharedCashAsync(body.AvailableCashVnd, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_CASH_INPUT", ex.Message);
        }
    }

    /// <summary>Position-aware rules-only decision. AdminGuard'd (embeds personal holdings).</summary>
    [HttpGet("{symbol}/decision")]
    [AdminGuard]
    [ProducesResponseType(typeof(MbbDecisionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MbbDecisionDto>> GetDecision(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _decisions.DecideAsync(scope.Instrument!.Symbol, cancellationToken));
    }

    /// <summary>
    /// Quant base rates (equity-base-rates-v1, spec §5/§11) from this symbol's own verified
    /// artifact. decisionUse is non-null only when gate passed ∧ fresh ∧ fingerprint matches.
    /// </summary>
    [HttpGet("{symbol}/base-rates")]
    [ProducesResponseType(typeof(EquityBaseRatesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityBaseRatesResponse>> GetBaseRates(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _baseRates.GetAsync(scope.Instrument!.Symbol, cancellationToken));
    }

    /// <summary>Market context (equity-context-v1, spec §6/§11): RS, corporate actions, events, fundamentals.</summary>
    [HttpGet("{symbol}/context")]
    [ProducesResponseType(typeof(EquityContextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityContextResponse>> GetContext(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _context.GetAsync(scope.Instrument!.Symbol, cancellationToken));
    }

    /// <summary>Forecast journal (equity-forecast-journal-v1, spec §8/§11); configId optional.</summary>
    [HttpGet("{symbol}/forecast-journal")]
    [ProducesResponseType(typeof(EquityForecastJournalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityForecastJournalResponse>> GetForecastJournal(
        string symbol, [FromQuery] string? configId = null, [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        if (configId is { Length: > 40 })
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_CONFIG_ID", "configId is at most 40 characters.");
        return Ok(await _journal.GetJournalAsync(scope.Instrument!.Symbol, configId, take, cancellationToken));
    }
}

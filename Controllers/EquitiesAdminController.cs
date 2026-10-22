using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Equity lane ops (EQX spec §2): sync / backfill (Ingestion), recompute (TechnicalAnalysis),
/// observe (Decision) for one catalog symbol. AdminGuard'd. Each call holds the symbol's
/// <see cref="EquityLaneLocks"/> lease — shared with the /api/mbb/admin alias and
/// <see cref="EquityIngestionWorker"/> — and answers 409 LANE_BUSY when the lane is in use.
/// With MbbIngestion:Enabled=false sync/backfill return a truthful Disabled report.
/// </summary>
[ApiController]
[Route("api/equities/{symbol}/admin")]
[AdminGuard]
[SkipProductionSymbolScope]
public sealed class EquitiesAdminController : ControllerBase
{
    private readonly MbbIngestionService _ingestion;
    private readonly MbbTechnicalRecomputeService _recompute;
    private readonly MbbSignalObservationService _observations;
    private readonly InstrumentCatalog _catalog;
    private readonly EquityForecastJournalService _journal;
    private readonly EquityBaseRatesService _baseRates;
    private readonly EquityContextCollector _context;

    public EquitiesAdminController(
        MbbIngestionService ingestion,
        MbbTechnicalRecomputeService recompute,
        MbbSignalObservationService observations,
        EquityForecastJournalService journal,
        EquityBaseRatesService baseRates,
        EquityContextCollector context,
        InstrumentCatalog? catalog = null)
    {
        _ingestion = ingestion;
        _recompute = recompute;
        _observations = observations;
        _journal = journal;
        _baseRates = baseRates;
        _context = context;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    /// <summary>One incremental sync cycle over the symbol's timeframes (bounded, idempotent).</summary>
    [HttpPost("sync")]
    [ProducesResponseType(typeof(MbbSyncReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Sync(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Ingestion);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        using var lease = EquityLaneLocks.TryAcquire(instrument.Symbol);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, instrument.Symbol);
        return Ok(await _ingestion.ForSymbol(instrument.Symbol).SyncAsync(cancellationToken));
    }

    /// <summary>Bounded historical backfill for one timeframe; from/to are yyyy-MM-dd HOSE dates.</summary>
    [HttpPost("backfill")]
    [ProducesResponseType(typeof(MbbTimeframeSync), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Backfill(
        string symbol,
        [FromQuery] string? timeframe,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Ingestion);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        if (!EquityScope.TryResolveTimeframe(instrument, timeframe, out var tf))
            return EquityLaneHttp.InvalidTimeframe(this, instrument, timeframe);
        if (!EquityLaneHttp.TryParseLocalDate(this, from, out var fromDate, out var fromError)) return fromError!;
        if (!EquityLaneHttp.TryParseLocalDate(this, to, out var toDate, out var toError)) return toError!;

        using var lease = EquityLaneLocks.TryAcquire(instrument.Symbol);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, instrument.Symbol);
        try
        {
            return Ok(await _ingestion.ForSymbol(instrument.Symbol)
                .BackfillAsync(tf, fromDate, toDate, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_BACKFILL_ARGUMENT", ex.Message);
        }
    }

    /// <summary>Full technical-artifact rebuild from InstrumentCandles; timeframe optional (all active).</summary>
    [HttpPost("recompute")]
    [ProducesResponseType(typeof(IReadOnlyList<MbbTechnicalRecomputeReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Recompute(
        string symbol, [FromQuery] string? timeframe = null, CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.TechnicalAnalysis);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        IReadOnlyList<string> timeframes;
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            timeframes = instrument.ActiveTimeframes;
        }
        else
        {
            if (!EquityScope.TryResolveTimeframe(instrument, timeframe, out var tf))
                return EquityLaneHttp.InvalidTimeframe(this, instrument, timeframe);
            timeframes = [tf];
        }

        using var lease = EquityLaneLocks.TryAcquire(instrument.Symbol);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, instrument.Symbol);
        var reports = new List<MbbTechnicalRecomputeReport>(timeframes.Count);
        foreach (var tf in timeframes)
        {
            try
            {
                reports.Add(await _recompute.RecomputeAsync(instrument.Symbol, tf, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_RECOMPUTE_ARGUMENT", ex.Message);
            }
        }
        return Ok(reports);
    }

    /// <summary>One forward-observation poll for the latest finalized bar (never a backfill).</summary>
    [HttpPost("observe")]
    [ProducesResponseType(typeof(MbbSignalObservationService.ObserveReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Observe(
        string symbol, [FromQuery] string? timeframe, CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        if (!EquityScope.TryResolveTimeframe(instrument, timeframe, out var tf))
            return EquityLaneHttp.InvalidTimeframe(this, instrument, timeframe);

        using var lease = EquityLaneLocks.TryAcquire(instrument.Symbol);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, instrument.Symbol);
        try
        {
            var report = await _observations.RecordAsync(instrument.Symbol, tf, cancellationToken);
            // Same poll also resolves matured forecast-journal outcomes (write-once).
            return Ok(report with
            {
                ForecastOutcomesResolved = await _journal.ResolveOutcomesAsync(instrument.Symbol, cancellationToken),
            });
        }
        catch (ArgumentException ex)
        {
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_OBSERVE_ARGUMENT", ex.Message);
        }
    }

    /// <summary>Rebuild the quant base-rates artifact via the AI service (POST /api/equity-base-rates/build).</summary>
    [HttpPost("base-rates/rebuild")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RebuildBaseRates(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        using var lease = EquityLaneLocks.TryAcquire(instrument.Symbol);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, instrument.Symbol);
        var result = await _baseRates.RebuildAsync(instrument.Symbol, cancellationToken);
        return result.StatusCode == StatusCodes.Status200OK
            ? Ok(result.Summary)
            : EquityLaneHttp.Error(this, result.StatusCode, result.Code!, result.Message!,
                retryable: result.StatusCode >= 500);
    }

    /// <summary>
    /// Record the quant-knn forecast for the latest finalized 1d bar (idempotent; abstained rows when the
    /// artifact is stale/missing) and resolve matured outcomes — the manual twin of the worker step.
    /// </summary>
    [HttpPost("forecasts/record")]
    [ProducesResponseType(typeof(EquityForecastRecordReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecordForecasts(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var instrument = scope.Instrument!;
        using var lease = EquityLaneLocks.TryAcquire(instrument.Symbol);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, instrument.Symbol);
        var report = await _journal.RecordQuantAsync(instrument.Symbol, cancellationToken);
        await _journal.ResolveOutcomesAsync(instrument.Symbol, cancellationToken);
        return Ok(report);
    }

    /// <summary>Collect corporate actions/events (VCI IQ) + fundamentals (KBS CSTC) for an equity.</summary>
    [HttpPost("context/sync")]
    [ProducesResponseType(typeof(EquityContextSyncReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SyncContext(string symbol, CancellationToken cancellationToken)
    {
        var instrument = ResolveContextEquity(symbol, out var error);
        if (instrument is null) return error!;
        using var lease = EquityLaneLocks.TryAcquire(instrument.Symbol);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, instrument.Symbol);
        return Ok(await _context.SyncAsync(instrument.Symbol, cancellationToken));
    }

    /// <summary>Admin import of corporate actions (batch upsert, provenance admin-manual).</summary>
    [HttpPost("corporate-actions")]
    [ProducesResponseType(typeof(EquityImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> ImportCorporateActions(
        string symbol, [FromBody] List<EquityCorporateActionImport>? body, CancellationToken cancellationToken) =>
        ImportAsync(symbol, sym => _context.ImportCorporateActionsAsync(sym, body, cancellationToken));

    /// <summary>Admin import of events (batch upsert, provenance admin-manual).</summary>
    [HttpPost("events")]
    [ProducesResponseType(typeof(EquityImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> ImportEvents(
        string symbol, [FromBody] List<EquityEventImport>? body, CancellationToken cancellationToken) =>
        ImportAsync(symbol, sym => _context.ImportEventsAsync(sym, body, cancellationToken));

    /// <summary>Admin import of fundamentals (value null exactly when unavailableReason is set; source URL required).</summary>
    [HttpPost("fundamentals")]
    [ProducesResponseType(typeof(EquityImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> ImportFundamentals(
        string symbol, [FromBody] List<EquityFundamentalImport>? body, CancellationToken cancellationToken) =>
        ImportAsync(symbol, sym => _context.ImportFundamentalsAsync(sym, body, cancellationToken));

    private async Task<IActionResult> ImportAsync(string symbol, Func<string, Task<EquityImportReport>> run)
    {
        var instrument = ResolveContextEquity(symbol, out var error);
        if (instrument is null) return error!;
        try
        {
            return Ok(await run(instrument.Symbol));
        }
        catch (ArgumentException ex)
        {
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_CONTEXT_INPUT", ex.Message);
        }
    }

    /// <summary>Corporate actions/events/fundamentals exist for equities, not for the index.</summary>
    private InstrumentDescriptor? ResolveContextEquity(string symbol, out ObjectResult? error)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Ingestion);
        if (!scope.Ok)
        {
            error = EquityLaneHttp.ScopeError(this, scope);
            return null;
        }
        if (scope.Instrument!.AssetClass != InstrumentAssetClasses.Equity)
        {
            error = EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, EquityScope.UnsupportedCapability,
                $"Symbol '{scope.Instrument.Symbol}' ({scope.Instrument.AssetClass}) has no corporate actions, events or fundamentals.");
            return null;
        }
        error = null;
        return scope.Instrument;
    }
}

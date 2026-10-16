using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// MBB ops endpoints (MBB-06A): manual sync / backfill / technical recompute.
///
/// There is intentionally no hosted worker for MBB (MBB-02 contract), so these
/// AdminGuard'd endpoints are the only operational entry points — an operator
/// runs sync to top up recent bars, backfill for deeper history, and recompute
/// after ingest to rebuild indicators/patterns/volume stats/vectors/sequences.
///
/// When "MbbIngestion:Enabled" is false the sync/backfill reports come back with
/// a Disabled status / per-timeframe error — a truthful answer, not a silent
/// no-op. Recompute always works off stored InstrumentCandles regardless of the
/// ingestion flag.
/// </summary>
[ApiController]
[Route("api/mbb/admin")]
[AdminGuard]
public sealed class MbbAdminController : ControllerBase
{
    private readonly MbbIngestionService _ingestion;
    private readonly MbbTechnicalRecomputeService _recompute;
    private readonly MbbSignalObservationService _observations;
    private readonly InstrumentDescriptor _instrument;

    public MbbAdminController(
        MbbIngestionService ingestion,
        MbbTechnicalRecomputeService recompute,
        MbbSignalObservationService observations,
        InstrumentCatalog? instruments = null)
    {
        _ingestion = ingestion;
        _recompute = recompute;
        _observations = observations;
        _instrument = (instruments ?? InstrumentCatalog.Default).Get(MbbTechnicalRecomputeService.SupportedSymbol)
            ?? throw new InvalidOperationException("MBB is not registered in the instrument catalog.");
    }

    /// <summary>
    /// One incremental sync cycle: latest bars + bounded gap-fill for each active
    /// MBB timeframe. Idempotent; safe to call repeatedly until coverage is met.
    /// </summary>
    [HttpPost("sync")]
    [ProducesResponseType(typeof(MbbSyncReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MbbSyncReport>> Sync(CancellationToken cancellationToken)
    {
        return Ok(await _ingestion.SyncAsync(cancellationToken));
    }

    /// <summary>
    /// Bounded historical backfill for one timeframe (VCI pages backward until
    /// the configured backfill start is covered or the request budget runs out).
    /// `from`/`to` are yyyy-MM-dd HOSE local dates; defaults come from
    /// MbbIngestion options (1d: 2011-11-01, 1h: 2023-09-11).
    /// </summary>
    [HttpPost("backfill")]
    [ProducesResponseType(typeof(MbbTimeframeSync), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Backfill(
        [FromQuery] string? timeframe,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTimeframe(timeframe, out var tf))
            return InvalidTimeframe(timeframe);
        if (!TryParseLocalDate(from, out var fromDate, out var fromError))
            return BadRequest(fromError);
        if (!TryParseLocalDate(to, out var toDate, out var toError))
            return BadRequest(toError);

        try
        {
            return Ok(await _ingestion.BackfillAsync(tf, fromDate, toDate, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_BACKFILL_ARGUMENT",
                Message = ex.Message,
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier,
            });
        }
    }

    /// <summary>
    /// Full rebuild of MBB technical artifacts (indicators, candle patterns,
    /// volume stats, window vectors, pattern sequences) from InstrumentCandles.
    /// `timeframe` optional — omitted rebuilds every active MBB timeframe.
    /// </summary>
    [HttpPost("recompute")]
    [ProducesResponseType(typeof(IReadOnlyList<MbbTechnicalRecomputeReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<MbbTechnicalRecomputeReport>>> Recompute(
        [FromQuery] string? timeframe = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> timeframes;
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            timeframes = _instrument.ActiveTimeframes;
        }
        else
        {
            if (!TryResolveTimeframe(timeframe, out var tf))
                return InvalidTimeframe(timeframe);
            timeframes = new[] { tf };
        }

        var reports = new List<MbbTechnicalRecomputeReport>(timeframes.Count);
        foreach (var tf in timeframes)
        {
            try
            {
                reports.Add(await _recompute.RecomputeAsync(_instrument.Symbol, tf, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ApiErrorEnvelope
                {
                    Code = "INVALID_RECOMPUTE_ARGUMENT",
                    Message = ex.Message,
                    Retryable = false,
                    RequestId = HttpContext.TraceIdentifier,
                });
            }
        }
        return Ok(reports);
    }

    /// <summary>
    /// One forward-observation poll (MBB-07): records the live decision for the
    /// latest expected finalized bar if not yet observed, then resolves pending
    /// fills/outcomes from already-stored later bars. Never creates rows for
    /// historical bars — repeat calls are idempotent.
    /// </summary>
    [HttpPost("observe")]
    [ProducesResponseType(typeof(MbbSignalObservationService.ObserveReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Observe(
        [FromQuery] string? timeframe,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveTimeframe(timeframe, out var tf))
            return InvalidTimeframe(timeframe);

        try
        {
            return Ok(await _observations.RecordAsync(tf, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_OBSERVE_ARGUMENT",
                Message = ex.Message,
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier,
            });
        }
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

    private bool TryParseLocalDate(string? raw, out DateOnly? date, out ApiErrorEnvelope? error)
    {
        date = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", out var parsed))
        {
            date = parsed;
            return true;
        }
        error = new ApiErrorEnvelope
        {
            Code = "INVALID_DATE",
            Message = $"Expected a yyyy-MM-dd HOSE local date, got '{raw}'.",
            Retryable = false,
            RequestId = HttpContext.TraceIdentifier,
        };
        return false;
    }
}

using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Research session surface (EQX v2 §B/§C1/§C2) for Decision equities (MBB, BID):
/// <list type="bullet">
/// <item>GET research-readiness / research-snapshot?asOf= — public, market-only (no position/cash/lots);</item>
/// <item>GET source-documents — public metadata; POST admin/source-documents — AdminGuard;</item>
/// <item>POST / GET research-notes — AdminGuard (actionView can reveal holdings);</item>
/// <item>v3 document pipeline: POST admin/documents/pipeline (+ /{runId}/cancel) — AdminGuard; GET
///   documents/pipeline-runs, fundamentals/coverage, financial-facts — public, market-only.</item>
/// </list>
/// Symbols resolve through <see cref="EquityScope"/>; the BTC enabled-traffic gate is skipped.
/// </summary>
[ApiController]
[Route("api/equities")]
[SkipProductionSymbolScope]
public sealed class EquityResearchController : ControllerBase
{
    private readonly EquityResearchSnapshotService _snapshots;
    private readonly EquityResearchNoteService _notes;
    private readonly EquitySourceDocumentService _documents;
    private readonly EquityDocumentPipelineService _pipeline;
    private readonly EquityFinancialFactsService _facts;
    private readonly InstrumentCatalog _catalog;
    private readonly TimeProvider _timeProvider;

    public EquityResearchController(
        EquityResearchSnapshotService snapshots,
        EquityResearchNoteService notes,
        EquitySourceDocumentService documents,
        EquityDocumentPipelineService pipeline,
        EquityFinancialFactsService facts,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null)
    {
        _snapshots = snapshots;
        _notes = notes;
        _documents = documents;
        _pipeline = pipeline;
        _facts = facts;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    private bool TryScope(string symbol, out string sym, out ObjectResult? error)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        sym = scope.Instrument?.Symbol ?? "";
        error = scope.Ok ? null : EquityLaneHttp.ScopeError(this, scope);
        return scope.Ok;
    }

    private bool TryAsOf(string? raw, out DateTimeOffset asOf, out ObjectResult? error)
    {
        error = null;
        if (EquityResearchSnapshotService.TryParseAsOf(raw, _timeProvider.GetUtcNow(), out asOf, out var message)) return true;
        error = EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, "INVALID_AS_OF", message!);
        return false;
    }

    /// <summary>Per-purpose readiness (technical | fundamental | quantitative | portfolio-action); no global flag.</summary>
    [HttpGet("{symbol}/research-readiness")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityResearchReadiness), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityResearchReadiness>> GetReadiness(
        string symbol, [FromQuery] string? asOf = null, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        if (!TryAsOf(asOf, out var at, out error)) return error!;
        var snapshot = await _snapshots.BuildAsync(sym, at, cancellationToken);
        return Ok(EquityResearchSnapshotService.Readiness(snapshot));
    }

    /// <summary>Point-in-time, market-only research snapshot (equity-research-snapshot-v1).</summary>
    [HttpGet("{symbol}/research-snapshot")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityResearchSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityResearchSnapshot>> GetSnapshot(
        string symbol, [FromQuery] string? asOf = null, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        if (!TryAsOf(asOf, out var at, out error)) return error!;
        return Ok(await _snapshots.BuildAsync(sym, at, cancellationToken));
    }

    /// <summary>Issuer source documents (metadata only; files are never served).</summary>
    [HttpGet("{symbol}/source-documents")]
    [ProducesResponseType(typeof(EquitySourceDocumentListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquitySourceDocumentListResponse>> GetSourceDocuments(
        string symbol, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        return Ok(await _documents.ListAsync(sym, cancellationToken));
    }

    /// <summary>Register an already-downloaded document (hash re-verified on disk). 201 created / 200 already known.</summary>
    [HttpPost("{symbol}/admin/source-documents")]
    [AdminGuard]
    [ProducesResponseType(typeof(EquitySourceDocumentRegistrationResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(EquitySourceDocumentRegistrationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RegisterSourceDocument(
        string symbol, [FromBody] EquitySourceDocumentRegistration? body, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        var result = await _documents.RegisterAsync(sym, body, cancellationToken);
        return result.Outcome switch
        {
            EquitySourceDocumentService.Outcome.Ok => result.Result!.Created
                ? StatusCode(StatusCodes.Status201Created, result.Result)
                : Ok(result.Result),
            EquitySourceDocumentService.Outcome.RootUnavailable =>
                EquityLaneHttp.Error(this, StatusCodes.Status503ServiceUnavailable, result.Code!, result.Message!),
            EquitySourceDocumentService.Outcome.PublishedAtImmutable =>
                EquityLaneHttp.Error(this, StatusCodes.Status409Conflict, result.Code!, result.Message!),
            EquitySourceDocumentService.Outcome.FileNotFound or EquitySourceDocumentService.Outcome.HashMismatch =>
                EquityLaneHttp.Error(this, StatusCodes.Status422UnprocessableEntity, result.Code!, result.Message!),
            _ => EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, result.Code!, result.Message!),
        };
    }

    /// <summary>Record a session research note (equity-research-note-v1). AdminGuard'd.</summary>
    [HttpPost("{symbol}/research-notes")]
    [AdminGuard]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityResearchNoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecordNote(
        string symbol, [FromBody] EquityResearchNoteRequest? body, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        var result = await _notes.RecordAsync(sym, body, cancellationToken);
        return result.Outcome switch
        {
            EquityResearchNoteService.Outcome.Created => StatusCode(StatusCodes.Status201Created, result.Item),
            EquityResearchNoteService.Outcome.SignalBarNotLatest
                or EquityResearchNoteService.Outcome.ForecastWindowClosed
                or EquityResearchNoteService.Outcome.SnapshotMismatch
                or EquityResearchNoteService.Outcome.CalendarCoverageExpired =>
                EquityLaneHttp.Error(this, StatusCodes.Status409Conflict, result.Code!, result.Message!),
            _ => EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, result.Code!, result.Message!),
        };
    }

    /// <summary>Session research notes, newest signal bar first. AdminGuard'd.</summary>
    [HttpGet("{symbol}/research-notes")]
    [AdminGuard]
    [ProducesResponseType(typeof(EquityResearchNoteListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EquityResearchNoteListResponse>> ListNotes(
        string symbol, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        return Ok(await _notes.ListAsync(sym, take, cancellationToken));
    }

    // --- EQX v3 document pipeline -------------------------------------------------------------

    /// <summary>
    /// Start a document pipeline run (ai/equity_pipeline.py) in the background: 202 + run. 409 PIPELINE_BUSY when a
    /// run for the symbol is already running; 503 PIPELINE_UNAVAILABLE when the interpreter/script is missing.
    /// </summary>
    [HttpPost("{symbol}/admin/documents/pipeline")]
    [AdminGuard]
    [ProducesResponseType(typeof(EquityPipelineRunDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartPipeline(
        string symbol, [FromQuery] string? mode = null, [FromQuery] int? budget = null, [FromQuery] int? maxDocs = null,
        [FromQuery] bool dryRun = false, [FromQuery] string? extractScope = null, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        var result = await _pipeline.StartAsync(sym, mode, "admin", budget, maxDocs, dryRun, cancellationToken, extractScope);
        return result.Outcome switch
        {
            EquityDocumentPipelineService.Outcome.Started =>
                Accepted($"/api/equities/{sym}/documents/pipeline-runs", result.Run),
            EquityDocumentPipelineService.Outcome.Busy =>
                EquityLaneHttp.Error(this, StatusCodes.Status409Conflict, result.Code!, result.Message!, retryable: true),
            EquityDocumentPipelineService.Outcome.Unavailable =>
                EquityLaneHttp.Error(this, StatusCodes.Status503ServiceUnavailable, result.Code!, result.Message!),
            _ => EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, result.Code!, result.Message!),
        };
    }

    /// <summary>Cancel a run supervised by this backend (kills the process tree): 202 + run (status still running until finalized).</summary>
    [HttpPost("{symbol}/admin/documents/pipeline/{runId:guid}/cancel")]
    [AdminGuard]
    [ProducesResponseType(typeof(EquityPipelineRunDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CancelPipeline(string symbol, Guid runId, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        var result = await _pipeline.CancelAsync(sym, runId, cancellationToken);
        return result.Outcome switch
        {
            EquityDocumentPipelineService.Outcome.CancelRequested =>
                Accepted($"/api/equities/{sym}/documents/pipeline-runs", result.Run),
            EquityDocumentPipelineService.Outcome.NotFound =>
                EquityLaneHttp.Error(this, StatusCodes.Status404NotFound, result.Code!, result.Message!),
            _ => EquityLaneHttp.Error(this, StatusCodes.Status409Conflict, result.Code!, result.Message!),
        };
    }

    /// <summary>Pipeline runs, newest first (status, heartbeat, counts, scrubbed log tail; no secrets/absolute paths).</summary>
    [HttpGet("{symbol}/documents/pipeline-runs")]
    [ProducesResponseType(typeof(EquityPipelineRunListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityPipelineRunListResponse>> ListPipelineRuns(
        string symbol, [FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        return Ok(await _pipeline.ListAsync(sym, take, cancellationToken));
    }

    /// <summary>Fundamentals coverage grid (2016Q1..latest × Q/H1/FY × metric group × consolidation) with the highest stage reached.</summary>
    [HttpGet("{symbol}/fundamentals/coverage")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityFundamentalsCoverageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityFundamentalsCoverageResponse>> GetFundamentalsCoverage(
        string symbol, CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        return Ok(await _facts.GetCoverageAsync(sym, cancellationToken));
    }

    /// <summary>
    /// Normalized financial facts (public, market-only), each with its document ref (url, sha256, page, location),
    /// extraction and availability. asOf filters by availability; status defaults to validated (or all).
    /// </summary>
    [HttpGet("{symbol}/financial-facts")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityFinancialFactsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityFinancialFactsResponse>> GetFinancialFacts(
        string symbol, [FromQuery] string? metric = null, [FromQuery] string? periodType = null, [FromQuery] string? from = null,
        [FromQuery] string? to = null, [FromQuery] string? asOf = null, [FromQuery] string? status = null, [FromQuery] int? take = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryScope(symbol, out var sym, out var error)) return error!;
        if (!TryAsOf(asOf, out var at, out error)) return error!;
        var result = await _facts.GetFactsAsync(sym,
            new EquityFinancialFactsService.FactsQuery(metric, periodType, from, to, at, status, take), cancellationToken);
        if (result.Error is not null)
            return EquityLaneHttp.Error(this, StatusCodes.Status400BadRequest, EquityDocumentPipelineContracts.InvalidFactsQuery, result.Error);
        return Ok(result.Response);
    }
}

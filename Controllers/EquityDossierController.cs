using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Dossier + LLM runs for Decision equities (spec §7/§11). The dossier and the run list are
/// public and market-only (no position, lots, cost or cash). Creating a run is AdminGuard'd,
/// rate-limited ("expensive") and holds the symbol's lane lock.
/// </summary>
[ApiController]
[Route("api/equities")]
[SkipProductionSymbolScope]
public sealed class EquityDossierController : ControllerBase
{
    private readonly EquityDossierService _dossier;
    private readonly EquityLlmRunService _runs;
    private readonly InstrumentCatalog _catalog;

    private readonly bool _llmEnabled;

    public EquityDossierController(
        EquityDossierService dossier,
        EquityLlmRunService runs,
        InstrumentCatalog? catalog = null,
        Microsoft.Extensions.Options.IOptions<Backend.Options.EquityLlmOptions>? llm = null)
    {
        _dossier = dossier;
        _runs = runs;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _llmEnabled = llm?.Value.Enabled ?? false;
    }

    /// <summary>Market-only research dossier (equity-dossier-v1) for the latest stored finalized 1d bar.</summary>
    [HttpGet("{symbol}/dossier")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityDossierResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityDossierResponse>> GetDossier(string symbol, CancellationToken cancellationToken)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _dossier.BuildAsync(scope.Instrument!.Symbol, cancellationToken));
    }

    /// <summary>LLM runs for the symbol, newest first (take 1..50). Market-only.</summary>
    [HttpGet("{symbol}/llm-runs")]
    [ProducesResponseType(typeof(EquityLlmRunListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EquityLlmRunListResponse>> ListRuns(
        string symbol, [FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _runs.ListAsync(scope.Instrument!.Symbol, take, cancellationToken));
    }

    /// <summary>
    /// Create one LLM run (expensive). 201 succeeded/failed; 503 LLM_UNAVAILABLE (+item);
    /// 502 AI unreachable/rejected (+item, persisted as failed); 409 LANE_BUSY / SIGNAL_BAR_NOT_LATEST /
    /// FORECAST_WINDOW_CLOSED (the next session already opened — no look-ahead forecasts).
    /// </summary>
    [HttpPost("{symbol}/llm-runs")]
    [AdminGuard]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(typeof(EquityLlmRunDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(EquityLlmRunErrorEnvelope), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(EquityLlmRunErrorEnvelope), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateRun(string symbol, CancellationToken cancellationToken)
    {
        // Retired lane (EQX v2 B): refuse before scope/lock/dossier so nothing reaches the AI service.
        if (!_llmEnabled)
            return EquityLaneHttp.Error(this, StatusCodes.Status503ServiceUnavailable, "LLM_DISABLED",
                "In-app LLM runs are disabled; research is done in an interactive session (skill investment-research). " +
                "Existing runs remain readable via GET.", retryable: false);
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        var sym = scope.Instrument!.Symbol;
        using var lease = EquityLaneLocks.TryAcquire(sym);
        if (lease is null) return EquityLaneHttp.LaneBusy(this, sym);

        var result = await _runs.RunAsync(sym, cancellationToken);
        return result.Outcome switch
        {
            EquityLlmRunService.Outcome.Created =>
                Created($"/api/equities/{sym}/llm-runs", result.Item),
            EquityLlmRunService.Outcome.SignalBarNotLatest =>
                EquityLaneHttp.Error(this, StatusCodes.Status409Conflict, "SIGNAL_BAR_NOT_LATEST", result.Message!, retryable: true),
            EquityLlmRunService.Outcome.ForecastWindowClosed =>
                EquityLaneHttp.Error(this, StatusCodes.Status409Conflict, "FORECAST_WINDOW_CLOSED", result.Message!, retryable: true),
            EquityLlmRunService.Outcome.Unavailable => WithItem(StatusCodes.Status503ServiceUnavailable, "LLM_UNAVAILABLE", result),
            _ => WithItem(StatusCodes.Status502BadGateway, "LLM_SOURCE_UNAVAILABLE", result),
        };
    }

    private ObjectResult WithItem(int status, string code, EquityLlmRunService.RunResult result) =>
        StatusCode(status, new EquityLlmRunErrorEnvelope
        {
            Code = code,
            Message = result.Message ?? code,
            Retryable = true,
            RequestId = HttpContext.TraceIdentifier,
            Item = result.Item,
        });
}

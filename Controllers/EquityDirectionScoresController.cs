using Backend.Data;
using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Direction scores of session research notes (EQTECH-1 lane S, rule direction-score-v1) for Decision
/// equities (MBB, BID). AdminGuard'd like GET research-notes (notes carry actionView). Read-only: the
/// backend computes every number from stored 1d closes; the note's direction is the only input.
/// The scoring service is built here from already-registered dependencies (no DI change).
/// </summary>
[ApiController]
[Route("api/equities")]
[SkipProductionSymbolScope]
[AdminGuard]
public sealed class EquityDirectionScoresController : ControllerBase
{
    private readonly EquityDirectionScoringService _scores;
    private readonly InstrumentCatalog _catalog;

    public EquityDirectionScoresController(
        AppDbContext db,
        VietnamMarketCalendar calendar,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null)
    {
        _catalog = catalog ?? InstrumentCatalog.Default;
        _scores = new EquityDirectionScoringService(db, calendar, timeProvider, _catalog);
    }

    /// <summary>
    /// Per-horizon direction scores (hits, coverage, nEffective, point-in-time prior and hindsight baselines)
    /// plus one item per note, newest signal bar first; take is clamped to 1..500 (items only).
    /// </summary>
    [HttpGet("{symbol}/research-notes/direction-scores")]
    [ProducesResponseType(typeof(EquityDirectionScoresResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EquityDirectionScoresResponse>> GetDirectionScores(
        string symbol, [FromQuery] int take = EquityDirectionScoringService.DefaultTake,
        CancellationToken cancellationToken = default)
    {
        var scope = EquityScope.Resolve(_catalog, symbol, EquityCapability.Decision);
        if (!scope.Ok) return EquityLaneHttp.ScopeError(this, scope);
        return Ok(await _scores.GetAsync(scope.Instrument!.Symbol, take, cancellationToken));
    }
}

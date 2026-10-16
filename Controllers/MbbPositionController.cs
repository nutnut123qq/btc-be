using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// MBB position + decision endpoints (MBB-05).
///
/// NOTE on the symbol gate: MBB is a known-but-DISABLED instrument in
/// <see cref="ProductionSymbolPolicy"/>, and the global
/// <see cref="ProductionSymbolScopeFilter"/> rejects any action argument named
/// `symbol`/`symbols` or any request DTO carrying a `Symbol` property. This
/// controller therefore accepts NO symbol input at all — the lane is locked to
/// MBB server-side (<see cref="MbbDecisionService.SupportedSymbol"/>) so the
/// filter simply has nothing to inspect. Do not add a symbol parameter here
/// until MBB is enabled in the production scope.
///
/// Personal position data (cost basis, shares, cash, PnL) stays off public
/// endpoints: ALL actions are AdminGuard'd — reads included, because both the
/// position DTO and the decision's embedded position summary expose personal
/// holdings. Unauthenticated callers receive 401 like other admin surfaces.
/// </summary>
[ApiController]
[Route("api/mbb")]
public sealed class MbbPositionController : ControllerBase
{
    private readonly MbbDecisionService _decisions;

    public MbbPositionController(MbbDecisionService decisions)
    {
        _decisions = decisions;
    }

    /// <summary>Current MBB position: lots with derived T+2 settlement + cash.</summary>
    [HttpGet("position")]
    [AdminGuard]
    [ProducesResponseType(typeof(MbbPositionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<MbbPositionDto>> GetPosition(CancellationToken cancellationToken)
    {
        return Ok(await _decisions.GetPositionAsync(cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Full-replacement write of the position (lots + cash). Idempotent: the body
    /// IS the desired state, so repeating the same PUT converges to identical rows.
    /// </summary>
    [HttpPut("position")]
    [AdminGuard]
    [ProducesResponseType(typeof(MbbPositionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MbbPositionDto>> PutPosition(
        [FromBody] UpdateMbbPositionRequest body,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _decisions.ReplacePositionAsync(body, cancellationToken: cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_POSITION_INPUT",
                Message = ex.Message,
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier,
            });
        }
    }

    /// <summary>
    /// Position-aware decision for MBB (rules-only evidence; no MBB model exists
    /// so no probability is attached). Never throws on missing market data —
    /// insufficient input returns action "wait" with reasons.
    /// </summary>
    [HttpGet("decision")]
    [AdminGuard]
    [ProducesResponseType(typeof(MbbDecisionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<MbbDecisionDto>> GetDecision(CancellationToken cancellationToken)
    {
        return Ok(await _decisions.DecideAsync(cancellationToken: cancellationToken));
    }
}

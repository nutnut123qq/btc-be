using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/paper-observations")]
[Route("api/paper-trades/observations")]
public sealed class PaperObservationsController(IPaperObservationReader reader) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PaperObservationListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaperObservationListResponse>> Get(
        [FromQuery] string symbol = ProductionSymbolPolicy.Symbol,
        [FromQuery] int take = 25,
        CancellationToken cancellationToken = default) =>
        Ok(await reader.GetLatestAsync(symbol, Math.Clamp(take, 1, 200), cancellationToken));
}

using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/market/btc")]
public class MarketRetrievalController : ControllerBase
{
    private readonly IBinanceKlinesService _binanceKlines;

    public MarketRetrievalController(IBinanceKlinesService binanceKlines)
    {
        _binanceKlines = binanceKlines;
    }

    [HttpGet("tech-summary")]
    public async Task<IActionResult> GetTechSummary(
        [FromQuery] string interval = "1h",
        [FromQuery] int limit = 48,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interval))
            return BadRequest("interval is required.");

        if (limit is < 1 or > 2000)
            return BadRequest("limit must be between 1 and 2000.");

        var context = await _binanceKlines.BuildTechSummaryAsync(
            interval: interval,
            limit: limit,
            cancellationToken: cancellationToken);

        return Ok(new
        {
            interval,
            limit,
            tech_context = context
        });
    }
}


using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SentimentController : ControllerBase
{
    private readonly ISentimentService _sentimentService;

    public SentimentController(ISentimentService sentimentService)
    {
        _sentimentService = sentimentService;
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentSentiment([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var result = await _sentimentService.GetLatestSentimentAsync(symbol, ct);
        return Ok(result);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetSentimentHistory([FromQuery] string symbol = "BTCUSDT", [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var result = await _sentimentService.GetSentimentHistoryAsync(symbol, limit, ct);
        return Ok(result);
    }

    [HttpPost("refresh")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> RefreshSentiment([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var result = await _sentimentService.CalculateAndSaveSnapshotAsync(symbol, ct);
        return Ok(result);
    }
}

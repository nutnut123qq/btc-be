using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

/// <summary>
/// Endpoint thủ công để chạy các indexer (dùng cho backfill hoặc debug).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class IndexerController : ControllerBase
{
    private readonly TechnicalIndicatorIndexer _techIndexer;
    private readonly ILogger<IndexerController> _logger;

    public IndexerController(TechnicalIndicatorIndexer techIndexer, ILogger<IndexerController> logger)
    {
        _techIndexer = techIndexer;
        _logger = logger;
    }

    /// <summary>
    /// Chạy TechnicalIndicatorIndexer cho một hoặc nhiều timeframe.
    /// Mặc định chạy cho BTCUSDT trên các timeframe: 1m, 5m, 15m, 1h, 4h, 1d.
    /// </summary>
    [HttpPost("technical-indicators")]
    public async Task<IActionResult> IndexTechnicalIndicators(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null,
        CancellationToken cancellationToken = default)
    {
        var timeframes = string.IsNullOrWhiteSpace(timeframe)
            ? new[] { "1m", "5m", "15m", "1h", "4h", "1d" }
            : new[] { timeframe.Trim() };

        var results = new Dictionary<string, object>();
        foreach (var tf in timeframes)
        {
            try
            {
                var indexed = await _techIndexer.IndexAsync(symbol, tf, cancellationToken);
                results[tf] = new { indexed, status = "ok" };
                _logger.LogInformation("Manually indexed {Indexed} technical indicators for {Symbol} {Timeframe}", indexed, symbol, tf);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index technical indicators for {Symbol} {Timeframe}", symbol, tf);
                results[tf] = new { indexed = 0, status = "error", error = ex.Message };
            }
        }

        return Ok(new { symbol, timeframes = results.Keys, results });
    }
}

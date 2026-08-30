using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Controllers;

[ApiController]
[Route("api/regime")]
public class RegimeController : ControllerBase
{
    private readonly IRegimeDetectionService _regimeService;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CurrentTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SummaryTtl = TimeSpan.FromSeconds(15);

    [ActivatorUtilitiesConstructor]
    public RegimeController(IRegimeDetectionService regimeService, IMemoryCache cache)
    {
        _regimeService = regimeService;
        _cache = cache;
    }

    public RegimeController(IRegimeDetectionService regimeService)
        : this(regimeService, new MemoryCache(new MemoryCacheOptions()))
    {
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentRegime([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", CancellationToken ct = default)
    {
        var cacheKey = $"regime:current:{symbol.ToUpperInvariant()}:{timeframe}";
        if (_cache.TryGetValue(cacheKey, out MarketRegime? cached) && cached != null)
        {
            return Ok(cached);
        }

        var regime = await _regimeService.GetCurrentRegimeAsync(symbol, timeframe, ct);
        if (regime == null) return NotFound("No regime found.");

        _cache.Set(cacheKey, regime, CurrentTtl);
        return Ok(regime);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var cacheKey = $"regime:history:{symbol.ToUpperInvariant()}:{timeframe}:{limit}";
        if (_cache.TryGetValue(cacheKey, out List<MarketRegime>? cached) && cached != null)
        {
            return Ok(cached);
        }

        var history = await _regimeService.GetRegimeHistoryAsync(symbol, timeframe, limit, ct);
        _cache.Set(cacheKey, history, HistoryTtl);
        return Ok(history);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", CancellationToken ct = default)
    {
        var cacheKey = $"regime:summary:{symbol.ToUpperInvariant()}:{timeframe}";
        if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
        {
            return Ok(cached);
        }

        var summary = await _regimeService.GetRegimeSummaryAsync(symbol, timeframe, ct);
        _cache.Set(cacheKey, summary, SummaryTtl);
        return Ok(summary);
    }

    [HttpPost("build")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> BuildRegimes([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int lookbackBars = 1000, CancellationToken ct = default)
    {
        await _regimeService.BuildRegimesAsync(symbol, timeframe, lookbackBars, ct);
        return Ok(new { message = $"Built regimes for {symbol} {timeframe}" });
    }
}

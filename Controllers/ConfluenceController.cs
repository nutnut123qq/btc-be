using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfluenceController : ControllerBase
{
    private readonly IConfluenceService _confluenceService;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CurrentTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoryTtl = TimeSpan.FromSeconds(15);

    [ActivatorUtilitiesConstructor]
    public ConfluenceController(IConfluenceService confluenceService, IMemoryCache cache)
    {
        _confluenceService = confluenceService;
        _cache = cache;
    }

    public ConfluenceController(IConfluenceService confluenceService)
        : this(confluenceService, new MemoryCache(new MemoryCacheOptions()))
    {
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var cacheKey = $"confluence:current:{symbol.ToUpperInvariant()}";
        if (_cache.TryGetValue(cacheKey, out ConfluenceSnapshot? cached) && cached != null)
        {
            return Ok(MapToDto(cached));
        }

        var snapshot = await _confluenceService.GetLatestConfluenceAsync(symbol, ct);
        if (snapshot == null) return NotFound();

        _cache.Set(cacheKey, snapshot, CurrentTtl);
        return Ok(MapToDto(snapshot));
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] string symbol = "BTCUSDT", [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var cacheKey = $"confluence:history:{symbol.ToUpperInvariant()}:{limit}";
        if (_cache.TryGetValue(cacheKey, out List<ConfluenceSnapshot>? cached) && cached != null)
        {
            return Ok(cached.Select(MapToDto));
        }

        var history = await _confluenceService.GetConfluenceHistoryAsync(symbol, limit, ct);
        _cache.Set(cacheKey, history, HistoryTtl);
        return Ok(history.Select(MapToDto));
    }

    [HttpPost("calculate")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> Calculate([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var snapshot = await _confluenceService.CalculateConfluenceAsync(symbol, ct);
        return Ok(MapToDto(snapshot));
    }

    private static object MapToDto(ConfluenceSnapshot snapshot)
    {
        var alignments = System.Text.Json.JsonSerializer.Deserialize<List<object>>(snapshot.TimeframeAlignmentsJson) ?? new List<object>();
        return new
        {
            snapshot.Id,
            snapshot.Symbol,
            snapshot.TimeMs,
            snapshot.ConfluenceScore,
            snapshot.OverallDirection,
            snapshot.HasConflict,
            snapshot.ConflictDetails,
            snapshot.CreatedAtUtc,
            timeframeAlignments = alignments
        };
    }
}

using Backend.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Controllers;

[ApiController]
[Route("api/liquidation")]
public class LiquidationController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan LatestTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HistoryTtl = TimeSpan.FromSeconds(30);

    [ActivatorUtilitiesConstructor]
    public LiquidationController(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public LiquidationController(AppDbContext db)
        : this(db, new MemoryCache(new MemoryCacheOptions()))
    {
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        var cacheKey = $"liq:latest:{symbol}:{timeframe}";
        if (_cache.TryGetValue(cacheKey, out LiquidationSnapshot? cached) && cached != null)
        {
            return Ok(cached);
        }

        var snapshot = await _db.LiquidationSnapshots
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .OrderByDescending(x => x.TimestampUtc)
            .FirstOrDefaultAsync(ct);

        if (snapshot == null)
        {
            // Fallback to any timeframe for this symbol
            snapshot = await _db.LiquidationSnapshots
                .AsNoTracking()
                .Where(x => x.Symbol == symbol)
                .OrderByDescending(x => x.TimestampUtc)
                .FirstOrDefaultAsync(ct);
        }

        if (snapshot == null)
        {
            return NotFound(new { message = $"No liquidation snapshot found for {symbol}" });
        }

        _cache.Set(cacheKey, snapshot, LatestTtl);
        return Ok(snapshot);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        var cacheKey = $"liq:history:{symbol}:{timeframe}:{limit}";
        if (_cache.TryGetValue(cacheKey, out List<LiquidationSnapshot>? cached) && cached != null)
        {
            return Ok(cached);
        }

        var snapshots = await _db.LiquidationSnapshots
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && (string.IsNullOrEmpty(timeframe) || x.Timeframe == timeframe))
            .OrderByDescending(x => x.TimestampUtc)
            .Take(limit)
            .ToListAsync(ct);

        _cache.Set(cacheKey, snapshots, HistoryTtl);
        return Ok(snapshots);
    }

    private static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "BTCUSDT";
        symbol = symbol.Trim().ToUpperInvariant();
        if (!symbol.EndsWith("USDT") && !symbol.Contains('/'))
            symbol += "USDT";
        return symbol;
    }
}

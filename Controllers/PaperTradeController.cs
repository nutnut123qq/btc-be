using Backend.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/paper-trades")]
public class PaperTradeController : ControllerBase
{
    private readonly AppDbContext _db;

    public PaperTradeController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null,
        [FromQuery] string? status = null,
        [FromQuery] string? side = null,
        [FromQuery] int take = 50,
        [FromQuery] int page = 1)
    {
        take = Math.Min(take, 200);
        page = Math.Max(1, page);

        var query = _db.PaperTrades.AsNoTracking().Where(t => t.Symbol == symbol);

        if (!string.IsNullOrEmpty(timeframe))
            query = query.Where(t => t.Timeframe == timeframe);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(t => t.Status == status);

        if (!string.IsNullOrEmpty(side))
            query = query.Where(t => t.Side == side);

        var count = await query.CountAsync();

        var items = await query
            .OrderByDescending(t => t.EntryTimeMs)
            .Skip((page - 1) * take)
            .Take(take)
            .ToListAsync();

        return Ok(new { symbol, status, count, items });
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null)
    {
        var query = _db.PaperTrades.AsNoTracking().Where(t => t.Symbol == symbol);
        
        if (!string.IsNullOrEmpty(timeframe))
            query = query.Where(t => t.Timeframe == timeframe);

        var allTrades = await query.ToListAsync();

        var totalTrades = allTrades.Count;
        var openTrades = allTrades.Count(t => t.Status == "open");
        var closedTrades = allTrades.Where(t => t.Status == "closed").ToList();
        
        var winCount = closedTrades.Count(t => t.NetReturn > 0);
        var winRate = closedTrades.Count > 0 ? (double)winCount / closedTrades.Count * 100 : 0;
        
        var totalNetReturnPct = closedTrades.Sum(t => t.NetReturn ?? 0) * 100;
        var avgReturnPct = closedTrades.Count > 0 ? closedTrades.Average(t => t.NetReturn ?? 0) * 100 : 0;
        
        var bestTradePct = closedTrades.Count > 0 ? closedTrades.Max(t => t.NetReturn ?? 0) * 100 : 0;
        var worstTradePct = closedTrades.Count > 0 ? closedTrades.Min(t => t.NetReturn ?? 0) * 100 : 0;

        var longCount = allTrades.Count(t => t.Side == "long");
        var shortCount = allTrades.Count(t => t.Side == "short");

        double maxDrawdownPct = 0;
        double peak = 1;
        double currentEquity = 1;

        foreach (var t in closedTrades.OrderBy(t => t.ExitTimeMs))
        {
            currentEquity *= (1 + (t.NetReturn ?? 0));
            if (currentEquity > peak)
            {
                peak = currentEquity;
            }
            var drawdown = (peak - currentEquity) / peak * 100;
            if (drawdown > maxDrawdownPct)
            {
                maxDrawdownPct = drawdown;
            }
        }

        return Ok(new
        {
            totalTrades,
            openTrades,
            closedTrades = closedTrades.Count,
            winRate,
            totalNetReturnPct,
            avgReturnPct,
            maxDrawdownPct,
            bestTradePct,
            worstTradePct,
            longCount,
            shortCount
        });
    }

    [HttpGet("equity-curve")]
    public async Task<IActionResult> GetEquityCurve(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null)
    {
        var query = _db.PaperTrades.AsNoTracking()
            .Where(t => t.Symbol == symbol && t.Status == "closed");
        
        if (!string.IsNullOrEmpty(timeframe))
            query = query.Where(t => t.Timeframe == timeframe);

        var closedTrades = await query
            .OrderBy(t => t.ExitTimeMs)
            .Select(t => new { t.ExitTimeMs, t.NetReturn })
            .ToListAsync();

        var result = new List<object>();
        double cumulativeProduct = 1;
        int tradeCount = 0;

        foreach (var t in closedTrades)
        {
            cumulativeProduct *= (1 + (t.NetReturn ?? 0));
            tradeCount++;
            result.Add(new
            {
                timeMs = t.ExitTimeMs,
                cumulativeReturnPct = (cumulativeProduct - 1) * 100,
                tradeCount
            });
        }

        return Ok(result);
    }

    [HttpGet("open")]
    public async Task<IActionResult> GetOpen(
        [FromQuery] string symbol = "BTCUSDT")
    {
        var items = await _db.PaperTrades.AsNoTracking()
            .Where(t => t.Symbol == symbol && t.Status == "open")
            .OrderByDescending(t => t.EntryTimeMs)
            .ToListAsync();

        return Ok(items);
    }
}

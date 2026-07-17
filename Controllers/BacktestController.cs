using Backend.Data;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BacktestController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(AppDbContext db, ILogger<BacktestController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("runs")]
    public async Task<ActionResult<object>> GetRuns(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 200);
        var query = _db.BacktestRuns.AsNoTracking().Where(x => x.Symbol == symbol);
        if (!string.IsNullOrWhiteSpace(timeframe))
            query = query.Where(x => x.Timeframe == timeframe);

        var items = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.Symbol,
                x.Timeframe,
                x.WindowSize,
                x.Horizon,
                x.ModelName,
                x.StartTimeMs,
                x.EndTimeMs,
                x.TotalTrades,
                x.WinRate,
                x.TotalReturnPct,
                x.BuyHoldReturnPct,
                x.MaxDrawdownPct,
                x.SharpeRatio,
                x.ProfitFactor,
                x.FinalEquity,
                x.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { symbol, timeframe, count = items.Count, items });
    }

    [HttpGet("runs/{id:int}")]
    public async Task<ActionResult<object>> GetRunDetail(int id, CancellationToken cancellationToken = default)
    {
        var run = await _db.BacktestRuns
            .AsNoTracking()
            .Include(x => x.Trades.OrderBy(t => t.EntryTimeMs).Take(1000))
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (run == null)
            return NotFound(new ApiErrorEnvelope { Code = "BACKTEST_NOT_FOUND", Message = $"Backtest run {id} not found.", Retryable = false, RequestId = HttpContext.TraceIdentifier });

        return Ok(new
        {
            run.Id,
            run.Symbol,
            run.Timeframe,
            run.WindowSize,
            run.Horizon,
            run.ModelName,
            run.StartTimeMs,
            run.EndTimeMs,
            run.FeeBps,
            run.SlippageBps,
            run.TotalTrades,
            run.WinRate,
            run.TotalReturnPct,
            run.BuyHoldReturnPct,
            run.MaxDrawdownPct,
            run.SharpeRatio,
            run.SortinoRatio,
            run.ProfitFactor,
            run.FinalEquity,
            run.MetricsJson,
            run.EquityCurveJson,
            run.CreatedAtUtc,
            trades = run.Trades.Select(t => new
            {
                t.Id,
                t.EntryTimeMs,
                t.ExitTimeMs,
                t.Side,
                t.EntryPrice,
                t.ExitPrice,
                t.PnlPct,
                t.Confidence,
                t.TrueLabel,
            })
        });
    }
}

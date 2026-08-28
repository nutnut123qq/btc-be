using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/ensemble-backtest")]
public class EnsembleBacktestController : ControllerBase
{
    private readonly IEnsembleBacktestService _backtestService;
    private readonly ILogger<EnsembleBacktestController> _logger;

    public EnsembleBacktestController(
        IEnsembleBacktestService backtestService,
        ILogger<EnsembleBacktestController> logger)
    {
        _backtestService = backtestService;
        _logger = logger;
    }

    [HttpPost("run")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> RunBacktest([FromBody] EnsembleBacktestRunRequestDto req, CancellationToken ct)
    {
        try
        {
            var (run, trades, equityCurve) = await _backtestService.RunEnsembleBacktestAsync(
                req.Symbol ?? "BTCUSDT",
                req.Timeframe ?? "1h",
                req.StartTimeMs,
                req.EndTimeMs,
                req.InitialCapital ?? 10000,
                req.FeeBps ?? 10,
                req.MinConfidence ?? 0.55,
                req.CustomWeights,
                ct);

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
                run.TotalTrades,
                run.WinRate,
                run.TotalReturnPct,
                run.BuyHoldReturnPct,
                run.MaxDrawdownPct,
                run.SharpeRatio,
                run.ProfitFactor,
                run.FinalEquity,
                run.CreatedAtUtc,
                trades = trades.Select(t => new
                {
                    t.Id,
                    t.EntryTimeMs,
                    t.ExitTimeMs,
                    t.Side,
                    t.EntryPrice,
                    t.ExitPrice,
                    t.PnlPct,
                    t.Confidence,
                    t.TrueLabel
                }),
                equityCurve
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("INSUFFICIENT_POINT_IN_TIME_DATA"))
        {
            _logger.LogWarning(ex, "Rejected fake backtest");
            return BadRequest(new { message = "INSUFFICIENT_POINT_IN_TIME_DATA", detail = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Ensemble backtest");
            return StatusCode(500, new { message = "Ensemble backtest failed", detail = ex.Message });
        }
    }

    [HttpPost("optimize")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> OptimizeWeights(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        CancellationToken ct = default)
    {
        try
        {
            var result = await _backtestService.OptimizeWeightsAsync(symbol, timeframe, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to optimize Ensemble weights");
            return StatusCode(500, new { message = "Weight optimization failed", detail = ex.Message });
        }
    }
}

public class EnsembleBacktestRunRequestDto
{
    public string? Symbol { get; set; }
    public string? Timeframe { get; set; }
    public long? StartTimeMs { get; set; }
    public long? EndTimeMs { get; set; }
    public double? InitialCapital { get; set; }
    public double? FeeBps { get; set; }
    public double? MinConfidence { get; set; }
    public Dictionary<string, double>? CustomWeights { get; set; }
}

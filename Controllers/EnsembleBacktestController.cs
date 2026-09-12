using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/ensemble-backtest")]
public class EnsembleBacktestController : ControllerBase
{
    private readonly IEnsembleBacktestService _backtestService;
    private readonly ILogger<EnsembleBacktestController> _logger;
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public EnsembleBacktestController(
        IEnsembleBacktestService backtestService,
        ILogger<EnsembleBacktestController> logger,
        ProductionTimeframePolicy? timeframePolicy = null)
    {
        _backtestService = backtestService;
        _logger = logger;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
    }

    [HttpPost("run")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> RunBacktest([FromBody] EnsembleBacktestRunRequestDto req, CancellationToken ct)
    {
        var timeframe = ProductionTimeframePolicy.Canonicalize(req.Timeframe);
        if (string.IsNullOrEmpty(timeframe)) timeframe = _timeframePolicy.Default;
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));

        try
        {
            var (run, trades, equityCurve) = await _backtestService.RunEnsembleBacktestAsync(
                req.Symbol ?? "BTCUSDT",
                timeframe,
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
                run.PipelineVersion,
                run.EvaluationVersion,
                run.ValidityStatus,
                run.InvalidReason,
                run.ArchivedAtUtc,
                Validated = false,
                Maturity = "Experimental",
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
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("INSUFFICIENT_POINT_IN_TIME_", StringComparison.Ordinal))
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
        [FromQuery] string timeframe = "4h",
        CancellationToken ct = default)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));

        try
        {
            var result = await _backtestService.OptimizeWeightsAsync(symbol, timeframe, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("INSUFFICIENT_POINT_IN_TIME_", StringComparison.Ordinal))
        {
            _logger.LogWarning(ex, "Rejected weight optimization without point-in-time layer data");
            return BadRequest(new { message = "INSUFFICIENT_POINT_IN_TIME_LAYER_DATA", detail = ex.Message });
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
    public string? Timeframe { get; set; } = "4h";
    public long? StartTimeMs { get; set; }
    public long? EndTimeMs { get; set; }
    public double? InitialCapital { get; set; }
    public double? FeeBps { get; set; }
    public double? MinConfidence { get; set; }
    public Dictionary<string, double>? CustomWeights { get; set; }
}

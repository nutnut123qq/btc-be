using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EnsembleController : ControllerBase
{
    private readonly IEnsembleService _ensembleService;

    public EnsembleController(IEnsembleService ensembleService)
    {
        _ensembleService = ensembleService;
    }

    [HttpGet("predict")]
    public async Task<IActionResult> PredictEnsemble([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", CancellationToken ct = default)
    {
        var result = await _ensembleService.PredictEnsembleAsync(symbol, timeframe, ct);
        return Ok(MapToDto(result));
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetEnsembleHistory([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var result = await _ensembleService.GetEnsembleHistoryAsync(symbol, timeframe, limit, ct);
        return Ok(result.Select(MapToDto));
    }

    [HttpPost("evaluate")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> EvaluatePredictions([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var result = await _ensembleService.EvaluatePredictionsAsync(symbol, ct);
        return Ok(new
        {
            result.Symbol,
            result.TotalPredictions,
            result.TrueCount,
            result.FalseCount,
            result.PendingCount,
            result.WinRatePct,
            Items = result.Items.Select(MapToDto)
        });
    }

    [HttpGet("evaluations")]
    public async Task<IActionResult> GetEvaluations([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var result = await _ensembleService.EvaluatePredictionsAsync(symbol, ct);
        return Ok(new
        {
            result.Symbol,
            result.TotalPredictions,
            result.TrueCount,
            result.FalseCount,
            result.PendingCount,
            result.WinRatePct,
            Items = result.Items.Select(MapToDto)
        });
    }

    [HttpPost("batch-replay")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> BatchReplay([FromQuery] int sampleCount = 2000, [FromQuery] double minConfidence = 0.60, [FromQuery] bool enableMtfFilter = true, [FromQuery] bool enableSmcFilter = true, [FromQuery] bool enableAtrRrEngine = true, [FromQuery] bool enableVolumeFilter = true, [FromQuery] bool enableMlClassifier = true, [FromQuery] bool enableKellySizing = true, [FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", CancellationToken ct = default)
    {
        var result = await _ensembleService.BatchReplayAsync(sampleCount, minConfidence, enableMtfFilter, enableSmcFilter, enableAtrRrEngine, enableVolumeFilter, enableMlClassifier, enableKellySizing, symbol, timeframe, ct);
        return Ok(result); // Already a clean DTO
    }

    private static object MapToDto(Backend.Data.EnsemblePredictionRecord r)
    {
        var layerBreakdown = System.Text.Json.JsonSerializer.Deserialize<List<object>>(r.LayerBreakdownJson) ?? new List<object>();
        return new
        {
            r.Id,
            r.Symbol,
            r.Timeframe,
            r.TimeMs,
            r.EntryPrice,
            r.FinalDirection,
            r.ProbUp,
            r.ProbDown,
            r.ProbSideways,
            r.EnsembleConfidence,
            r.ActualPrice24h,
            r.ActualReturnPct,
            r.EvaluationStatus,
            r.EvaluatedAtMs,
            r.CreatedAtUtc,
            layerBreakdown
        };
    }
}

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
        return Ok(result);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetEnsembleHistory([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var result = await _ensembleService.GetEnsembleHistoryAsync(symbol, timeframe, limit, ct);
        return Ok(result);
    }

    [HttpPost("evaluate")]
    public async Task<IActionResult> EvaluatePredictions([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var result = await _ensembleService.EvaluatePredictionsAsync(symbol, ct);
        return Ok(result);
    }

    [HttpGet("evaluations")]
    public async Task<IActionResult> GetEvaluations([FromQuery] string symbol = "BTCUSDT", CancellationToken ct = default)
    {
        var result = await _ensembleService.EvaluatePredictionsAsync(symbol, ct);
        return Ok(result);
    }

    [HttpPost("batch-replay")]
    public async Task<IActionResult> BatchReplay([FromQuery] int sampleCount = 2000, [FromQuery] double minConfidence = 0.60, [FromQuery] bool enableMtfFilter = true, [FromQuery] bool enableSmcFilter = true, [FromQuery] bool enableAtrRrEngine = true, [FromQuery] bool enableVolumeFilter = true, [FromQuery] bool enableMlClassifier = true, [FromQuery] bool enableKellySizing = true, [FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", CancellationToken ct = default)
    {
        var result = await _ensembleService.BatchReplayAsync(sampleCount, minConfidence, enableMtfFilter, enableSmcFilter, enableAtrRrEngine, enableVolumeFilter, enableMlClassifier, enableKellySizing, symbol, timeframe, ct);
        return Ok(result);
    }
}

namespace Backend.Controllers;

using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Backend.Services;
using Backend.Data;

[ApiController]
[Route("api/[controller]")]
public class TransitionsController : ControllerBase
{
    private readonly ITransitionService _transitionService;

    public TransitionsController(ITransitionService transitionService)
    {
        _transitionService = transitionService;
    }

    [HttpGet("from/{id}")]
    public async Task<ActionResult<List<ArchetypeTransition>>> GetTransitionsFrom(long id, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetTransitionsFromAsync(id, top, ct));
    }

    [HttpGet("to/{id}")]
    public async Task<ActionResult<List<ArchetypeTransition>>> GetTransitionsTo(long id, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetTransitionsToAsync(id, top, ct));
    }

    [HttpGet("predict")]
    public async Task<ActionResult<List<ArchetypeTransition>>> PredictNext([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int windowSize = 20, CancellationToken ct = default)
    {
        return Ok(await _transitionService.PredictNextAsync(symbol, timeframe, windowSize, ct));
    }

    [HttpGet("predict-sequence")]
    public async Task<ActionResult<List<ArchetypeSequence>>> PredictSequence([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int windowSize = 20, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetSequencePredictionAsync(symbol, timeframe, windowSize, ct));
    }

    [HttpGet("entropy-ranking")]
    public async Task<ActionResult<object>> GetEntropyRanking([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int? windowSize = null, [FromQuery] int top = 50, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetEntropyRankingAsync(symbol, timeframe, windowSize, top, ct));
    }

    [HttpGet("matrix")]
    public async Task<ActionResult<List<ArchetypeTransition>>> GetTransitionMatrix([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int windowSize = 20, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetTransitionMatrixAsync(symbol, timeframe, windowSize, ct));
    }

    [HttpPost("build")]
    public ActionResult BuildMatrix()
    {
        // Trigger rebuild (just returns accepted, actual build done by Python)
        return Accepted(new { message = "Build triggered." });
    }
}

namespace Backend.Controllers;

using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using Backend.Services;
using Backend.Services.Models;

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
    public async Task<ActionResult<ArchetypeTransitionsResponse>> GetTransitionsFrom(long id, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetTransitionsFromAsync(id, Math.Clamp(top, 1, 100), ct));
    }

    [HttpGet("to/{id}")]
    public async Task<ActionResult<ArchetypeTransitionsResponse>> GetTransitionsTo(long id, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetTransitionsToAsync(id, Math.Clamp(top, 1, 100), ct));
    }

    [HttpGet("predict")]
    public async Task<ActionResult<TransitionPredictionDto>> PredictNext([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int windowSize = 20, CancellationToken ct = default)
    {
        return Ok(await _transitionService.PredictNextAsync(symbol, timeframe, windowSize, ct));
    }

    [HttpGet("predict-sequence")]
    public async Task<ActionResult<SequencePredictionDto>> PredictSequence([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int windowSize = 20, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetSequencePredictionAsync(symbol, timeframe, windowSize, ct));
    }

    [HttpGet("entropy-ranking")]
    public async Task<ActionResult<EntropyRankingResponse>> GetEntropyRanking([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int? windowSize = null, [FromQuery] int top = 50, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetEntropyRankingAsync(symbol, timeframe, windowSize, Math.Clamp(top, 1, 200), ct));
    }

    [HttpGet("matrix")]
    public async Task<ActionResult<TransitionMatrixDto>> GetTransitionMatrix([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int windowSize = 20, CancellationToken ct = default)
    {
        return Ok(await _transitionService.GetTransitionMatrixAsync(symbol, timeframe, windowSize, ct));
    }

    [HttpPost("build")]
    [Backend.Filters.AdminGuard]
    public ActionResult BuildMatrix()
    {
        return StatusCode(StatusCodes.Status501NotImplemented, new ApiErrorEnvelope
        {
            Code = "TRANSITION_BUILD_UNAVAILABLE",
            Message = "Transition matrix rebuild is not available through this API.",
            Retryable = false,
            RequestId = HttpContext.TraceIdentifier
        });
    }
}

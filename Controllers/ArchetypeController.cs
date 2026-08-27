using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/archetypes")]
public class ArchetypeController : ControllerBase
{
    private readonly IArchetypeService _archetypeService;
    private readonly ILogger<ArchetypeController> _logger;

    public ArchetypeController(
        IArchetypeService archetypeService,
        ILogger<ArchetypeController> logger)
    {
        _archetypeService = archetypeService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<object>> GetArchetypes(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int? windowSize = null,
        [FromQuery] string sortBy = "winRate",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 200);

        try
        {
            var (total, items) = await _archetypeService.GetArchetypesAsync(symbol, timeframe, windowSize, sortBy, page, pageSize, cancellationToken);
            return Ok(new
            {
                requestId = HttpContext.TraceIdentifier,
                symbol,
                timeframe,
                windowSize,
                page,
                pageSize,
                total,
                items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get archetypes");
            return StatusCode(500, new ApiErrorEnvelope
            {
                Code = "ARCHETYPE_FETCH_FAILED",
                Message = "Failed to fetch archetypes.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetArchetypeDetail(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var archetype = await _archetypeService.GetArchetypeDetailAsync(id, cancellationToken);
            if (archetype == null)
            {
                return NotFound(new ApiErrorEnvelope
                {
                    Code = "ARCHETYPE_NOT_FOUND",
                    Message = $"Archetype with ID {id} not found.",
                    Retryable = false,
                    RequestId = HttpContext.TraceIdentifier
                });
            }

            return Ok(new
            {
                requestId = HttpContext.TraceIdentifier,
                archetype
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get archetype detail for id {Id}", id);
            return StatusCode(500, new ApiErrorEnvelope
            {
                Code = "ARCHETYPE_DETAIL_FAILED",
                Message = "Failed to fetch archetype detail.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpGet("{id}/occurrences")]
    public async Task<ActionResult<object>> GetOccurrences(
        long id,
        [FromQuery] string horizon = "4h",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 200);

        try
        {
            var (total, items) = await _archetypeService.GetOccurrencesAsync(id, horizon, page, pageSize, cancellationToken);
            return Ok(new
            {
                requestId = HttpContext.TraceIdentifier,
                archetypeId = id,
                horizon,
                page,
                pageSize,
                total,
                items
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get occurrences for archetype {Id}", id);
            return StatusCode(500, new ApiErrorEnvelope
            {
                Code = "ARCHETYPE_OCCURRENCES_FAILED",
                Message = "Failed to fetch occurrences.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpGet("match")]
    public async Task<ActionResult<object>> MatchCurrentWindow(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int windowSize = 15,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var match = await _archetypeService.MatchCurrentWindowAsync(symbol, timeframe, windowSize, cancellationToken);
            return Ok(new
            {
                requestId = HttpContext.TraceIdentifier,
                match
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to match current window");
            return StatusCode(500, new ApiErrorEnvelope
            {
                Code = "ARCHETYPE_MATCH_FAILED",
                Message = "Failed to match current window.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpGet("match-multi")]
    public async Task<ActionResult<object>> MatchMultiWindow(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (matches, weightedSignal) = await _archetypeService.MatchMultiWindowAsync(symbol, timeframe, cancellationToken);
            return Ok(new
            {
                requestId = HttpContext.TraceIdentifier,
                symbol,
                timeframe,
                matches,
                weightedSignal
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to match multi window");
            return StatusCode(500, new ApiErrorEnvelope
            {
                Code = "ARCHETYPE_MATCH_MULTI_FAILED",
                Message = "Failed to match multi window.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpGet("rankings")]
    public async Task<ActionResult<object>> GetRankings(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int? windowSize = null,
        [FromQuery] string? horizon = null,
        [FromQuery] string sortBy = "winRate",
        [FromQuery] int top = 20,
        CancellationToken cancellationToken = default)
    {
        top = Math.Clamp(top, 1, 100);

        try
        {
            var rankings = await _archetypeService.GetRankingsAsync(symbol, timeframe, windowSize, horizon, sortBy, top, cancellationToken);
            return Ok(new
            {
                requestId = HttpContext.TraceIdentifier,
                rankings
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get rankings");
            return StatusCode(500, new ApiErrorEnvelope
            {
                Code = "ARCHETYPE_RANKINGS_FAILED",
                Message = "Failed to fetch rankings.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }
}

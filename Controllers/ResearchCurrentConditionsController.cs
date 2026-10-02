using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Controllers;

/// <summary>
/// GET /api/research/current-conditions — proxies the AI current-conditions
/// endpoint and joins each returned condition with the verified technical-event
/// statistical report (contract §3). Read-only; no scoring or trading claims.
/// </summary>
[ApiController]
[Route("api/research/current-conditions")]
public sealed class ResearchCurrentConditionsController : ControllerBase
{
    private readonly CurrentConditionsService _conditions;

    // Depends only on already-registered services (the "AIService" named
    // HttpClient and the IResearchEvidenceCatalog singleton). The optional
    // service parameter lets the coordinator register CurrentConditionsService
    // in Program.cs later; until then it is constructed in place.
    public ResearchCurrentConditionsController(
        IHttpClientFactory httpClientFactory,
        IResearchEvidenceCatalog catalog,
        ILogger<CurrentConditionsService> serviceLogger,
        ILogger<ResearchCurrentConditionsController> logger,
        CurrentConditionsService? service = null,
        IMemoryCache? cache = null)
    {
        _ = logger;
        _conditions = service ?? new CurrentConditionsService(httpClientFactory, catalog, serviceLogger, cache);
    }

    [HttpGet]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetCurrentConditions(
        [FromQuery] string? timeframe,
        CancellationToken cancellationToken = default)
    {
        if (timeframe is not ("1h" or "4h" or "1d"))
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_TIMEFRAME",
                Message = "timeframe must be one of 1h, 4h, 1d.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }

        var result = await _conditions.GetAsync(timeframe, cancellationToken);
        return result.Outcome switch
        {
            CurrentConditionsOutcome.Ok => Ok(result.Payload),
            CurrentConditionsOutcome.SourceUnavailable => StatusCode(
                StatusCodes.Status502BadGateway,
                new ApiErrorEnvelope
                {
                    Code = "CONDITIONS_SOURCE_UNAVAILABLE",
                    Message = "The AI current-conditions service is unavailable.",
                    Retryable = true,
                    RequestId = HttpContext.TraceIdentifier
                }),
            _ => StatusCode(
                StatusCodes.Status502BadGateway,
                new ApiErrorEnvelope
                {
                    Code = "CONDITIONS_PAYLOAD_INVALID",
                    Message = "The AI current-conditions payload failed schema validation.",
                    Retryable = false,
                    RequestId = HttpContext.TraceIdentifier
                })
        };
    }
}

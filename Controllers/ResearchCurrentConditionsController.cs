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
    private readonly ProductionSymbolPolicy _symbolPolicy;

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
        IMemoryCache? cache = null,
        ProductionSymbolPolicy? symbolPolicy = null)
    {
        _ = logger;
        _conditions = service ?? new CurrentConditionsService(httpClientFactory, catalog, serviceLogger, cache);
        _symbolPolicy = symbolPolicy ?? new ProductionSymbolPolicy();
    }

    [HttpGet]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetCurrentConditions(
        [FromQuery] string? timeframe,
        [FromQuery] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        // Catalog check only: the global ProductionSymbolScopeFilter still rejects
        // known-but-disabled instruments (MBB) before this action runs, so the
        // per-instrument timeframe validation below is the ready-state behavior.
        var descriptor = _symbolPolicy.GetInstrument(
            string.IsNullOrWhiteSpace(symbol) ? ProductionSymbolPolicy.Symbol : symbol);
        if (descriptor is null)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "UNSUPPORTED_SYMBOL",
                Message = _symbolPolicy.InactiveMessage(symbol),
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }

        if (timeframe is null
            || !descriptor.ActiveTimeframes.Contains(timeframe, StringComparer.Ordinal))
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_TIMEFRAME",
                Message = $"timeframe must be one of {string.Join(", ", descriptor.ActiveTimeframes)} for {descriptor.Symbol}.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }

        var result = await _conditions.GetAsync(descriptor.Symbol, timeframe, cancellationToken);
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

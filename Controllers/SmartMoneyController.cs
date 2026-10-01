using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/smart-money")]
public class SmartMoneyController : ControllerBase
{
    private readonly ISmartMoneyService _service;
    private readonly ProductionTimeframePolicy _timeframePolicy;
    private readonly ProductionSymbolPolicy _symbolPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly ITechnicalEvidenceRebuildService? _evidenceRebuild;
    private readonly ICausalSmartMoneyRebuildService? _causalSmcRebuild;

    public SmartMoneyController(
        ISmartMoneyService service,
        ProductionTimeframePolicy? timeframePolicy = null,
        ProductionSymbolPolicy? symbolPolicy = null,
        TimeProvider? timeProvider = null,
        ITechnicalEvidenceRebuildService? evidenceRebuild = null,
        ICausalSmartMoneyRebuildService? causalSmcRebuild = null)
    {
        _service = service;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
        _symbolPolicy = symbolPolicy ?? new ProductionSymbolPolicy();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _evidenceRebuild = evidenceRebuild;
        _causalSmcRebuild = causalSmcRebuild;
    }

    [HttpPost("causal-rebuild")]
    [Backend.Filters.AdminGuard]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType<CausalSmartMoneyRebuildResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RebuildCausalSmc(
        [FromBody] CausalSmartMoneyRebuildRequest request,
        CancellationToken ct = default)
    {
        if (_causalSmcRebuild is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Error("SMC_REBUILD_UNAVAILABLE", "Causal SMC rebuild service is unavailable."));
        try
        {
            return Ok(await _causalSmcRebuild.RebuildAsync(request, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Error("INVALID_SMC_REBUILD_REQUEST", ex.Message));
        }
        catch (CausalSmartMoneyRebuildLimitException ex)
        {
            return Conflict(Error(ex.Code, ex.Message));
        }
    }

    [HttpGet("causal-coverage")]
    [ProducesResponseType<CausalSmartMoneyCoverageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CausalSmcCoverage(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "4h",
        CancellationToken ct = default)
    {
        symbol = ProductionSymbolPolicy.Canonicalize(symbol);
        if (!_symbolPolicy.IsActive(symbol))
            return BadRequest(Error("UNSUPPORTED_SYMBOL", _symbolPolicy.InactiveMessage(symbol)));
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));
        if (_causalSmcRebuild is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Error("SMC_COVERAGE_UNAVAILABLE", "Causal SMC coverage service is unavailable."));
        return Ok(await _causalSmcRebuild.GetCoverageAsync(symbol, timeframe, ct));
    }

    [HttpPost("replay/rebuild")]
    [Backend.Filters.AdminGuard]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType<TechnicalEvidenceRebuildResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RebuildReplayEvidence(
        [FromBody] TechnicalEvidenceRebuildRequest request,
        CancellationToken ct = default)
    {
        if (_evidenceRebuild is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Error("REBUILD_UNAVAILABLE", "Technical evidence rebuild service is unavailable."));
        try
        {
            var result = await _evidenceRebuild.RebuildAsync(request, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Error("INVALID_REBUILD_REQUEST", ex.Message));
        }
        catch (TechnicalEvidenceRebuildLimitException ex)
        {
            return Conflict(Error("REBUILD_SIZE_LIMIT_EXCEEDED", ex.Message));
        }
    }

    [HttpGet("replay/coverage")]
    [ProducesResponseType<TechnicalEvidenceCoverageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReplayCoverage(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "4h",
        CancellationToken ct = default)
    {
        symbol = ProductionSymbolPolicy.Canonicalize(symbol);
        if (!_symbolPolicy.IsActive(symbol))
            return BadRequest(Error("UNSUPPORTED_SYMBOL", _symbolPolicy.InactiveMessage(symbol)));
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));
        if (_evidenceRebuild is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Error("COVERAGE_UNAVAILABLE", "Technical evidence coverage service is unavailable."));
        return Ok(await _evidenceRebuild.GetCoverageAsync(symbol, timeframe, ct));
    }

    [HttpGet("structures")]
    public async Task<IActionResult> GetStructures([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "4h", [FromQuery] int lookbackBars = 100)
    {
        symbol = ProductionSymbolPolicy.Canonicalize(symbol);
        if (!_symbolPolicy.IsActive(symbol))
            return BadRequest(Error("UNSUPPORTED_SYMBOL", _symbolPolicy.InactiveMessage(symbol)));
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));
        var result = await _service.GetSmartMoneyStructuresAsync(symbol, timeframe, lookbackBars);
        return Ok(result);
    }

    [HttpPost("detect")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> Detect([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "4h", [FromQuery] int lookbackBars = 100)
    {
        symbol = ProductionSymbolPolicy.Canonicalize(symbol);
        if (!_symbolPolicy.IsActive(symbol))
            return BadRequest(Error("UNSUPPORTED_SYMBOL", _symbolPolicy.InactiveMessage(symbol)));
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));
        var result = await _service.GetSmartMoneyStructuresAsync(symbol, timeframe, lookbackBars);
        return Ok(result);
    }

    [HttpGet("replay")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType<TechnicalReplayResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Replay(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "4h",
        [FromQuery] long? asOfTimeMs = null,
        [FromQuery] int lookbackBars = 500,
        CancellationToken ct = default)
    {
        symbol = ProductionSymbolPolicy.Canonicalize(symbol);
        if (!_symbolPolicy.IsActive(symbol))
            return BadRequest(Error("UNSUPPORTED_SYMBOL", _symbolPolicy.InactiveMessage(symbol)));

        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var requestedAsOf = asOfTimeMs ?? nowMs;
        if (requestedAsOf <= 0 || requestedAsOf > nowMs)
            return BadRequest(Error("INVALID_AS_OF", "asOfTimeMs must be a positive Unix timestamp in milliseconds and cannot be in the future."));
        if (lookbackBars is < 5 or > 10_000)
            return BadRequest(Error("INVALID_LOOKBACK", "lookbackBars must be between 5 and 10000."));

        try
        {
            var result = await _service.GetReplayAsync(symbol, timeframe, requestedAsOf, lookbackBars, ct);
            return Ok(result);
        }
        catch (TechnicalReplayContextLimitException ex)
        {
            return Conflict(Error("REPLAY_CONTEXT_LIMIT_EXCEEDED",
                $"Replay requires more than {ex.MaxBars} finalized candles. Refusing to truncate causal context."));
        }
    }

    private ApiErrorEnvelope Error(string code, string message) => new()
    {
        Code = code,
        Message = message,
        Retryable = false,
        RequestId = HttpContext.TraceIdentifier
    };
}

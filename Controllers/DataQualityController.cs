using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/market/data-quality")]
public sealed class DataQualityController(IKlineDataQualityService service) : ControllerBase
{
    [HttpGet("issues")]
    [ProducesResponseType<KlineDataIssuesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetIssues(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "4h",
        [FromQuery] long? startOpenTimeMs = null,
        [FromQuery] long? endOpenTimeMs = null,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await service.GetIssuesAsync(symbol, timeframe, startOpenTimeMs, endOpenTimeMs,
                limit, cancellationToken));
        }
        catch (KlineDataQualityException ex)
        {
            return BadRequest(Error(ex.Code, ex.Message));
        }
    }

    [HttpPost("repair")]
    [Backend.Filters.AdminGuard]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    [ProducesResponseType<KlineDataRepairResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiErrorEnvelope>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Repair(
        [FromBody] KlineDataRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await service.RepairAsync(request, cancellationToken));
        }
        catch (KlineDataQualityException ex) when (ex.Code is "PREVIEW_DRIFT" or "SOURCE_PREVIEW_DRIFT")
        {
            return Conflict(Error(ex.Code, ex.Message));
        }
        catch (KlineDataQualityException ex)
        {
            return BadRequest(Error(ex.Code, ex.Message));
        }
        catch (KlineDataRepairSourceException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                Error("REPAIR_SOURCE_UNAVAILABLE", ex.Message, retryable: true));
        }
    }

    private ApiErrorEnvelope Error(string code, string message, bool retryable = false) => new()
    {
        Code = code,
        Message = message,
        Retryable = retryable,
        RequestId = HttpContext.TraceIdentifier
    };
}

using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Backend.Controllers;

[ApiController]
[Route("api/historical-analogs")]
public sealed class HistoricalAnalogsController : ControllerBase
{
    private readonly IHistoricalAnalogService _service;
    private readonly ILogger<HistoricalAnalogsController> _logger;

    public HistoricalAnalogsController(
        IHistoricalAnalogService service,
        ILogger<HistoricalAnalogsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    [EnableRateLimiting("expensive")]
    public async Task<ActionResult<HistoricalAnalogResponse>> Search(
        [FromQuery] HistoricalAnalogRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _service.SearchAsync(request, HttpContext.TraceIdentifier, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_HISTORICAL_ANALOG_REQUEST",
                Message = ex.Message,
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INACTIVE_TIMEFRAME",
                Message = ex.Message,
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historical analog search failed");
            return StatusCode(500, new ApiErrorEnvelope
            {
                Code = "HISTORICAL_ANALOG_SEARCH_FAILED",
                Message = "Historical analog search failed.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }
}

using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/smart-money")]
public class SmartMoneyController : ControllerBase
{
    private readonly ISmartMoneyService _service;
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public SmartMoneyController(ISmartMoneyService service, ProductionTimeframePolicy? timeframePolicy = null)
    {
        _service = service;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
    }

    [HttpGet("structures")]
    public async Task<IActionResult> GetStructures([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "4h", [FromQuery] int lookbackBars = 100)
    {
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
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));
        var result = await _service.GetSmartMoneyStructuresAsync(symbol, timeframe, lookbackBars);
        return Ok(result);
    }
}

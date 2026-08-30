using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/smart-money")]
public class SmartMoneyController : ControllerBase
{
    private readonly ISmartMoneyService _service;

    public SmartMoneyController(ISmartMoneyService service)
    {
        _service = service;
    }

    [HttpGet("structures")]
    public async Task<IActionResult> GetStructures([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int lookbackBars = 100)
    {
        var result = await _service.GetSmartMoneyStructuresAsync(symbol, timeframe, lookbackBars);
        return Ok(result);
    }

    [HttpPost("detect")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> Detect([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int lookbackBars = 100)
    {
        var result = await _service.GetSmartMoneyStructuresAsync(symbol, timeframe, lookbackBars);
        return Ok(result);
    }
}

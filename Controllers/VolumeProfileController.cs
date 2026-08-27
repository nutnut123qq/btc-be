using Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/volume-profile")]
public class VolumeProfileController : ControllerBase
{
    private readonly IVolumeProfileService _service;

    public VolumeProfileController(IVolumeProfileService service)
    {
        _service = service;
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int lookbackBars = 100)
    {
        var result = await _service.GetVolumeProfileAsync(symbol, timeframe, lookbackBars);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("calculate")]
    public async Task<IActionResult> Calculate([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int lookbackBars = 100)
    {
        var result = await _service.GetVolumeProfileAsync(symbol, timeframe, lookbackBars);
        if (result == null) return NotFound();
        return Ok(result);
    }
}

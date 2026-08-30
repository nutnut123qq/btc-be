using Backend.Services;
using Backend.Services.Models;
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
        return Ok(MapToDto(result));
    }

    [HttpPost("calculate")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> Calculate([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "1h", [FromQuery] int lookbackBars = 100)
    {
        var result = await _service.GetVolumeProfileAsync(symbol, timeframe, lookbackBars);
        if (result == null) return NotFound();
        return Ok(MapToDto(result));
    }

    private static object MapToDto(Backend.Data.VolumeProfileSnapshot result)
    {
        VolumeProfileBinDto[] bins;
        try
        {
            bins = System.Text.Json.JsonSerializer.Deserialize<VolumeProfileBinDto[]>(result.ProfileBinsJson)
                ?? Array.Empty<VolumeProfileBinDto>();
        }
        catch (System.Text.Json.JsonException)
        {
            bins = Array.Empty<VolumeProfileBinDto>();
        }

        return new
        {
            result.Id,
            result.Symbol,
            result.Timeframe,
            result.PocPrice,
            result.VahPrice,
            result.ValPrice,
            bins,
            result.CreatedAtUtc
        };
    }
}

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
        var rawBins = System.Text.Json.JsonSerializer.Deserialize<double[]>(result.ProfileBinsJson) ?? Array.Empty<double>();
        var maxVol = rawBins.Length > 0 ? rawBins.Max() : 0;
        int pocIndex = Array.IndexOf(rawBins, maxVol);
        int lower = pocIndex, upper = pocIndex;
        double currentVol = maxVol, targetVol = rawBins.Sum() * 0.70;

        while (currentVol < targetVol && (lower > 0 || upper < rawBins.Length - 1))
        {
            double leftVol = lower > 0 ? rawBins[lower - 1] : -1;
            double rightVol = upper < rawBins.Length - 1 ? rawBins[upper + 1] : -1;
            if (leftVol >= rightVol && leftVol != -1)
                currentVol += rawBins[--lower];
            else if (rightVol != -1)
                currentVol += rawBins[++upper];
            else break;
        }

        var bins = rawBins.Select((vol, i) => new
        {
            priceLevel = 0,
            volume = vol,
            volumePct = maxVol > 0 ? (vol / maxVol) * 100 : 0,
            isPoc = i == pocIndex,
            isValueArea = i >= lower && i <= upper
        }).ToList();

        return new
        {
            result.Id,
            result.Symbol,
            result.Timeframe,
            result.PocPrice,
            result.VahPrice,
            result.ValPrice,
            bins = bins,
            result.CreatedAtUtc
        };
    }
}

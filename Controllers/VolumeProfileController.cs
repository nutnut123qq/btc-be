using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/volume-profile")]
public class VolumeProfileController : ControllerBase
{
    private readonly IVolumeProfileService _service;
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public VolumeProfileController(IVolumeProfileService service, ProductionTimeframePolicy? timeframePolicy = null)
    {
        _service = service;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "4h", [FromQuery] int lookbackBars = 100)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));
        var result = await _service.GetVolumeProfileAsync(symbol, timeframe, lookbackBars);
        if (result == null) return NotFound();
        return Ok(MapToDto(result));
    }

    [HttpPost("calculate")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> Calculate([FromQuery] string symbol = "BTCUSDT", [FromQuery] string timeframe = "4h", [FromQuery] int lookbackBars = 100)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));
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
            capabilityState = "descriptive",
            estimatorKind = "ohlcv_uniform_range_approximation_v1",
            isApproximation = true,
            limitation = "Candle volume is spread uniformly across each candle's high-low bins; this is not observed traded-at-price volume.",
            result.PocPrice,
            result.VahPrice,
            result.ValPrice,
            bins,
            inputVolume = result.InputVolume,
            allocatedVolume = bins.Sum(x => x.Volume),
            volumeConservationErrorPct = result.InputVolume is > 0
                ? Math.Abs(bins.Sum(x => x.Volume) - result.InputVolume.Value) / result.InputVolume.Value * 100
                : (double?)null,
            result.CreatedAtUtc
        };
    }
}

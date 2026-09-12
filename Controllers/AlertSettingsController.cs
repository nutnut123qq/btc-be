using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/alert-settings")]
public class AlertSettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public AlertSettingsController(AppDbContext db, ProductionTimeframePolicy? timeframePolicy = null)
    {
        _db = db;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
    }

    [HttpGet]
    public async Task<ActionResult<PriceAlertSettingsDto>> Get(
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("userId is required.");

        userId = userId.Trim();
        if (userId.Length > 128)
            return BadRequest("userId too long.");

        var row = await _db.PriceAlertSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        if (row == null)
            return NotFound();

        return Ok(PriceAlertSettingsDto.FromEntity(row));
    }

    [HttpPut]
    [Backend.Filters.AdminGuard]
    public async Task<ActionResult<PriceAlertSettingsDto>> Put(
        [FromBody] UpdatePriceAlertSettingsDto body,
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        if (body == null)
            return BadRequest("Body is required.");

        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest("userId is required.");

        userId = userId.Trim();
        if (userId.Length > 128)
            return BadRequest("userId too long.");

        if (body.CooldownMinutes is < 1 or > 1440)
            return BadRequest("CooldownMinutes must be between 1 and 1440.");

        var interval = string.IsNullOrWhiteSpace(body.KlineInterval)
            ? _timeframePolicy.Default
            : ProductionTimeframePolicy.Canonicalize(body.KlineInterval);
        if (!_timeframePolicy.IsActive(interval))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, interval, HttpContext.TraceIdentifier));

        // Upper threshold (breakout up) must be above lower threshold (breakout down): a valid price band.
        if (body.PriceAboveUsd.HasValue && body.PriceBelowUsd.HasValue
            && body.PriceAboveUsd.Value <= body.PriceBelowUsd.Value)
            return BadRequest("PriceAboveUsd must be greater than PriceBelowUsd when both are set (upper band > lower band).");

        var row = await _db.PriceAlertSettings.FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (row == null)
        {
            row = new PriceAlertSettings { UserId = userId };
            _db.PriceAlertSettings.Add(row);
        }

        row.Enabled = body.Enabled;
        row.PriceAboveUsd = body.PriceAboveUsd;
        row.PriceBelowUsd = body.PriceBelowUsd;
        row.KlineInterval = interval;
        row.CooldownMinutes = body.CooldownMinutes;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(PriceAlertSettingsDto.FromEntity(row));
    }

    public sealed class PriceAlertSettingsDto
    {
        public string UserId { get; set; } = "";
        public bool Enabled { get; set; }
        public decimal? PriceAboveUsd { get; set; }
        public decimal? PriceBelowUsd { get; set; }
        public string KlineInterval { get; set; } = "4h";
        public int CooldownMinutes { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public static PriceAlertSettingsDto FromEntity(PriceAlertSettings s) => new()
        {
            UserId = s.UserId,
            Enabled = s.Enabled,
            PriceAboveUsd = s.PriceAboveUsd,
            PriceBelowUsd = s.PriceBelowUsd,
            KlineInterval = s.KlineInterval,
            CooldownMinutes = s.CooldownMinutes,
            UpdatedAt = s.UpdatedAt
        };
    }

    public sealed class UpdatePriceAlertSettingsDto
    {
        public bool Enabled { get; set; }
        public decimal? PriceAboveUsd { get; set; }
        public decimal? PriceBelowUsd { get; set; }
        public string KlineInterval { get; set; } = "4h";
        public int CooldownMinutes { get; set; } = 30;
    }
}

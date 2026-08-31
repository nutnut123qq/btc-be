using Backend.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController(AppDbContext db, ILogger<HealthController> logger) : ControllerBase
{
    private static readonly (string Timeframe, TimeSpan MaxAge)[] FreshnessChecks =
    [
        ("1m", TimeSpan.FromMinutes(20)),
        ("5m", TimeSpan.FromMinutes(20)),
        ("15m", TimeSpan.FromMinutes(30)),
        ("30m", TimeSpan.FromHours(1)),
        ("1h", TimeSpan.FromHours(2)),
        ("4h", TimeSpan.FromHours(8)),
        ("1d", TimeSpan.FromDays(2))
    ];

    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "healthy" });

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? Ok(new { status = "ready" })
                : StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health readiness check could not connect to the database");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }
    }

    [HttpGet]
    public async Task<ActionResult<HealthResponse>> Get(CancellationToken cancellationToken)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            var latestByTimeframe = new Dictionary<string, long>();
            foreach (var check in FreshnessChecks)
            {
                var latest = await db.Klines
                    .AsNoTracking()
                    .Where(k => k.Symbol == "BTCUSDT" && k.Timeframe == check.Timeframe)
                    .OrderByDescending(k => k.OpenTimeMs)
                    .Select(k => (long?)k.OpenTimeMs)
                    .FirstOrDefaultAsync(cancellationToken);
                if (latest.HasValue)
                    latestByTimeframe[check.Timeframe] = latest.Value;
            }

            var freshness = FreshnessChecks.Select(check =>
            {
                var hasData = latestByTimeframe.TryGetValue(check.Timeframe, out var latestOpenTimeMs);
                var latestOpenTimeUtc = hasData
                    ? DateTimeOffset.FromUnixTimeMilliseconds(latestOpenTimeMs)
                    : (DateTimeOffset?)null;
                var age = latestOpenTimeUtc.HasValue
                    ? checkedAtUtc - latestOpenTimeUtc.Value
                    : (TimeSpan?)null;
                var isFresh = age.HasValue && age.Value <= check.MaxAge;

                return new KlineFreshness(
                    check.Timeframe,
                    isFresh ? "fresh" : hasData ? "stale" : "missing",
                    latestOpenTimeUtc,
                    age.HasValue ? Math.Max(0, (long)age.Value.TotalSeconds) : null,
                    (long)check.MaxAge.TotalSeconds);
            }).ToArray();

            var response = new HealthResponse(
                freshness.All(x => x.Status == "fresh") ? "healthy" : "degraded",
                true,
                checkedAtUtc,
                "BTCUSDT",
                freshness);

            return Ok(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check could not query the database");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new HealthResponse(
                "unhealthy",
                false,
                checkedAtUtc,
                "BTCUSDT",
                []));
        }
    }
}

public sealed record HealthResponse(
    string Status,
    bool DatabaseReachable,
    DateTimeOffset CheckedAtUtc,
    string Symbol,
    IReadOnlyList<KlineFreshness> Klines);

public sealed record KlineFreshness(
    string Timeframe,
    string Status,
    DateTimeOffset? LatestOpenTimeUtc,
    long? AgeSeconds,
    long MaxAgeSeconds);

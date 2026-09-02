using System.Diagnostics;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController(AppDbContext db, ILogger<HealthController> logger) : ControllerBase
{
    private static readonly (string Timeframe, TimeSpan MaxAge)[] FreshnessChecks =
    [
        ("1m", TimeSpan.FromMinutes(20)), ("5m", TimeSpan.FromMinutes(20)),
        ("15m", TimeSpan.FromMinutes(30)), ("30m", TimeSpan.FromHours(1)),
        ("1h", TimeSpan.FromHours(2)), ("4h", TimeSpan.FromHours(8)), ("1d", TimeSpan.FromDays(2))
    ];

    private static readonly (string Name, TimeSpan MaxAge)[] ExpectedWorkers =
    [
        (nameof(Services.KlinesIngestionWorker), TimeSpan.FromMinutes(40)),
        (nameof(Services.IndexingBackgroundWorker), TimeSpan.FromMinutes(70)),
        (nameof(Services.RssIngestionService), TimeSpan.FromMinutes(40)),
        (nameof(Services.EmbeddingBackfillWorker), TimeSpan.FromMinutes(130))
    ];

    [HttpGet("live")]
    public IActionResult Live() => Ok(new LivenessResponse("healthy", DateTimeOffset.UtcNow));

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var reachable = await db.Database.CanConnectAsync(timeout.Token);
            var response = new ReadinessResponse(reachable ? "ready" : "not_ready", reachable, checkedAtUtc, stopwatch.ElapsedMilliseconds);
            return reachable ? Ok(response) : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health readiness check could not connect to the database");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ReadinessResponse("not_ready", false, checkedAtUtc, stopwatch.ElapsedMilliseconds));
        }
    }

    [HttpGet("freshness")]
    public async Task<ActionResult<HealthResponse>> Freshness(
        [FromQuery] string symbol = "BTCUSDT", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new ApiErrorEnvelope { Code = "INVALID_SYMBOL", Message = "symbol is required.", Retryable = false, RequestId = HttpContext.TraceIdentifier });
        return await GetFreshnessAsync(symbol.Trim().ToUpperInvariant(), cancellationToken);
    }

    [HttpGet("workers")]
    public async Task<ActionResult<WorkerHealthResponse>> Workers(CancellationToken cancellationToken)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var rows = await db.WorkerHeartbeats.AsNoTracking().ToListAsync(cancellationToken);
            var workers = ExpectedWorkers.Select(expected =>
            {
                var row = rows.FirstOrDefault(x => x.WorkerName == expected.Name);
                if (row is null)
                    return new WorkerHealth(expected.Name, "never", null, null, null, null,
                        (long)expected.MaxAge.TotalSeconds, null, "No heartbeat has been recorded.");

                var lastSuccess = row.LastSucceededAtUtc.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(row.LastSucceededAtUtc.Value, DateTimeKind.Utc))
                    : (DateTimeOffset?)null;
                var ageSeconds = lastSuccess.HasValue ? Math.Max(0, (long)(checkedAtUtc - lastSuccess.Value).TotalSeconds) : (long?)null;
                var failed = string.Equals(row.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                    || row.LastFailedAtUtc.HasValue && (!row.LastSucceededAtUtc.HasValue || row.LastFailedAtUtc > row.LastSucceededAtUtc);
                var status = failed ? "failed" : ageSeconds.HasValue && ageSeconds <= expected.MaxAge.TotalSeconds ? "healthy" : "stale";
                return new WorkerHealth(row.WorkerName, status, row.LastStartedAtUtc, row.LastSucceededAtUtc,
                    row.LastFailedAtUtc, ageSeconds, (long)expected.MaxAge.TotalSeconds, row.LastDurationMs,
                    failed ? row.LastError : null);
            }).ToArray();
            return Ok(new WorkerHealthResponse(checkedAtUtc, workers));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker health could not query heartbeat state");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiErrorEnvelope
            {
                Code = "HEALTH_DATABASE_UNAVAILABLE", Message = "Worker heartbeat state is unavailable.",
                Retryable = true, RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpGet]
    public Task<ActionResult<HealthResponse>> Get(CancellationToken cancellationToken) => GetFreshnessAsync("BTCUSDT", cancellationToken);

    private async Task<ActionResult<HealthResponse>> GetFreshnessAsync(string symbol, CancellationToken cancellationToken)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var freshness = new List<KlineFreshness>(FreshnessChecks.Length);
            foreach (var check in FreshnessChecks)
            {
                var latest = await db.Klines.AsNoTracking()
                    .Where(k => k.Symbol == symbol && k.Timeframe == check.Timeframe)
                    .OrderByDescending(k => k.OpenTimeMs)
                    .Select(k => (long?)k.OpenTimeMs)
                    .FirstOrDefaultAsync(cancellationToken);
                var latestUtc = latest.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(latest.Value) : (DateTimeOffset?)null;
                var ageSeconds = latestUtc.HasValue ? Math.Max(0, (long)(checkedAtUtc - latestUtc.Value).TotalSeconds) : (long?)null;
                freshness.Add(new KlineFreshness(check.Timeframe,
                    ageSeconds.HasValue && ageSeconds <= check.MaxAge.TotalSeconds ? "fresh" : latest.HasValue ? "stale" : "missing",
                    latestUtc, ageSeconds, (long)check.MaxAge.TotalSeconds));
            }
            return Ok(new HealthResponse(freshness.All(x => x.Status == "fresh") ? "healthy" : "degraded",
                true, checkedAtUtc, symbol, freshness));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Freshness health could not query the database");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiErrorEnvelope
            {
                Code = "HEALTH_DATABASE_UNAVAILABLE", Message = "Data freshness is unavailable.",
                Retryable = true, RequestId = HttpContext.TraceIdentifier
            });
        }
    }
}

public sealed record LivenessResponse(string Status, DateTimeOffset CheckedAtUtc);
public sealed record ReadinessResponse(string Status, bool DatabaseReachable, DateTimeOffset CheckedAtUtc, long ResponseTimeMs);
public sealed record HealthResponse(string Status, bool DatabaseReachable, DateTimeOffset CheckedAtUtc, string Symbol, IReadOnlyList<KlineFreshness> Klines);
public sealed record KlineFreshness(string Timeframe, string Status, DateTimeOffset? LatestOpenTimeUtc, long? AgeSeconds, long MaxAgeSeconds);
public sealed record WorkerHealthResponse(DateTimeOffset CheckedAtUtc, IReadOnlyList<WorkerHealth> Workers);
public sealed record WorkerHealth(string Name, string Status, DateTime? LastStartedAtUtc, DateTime? LastSucceededAtUtc,
    DateTime? LastFailedAtUtc, long? AgeSeconds, long MaxAgeSeconds, long? LastDurationMs, string? Message);

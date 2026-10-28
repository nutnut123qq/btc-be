using Backend.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Backend.Services;

/// <summary>
/// Keeps the research evidence catalog verified so a catalog / current-conditions request, however long after a
/// restart it comes, is served the verified snapshot instead of waiting for the full verification itself. Verifies
/// once on a background thread as soon as the host has started, then calls the catalog every 0.6 x
/// <see cref="EvidenceCatalogOptions.CacheVerificationSeconds"/>: each call lands in the catalog's refresh-ahead
/// window [TTL/2, TTL), so the snapshot is re-verified in the background before it expires; the catalog alone still
/// guarantees no snapshot is served at or past the TTL. TTL 0 (no cache) = the startup verification only. Registered
/// through <see cref="BackgroundWorkerSwitch"/>. A failure is only logged and the loop goes on: nothing gets cached,
/// so the next request verifies synchronously and fails closed exactly as before.
/// </summary>
public sealed class ResearchEvidenceCatalogWarmup(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    IOptions<EvidenceCatalogOptions> options,
    TimeProvider timeProvider,
    ILogger<ResearchEvidenceCatalogWarmup> logger) : IHostedService
{
    // At TTL <= 30 s this floor puts every call at or past the TTL, outside the refresh-ahead window; the loop then
    // just re-verifies at this pace, which bounds the CPU of a tiny configured TTL to one verification (4-6 s) per 30 s.
    private static readonly TimeSpan MinPeriod = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => RunAsync(_stopping.Token)));
        return Task.CompletedTask;
    }

    // Does not wait for a verification in flight (not cancellable, 4-6 s); the loop exits at its next delay.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            Warm(startup: true);
            // Same clamp as the catalog.
            var ttl = TimeSpan.FromSeconds(Math.Clamp(options.Value.CacheVerificationSeconds, 0, 86_400));
            if (ttl == TimeSpan.Zero)
                return;
            var period = ttl * 0.6;
            if (period < MinPeriod)
                period = MinPeriod;
            while (true)
            {
                await Task.Delay(period, timeProvider, stoppingToken);
                Warm(startup: false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research evidence catalog warm-up stopped; requests verify synchronously.");
        }
    }

    private void Warm(bool startup)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            // Resolved here, not injected: a misconfigured catalog must fail its own requests, not host startup.
            var catalog = services.GetRequiredService<IResearchEvidenceCatalog>().GetCatalog();
            // A periodic call normally returns the cached snapshot at once and re-verifies it in the background,
            // so its elapsed time says nothing about verification; only the startup run is logged.
            if (startup)
                logger.LogInformation(
                    "Research evidence catalog verified at startup in {ElapsedMs} ms ({Items} items, {Rejected} rejected).",
                    stopwatch.ElapsedMilliseconds, catalog.Items.Count, catalog.Integrity.RejectedArtifactCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research evidence catalog {Phase} verification failed; the next request verifies synchronously.",
                startup ? "startup" : "periodic");
        }
    }
}

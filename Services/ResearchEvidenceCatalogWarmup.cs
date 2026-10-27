using System.Diagnostics;

namespace Backend.Services;

/// <summary>
/// Verifies the research evidence catalog once, on a background thread, as soon as the host has started, so the
/// first catalog / current-conditions request after a restart is served the verified snapshot instead of waiting
/// for the full verification itself. Registered through <see cref="BackgroundWorkerSwitch"/>. A failure is only
/// logged: nothing gets cached, so the next request verifies synchronously and fails closed exactly as before.
/// </summary>
public sealed class ResearchEvidenceCatalogWarmup(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<ResearchEvidenceCatalogWarmup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(Warm));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Warm()
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            // Resolved here, not injected: a misconfigured catalog must fail its own requests, not host startup.
            var catalog = services.GetRequiredService<IResearchEvidenceCatalog>().GetCatalog();
            logger.LogInformation(
                "Research evidence catalog verified at startup in {ElapsedMs} ms ({Items} items, {Rejected} rejected).",
                stopwatch.ElapsedMilliseconds, catalog.Items.Count, catalog.Integrity.RejectedArtifactCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research evidence catalog startup verification failed; the next request verifies synchronously.");
        }
    }
}

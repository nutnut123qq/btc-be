using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Backfill embedding cho NewsChunks chưa có embedding (hoặc embed bằng model khác).
/// Reports a WorkerCycleReport per cycle so /api/health/workers distinguishes
/// disabled / idle / succeeded / partial / failed instead of silently succeeding.
/// </summary>
public class EmbeddingBackfillWorker : BackgroundService
{
    /// <summary>Same cap convention as KlineGapState: 3 consecutive failures -> stop retrying.</summary>
    internal const int MaxConsecutiveChunkFailures = 3;
    internal const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmbeddingBackfillWorker> _logger;
    private bool _reportedDisabled;

    public EmbeddingBackfillWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EmbeddingBackfillWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAtUtc = DateTime.UtcNow;
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding backfill cycle failed");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await WorkerHeartbeatStore.MarkFailedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(EmbeddingBackfillWorker), startedAtUtc, DateTime.UtcNow, ex, stoppingToken);
                }
                catch (Exception heartbeatException) { _logger.LogWarning(heartbeatException, "Could not persist failed embedding heartbeat"); }
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<WorkerCycleReport> RunCycleAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await WorkerHeartbeatStore.MarkStartedAsync(db, nameof(EmbeddingBackfillWorker), startedAtUtc, cancellationToken);

        var articlesWithoutChunks = await db.NewsArticles
            .Where(article => !article.Chunks.Any())
            .OrderByDescending(article => article.PublishedAt ?? article.FetchedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        foreach (var article in articlesWithoutChunks)
        {
            var body = string.Join("\n\n", new[] { article.Title, article.Summary }.Where(text => !string.IsNullOrWhiteSpace(text)));
            var index = 0;
            foreach (var text in TextChunker.Chunk(body, maxChars: 1000, overlap: 120))
            {
                db.NewsChunks.Add(new NewsChunk
                {
                    Id = Guid.NewGuid(), ArticleId = article.Id, ChunkIndex = index++,
                    Text = text.Length > 16000 ? text[..16000] : text
                });
            }
        }
        if (articlesWithoutChunks.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Created missing chunks for {Count} restored news articles", articlesWithoutChunks.Count);
        }

        var embedder = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

        // Chunks needing (re-)embedding: no vector yet, or a vector from a different/unknown model.
        if (!embedder.IsConfigured)
        {
            var remaining = await db.NewsChunks.CountAsync(c => c.Embedding == null || c.Embedding.Length == 0, cancellationToken);
            if (!_reportedDisabled)
            {
                _logger.LogInformation("Embedding backfill disabled because no embedding provider API key is configured.");
                _reportedDisabled = true;
            }
            var disabledReport = WorkerCycleReport.Disabled(
                "Embedding provider API key not configured (OPENROUTER_API_KEY / OpenRouter:ApiKey, or Gemini:ApiKey / GEMINI_API_KEY / GOOGLE_API_KEY); embedding backfill is disabled.", remaining);
            await WorkerHeartbeatStore.MarkCompletedAsync(db, nameof(EmbeddingBackfillWorker), startedAtUtc, DateTime.UtcNow, disabledReport, cancellationToken);
            _logger.LogInformation("Embedding backfill cycle: status={Status} attempted={Attempted} succeeded={Succeeded} failed={Failed} skipped={Skipped} remaining={Remaining}",
                disabledReport.Outcome, disabledReport.Attempted, disabledReport.Succeeded, disabledReport.Failed, disabledReport.Skipped, disabledReport.Remaining);
            return disabledReport;
        }

        var poisoned = await db.NewsChunks.CountAsync(c =>
            (c.Embedding == null || c.Embedding.Length == 0 || c.EmbeddingModel != embedder.ModelId)
            && c.EmbeddingFailureCount >= MaxConsecutiveChunkFailures, cancellationToken);

        var chunks = await db.NewsChunks
            .Where(c => (c.Embedding == null || c.Embedding.Length == 0 || c.EmbeddingModel != embedder.ModelId)
                        && c.EmbeddingFailureCount < MaxConsecutiveChunkFailures)
            .OrderByDescending(c => c.Article.PublishedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (chunks.Count == 0)
        {
            var idleReport = WorkerCycleReport.Idle(
                skipped: poisoned, remaining: 0,
                detail: poisoned > 0
                    ? $"Nothing retriable left; skipped_poisoned={poisoned} (>= {MaxConsecutiveChunkFailures} consecutive failures)."
                    : "No news chunks need embedding backfill.");
            await WorkerHeartbeatStore.MarkCompletedAsync(db, nameof(EmbeddingBackfillWorker), startedAtUtc, DateTime.UtcNow, idleReport, cancellationToken);
            _logger.LogInformation("Embedding backfill cycle: status={Status} attempted={Attempted} succeeded={Succeeded} failed={Failed} skipped={Skipped} remaining={Remaining} detail={Detail}",
                idleReport.Outcome, idleReport.Attempted, idleReport.Succeeded, idleReport.Failed, idleReport.Skipped, idleReport.Remaining, idleReport.Detail);
            return idleReport;
        }

        int succeeded = 0;
        int failed = 0;
        var failureBreakdown = new Dictionary<EmbeddingErrorKind, int>();
        EmbeddingResult[] results;
        try
        {
            results = await embedder.EmbedBatchAsync(chunks.Select(c => c.Text).ToArray(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            results = Enumerable.Repeat(
                EmbeddingResult.Fail(EmbeddingErrorKind.ProviderError, $"{ex.GetType().Name}: {ex.Message}"),
                chunks.Count).ToArray();
        }
        if (results.Length != chunks.Count)
        {
            results = Enumerable.Repeat(
                EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse,
                    $"Batch returned {results.Length} results for {chunks.Count} inputs."),
                chunks.Count).ToArray();
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var result = results[i];

            if (result.IsSuccess && EmbeddingResult.IsUsableVector(result.Vector, embedder.EmbeddingDimensions))
            {
                chunk.Embedding = result.Vector;
                chunk.EmbeddedAt = DateTimeOffset.UtcNow;
                chunk.EmbeddingModel = embedder.ModelId;
                chunk.EmbeddingFailureCount = 0;
                succeeded++;
            }
            else
            {
                var kind = result.Error == EmbeddingErrorKind.None ? EmbeddingErrorKind.InvalidResponse : result.Error;
                failureBreakdown[kind] = failureBreakdown.GetValueOrDefault(kind) + 1;
                // Only per-chunk content faults poison a chunk; request-level failures
                // (quota/auth/network/provider outage) must not burn the retry budget.
                if (kind == EmbeddingErrorKind.InvalidResponse)
                    chunk.EmbeddingFailureCount++;
                failed++;
                _logger.LogWarning(
                    "Failed to embed chunk {ChunkId}: kind={Kind} detail={Detail} consecutiveFailures={ConsecutiveFailures}",
                    chunk.Id, EmbeddingResult.KindToken(kind), result.Detail, chunk.EmbeddingFailureCount);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var skipped = await db.NewsChunks.CountAsync(c =>
            (c.Embedding == null || c.Embedding.Length == 0 || c.EmbeddingModel != embedder.ModelId)
            && c.EmbeddingFailureCount >= MaxConsecutiveChunkFailures, cancellationToken);
        var remainingAfter = await db.NewsChunks.CountAsync(c =>
            (c.Embedding == null || c.Embedding.Length == 0 || c.EmbeddingModel != embedder.ModelId)
            && c.EmbeddingFailureCount < MaxConsecutiveChunkFailures, cancellationToken);

        // A cycle where every failure is transient (rate limit / network outage)
        // recovers on its own — report Partial so the watchdog allow-list stays
        // green. Auth/InvalidResponse/ProviderError still report Failed: those
        // need a human, restart does not fix them.
        var transientOnly = failureBreakdown.Count > 0 && failureBreakdown.Keys
            .All(k => k is EmbeddingErrorKind.RateLimited or EmbeddingErrorKind.TimeoutOrNetwork);
        var outcome = failed == 0
            ? WorkerCycleOutcome.Succeeded
            : succeeded > 0 || transientOnly
                ? WorkerCycleOutcome.Partial
                : WorkerCycleOutcome.Failed;

        var detailParts = new List<string>();
        if (failureBreakdown.Count > 0)
            detailParts.Add("failures: " + string.Join(", ", failureBreakdown.Select(kv => $"{EmbeddingResult.KindToken(kv.Key)}={kv.Value}")));
        if (skipped > 0)
            detailParts.Add($"skipped_poisoned={skipped}");

        var report = new WorkerCycleReport(
            outcome,
            Attempted: chunks.Count,
            Succeeded: succeeded,
            Failed: failed,
            Skipped: skipped,
            Remaining: remainingAfter,
            Detail: detailParts.Count > 0 ? string.Join("; ", detailParts) : null);

        await WorkerHeartbeatStore.MarkCompletedAsync(db, nameof(EmbeddingBackfillWorker), startedAtUtc, DateTime.UtcNow, report, cancellationToken);
        _logger.LogInformation(
            "Embedding backfill cycle: status={Status} attempted={Attempted} succeeded={Succeeded} failed={Failed} skipped={Skipped} remaining={Remaining} detail={Detail}",
            report.Outcome, report.Attempted, report.Succeeded, report.Failed, report.Skipped, report.Remaining, report.Detail ?? "-");
        return report;
    }
}

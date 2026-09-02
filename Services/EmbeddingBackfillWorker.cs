using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Backfill embedding cho NewsChunks chưa có embedding.
/// </summary>
public class EmbeddingBackfillWorker : BackgroundService
{
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
                using (var scope = _scopeFactory.CreateScope())
                    await WorkerHeartbeatStore.MarkStartedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(EmbeddingBackfillWorker), startedAtUtc, stoppingToken);
                await RunCycleAsync(stoppingToken);
                using (var scope = _scopeFactory.CreateScope())
                    await WorkerHeartbeatStore.MarkSucceededAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), nameof(EmbeddingBackfillWorker), startedAtUtc, DateTime.UtcNow, stoppingToken);
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

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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

        var embedder = scope.ServiceProvider.GetRequiredService<IGeminiEmbeddingClient>();
        if (!embedder.IsConfigured)
        {
            if (!_reportedDisabled)
            {
                _logger.LogInformation("Embedding backfill disabled because no Gemini API key is configured.");
                _reportedDisabled = true;
            }
            return;
        }

        var chunks = await db.NewsChunks
            .Where(c => c.Embedding == null || c.Embedding.Length == 0)
            .OrderByDescending(c => c.Article.PublishedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (chunks.Count == 0)
        {
            _logger.LogInformation("No news chunks need embedding backfill.");
            return;
        }

        int success = 0;
        foreach (var chunk in chunks)
        {
            try
            {
                var vec = await embedder.EmbedAsync(chunk.Text, cancellationToken);
                if (vec != null)
                {
                    chunk.Embedding = vec;
                    chunk.EmbeddedAt = DateTimeOffset.UtcNow;
                    success++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to embed chunk {ChunkId}", chunk.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Backfilled {Success}/{Total} news chunk embeddings", success, chunks.Count);
    }
}

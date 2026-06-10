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
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding backfill cycle failed");
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

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<IGeminiEmbeddingClient>();

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

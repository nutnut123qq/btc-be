using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class NewsRagServiceTests
{
    [Fact]
    public async Task RecencyFallbackMarksStoredNewsAsStale()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.NewsArticles.Add(new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = "Old article", Link = "https://example.test/old",
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-2), FetchedAt = DateTimeOffset.UtcNow.AddDays(-2)
        });
        await db.SaveChangesAsync();
        var service = new NewsRagService(db, new DisabledEmbedder(), NullLogger<NewsRagService>.Instance);

        var context = await service.BuildNewsContextAsync("bitcoin");

        Assert.Contains("WARNING: stored news is stale", context);
        Assert.Contains("Old article", context);
    }

    [Fact]
    public async Task ConfiguredEmbeddingRanksSimilarChunk()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var relevantArticle = new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = "Relevant article", Link = "https://example.test/relevant",
            PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow
        };
        var unrelatedArticle = new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = "Unrelated article", Link = "https://example.test/unrelated",
            PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow
        };
        db.NewsArticles.AddRange(relevantArticle, unrelatedArticle);
        db.NewsChunks.AddRange(
            new NewsChunk
            {
                Id = Guid.NewGuid(), ArticleId = relevantArticle.Id, ChunkIndex = 0,
                Text = "Relevant content", Embedding = [1f, 0f]
            },
            new NewsChunk
            {
                Id = Guid.NewGuid(), ArticleId = unrelatedArticle.Id, ChunkIndex = 0,
                Text = "Unrelated content", Embedding = [-1f, 0f]
            });
        await db.SaveChangesAsync();
        var service = new NewsRagService(db, new FixedEmbedder([1f, 0f]), NullLogger<NewsRagService>.Instance);

        var context = await service.BuildNewsContextAsync("bitcoin", topK: 1);

        Assert.Contains("Relevant article", context);
        Assert.DoesNotContain("Unrelated article", context);
        Assert.DoesNotContain("Retrieved by recency", context);
    }

    private sealed class DisabledEmbedder : IGeminiEmbeddingClient
    {
        public bool IsConfigured => false;
        public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<float[]?>(null);
    }

    private sealed class FixedEmbedder(float[] embedding) : IGeminiEmbeddingClient
    {
        public bool IsConfigured => true;
        public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<float[]?>(embedding);
    }
}

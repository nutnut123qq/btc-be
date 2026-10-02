using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class NewsRagServiceTests
{
    private const string ModelId = "test-model";

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
        Assert.Contains("Retrieved by recency", context);
    }

    [Fact]
    public async Task DisabledEmbedder_StatesDegradeReason()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.NewsArticles.Add(new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = "Some article", Link = "https://example.test/a",
            PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = new NewsRagService(db, new DisabledEmbedder(), NullLogger<NewsRagService>.Instance);

        var context = await service.BuildNewsContextAsync("bitcoin");

        Assert.Contains("not configured", context);
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
                Text = "Relevant content", Embedding = [1f, 0f], EmbeddingModel = ModelId
            },
            new NewsChunk
            {
                Id = Guid.NewGuid(), ArticleId = unrelatedArticle.Id, ChunkIndex = 0,
                Text = "Unrelated content", Embedding = [-1f, 0f], EmbeddingModel = ModelId
            });
        await db.SaveChangesAsync();
        var service = new NewsRagService(db, new FixedEmbedder([1f, 0f]), NullLogger<NewsRagService>.Instance);

        var context = await service.BuildNewsContextAsync("bitcoin", topK: 1);

        Assert.Contains("Relevant article", context);
        Assert.DoesNotContain("Unrelated article", context);
        Assert.DoesNotContain("Retrieved by recency", context);
    }

    [Fact]
    public async Task ForeignModelEmbeddingsAreExcludedAndReported()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var article = new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = "Foreign article", Link = "https://example.test/foreign",
            PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow
        };
        db.NewsArticles.Add(article);
        db.NewsChunks.Add(new NewsChunk
        {
            Id = Guid.NewGuid(), ArticleId = article.Id, ChunkIndex = 0,
            Text = "Foreign content", Embedding = [1f, 0f], EmbeddingModel = "some-other-model"
        });
        await db.SaveChangesAsync();
        var service = new NewsRagService(db, new FixedEmbedder([1f, 0f]), NullLogger<NewsRagService>.Instance);

        var results = await service.SearchSimilarChunksAsync("bitcoin");
        Assert.Empty(results); // foreign-model vectors are never mixed into semantic results

        var context = await service.BuildNewsContextAsync("bitcoin");
        Assert.Contains("Retrieved by recency", context);
        Assert.Contains("different or unknown model", context);
        Assert.Contains(ModelId, context);
    }

    [Fact]
    public async Task NullModelEmbeddingsAreExcludedFromSemanticSearch()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var article = new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = "Legacy article", Link = "https://example.test/legacy",
            PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow
        };
        db.NewsArticles.Add(article);
        // Legacy row: embedding written before provenance existed.
        db.NewsChunks.Add(new NewsChunk
        {
            Id = Guid.NewGuid(), ArticleId = article.Id, ChunkIndex = 0,
            Text = "Legacy content", Embedding = [1f, 0f], EmbeddingModel = null
        });
        await db.SaveChangesAsync();
        var service = new NewsRagService(db, new FixedEmbedder([1f, 0f]), NullLogger<NewsRagService>.Instance);

        var results = await service.SearchSimilarChunksAsync("bitcoin");

        Assert.Empty(results);
    }

    [Fact]
    public async Task MixedModels_OnlyMatchingChunksAreRanked()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var article = new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = "Mixed article", Link = "https://example.test/mixed",
            PublishedAt = DateTimeOffset.UtcNow, FetchedAt = DateTimeOffset.UtcNow
        };
        db.NewsArticles.Add(article);
        db.NewsChunks.AddRange(
            new NewsChunk
            {
                Id = Guid.NewGuid(), ArticleId = article.Id, ChunkIndex = 0,
                Text = "Current model content", Embedding = [0f, 1f], EmbeddingModel = ModelId
            },
            new NewsChunk
            {
                Id = Guid.NewGuid(), ArticleId = article.Id, ChunkIndex = 1,
                Text = "Foreign model content", Embedding = [1f, 0f], EmbeddingModel = "other-model"
            });
        await db.SaveChangesAsync();
        var service = new NewsRagService(db, new FixedEmbedder([1f, 0f]), NullLogger<NewsRagService>.Instance);

        var results = await service.SearchSimilarChunksAsync("bitcoin", topK: 8);

        // Even though the foreign-model chunk is the perfect cosine match, it must not be returned.
        Assert.Single(results);
        Assert.Equal("Current model content", results[0].Content);
    }

    private sealed class DisabledEmbedder : IGeminiEmbeddingClient
    {
        public bool IsConfigured => false;
        public string ModelId => GeminiEmbeddingClient.DefaultModelId;
        public int EmbeddingDimensions => GeminiEmbeddingClient.Dimensions;
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(EmbeddingResult.Fail(EmbeddingErrorKind.NotConfigured));
    }

    private sealed class FixedEmbedder(float[] embedding) : IGeminiEmbeddingClient
    {
        public bool IsConfigured => true;
        public string ModelId => NewsRagServiceTests.ModelId;
        public int EmbeddingDimensions => embedding.Length;
        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(EmbeddingResult.Ok(embedding));
    }
}

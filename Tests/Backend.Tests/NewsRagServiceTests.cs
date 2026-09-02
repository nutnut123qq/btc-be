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

    private sealed class DisabledEmbedder : IGeminiEmbeddingClient
    {
        public bool IsConfigured => false;
        public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<float[]?>(null);
    }
}

using Backend.Services;
using Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class EmbeddingBackfillWorkerTests
{
    [Fact]
    public async Task RunCycleAsync_WhenEmbeddingIsNotConfigured_CreatesMissingChunksWithoutEmbedding()
    {
        var embedder = new DisabledEmbeddingClient();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var services = new ServiceCollection()
            .AddSingleton<IGeminiEmbeddingClient>(embedder)
            .AddScoped(_ => new AppDbContext(options))
            .BuildServiceProvider();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.NewsArticles.Add(new NewsArticle
            {
                Id = Guid.NewGuid(), Source = "test", Title = "Restored article",
                Link = "https://example.test/article", Summary = "Summary", FetchedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        await worker.RunCycleAsync(default);

        Assert.Equal(0, embedder.CallCount);
        await using var verifyScope = services.CreateAsyncScope();
        Assert.Single(await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().NewsChunks.ToListAsync());
    }

    private sealed class DisabledEmbeddingClient : IGeminiEmbeddingClient
    {
        public bool IsConfigured => false;
        public int CallCount { get; private set; }

        public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<float[]?>(null);
        }
    }
}

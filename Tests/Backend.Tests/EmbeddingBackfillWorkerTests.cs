using Backend.Services;
using Backend.Services.Models;
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
        var embedder = new StubEmbeddingClient { IsConfigured = false };
        var services = BuildServices(embedder, out _);
        await using (var scope = services.CreateAsyncScope())
        {
            var seedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.NewsArticles.Add(new NewsArticle
            {
                Id = Guid.NewGuid(), Source = "test", Title = "Restored article",
                Link = "https://example.test/article", Summary = "Summary", FetchedAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(0, embedder.CallCount);
        // Silent-false-success bug: a missing key must report disabled, not success.
        Assert.Equal(WorkerCycleOutcome.Disabled, report.Outcome);
        Assert.Equal(1, report.Remaining);
        await using var verifyScope = services.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.NewsChunks.ToListAsync());
        var heartbeat = await db.WorkerHeartbeats.SingleAsync();
        Assert.Equal("Disabled", heartbeat.Status);
        Assert.NotNull(heartbeat.LastSucceededAtUtc); // still proves liveness
        Assert.Null(heartbeat.LastFailedAtUtc);
        Assert.Equal(1, heartbeat.LastCycleRemaining);
        Assert.Contains("not configured", heartbeat.LastCycleDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCycleAsync_NoPendingChunks_ReportsIdle()
    {
        var embedder = new StubEmbeddingClient();
        var services = BuildServices(embedder, out _);
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Idle, report.Outcome);
        Assert.Equal(0, embedder.CallCount);
        await using var scope = services.CreateAsyncScope();
        var heartbeat = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WorkerHeartbeats.SingleAsync();
        Assert.Equal("Idle", heartbeat.Status);
    }

    [Fact]
    public async Task RunCycleAsync_AllChunksFail_ReportsFailedWithAuthClassification()
    {
        var embedder = new StubEmbeddingClient
        {
            Default = EmbeddingResult.Fail(EmbeddingErrorKind.Auth, "HTTP 401: API key invalid")
        };
        var services = BuildServices(embedder, out _);
        await SeedChunkAsync(services, "a1", DateTimeOffset.UtcNow);
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Failed, report.Outcome);
        Assert.Equal(1, report.Attempted);
        Assert.Equal(0, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Contains("auth=1", report.Detail);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var heartbeat = await db.WorkerHeartbeats.SingleAsync();
        Assert.Equal("Failed", heartbeat.Status);
        Assert.NotNull(heartbeat.LastFailedAtUtc);
        Assert.Equal(1, heartbeat.LastCycleFailed);
        var chunk = await db.NewsChunks.SingleAsync();
        Assert.Null(chunk.Embedding);
        Assert.Null(chunk.EmbeddedAt);
        Assert.Null(chunk.EmbeddingModel);
        Assert.Equal(1, chunk.EmbeddingFailureCount);
    }

    [Fact]
    public async Task RunCycleAsync_PartialFailure_ReportsPartialAndCounters()
    {
        var embedder = new StubEmbeddingClient();
        embedder.Results.Enqueue(EmbeddingResult.Ok(new float[768]));
        embedder.Results.Enqueue(EmbeddingResult.Fail(EmbeddingErrorKind.RateLimited, "HTTP 429"));
        embedder.Default = EmbeddingResult.Fail(EmbeddingErrorKind.RateLimited, "HTTP 429");
        var services = BuildServices(embedder, out _);
        // Newer article is attempted first (OrderByDescending PublishedAt).
        await SeedChunkAsync(services, "newer", DateTimeOffset.UtcNow);
        await SeedChunkAsync(services, "older", DateTimeOffset.UtcNow.AddDays(-1));
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Partial, report.Outcome);
        Assert.Equal(2, report.Attempted);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Contains("rate_limit=1", report.Detail);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var heartbeat = await db.WorkerHeartbeats.SingleAsync();
        Assert.Equal("Partial", heartbeat.Status);
        Assert.Equal(2, heartbeat.LastCycleAttempted);
        Assert.Equal(1, heartbeat.LastCycleSucceeded);
        Assert.Equal(1, heartbeat.LastCycleFailed);
        Assert.Equal(1, heartbeat.LastCycleRemaining); // the failed chunk is still pending & retriable
    }

    [Fact]
    public async Task RunCycleAsync_TimeoutFailure_ClassifiedAsNetwork()
    {
        var embedder = new StubEmbeddingClient
        {
            Default = EmbeddingResult.Fail(EmbeddingErrorKind.TimeoutOrNetwork, "Request timed out")
        };
        var services = BuildServices(embedder, out _);
        await SeedChunkAsync(services, "a1", DateTimeOffset.UtcNow);
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Failed, report.Outcome);
        Assert.Contains("timeout_or_network=1", report.Detail);
    }

    [Fact]
    public async Task RunCycleAsync_WrongDimensionVector_NeverWritten()
    {
        var embedder = new StubEmbeddingClient { Default = EmbeddingResult.Ok(new float[4]) };
        var services = BuildServices(embedder, out _);
        await SeedChunkAsync(services, "a1", DateTimeOffset.UtcNow);
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Failed, report.Outcome);
        Assert.Contains("invalid_response=1", report.Detail);
        await using var scope = services.CreateAsyncScope();
        var chunk = await scope.ServiceProvider.GetRequiredService<AppDbContext>().NewsChunks.SingleAsync();
        Assert.Null(chunk.Embedding);
        Assert.Null(chunk.EmbeddingModel);
        Assert.Equal(1, chunk.EmbeddingFailureCount);
    }

    [Fact]
    public async Task RunCycleAsync_NaNVector_NeverWritten()
    {
        var embedder = new StubEmbeddingClient
        {
            Default = EmbeddingResult.Ok(Enumerable.Repeat(float.NaN, 768).ToArray())
        };
        var services = BuildServices(embedder, out _);
        await SeedChunkAsync(services, "a1", DateTimeOffset.UtcNow);
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Failed, report.Outcome);
        Assert.Contains("invalid_response=1", report.Detail);
        await using var scope = services.CreateAsyncScope();
        var chunk = await scope.ServiceProvider.GetRequiredService<AppDbContext>().NewsChunks.SingleAsync();
        Assert.Null(chunk.Embedding);
        Assert.Equal(1, chunk.EmbeddingFailureCount);
    }

    [Fact]
    public async Task RunCycleAsync_Success_WritesProvenanceAndReportsSucceeded()
    {
        var vector = Enumerable.Range(0, 768).Select(i => i * 0.001f).ToArray();
        var embedder = new StubEmbeddingClient { Default = EmbeddingResult.Ok(vector) };
        var services = BuildServices(embedder, out _);
        var chunkId = await SeedChunkAsync(services, "a1", DateTimeOffset.UtcNow);
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var before = DateTimeOffset.UtcNow;
        var report = await worker.RunCycleAsync(default);

        Assert.Equal(WorkerCycleOutcome.Succeeded, report.Outcome);
        Assert.Equal(1, report.Attempted);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(0, report.Remaining);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chunk = await db.NewsChunks.SingleAsync();
        Assert.Equal(vector, chunk.Embedding);
        Assert.Equal("test-model", chunk.EmbeddingModel);
        Assert.NotNull(chunk.EmbeddedAt);
        Assert.True(chunk.EmbeddedAt >= before);
        Assert.Equal(0, chunk.EmbeddingFailureCount);
        var heartbeat = await db.WorkerHeartbeats.SingleAsync();
        Assert.Equal("Succeeded", heartbeat.Status);
        Assert.Equal(1, heartbeat.LastCycleAttempted);
        Assert.Equal(1, heartbeat.LastCycleSucceeded);
        Assert.Equal(0, heartbeat.LastCycleFailed);
        Assert.Equal(0, heartbeat.LastCycleRemaining);
    }

    [Fact]
    public async Task RunCycleAsync_ForeignModelChunk_IsReEmbeddedWithCurrentModel()
    {
        var embedder = new StubEmbeddingClient { Default = EmbeddingResult.Ok(new float[768]) };
        var services = BuildServices(embedder, out _);
        await SeedChunkAsync(services, "a1", DateTimeOffset.UtcNow,
            embedding: new float[768], embeddingModel: "other-model");
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(1, embedder.CallCount);
        await using var scope = services.CreateAsyncScope();
        var chunk = await scope.ServiceProvider.GetRequiredService<AppDbContext>().NewsChunks.SingleAsync();
        Assert.Equal("test-model", chunk.EmbeddingModel);
        Assert.NotNull(chunk.EmbeddedAt);
    }

    [Fact]
    public async Task RunCycleAsync_PoisonPill_StopsRetryingAfterCap()
    {
        var embedder = new StubEmbeddingClient
        {
            Default = EmbeddingResult.Fail(EmbeddingErrorKind.ProviderError, "HTTP 500")
        };
        var services = BuildServices(embedder, out _);
        await SeedChunkAsync(services, "a1", DateTimeOffset.UtcNow);
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        // Three consecutive failing cycles hit the cap (same convention as KlineGapState AttemptCount>=3).
        for (var i = 0; i < EmbeddingBackfillWorker.MaxConsecutiveChunkFailures; i++)
        {
            var r = await worker.RunCycleAsync(default);
            Assert.Equal(WorkerCycleOutcome.Failed, r.Outcome);
        }
        Assert.Equal(3, embedder.CallCount);

        var report = await worker.RunCycleAsync(default);

        Assert.Equal(3, embedder.CallCount); // poisoned chunk was never attempted again
        Assert.Equal(WorkerCycleOutcome.Idle, report.Outcome);
        Assert.Equal(1, report.Skipped);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chunk = await db.NewsChunks.SingleAsync();
        Assert.Equal(3, chunk.EmbeddingFailureCount);
        Assert.Null(chunk.Embedding);
        var heartbeat = await db.WorkerHeartbeats.SingleAsync();
        Assert.Equal("Idle", heartbeat.Status);
        Assert.Equal(1, heartbeat.LastCycleSkipped);
        Assert.Contains("skipped_poisoned=1", heartbeat.LastCycleDetail);
    }

    private static ServiceProvider BuildServices(StubEmbeddingClient embedder, out DbContextOptions<AppDbContext> options)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        options = opts;
        return new ServiceCollection()
            .AddSingleton<IEmbeddingClient>(embedder)
            .AddScoped(_ => new AppDbContext(opts))
            .BuildServiceProvider();
    }

    private static async Task<Guid> SeedChunkAsync(
        ServiceProvider services,
        string linkSuffix,
        DateTimeOffset publishedAt,
        float[]? embedding = null,
        string? embeddingModel = null)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var article = new NewsArticle
        {
            Id = Guid.NewGuid(), Source = "test", Title = $"Article {linkSuffix}",
            Link = $"https://example.test/{linkSuffix}", PublishedAt = publishedAt, FetchedAt = publishedAt
        };
        var chunk = new NewsChunk
        {
            Id = Guid.NewGuid(), ArticleId = article.Id, ChunkIndex = 0,
            Text = "chunk text", Embedding = embedding, EmbeddingModel = embeddingModel
        };
        db.NewsArticles.Add(article);
        db.NewsChunks.Add(chunk);
        await db.SaveChangesAsync();
        return chunk.Id;
    }

    private sealed class StubEmbeddingClient : IEmbeddingClient
    {
        public bool IsConfigured { get; set; } = true;
        public string ModelId => "test-model";
        public int EmbeddingDimensions => 768;
        public EmbeddingResult Default { get; set; } = EmbeddingResult.Ok(new float[768]);
        public Queue<EmbeddingResult> Results { get; } = new();
        public int CallCount { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Default);
        }
    }
}

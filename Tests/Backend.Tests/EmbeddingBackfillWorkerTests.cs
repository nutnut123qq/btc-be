using Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class EmbeddingBackfillWorkerTests
{
    [Fact]
    public async Task RunCycleAsync_WhenEmbeddingIsNotConfigured_SkipsDatabaseAndEmbedding()
    {
        var embedder = new DisabledEmbeddingClient();
        var services = new ServiceCollection()
            .AddSingleton<IGeminiEmbeddingClient>(embedder)
            .BuildServiceProvider();
        var worker = new EmbeddingBackfillWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmbeddingBackfillWorker>.Instance);

        await worker.RunCycleAsync(default);

        Assert.Equal(0, embedder.CallCount);
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

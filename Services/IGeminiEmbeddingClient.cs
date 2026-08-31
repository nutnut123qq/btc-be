namespace Backend.Services;

public interface IGeminiEmbeddingClient
{
    bool IsConfigured { get; }

    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

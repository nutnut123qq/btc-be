namespace Backend.Services;

public interface IGeminiEmbeddingClient
{
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

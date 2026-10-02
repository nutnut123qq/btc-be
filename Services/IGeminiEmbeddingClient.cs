using Backend.Services.Models;

namespace Backend.Services;

public interface IGeminiEmbeddingClient
{
    bool IsConfigured { get; }

    /// <summary>Configured embedding model id (e.g. "gemini-embedding-001") — the provenance value stored on chunks.</summary>
    string ModelId { get; }

    /// <summary>Expected vector dimensionality (must match the pgvector vector(768) column/trigger).</summary>
    int EmbeddingDimensions { get; }

    /// <summary>Never throws for provider errors; inspect <see cref="EmbeddingResult.Error"/>.</summary>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

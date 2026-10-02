using Backend.Services.Models;

namespace Backend.Services;

public interface IEmbeddingClient
{
    bool IsConfigured { get; }

    /// <summary>Configured embedding model id (e.g. "nvidia/llama-nemotron-embed-vl-1b-v2:free") — the provenance value stored on chunks.</summary>
    string ModelId { get; }

    /// <summary>Expected vector dimensionality (must match the pgvector vector(768) column/trigger).</summary>
    int EmbeddingDimensions { get; }

    /// <summary>Never throws for provider errors; inspect <see cref="EmbeddingResult.Error"/>.</summary>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

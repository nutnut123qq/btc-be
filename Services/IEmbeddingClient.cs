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

    /// <summary>
    /// Embeds several texts, aligned to input order. Default loops <see cref="EmbedAsync"/> (one
    /// request per text); providers whose endpoint accepts array input override this so a whole
    /// batch costs a single request — matters because the free tier caps requests/day, not items.
    /// </summary>
    async Task<EmbeddingResult[]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var results = new EmbeddingResult[texts.Count];
        for (var i = 0; i < texts.Count; i++)
            results[i] = await EmbedAsync(texts[i], cancellationToken);
        return results;
    }
}

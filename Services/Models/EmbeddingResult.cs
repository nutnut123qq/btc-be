namespace Backend.Services.Models;

/// <summary>Classification of an embedding-provider failure.</summary>
public enum EmbeddingErrorKind
{
    /// <summary>Embedding succeeded.</summary>
    None,
    /// <summary>No API key configured; the provider was never called.</summary>
    NotConfigured,
    /// <summary>HTTP 401/403 — credentials rejected.</summary>
    Auth,
    /// <summary>HTTP 429 — provider rate limit / quota.</summary>
    RateLimited,
    /// <summary>Request timeout or transport/network failure.</summary>
    TimeoutOrNetwork,
    /// <summary>Provider answered but the payload was malformed, wrong dimensionality, or non-finite.</summary>
    InvalidResponse,
    /// <summary>Any other provider-side error (5xx, unexpected 4xx, unexpected exception).</summary>
    ProviderError
}

/// <summary>Outcome of a single embedding request. <see cref="Vector"/> is null unless <see cref="Error"/> is None.</summary>
public sealed record EmbeddingResult(float[]? Vector, EmbeddingErrorKind Error, string? Detail = null)
{
    public bool IsSuccess => Error == EmbeddingErrorKind.None && Vector != null;

    public static EmbeddingResult Ok(float[] vector) => new(vector, EmbeddingErrorKind.None);

    public static EmbeddingResult Fail(EmbeddingErrorKind error, string? detail = null) => new(null, error, detail);

    /// <summary>
    /// Guard applied before a vector is written to NewsChunks: exact dimensionality (the
    /// out-of-band pgvector trigger fails the write otherwise) and only finite values.
    /// </summary>
    public static bool IsUsableVector(float[]? vector, int dimensions) =>
        vector != null && vector.Length == dimensions && Array.TrueForAll(vector, float.IsFinite);

    /// <summary>Stable snake_case token for counters/log lines (e.g. rate_limit, invalid_response).</summary>
    public static string KindToken(EmbeddingErrorKind kind) => kind switch
    {
        EmbeddingErrorKind.None => "none",
        EmbeddingErrorKind.NotConfigured => "not_configured",
        EmbeddingErrorKind.Auth => "auth",
        EmbeddingErrorKind.RateLimited => "rate_limit",
        EmbeddingErrorKind.TimeoutOrNetwork => "timeout_or_network",
        EmbeddingErrorKind.InvalidResponse => "invalid_response",
        _ => "provider_error"
    };
}

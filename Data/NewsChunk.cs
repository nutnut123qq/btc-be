namespace Backend.Data;

public class NewsChunk
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }
    public NewsArticle Article { get; set; } = null!;
    public int ChunkIndex { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>768-dim embedding from Gemini; stored as PostgreSQL real[] (no pgvector extension).</summary>
    public float[]? Embedding { get; set; }

    public DateTimeOffset? EmbeddedAt { get; set; }

    /// <summary>Embedding model id that produced <see cref="Embedding"/> (e.g. "gemini-embedding-001"); null for legacy/unknown vectors.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Consecutive embedding-backfill cycles where this chunk failed. Chunks at the cap are poison-pilled and skipped.</summary>
    public int EmbeddingFailureCount { get; set; }
}

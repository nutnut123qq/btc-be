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
}

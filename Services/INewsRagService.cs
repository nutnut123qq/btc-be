namespace Backend.Services;

public class NewsChunkSearchResult
{
    public Guid Id { get; set; }
    public Guid ArticleId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Similarity { get; set; }
}

public interface INewsRagService
{
    Task<string> BuildNewsContextAsync(string query, int topK = 8, CancellationToken cancellationToken = default);
    Task<List<NewsChunkSearchResult>> SearchSimilarChunksAsync(string query, int topK = 8, CancellationToken cancellationToken = default);
}

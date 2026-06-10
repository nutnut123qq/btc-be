namespace Backend.Data;

public class NewsArticle
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public string? Summary { get; set; }
    public string? Content { get; set; }
    public DateTimeOffset FetchedAt { get; set; }

    public ICollection<NewsChunk> Chunks { get; set; } = new List<NewsChunk>();
}

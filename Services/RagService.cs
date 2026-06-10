using System.Text;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class RagService : IRagService
{
    private readonly AppDbContext _db;
    private readonly IGeminiEmbeddingClient _embedder;
    private readonly ILogger<RagService> _logger;

    public RagService(
        AppDbContext db,
        IGeminiEmbeddingClient embedder,
        ILogger<RagService> logger)
    {
        _db = db;
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<string> BuildNewsContextAsync(string query, int topK = 8, CancellationToken cancellationToken = default)
    {
        var hasEmbeddings = await _db.NewsChunks.AnyAsync(
            c => c.Embedding != null && c.Embedding.Length > 0,
            cancellationToken);
        if (!hasEmbeddings)
        {
            return await BuildFallbackLatestAsync(topK, cancellationToken);
        }

        var qvec = await _embedder.EmbedAsync(query, cancellationToken);
        if (qvec == null)
        {
            _logger.LogWarning("Query embedding failed; using latest articles fallback.");
            return await BuildFallbackLatestAsync(topK, cancellationToken);
        }

        try
        {
            var chunks = await _db.NewsChunks
                .AsNoTracking()
                .Include(c => c.Article)
                .Where(c => c.Embedding != null && c.Embedding.Length == qvec.Length)
                .ToListAsync(cancellationToken);

            if (chunks.Count == 0)
                return await BuildFallbackLatestAsync(topK, cancellationToken);

            var top = chunks
                .Select(c => (Chunk: c, Score: CosineSimilarity(qvec, c.Embedding!)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();

            var sb = new StringBuilder();
            foreach (var (chunk, _) in top)
            {
                sb.AppendLine("---");
                sb.AppendLine($"Title: {chunk.Article.Title}");
                sb.AppendLine($"Link: {chunk.Article.Link}");
                sb.AppendLine(chunk.Text);
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Similarity search failed; falling back to latest articles.");
            return await BuildFallbackLatestAsync(topK, cancellationToken);
        }
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }

    private async Task<string> BuildFallbackLatestAsync(int topK, CancellationToken cancellationToken)
    {
        var articles = await _db.NewsArticles
            .AsNoTracking()
            .OrderByDescending(a => a.PublishedAt ?? a.FetchedAt)
            .Take(topK)
            .Select(a => new { a.Title, a.Link, a.Summary })
            .ToListAsync(cancellationToken);

        if (articles.Count == 0)
        {
            return "No news articles are stored in the database yet. The RSS ingestion worker may still be running or feeds may be unavailable.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("(Retrieved by recency; embedding similarity unavailable.)");
        foreach (var a in articles)
        {
            sb.AppendLine("---");
            sb.AppendLine($"Title: {a.Title}");
            sb.AppendLine($"Link: {a.Link}");
            if (!string.IsNullOrWhiteSpace(a.Summary))
                sb.AppendLine(a.Summary);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }
}

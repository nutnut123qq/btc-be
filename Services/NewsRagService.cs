using System.Globalization;
using System.Numerics;
using System.Text;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class NewsRagService : INewsRagService, IRagService
{
    private readonly AppDbContext _db;
    private readonly IGeminiEmbeddingClient _embedder;
    private readonly ILogger<NewsRagService> _logger;

    public NewsRagService(
        AppDbContext db,
        IGeminiEmbeddingClient embedder,
        ILogger<NewsRagService> logger)
    {
        _db = db;
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<List<NewsChunkSearchResult>> SearchSimilarChunksAsync(
        string query,
        int topK = 8,
        CancellationToken cancellationToken = default)
    {
        var qvec = await _embedder.EmbedAsync(query, cancellationToken);
        if (qvec == null || qvec.Length == 0)
        {
            _logger.LogWarning("Query embedding failed; unable to perform vector search.");
            return new List<NewsChunkSearchResult>();
        }

        // 1. Fast Path: Native PostgreSQL pgvector HNSW index search (<=> cosine operator)
        if (_db.Database.IsRelational())
        {
            try
            {
                var vectorStr = "[" + string.Join(",", qvec.Select(v => v.ToString("G9", CultureInfo.InvariantCulture))) + "]";
                var rawSql = @"
                    SELECT c.""Id"", c.""ArticleId"", COALESCE(a.""Title"", '') AS ""Title"", COALESCE(a.""Link"", '') AS ""Link"",
                           c.""Text"" AS ""Content"",
                           1.0 - (c.""EmbeddingVector"" <=> {0}::vector) AS ""Similarity""
                    FROM ""NewsChunks"" c
                    LEFT JOIN ""NewsArticles"" a ON c.""ArticleId"" = a.""Id""
                    WHERE c.""EmbeddingVector"" IS NOT NULL
                    ORDER BY c.""EmbeddingVector"" <=> {0}::vector ASC
                    LIMIT {1};
                ";

                var results = await _db.Database
                    .SqlQueryRaw<NewsChunkSearchResult>(rawSql, vectorStr, topK)
                    .ToListAsync(cancellationToken);

                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Native pgvector query failed; falling back to in-memory SIMD similarity.");
            }
        }

        // 2. Fallback Path: In-memory SIMD Cosine calculation (for non-relational test fixtures)
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-30);
        var chunks = await _db.NewsChunks
            .AsNoTracking()
            .Where(c => c.Embedding != null && c.Embedding.Length == qvec.Length &&
                        (c.Article.PublishedAt >= cutoffDate || c.Article.FetchedAt >= cutoffDate))
            .OrderByDescending(c => c.Article.PublishedAt ?? c.Article.FetchedAt)
            .Take(500)
            .Select(c => new
            {
                c.Id,
                c.ArticleId,
                Title = c.Article.Title,
                Link = c.Article.Link,
                Content = c.Text,
                Embedding = c.Embedding!
            })
            .ToListAsync(cancellationToken);

        if (chunks.Count == 0)
        {
            chunks = await _db.NewsChunks
                .AsNoTracking()
                .Where(c => c.Embedding != null && c.Embedding.Length == qvec.Length)
                .OrderByDescending(c => c.Article.PublishedAt ?? c.Article.FetchedAt)
                .Take(500)
                .Select(c => new
                {
                    c.Id,
                    c.ArticleId,
                    Title = c.Article.Title,
                    Link = c.Article.Link,
                    Content = c.Text,
                    Embedding = c.Embedding!
                })
                .ToListAsync(cancellationToken);
        }

        return chunks
            .Select(c => new NewsChunkSearchResult
            {
                Id = c.Id,
                ArticleId = c.ArticleId,
                Title = c.Title,
                Link = c.Link,
                Content = c.Content,
                Similarity = CosineSimilarity(qvec, c.Embedding)
            })
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .ToList();
    }

    public async Task<string> BuildNewsContextAsync(
        string query,
        int topK = 8,
        CancellationToken cancellationToken = default)
    {
        var hasEmbeddings = await _db.NewsChunks.AnyAsync(
            c => (c.Embedding != null && c.Embedding.Length > 0),
            cancellationToken);

        if (!hasEmbeddings)
        {
            return await BuildFallbackLatestAsync(topK, cancellationToken);
        }

        var results = await SearchSimilarChunksAsync(query, topK, cancellationToken);
        if (results.Count == 0)
        {
            return await BuildFallbackLatestAsync(topK, cancellationToken);
        }

        var sb = new StringBuilder();
        foreach (var chunk in results)
        {
            sb.AppendLine("---");
            if (!string.IsNullOrWhiteSpace(chunk.Title))
                sb.AppendLine($"Title: {chunk.Title}");
            if (!string.IsNullOrWhiteSpace(chunk.Link))
                sb.AppendLine($"Link: {chunk.Link}");
            sb.AppendLine(chunk.Content);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        int i = 0;
        int vectorSize = Vector<float>.Count;
        var dotVec = Vector<float>.Zero;
        var naVec = Vector<float>.Zero;
        var nbVec = Vector<float>.Zero;

        if (Vector.IsHardwareAccelerated && a.Length >= vectorSize)
        {
            int limit = a.Length - vectorSize;
            while (i <= limit)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                dotVec += va * vb;
                naVec += va * va;
                nbVec += vb * vb;
                i += vectorSize;
            }
        }

        float dot = Vector.Dot(dotVec, Vector<float>.One);
        float na = Vector.Dot(naVec, Vector<float>.One);
        float nb = Vector.Dot(nbVec, Vector<float>.One);

        for (; i < a.Length; i++)
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

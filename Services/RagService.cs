using System.Numerics;
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
            // 1. Fast path: Direct HNSW vector index search in PostgreSQL (<=> cosine distance)
            if (_db.Database.IsRelational())
            {
                var vectorStr = "[" + string.Join(",", qvec.Select(v => v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture))) + "]";
                try
                {
                    var hnswResults = await _db.Database
                        .SqlQueryRaw<NewsChunkHnswResult>(
                            @"SELECT a.""Title"", a.""Link"", c.""Text"", (c.""EmbeddingVector"" <=> {0}::vector) AS ""Distance""
                              FROM ""NewsChunks"" c
                              JOIN ""NewsArticles"" a ON c.""ArticleId"" = a.""Id""
                              WHERE c.""EmbeddingVector"" IS NOT NULL
                              ORDER BY c.""EmbeddingVector"" <=> {0}::vector ASC
                              LIMIT {1}",
                            vectorStr, topK)
                        .ToListAsync(cancellationToken);

                    if (hnswResults.Count > 0)
                    {
                        var sbHnsw = new StringBuilder();
                        foreach (var chunk in hnswResults)
                        {
                            sbHnsw.AppendLine("---");
                            sbHnsw.AppendLine($"Title: {chunk.Title}");
                            sbHnsw.AppendLine($"Link: {chunk.Link}");
                            sbHnsw.AppendLine(chunk.Text);
                            sbHnsw.AppendLine();
                        }
                        return sbHnsw.ToString().Trim();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "HNSW vector query failed; falling back to in-memory cosine.");
                }
            }

            // 2. Fallback path: in-memory SIMD cosine similarity
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-30);
            var chunks = await _db.NewsChunks
                .AsNoTracking()
                .Where(c => c.Embedding != null && c.Embedding.Length == qvec.Length &&
                            (c.Article.PublishedAt >= cutoffDate || c.Article.FetchedAt >= cutoffDate))
                .OrderByDescending(c => c.Article.PublishedAt ?? c.Article.FetchedAt)
                .Take(500)
                .Select(c => new
                {
                    Title = c.Article.Title,
                    Link = c.Article.Link,
                    Text = c.Text,
                    Embedding = c.Embedding!
                })
                .ToListAsync(cancellationToken);

            // If 30-day window yielded no results, fallback to latest 500 chunks overall
            if (chunks.Count == 0)
            {
                chunks = await _db.NewsChunks
                    .AsNoTracking()
                    .Where(c => c.Embedding != null && c.Embedding.Length == qvec.Length)
                    .OrderByDescending(c => c.Article.PublishedAt ?? c.Article.FetchedAt)
                    .Take(500)
                    .Select(c => new
                    {
                        Title = c.Article.Title,
                        Link = c.Article.Link,
                        Text = c.Text,
                        Embedding = c.Embedding!
                    })
                    .ToListAsync(cancellationToken);
            }

            if (chunks.Count == 0)
                return await BuildFallbackLatestAsync(topK, cancellationToken);

            var top = chunks
                .Select(c => (Chunk: c, Score: CosineSimilarity(qvec, c.Embedding)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();

            var sb = new StringBuilder();
            foreach (var (chunk, _) in top)
            {
                sb.AppendLine("---");
                sb.AppendLine($"Title: {chunk.Title}");
                sb.AppendLine($"Link: {chunk.Link}");
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

    public class NewsChunkHnswResult
    {
        public string Title { get; set; } = string.Empty;
        public string Link { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public double Distance { get; set; }
    }

    /// <summary>
    /// Hardware-accelerated SIMD cosine similarity calculation using Vector&lt;float&gt;.
    /// </summary>
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

using System.Numerics;
using System.Text;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class NewsRagService : INewsRagService, IRagService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingClient _embedder;
    private readonly ILogger<NewsRagService> _logger;

    public NewsRagService(
        AppDbContext db,
        IEmbeddingClient embedder,
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
        if (!_embedder.IsConfigured)
            return new List<NewsChunkSearchResult>();

        var qres = await _embedder.EmbedAsync(query, cancellationToken);
        var qvec = qres.Vector;
        if (qvec == null || qvec.Length == 0)
        {
            _logger.LogWarning("Query embedding failed (kind={Kind} detail={Detail}); unable to perform vector search.",
                EmbeddingResult.KindToken(qres.Error), qres.Detail);
            return new List<NewsChunkSearchResult>();
        }

        // Only rank chunks embedded by the currently configured model — vectors from
        // different models are not comparable in the same space and must not be mixed.
        var modelId = _embedder.ModelId;
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-30);
        var chunks = await _db.NewsChunks
            .AsNoTracking()
            .Where(c => c.Embedding != null && c.Embedding.Length == qvec.Length &&
                        c.EmbeddingModel == modelId &&
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
                .Where(c => c.Embedding != null && c.Embedding.Length == qvec.Length &&
                            c.EmbeddingModel == modelId)
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
        if (!_embedder.IsConfigured)
        {
            return await BuildFallbackLatestAsync(topK,
                "embedding provider is not configured (no Gemini API key)", cancellationToken);
        }

        var embeddedTotal = await _db.NewsChunks.CountAsync(
            c => c.Embedding != null && c.Embedding.Length > 0,
            cancellationToken);
        var usableForModel = await _db.NewsChunks.CountAsync(
            c => c.Embedding != null && c.Embedding.Length > 0 && c.EmbeddingModel == _embedder.ModelId,
            cancellationToken);

        if (usableForModel == 0)
        {
            var reason = embeddedTotal == 0
                ? "no embedded news chunks are stored yet"
                : $"all {embeddedTotal} stored embeddings were produced by a different or unknown model (expected '{_embedder.ModelId}'); they are excluded from semantic search";
            return await BuildFallbackLatestAsync(topK, reason, cancellationToken);
        }

        var results = await SearchSimilarChunksAsync(query, topK, cancellationToken);
        if (results.Count == 0)
        {
            var excluded = embeddedTotal - usableForModel;
            var reason = "query embedding failed or no matching chunks scored" +
                (excluded > 0
                    ? $"; {excluded} embedded chunks were excluded because their model differs from '{_embedder.ModelId}'"
                    : "");
            return await BuildFallbackLatestAsync(topK, reason, cancellationToken);
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

    private async Task<string> BuildFallbackLatestAsync(int topK, string degradeReason, CancellationToken cancellationToken)
    {
        var articles = await _db.NewsArticles
            .AsNoTracking()
            .OrderByDescending(a => a.PublishedAt ?? a.FetchedAt)
            .Take(topK)
            .Select(a => new { a.Title, a.Link, a.Summary, Date = a.PublishedAt ?? a.FetchedAt })
            .ToListAsync(cancellationToken);

        if (articles.Count == 0)
        {
            return $"No news articles are stored in the database yet. The RSS ingestion worker may still be running or feeds may be unavailable. (Semantic search degraded: {degradeReason}.)";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"(Retrieved by recency; embedding similarity unavailable — {degradeReason}.)");
        var newest = articles.Max(a => a.Date);
        if (newest < DateTimeOffset.UtcNow.AddHours(-6))
            sb.AppendLine($"WARNING: stored news is stale; newest article is from {newest:O}.");
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

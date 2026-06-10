using System.ServiceModel.Syndication;
using System.Xml;
using Backend.Data;
using Backend.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public class RssIngestionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<RssOptions> _rssOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RssIngestionService> _logger;

    public RssIngestionService(
        IServiceScopeFactory scopeFactory,
        IOptions<RssOptions> rssOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<RssIngestionService> logger)
    {
        _scopeFactory = scopeFactory;
        _rssOptions = rssOptions;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromMinutes(Math.Max(1, _rssOptions.Value.PollMinutes));

        // Run once shortly after startup, then on interval
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await IngestAllFeedsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSS ingestion cycle failed");
            }

            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task IngestAllFeedsAsync(CancellationToken cancellationToken)
    {
        var feeds = _rssOptions.Value.Feeds.Where(f => !string.IsNullOrWhiteSpace(f.Url)).ToList();
        if (feeds.Count == 0)
        {
            _logger.LogWarning("No RSS feeds configured.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<IGeminiEmbeddingClient>();

        var http = _httpClientFactory.CreateClient("RssFetcher");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BitcoinAnalyst/1.0 (Capstone; +https://localhost)");

        foreach (var feed in feeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await IngestFeedAsync(db, embedder, http, feed.Source, feed.Url, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ingest feed {Source} {Url}", feed.Source, feed.Url);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task IngestFeedAsync(
        AppDbContext db,
        IGeminiEmbeddingClient embedder,
        HttpClient http,
        string source,
        string feedUrl,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(feedUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var feed = SyndicationFeed.Load(reader);

        foreach (var item in feed.Items ?? Array.Empty<SyndicationItem>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var link = item.Links.FirstOrDefault(l =>
                           string.Equals(l.RelationshipType, "alternate", StringComparison.OrdinalIgnoreCase))
                       ?.Uri?.ToString()
                       ?? item.Links.FirstOrDefault()?.Uri?.ToString();
            if (string.IsNullOrWhiteSpace(link))
                continue;

            var title = item.Title?.Text?.Trim() ?? "(no title)";
            var summary = item.Summary?.Text?.Trim();
            var published = item.PublishDate != default
                ? new DateTimeOffset(item.PublishDate.UtcDateTime, TimeSpan.Zero)
                : (DateTimeOffset?)null;

            var exists = await db.NewsArticles.AnyAsync(a => a.Link == link, cancellationToken);
            if (exists)
                continue;

            var article = new NewsArticle
            {
                Id = Guid.NewGuid(),
                Source = string.IsNullOrWhiteSpace(source) ? (feed.Title?.Text ?? "RSS") : source,
                Title = title.Length > 2000 ? title[..2000] : title,
                Link = link.Length > 4000 ? link[..4000] : link,
                PublishedAt = published,
                Summary = summary,
                Content = null,
                FetchedAt = DateTimeOffset.UtcNow
            };

            db.NewsArticles.Add(article);

            var body = string.Join("\n\n", new[] { title, summary }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var chunkTexts = TextChunker.Chunk(body, maxChars: 1000, overlap: 120);
            var index = 0;
            foreach (var chunkText in chunkTexts)
            {
                var chunk = new NewsChunk
                {
                    Id = Guid.NewGuid(),
                    ArticleId = article.Id,
                    ChunkIndex = index++,
                    Text = chunkText.Length > 16000 ? chunkText[..16000] : chunkText,
                    Embedding = null,
                    EmbeddedAt = null
                };

                var vec = await embedder.EmbedAsync(chunk.Text, cancellationToken);
                if (vec != null)
                {
                    chunk.Embedding = vec;
                    chunk.EmbeddedAt = DateTimeOffset.UtcNow;
                }

                db.NewsChunks.Add(chunk);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

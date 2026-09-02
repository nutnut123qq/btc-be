using Backend.Data;
using Backend.Hubs;
using Backend.Options;
using Backend.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using System.IO.Compression;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "Bitcoin AI Analyst Backend",
    Version = ResearchVersions.ApiContract
}));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<DataAuditCache>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSignalR();

// Response Compression (Brotli + Gzip) for High Concurrency Payload Optimization
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/json",
        "application/problem+json"
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

// High-Concurrency Rate Limiter (.NET 8 built-in)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetSlidingWindowLimiter(clientIp, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 20
        });
    });
    options.AddPolicy("expensive", httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
});

builder.Services.Configure<RssOptions>(builder.Configuration.GetSection(RssOptions.SectionName));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.SectionName));
builder.Services.Configure<KlinesIngestionOptions>(builder.Configuration.GetSection(KlinesIngestionOptions.SectionName));
builder.Services.Configure<IndexingOptions>(builder.Configuration.GetSection(IndexingOptions.SectionName));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<BinanceTestnetOptions>(builder.Configuration.GetSection(BinanceTestnetOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHttpClient("AIService", client =>
{
    var aiUrl = builder.Configuration["AiService:BaseUrl"] ?? "http://127.0.0.1:8000";
    client.BaseAddress = new Uri(aiUrl);
    // LangGraph + many Ollama calls: default 15m was often too short on CPU.
    var minutes = builder.Configuration.GetValue("AiService:RequestTimeoutMinutes", 60);
    client.Timeout = minutes <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(minutes);
});

builder.Services.AddHttpClient("Binance", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("RssFetcher", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("GeminiEmbedding", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("Telegram", client => { client.Timeout = TimeSpan.FromSeconds(30); });

builder.Services.AddScoped<IGeminiEmbeddingClient, GeminiEmbeddingClient>();
builder.Services.AddScoped<INewsRagService, NewsRagService>();
builder.Services.AddScoped<NewsRagService>();
builder.Services.AddScoped<IRagService, NewsRagService>();
builder.Services.AddScoped<RagService>();
builder.Services.AddScoped<IBinanceKlinesService, BinanceKlinesService>();
builder.Services.AddScoped<KlinesBackfillService>();
builder.Services.AddScoped<IPatternSearchService, PatternSearchService>();
builder.Services.AddScoped<IWindowVectorIndexer, WindowVectorIndexer>();
builder.Services.AddScoped<ICandlePatternIndexer, CandlePatternIndexer>();
builder.Services.AddScoped<ICandleSequenceRulesEngine, CandleSequenceRulesEngine>();
builder.Services.AddScoped<CandleVolumeIndexer>();
builder.Services.AddScoped<TechnicalIndicatorIndexer>();
builder.Services.AddScoped<MarketMetricsIndexer>();
builder.Services.AddScoped<CandlePatternSequenceIndexer>();
builder.Services.AddScoped<IMlDatasetService, MlDatasetService>();
builder.Services.AddScoped<IWindowDatasetService, WindowDatasetService>();
builder.Services.AddScoped<IArchetypeService, ArchetypeService>();
builder.Services.AddScoped<ITransitionService, TransitionService>();
builder.Services.AddScoped<IDataAuditService, DataAuditService>();
builder.Services.AddScoped<IRegimeDetectionService, RegimeDetectionService>();
builder.Services.AddScoped<IConfluenceService, ConfluenceService>();
builder.Services.AddScoped<ITelegramNotificationService, TelegramNotificationService>();
builder.Services.AddScoped<IVolumeProfileService, VolumeProfileService>();
builder.Services.AddScoped<ISmartMoneyService, SmartMoneyService>();
builder.Services.AddScoped<ISentimentService, SentimentService>();
builder.Services.AddScoped<IEnsembleService, EnsembleService>();
builder.Services.AddScoped<IEnsembleBacktestService, EnsembleBacktestService>();
builder.Services.AddScoped<IEnsemblePaperTraderService, EnsemblePaperTraderService>();
builder.Services.AddScoped<IAiContextService, AiContextService>();
builder.Services.AddScoped<IFuturesMetricsService, FuturesMetricsService>();
builder.Services.AddScoped<IBtcDominanceService, BtcDominanceService>();
builder.Services.AddHttpClient("BinanceFuturesTestnet", client =>
{
    var baseUrl = builder.Configuration["BinanceTestnet:BaseUrl"] ?? "https://testnet.binancefuture.com";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IUserDataStreamHandlerService, UserDataStreamHandlerService>();
builder.Services.AddHttpClient<ILiveOrderExecutionService, LiveOrderExecutionService>();

builder.Services.AddSingleton<BinanceUserDataStreamService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("BinanceFuturesTestnet");
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BinanceTestnetOptions>>();
    var logger = sp.GetRequiredService<ILogger<BinanceUserDataStreamService>>();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    return new BinanceUserDataStreamService(http, options, logger, scopeFactory);
});
builder.Services.AddSingleton<IBinanceUserDataStreamService>(sp => sp.GetRequiredService<BinanceUserDataStreamService>());

// FullReindexService and MlDatasetRebuildService are kept as scoped helpers
// (used by tests / manual triggers). The queue/worker/controller glue has been removed.
builder.Services.AddScoped<FullReindexService>();
builder.Services.AddScoped<MlDatasetRebuildService>();

// Maintenance/smoke-test mode can start the API without mutating background jobs.
if (builder.Configuration.GetValue("BackgroundWorkers:Enabled", true))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BinanceUserDataStreamService>());
    builder.Services.AddHostedService<KlinesIngestionWorker>();
    builder.Services.AddHostedService<IndexingBackgroundWorker>();
    builder.Services.AddHostedService<RssIngestionService>();
    builder.Services.AddHostedService<PriceAlertWorker>();
    builder.Services.AddHostedService<EmbeddingBackfillWorker>();
    builder.Services.AddHostedService<MlDatasetBuilder>();
    builder.Services.AddHostedService<WindowDatasetBuilder>();
}

// CORS: Next (3000), Flutter web (port ngẫu nhiên ví dụ 58340), Swagger — cùng máy thì origin hay đổi port.
// Tránh chỉ WithOrigins("http://localhost:3000"): Production mặc định sẽ chặn Flutter web.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowNextJs", policy =>
    {
        policy.SetIsOriginAllowed(static origin =>
            {
                if (string.IsNullOrWhiteSpace(origin)) return false;
                try
                {
                    var u = new Uri(origin);
                    return u.Scheme is "http" or "https"
                        && (string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(u.Host, "127.0.0.1", StringComparison.Ordinal));
                }
                catch (UriFormatException)
                {
                    return false;
                }
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.UseResponseCompression();
app.UseRateLimiter();
app.UseCors("AllowNextJs");

// Development keeps the existing convenience behavior. Production-like runs use
// ops/migrate.ps1 so schema changes stay an explicit, backed-up operation.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
    {
        try
        {
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            db.Database.Migrate();
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogCritical(ex, "FATAL: Database migration failed. Ensure PostgreSQL is running and accessible.");
            throw;
        }
    }
}

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("ContractGeneration"))
{
    app.UseSwagger();
    if (app.Environment.IsDevelopment()) app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler(errorApp => 
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { 
                Code = "INTERNAL_SERVER_ERROR", 
                Message = "An unexpected error occurred. Please try again later." 
            });
        });
    });
}

app.MapControllers();
app.MapHub<TradeNotificationHub>(TradeNotificationHub.HubUrl);

app.Run();

public partial class Program { }

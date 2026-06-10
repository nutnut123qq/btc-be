using Backend.Data;
using Backend.Options;
using Backend.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<RssOptions>(builder.Configuration.GetSection(RssOptions.SectionName));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHttpClient("AIService", client =>
{
    var aiUrl = builder.Configuration["AiService:BaseUrl"] ?? "http://localhost:8000";
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

builder.Services.AddScoped<IGeminiEmbeddingClient, GeminiEmbeddingClient>();
builder.Services.AddScoped<IRagService, RagService>();
builder.Services.AddScoped<IBinanceKlinesService, BinanceKlinesService>();
builder.Services.AddScoped<IPatternSearchService, PatternSearchService>();
builder.Services.AddScoped<IWindowVectorIndexer, WindowVectorIndexer>();
builder.Services.AddScoped<ICandlePatternIndexer, CandlePatternIndexer>();
builder.Services.AddScoped<ICandleSequenceRulesEngine, CandleSequenceRulesEngine>();
builder.Services.AddScoped<CandleVolumeIndexer>();
builder.Services.AddScoped<TechnicalIndicatorIndexer>();
builder.Services.AddScoped<MarketMetricsIndexer>();

builder.Services.AddHostedService<RssIngestionService>();
builder.Services.AddHostedService<PriceAlertWorker>();
builder.Services.AddHostedService<IndexingBackgroundWorker>();
builder.Services.AddHostedService<KlinesIngestionWorker>();
builder.Services.AddHostedService<EmbeddingBackfillWorker>();

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
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("AllowNextJs");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Database migration skipped or failed (ensure PostgreSQL is running).");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();

using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public class MarketControllerIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public MarketControllerIntegrationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetCandlePatterns_ReturnsSeededPatterns()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CandlePatterns.Add(new CandlePattern
        {
            Symbol = "BTCUSDT",
            Timeframe = "15m",
            OpenTimeMs = 1_000_000,
            Open = 64000m,
            High = 64100m,
            Low = 63900m,
            Close = 64050m,
            Volume = 100m,
            PatternType = "Hammer",
            PatternCategory = "Single",
            TrendDirection = "Downtrend",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/market/candle-patterns?symbol=BTCUSDT&timeframe=15m&page=1&pageSize=10");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hammer", content);
        Assert.Contains("BTCUSDT", content);
    }

    [Fact]
    public async Task IndexCandlePatterns_WithFakeBinance_ReturnsIndexedCount()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsync(
            "/api/market/candle-patterns/index?symbol=BTCUSDT&timeframe=15m&lookbackBars=20",
            content: null);

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        Assert.True(doc.RootElement.TryGetProperty("indexed", out var indexedProp));
        Assert.True(indexedProp.GetInt32() >= 0);
    }
}

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Replace AppDbContext with InMemory database
            var dbContextDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (dbContextDescriptor != null) services.Remove(dbContextDescriptor);

            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase("BitcoinAnalystTestDb"));

            // Replace BinanceKlinesService with a fake that returns deterministic data
            var binanceDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IBinanceKlinesService));
            if (binanceDescriptor != null) services.Remove(binanceDescriptor);

            services.AddScoped<IBinanceKlinesService>(_ => new FakeBinanceKlinesService());

            // Remove all hosted services so they do not run during tests
            var hostedServiceDescriptors = services
                .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                .ToList();
            foreach (var descriptor in hostedServiceDescriptors)
            {
                services.Remove(descriptor);
            }
        });
    }
}

public class FakeBinanceKlinesService : IBinanceKlinesService
{
    public Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
        string symbol = "BTCUSDT",
        string interval = "1h",
        int limit = 48,
        long? startTimeMs = null,
        long? endTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        var klines = Enumerable.Range(0, Math.Max(limit, 20))
            .Select(i => new KlineDto
            {
                OpenTimeMs = 1_000_000L + i * 3_600_000L,
                TimeIso = DateTimeOffset.UtcNow.AddHours(i).ToString("o"),
                Open = 64000m + (i % 2 == 0 ? 0m : -100m),
                High = 64200m,
                Low = 63800m,
                Close = 64000m + (i % 2 == 0 ? 100m : -50m),
                Volume = 100m,
                CloseTimeMs = 1_000_000L + i * 3_600_000L + 3_599_999L,
                QuoteVolume = 1000m,
                TradeCount = 100,
                TakerBuyVolume = 50m,
                TakerBuyQuoteVolume = 500m,
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<KlineDto>>(klines);
    }

    public Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(
        string interval = "1h",
        int limit = 48,
        CancellationToken cancellationToken = default)
    {
        return GetKlinesAsync("BTCUSDT", interval, limit, cancellationToken: cancellationToken);
    }

    public Task<string> BuildTechSummaryAsync(
        string interval = "1h",
        int limit = 48,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Fake tech summary");
    }
}

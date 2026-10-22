using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Controllers;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backend.Tests;

/// <summary>
/// MBB-06A endpoint tests. HTTP-level via the shared TestWebApplicationFactory
/// (InMemory DB, hosted workers removed) for candles/data-quality/admin guard,
/// plus direct-construction tests for the AI-proxied conditions endpoint.
/// </summary>
[Collection(EquityLaneLockCollection.Name)]
public class MbbMarketDataEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    // Unique open-ms base per test keeps rows isolated inside the shared
    // InMemory fixture database (queries are additionally bounded by endTimeMs).
    private const long BaseMs = 1_600_000_000_000L;

    private readonly TestWebApplicationFactory _factory;

    public MbbMarketDataEndpointTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static InstrumentCandle Candle(long openMs, string symbol = "MBB", string tf = "1d") => new()
    {
        Symbol = symbol,
        Timeframe = tf,
        OpenTimeMs = openMs,
        CloseTimeMs = openMs + 86_399_999L,
        Open = 20_000m,
        High = 20_500m,
        Low = 19_800m,
        Close = 20_300m,
        Volume = 1_000_000m,
        Provider = "VCI",
        PriceBasis = "adjusted",
        Venue = "HOSE",
        SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private async Task SeedAsync(params InstrumentCandle[] candles)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.InstrumentCandles.AddRange(candles);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetCandles_ReturnsMbbRowsForTimeframe_Ascending()
    {
        var b = BaseMs;
        await SeedAsync(
            Candle(b + 86_400_000L * 0),
            Candle(b + 86_400_000L * 1),
            Candle(b + 86_400_000L * 2),
            Candle(b + 3_600_000L, tf: "1h"),            // wrong timeframe — excluded
            Candle(b + 86_400_000L, symbol: "BTCUSDT")); // wrong symbol — excluded

        var client = _factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/mbb/candles?timeframe=1d&limit=100&endTimeMs={b + 86_400_000L * 2}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, rows.GetArrayLength());
        for (var i = 0; i < 3; i++)
        {
            var bar = rows[i];
            Assert.Equal(b + 86_400_000L * i, bar.GetProperty("openTimeMs").GetInt64());
            Assert.Equal(20_300m, bar.GetProperty("close").GetDecimal());
            // Binance-only fields are present but null — absent, not fabricated.
            Assert.Equal(JsonValueKind.Null, bar.GetProperty("quoteVolume").ValueKind);
            Assert.Equal(JsonValueKind.Null, bar.GetProperty("tradeCount").ValueKind);
            Assert.Equal(JsonValueKind.Null, bar.GetProperty("takerBuyVolume").ValueKind);
            Assert.Equal(JsonValueKind.Null, bar.GetProperty("takerBuyQuoteVolume").ValueKind);
        }
    }

    [Fact]
    public async Task GetCandles_RejectsUnsupportedTimeframes()
    {
        var client = _factory.CreateClient();
        foreach (var tf in new[] { "4h", "15m", "1w", "xyz" })
        {
            var response = await client.GetAsync($"/api/mbb/candles?timeframe={tf}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("INVALID_TIMEFRAME", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task GetCandles_MissingTimeframe_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/mbb/candles");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetCandles_LimitTakesMostRecent_ClampedAtMax()
    {
        var b = BaseMs + 1_000_000_000L;
        await SeedAsync(
            Candle(b + 86_400_000L * 0),
            Candle(b + 86_400_000L * 1),
            Candle(b + 86_400_000L * 2));

        var client = _factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/mbb/candles?timeframe=1d&limit=2&endTimeMs={b + 86_400_000L * 2}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(b + 86_400_000L, rows[0].GetProperty("openTimeMs").GetInt64());
        Assert.Equal(b + 86_400_000L * 2, rows[1].GetProperty("openTimeMs").GetInt64());

        var zero = await client.GetAsync("/api/mbb/candles?timeframe=1d&limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
    }

    [Fact]
    public async Task GetDataQuality_ReturnsCoverageReport()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/mbb/data-quality");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("MBB", root.GetProperty("symbol").GetString());
        Assert.Equal("VCI", root.GetProperty("provider").GetString());
        var timeframes = root.GetProperty("timeframes");
        Assert.Equal(2, timeframes.GetArrayLength());
        Assert.Contains(timeframes.EnumerateArray(), t => t.GetProperty("timeframe").GetString() == "1d");
        Assert.Contains(timeframes.EnumerateArray(), t => t.GetProperty("timeframe").GetString() == "1h");
    }

    // --- admin guard ---------------------------------------------------------

    [Fact]
    public async Task AdminEndpoints_RequireAdminKey()
    {
        var client = _factory.CreateClient();
        foreach (var url in new[]
        {
            "/api/mbb/admin/sync",
            "/api/mbb/admin/backfill?timeframe=1d",
            "/api/mbb/admin/recompute?timeframe=1d",
        })
        {
            var response = await client.PostAsync(url, content: null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task PersonalReadEndpoints_RequireAdminKey()
    {
        // Position (cost basis, shares) and decision (embedded position summary)
        // expose personal holdings — reads are AdminGuard'd like the writes.
        var client = _factory.CreateClient();
        foreach (var url in new[] { "/api/mbb/position", "/api/mbb/decision" })
        {
            var unauth = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

            var wrong = new HttpRequestMessage(HttpMethod.Get, url);
            wrong.Headers.Add("X-Admin-Key", "wrong-key");
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.SendAsync(wrong)).StatusCode);
        }

        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);
        Assert.Equal(HttpStatusCode.OK,
            (await authed.GetAsync("/api/mbb/position")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await authed.GetAsync("/api/mbb/decision")).StatusCode);
    }

    [Fact]
    public async Task AdminSync_WithKey_ReturnsDisabledReport_WhenIngestionOff()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);

        var response = await client.PostAsync("/api/mbb/admin/sync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("MBB", root.GetProperty("symbol").GetString());
        // Test config leaves MbbIngestion:Enabled=false — the report must say so
        // rather than silently succeeding or erroring.
        Assert.Equal("Disabled", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AdminBackfill_RejectsInactiveTimeframe_AndBadDate()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);

        var tf4h = await client.PostAsync("/api/mbb/admin/backfill?timeframe=4h", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, tf4h.StatusCode);
        Assert.Contains("INVALID_TIMEFRAME", await tf4h.Content.ReadAsStringAsync());

        var badDate = await client.PostAsync(
            "/api/mbb/admin/backfill?timeframe=1d&from=20-01-01", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);

        // Disabled ingestion still returns a truthful per-timeframe report.
        var disabled = await client.PostAsync("/api/mbb/admin/backfill?timeframe=1d", content: null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var root = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("1d", root.GetProperty("timeframe").GetString());
        Assert.Contains("disabled", root.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminRecompute_Rejects4h_AndReportsPerTimeframe()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);

        var tf4h = await client.PostAsync("/api/mbb/admin/recompute?timeframe=4h", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, tf4h.StatusCode);

        var ok = await client.PostAsync("/api/mbb/admin/recompute?timeframe=1h", content: null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var root = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, root.GetArrayLength());
        var report = root[0];
        Assert.Equal("MBB", report.GetProperty("symbol").GetString());
        Assert.Equal("1h", report.GetProperty("timeframe").GetString());
    }

    // --- conditions (AI proxy) ------------------------------------------------

    [Fact]
    public async Task GetConditions_ProxiesAi_ForMbb_AndReportsEvidenceUnavailable()
    {
        var handler = new StubHandler(MbbAiPayload());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") };
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("MbbConditions").Options);
        var controller = new MbbMarketDataController(
            db,
            new MbbDataQualityService(db),
            new StubHttpClientFactory(httpClient),
            new EmptyEvidenceCatalog(),
            NullLogger<CurrentConditionsService>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await controller.GetConditions("1d");

        var ok = Assert.IsType<OkObjectResult>(action);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        Assert.Equal("1d", json.GetProperty("timeframe").GetString());
        // No MBB-scoped verified evidence bundle exists yet — honest envelope.
        Assert.False(json.GetProperty("evidence").GetProperty("available").GetBoolean());
        Assert.Equal("no-verified-evidence-bundle",
            json.GetProperty("evidence").GetProperty("reason").GetString());
        // The upstream call must have pinned symbol=MBB.
        Assert.Contains("symbol=MBB", handler.LastRequest);
    }

    [Fact]
    public async Task GetConditions_Rejects4h()
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("MbbConditions4h").Options);
        var controller = new MbbMarketDataController(
            db,
            new MbbDataQualityService(db),
            new StubHttpClientFactory(new HttpClient(new StubHandler("{}"))),
            new EmptyEvidenceCatalog(),
            NullLogger<CurrentConditionsService>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await controller.GetConditions("4h");

        var bad = Assert.IsType<BadRequestObjectResult>(action);
        Assert.Equal("INVALID_TIMEFRAME", Assert.IsType<ApiErrorEnvelope>(bad.Value).Code);
    }

    // ---------------------------------------------------------------- fixtures

    private static string MbbAiPayload() => """
        {
            "schemaVersion": "current-conditions-v1",
            "symbol": "MBB",
            "timeframe": "1d",
            "asOfMs": 1789948799999,
            "latestClosedBar": {
                "openTimeMs": 1789862400000,
                "closeTimeMs": 1789948799999,
                "open": 20000.0,
                "high": 20500.0,
                "low": 19800.0,
                "close": 20300.0,
                "volume": 1000000.0
            },
            "generatedAtMs": 1790000000123,
            "analysisWindow": {"firstOpenTimeMs": 1000, "bars": 2000, "contiguous": true, "windowBars": 2000},
            "contract": {
                "contractVersion": "btc-technical-module-contract/v9",
                "definitionsSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "rawFileSha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
            },
            "conditions": [
                {
                    "module": "technicalIndicators",
                    "eventType": "RSI_ENTER_OVERSOLD",
                    "kind": "state",
                    "direction": "bullish",
                    "eventId": null,
                    "formedTimeMs": 1789862400000,
                    "availableTimeMs": 1789948799999,
                    "context": {"trend": "down", "volatility": "normal"},
                    "details": {"rsi": 27.4}
                }
            ],
            "warnings": [],
            "unavailableModules": []
        }
        """;

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public string? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri?.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>No verified bundles — MBB evidence reports unavailable honestly.</summary>
    private sealed class EmptyEvidenceCatalog : IResearchEvidenceCatalog
    {
        public ResearchEvidenceCatalogResponse GetCatalog() => new(
            "btc-research-evidence-catalog-v1",
            "MBB",
            new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
            [],
            new EvidenceIntegritySummaryDto(0, 0, 0),
            null);

        public ResearchEvidenceDetailDto? GetDetail(string id) => null;
    }
}

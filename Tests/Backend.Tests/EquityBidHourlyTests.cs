using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

/// <summary>
/// EQTECH-1 H HTTP tests: BID declares 1h, so the public 1h reads answer 200 (candles, conditions,
/// data quality with a 1h block and the 1h caveats). Base (BID daily-only) answers 400 INVALID_TIMEFRAME.
/// The AI service is a stub that echoes the requested symbol/timeframe in a valid current-conditions-v1
/// payload; the InMemory database is private to this factory.
/// </summary>
public class EquityBidHourlyTests : IClassFixture<EquityBidHourlyTests.Factory>
{
    public sealed class AiRecorder
    {
        private readonly List<string> _requests = [];
        public void Add(string pathAndQuery) { lock (_requests) _requests.Add(pathAndQuery); }
        public IReadOnlyList<string> Requests { get { lock (_requests) return _requests.ToArray(); } }
    }

    /// <summary>Echoes symbol/timeframe from the query into a schema-valid current-conditions-v1 payload.</summary>
    private sealed class EchoConditionsHandler(AiRecorder recorder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            recorder.Add(uri.PathAndQuery);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "", StringComparer.Ordinal);
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schemaVersion"] = "current-conditions-v1",
                ["symbol"] = query.GetValueOrDefault("symbol"),
                ["timeframe"] = query.GetValueOrDefault("timeframe"),
                ["asOfMs"] = 1_792_400_000_000L,
                ["latestClosedBar"] = new
                {
                    openTimeMs = 1_792_395_200_000L, closeTimeMs = 1_792_398_799_999L,
                    open = 40_000.0, high = 40_200.0, low = 39_900.0, close = 40_100.0, volume = 1_000_000.0,
                },
                ["generatedAtMs"] = 1_792_400_000_123L,
                ["analysisWindow"] = new { firstOpenTimeMs = 1_000L, bars = 300, contiguous = true, windowBars = 300 },
                ["contract"] = new
                {
                    contractVersion = "btc-technical-module-contract/v9",
                    definitionsSha256 = new string('a', 64),
                    rawFileSha256 = new string('d', 64),
                },
                ["conditions"] = new[]
                {
                    new
                    {
                        module = "technicalIndicators", eventType = "RSI_ENTER_OVERSOLD", kind = "state",
                        direction = "bullish", eventId = (string?)null,
                        formedTimeMs = 1_792_395_200_000L, availableTimeMs = 1_792_398_799_999L,
                        context = new { trend = "down", volatility = "normal" }, details = new { rsi = 27.4 },
                    },
                },
                ["warnings"] = Array.Empty<string>(),
                ["unavailableModules"] = Array.Empty<string>(),
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    public sealed class Factory : TestWebApplicationFactory
    {
        public AiRecorder Ai { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList())
                    services.Remove(d);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("eqtech-h-bid-hourly"));
                services.AddHttpClient("AIService").ConfigurePrimaryHttpMessageHandler(() => new EchoConditionsHandler(Ai));
            });
        }
    }

    private readonly Factory _factory;

    public EquityBidHourlyTests(Factory factory) => _factory = factory;

    private static InstrumentCandle Candle(string tf, long openMs, long closeMs, decimal px) => new()
    {
        Symbol = "BID", Timeframe = tf, OpenTimeMs = openMs, CloseTimeMs = closeMs,
        Open = px, High = px + 100m, Low = px - 100m, Close = px + 50m, Volume = 100_000m,
        Provider = "VCI", PriceBasis = "adjusted", Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Bid1h_CandlesConditionsAndDataQuality_Return200()
    {
        var cal = new VietnamMarketCalendar();
        var session = new DateOnly(2026, 10, 13);
        var hourOpens = VietnamMarketCalendar.HourlyOpensUtcMs(session).Take(3).ToArray();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Seeded out of order: the endpoint must return ascending open time.
            foreach (var open in hourOpens.OrderByDescending(o => o))
                db.InstrumentCandles.Add(Candle("1h", open, cal.SessionCloseUtcMs("1h", open), 40_000m));
            var day = VietnamMarketCalendar.DailyOpenUtcMs(session);
            db.InstrumentCandles.Add(Candle("1d", day, cal.SessionCloseUtcMs("1d", day), 40_000m));
            await db.SaveChangesAsync();
        }
        var client = _factory.CreateClient();

        var candles = await client.GetAsync("/api/equities/BID/candles?timeframe=1h&limit=10");
        Assert.Equal(HttpStatusCode.OK, candles.StatusCode);
        var bars = (await JsonAsync(candles)).EnumerateArray().Select(b => b.GetProperty("openTimeMs").GetInt64()).ToArray();
        Assert.Equal(hourOpens, bars);

        var conditions = await client.GetAsync("/api/equities/BID/conditions?timeframe=1h");
        Assert.Equal(HttpStatusCode.OK, conditions.StatusCode);
        Assert.Equal("1h", (await JsonAsync(conditions)).GetProperty("timeframe").GetString());
        Assert.Contains("/api/current-conditions?timeframe=1h&symbol=BID", _factory.Ai.Requests);

        // Undeclared timeframes still fail closed before the upstream is called.
        var before = _factory.Ai.Requests.Count;
        var bad = await client.GetAsync("/api/equities/BID/conditions?timeframe=4h");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("INVALID_TIMEFRAME", (await JsonAsync(bad)).GetProperty("code").GetString());
        Assert.Equal(before, _factory.Ai.Requests.Count);

        var quality = await JsonAsync(await client.GetAsync("/api/equities/BID/data-quality"));
        var blocks = quality.GetProperty("timeframes").EnumerateArray().ToArray();
        Assert.Equal(["1d", "1h"], blocks.Select(t => t.GetProperty("timeframe").GetString()).ToArray());
        Assert.Equal(3, blocks[1].GetProperty("totalBars").GetInt64());
        var limitations = quality.GetProperty("limitations").EnumerateArray().Select(l => l.GetString()!).ToArray();
        Assert.Contains(limitations, l => l.StartsWith("1h opening bar", StringComparison.Ordinal));
        Assert.Contains(limitations, l => l.StartsWith("BID: the 2026-10-16 provider probe", StringComparison.Ordinal));
    }
}

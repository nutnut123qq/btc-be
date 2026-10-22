using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Backend.Tests;

/// <summary>
/// Tests that take <see cref="EquityLaneLocks"/> leases (static, process-wide) or call admin
/// endpoints that do, so lock-contention assertions never race unrelated tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EquityLaneLockCollection
{
    public const string Name = "EquityLaneLocks";
}

/// <summary>
/// EQX-A1 HTTP tests for /api/equities (TestWebApplicationFactory: InMemory DB, no hosted
/// workers): spec §2 error codes, AdminGuard on personal reads for both route families,
/// /api/mbb alias parity, lane-lock 409, and crypto endpoints still rejecting equities.
/// </summary>
[Collection(EquityLaneLockCollection.Name)]
public class EquityEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private const long BaseMs = 1_650_000_000_000L;
    private const long DayMs = 86_400_000L;
    private readonly TestWebApplicationFactory _factory;

    public EquityEndpointTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);
        return client;
    }

    private static InstrumentCandle Candle(string symbol, long openMs, decimal px = 20_000m) => new()
    {
        Symbol = symbol,
        Timeframe = "1d",
        OpenTimeMs = openMs,
        CloseTimeMs = openMs + 27_900_000L,
        Open = px, High = px + 500m, Low = px - 200m, Close = px + 300m,
        Volume = 1_000_000m,
        Provider = "VCI", PriceBasis = "adjusted", Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
    };

    private async Task SeedAsync(Action<AppDbContext> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("requestId").GetString()));
    }

    // --- spec §2 error codes ------------------------------------------------------

    [Fact]
    public async Task UnknownSymbol_Returns404_UnknownSymbol()
    {
        var client = _factory.CreateClient();
        await AssertErrorAsync(await client.GetAsync("/api/equities/NOPE/candles?timeframe=1d"),
            HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");
        await AssertErrorAsync(await client.GetAsync("/api/equities/NOPE/data-quality"),
            HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");
    }

    [Fact]
    public async Task UnsupportedCapability_Returns400_ForCryptoAndContextOnlySymbols()
    {
        var client = _factory.CreateClient();
        // BTCUSDT is known but not on the equity lane.
        await AssertErrorAsync(await client.GetAsync("/api/equities/BTCUSDT/candles?timeframe=1d"),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await client.GetAsync("/api/equities/BTCUSDT/conditions?timeframe=1d"),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        // Context-only equities/index: no decision, no TA.
        await AssertErrorAsync(await Admin().GetAsync("/api/equities/VCB/observations?timeframe=1d"),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await client.GetAsync("/api/equities/VNINDEX/conditions?timeframe=1d"),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");

        var admin = Admin();
        var vcbDecision = await admin.GetAsync("/api/equities/VCB/decision");
        await AssertErrorAsync(vcbDecision, HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await admin.PostAsync("/api/equities/VCB/admin/recompute", null),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await admin.PostAsync("/api/equities/CTG/admin/observe?timeframe=1d", null),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
    }

    [Fact]
    public async Task InvalidTimeframe_Returns400_ForBid1h()
    {
        var client = _factory.CreateClient();
        await AssertErrorAsync(await client.GetAsync("/api/equities/BID/candles?timeframe=1h"),
            HttpStatusCode.BadRequest, "INVALID_TIMEFRAME");
        await AssertErrorAsync(await client.GetAsync("/api/equities/BID/candles"),
            HttpStatusCode.BadRequest, "INVALID_TIMEFRAME");
        await AssertErrorAsync(await client.GetAsync("/api/equities/MBB/candles?timeframe=4h"),
            HttpStatusCode.BadRequest, "INVALID_TIMEFRAME");
        await AssertErrorAsync(await Admin().PostAsync("/api/equities/BID/admin/backfill?timeframe=1h", null),
            HttpStatusCode.BadRequest, "INVALID_TIMEFRAME");
        // MBB declares 1h, so the same request is valid there.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/equities/MBB/candles?timeframe=1h")).StatusCode);
    }

    [Fact]
    public async Task CryptoEndpoints_StillRejectEquities()
    {
        var client = _factory.CreateClient();
        foreach (var url in new[]
        {
            "/api/market/btc/klines?symbol=BID",
            "/api/market/btc/klines?symbol=VNINDEX",
            "/api/market/klines?symbol=MBB",
        })
        {
            await AssertErrorAsync(await client.GetAsync(url), HttpStatusCode.BadRequest, "UNSUPPORTED_SYMBOL");
        }
    }

    // --- auth ---------------------------------------------------------------------

    [Fact]
    public async Task PersonalReads_RequireAdminKey_OnBothRouteFamilies()
    {
        var client = _factory.CreateClient();
        foreach (var url in new[]
        {
            "/api/equities/BID/position", "/api/equities/BID/decision",
            "/api/equities/MBB/position", "/api/equities/MBB/decision",
            "/api/mbb/position", "/api/mbb/decision",
            "/api/mbb/observations?timeframe=1d", "/api/equities/MBB/observations?timeframe=1d",
            "/api/equities/BID/observations?timeframe=1d",
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
            var wrong = new HttpRequestMessage(HttpMethod.Get, url);
            wrong.Headers.Add("X-Admin-Key", "wrong-key");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync(url)).StatusCode);
        }
        var put = await client.PutAsJsonAsync("/api/equities/BID/position",
            new { lots = Array.Empty<object>(), availableCashVnd = 0 });
        Assert.True(put.StatusCode == HttpStatusCode.Unauthorized, await put.Content.ReadAsStringAsync());
        foreach (var url in new[]
        {
            "/api/equities/BID/admin/sync", "/api/equities/BID/admin/backfill?timeframe=1d",
            "/api/equities/BID/admin/recompute", "/api/equities/BID/admin/observe?timeframe=1d",
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(url, null)).StatusCode);
        }
    }

    [Fact]
    public async Task Position_IsPerSymbol_AndDecisionCarriesSymbol()
    {
        var admin = Admin();
        var put = await admin.PutAsJsonAsync("/api/equities/BID/position", new
        {
            lots = new[] { new { quantity = 300, costPerShareVnd = 41_000m, acquireDate = "2026-01-05" } },
            availableCashVnd = 2_000_000m,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var bid = await JsonAsync(await admin.GetAsync("/api/equities/BID/position"));
        Assert.Equal("BID", bid.GetProperty("symbol").GetString());
        Assert.Equal(300, bid.GetProperty("totalShares").GetInt32());

        // The alias PUT goes through the same body binding (regression: record DTO with
        // property-level [Required] made every PUT a 500 before AdminGuard ran).
        var aliasPut = await admin.PutAsJsonAsync("/api/mbb/position", new
        {
            lots = new[] { new { quantity = 100, costPerShareVnd = 20_000m, acquireDate = "2026-01-05" } },
            availableCashVnd = 0m,
        });
        Assert.Equal(HttpStatusCode.OK, aliasPut.StatusCode);
        var mbb = await JsonAsync(await admin.GetAsync("/api/mbb/position"));
        Assert.Equal(100, mbb.GetProperty("totalShares").GetInt32());
        var badBody = await admin.PutAsJsonAsync("/api/equities/BID/position", new
        {
            lots = new[] { new { quantity = -5, costPerShareVnd = 20_000m, acquireDate = "2026-01-05" } },
            availableCashVnd = 0m,
        });
        await AssertErrorAsync(badBody, HttpStatusCode.BadRequest, "INVALID_POSITION_INPUT");
        Assert.Equal("MBB", mbb.GetProperty("symbol").GetString());
        Assert.NotEqual(300, mbb.GetProperty("totalShares").GetInt32());

        var decision = await JsonAsync(await admin.GetAsync("/api/equities/BID/decision"));
        Assert.Equal("BID", decision.GetProperty("symbol").GetString());
        // Daily-only: freshness never lists a 1h block for BID.
        Assert.All(decision.GetProperty("dataFreshness").EnumerateArray(),
            f => Assert.Equal("1d", f.GetProperty("timeframe").GetString()));
    }

    // --- alias parity ------------------------------------------------------------

    [Fact]
    public async Task MbbAlias_MatchesEquitiesRoutes()
    {
        var b = BaseMs;
        await SeedAsync(db =>
        {
            for (var i = 0; i < 3; i++) db.InstrumentCandles.Add(Candle("MBB", b + DayMs * i));
            db.InstrumentCandles.Add(Candle("BID", b + DayMs, 40_000m));
            db.MbbSignalObservations.Add(new MbbSignalObservation
            {
                Id = Guid.NewGuid(), DecisionId = $"mbb-observe/1d/{b}",
                RecorderVersion = MbbSignalObservationService.LegacyRecorderVersion,
                Symbol = "MBB", Timeframe = "1d",
                SignalBarOpenTimeMs = b, SignalBarCloseTimeMs = b + 27_900_000L,
                ObservedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(b + 28_000_000L),
                AvailableTimeMs = b + 28_000_000L, Decision = "wait", PriceBasis = "adjusted",
                DecisionPayloadJson = "{}", CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(b + 28_000_000L),
            });
        });
        var client = _factory.CreateClient();

        var end = b + DayMs * 2;
        var alias = await (await client.GetAsync($"/api/mbb/candles?timeframe=1d&limit=10&endTimeMs={end}")).Content.ReadAsStringAsync();
        var equity = await (await client.GetAsync($"/api/equities/MBB/candles?timeframe=1d&limit=10&endTimeMs={end}")).Content.ReadAsStringAsync();
        Assert.Equal(alias, equity);
        Assert.Equal(3, JsonDocument.Parse(equity).RootElement.GetArrayLength());

        var aliasObs = await (await Admin().GetAsync("/api/mbb/observations?timeframe=1d")).Content.ReadAsStringAsync();
        var equityObs = await (await Admin().GetAsync("/api/equities/MBB/observations?timeframe=1d")).Content.ReadAsStringAsync();
        Assert.Equal(aliasObs, equityObs);
        var legacy = JsonDocument.Parse(equityObs).RootElement.EnumerateArray()
            .Single(o => o.GetProperty("decisionId").GetString() == $"mbb-observe/1d/{b}");
        Assert.Equal("MBB", legacy.GetProperty("symbol").GetString());

        var aliasDq = await JsonAsync(await client.GetAsync("/api/mbb/data-quality"));
        var equityDq = await JsonAsync(await client.GetAsync("/api/equities/MBB/data-quality"));
        foreach (var field in new[] { "symbol", "provider", "crossCheckProvider", "priceBasis", "venue", "sourceTimezone" })
            Assert.Equal(aliasDq.GetProperty(field).GetString(), equityDq.GetProperty(field).GetString());
        Assert.Equal(aliasDq.GetProperty("limitations").GetRawText(), equityDq.GetProperty("limitations").GetRawText());
        Assert.Equal(
            aliasDq.GetProperty("timeframes").EnumerateArray().Select(t => t.GetProperty("totalBars").GetInt64()),
            equityDq.GetProperty("timeframes").EnumerateArray().Select(t => t.GetProperty("totalBars").GetInt64()));

        var bidDq = await JsonAsync(await client.GetAsync("/api/equities/BID/data-quality"));
        Assert.Equal("BID", bidDq.GetProperty("symbol").GetString());
        Assert.Equal(1, bidDq.GetProperty("timeframes").GetArrayLength());
    }

    [Fact]
    public async Task List_ReturnsEquityAndIndexInstruments_WithStatus()
    {
        var root = await JsonAsync(await _factory.CreateClient().GetAsync("/api/equities"));
        Assert.Equal("equity-instruments-v1", root.GetProperty("contractVersion").GetString());
        Assert.False(root.GetProperty("workerActive").GetBoolean());
        var items = root.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("symbol").GetString()!);
        Assert.Equal(new[] { "BID", "CTG", "MBB", "VCB", "VNINDEX" }, items.Keys.Order().ToArray());

        var vnindex = items["VNINDEX"];
        Assert.Equal("index", vnindex.GetProperty("assetClass").GetString());
        Assert.False(vnindex.GetProperty("capabilities").GetProperty("decision").GetBoolean());
        Assert.False(vnindex.GetProperty("capabilities").GetProperty("technicalAnalysis").GetBoolean());
        Assert.True(items["BID"].GetProperty("capabilities").GetProperty("decision").GetBoolean());
        Assert.Equal(1, items["BID"].GetProperty("ingestion").GetProperty("timeframes").GetArrayLength());
        Assert.Equal(2, items["MBB"].GetProperty("ingestion").GetProperty("timeframes").GetArrayLength());
        Assert.All(items.Values, i => Assert.Equal("disabled", i.GetProperty("ingestion").GetProperty("status").GetString()));
    }

    // --- lane lock ----------------------------------------------------------------

    [Fact]
    public async Task AdminOps_WhileLaneBusy_Return409_LaneBusy_OnBothFamilies()
    {
        var admin = Admin();
        using (var lease = EquityLaneLocks.TryAcquire("CTG"))
        {
            Assert.NotNull(lease);
            var busy = await admin.PostAsync("/api/equities/CTG/admin/sync", null);
            await AssertErrorAsync(busy, HttpStatusCode.Conflict, "LANE_BUSY");
            Assert.True((await JsonAsync(busy)).GetProperty("retryable").GetBoolean());
            // Another symbol's lane is unaffected.
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/equities/VCB/admin/sync", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/equities/CTG/admin/sync", null)).StatusCode);

        using (var lease = EquityLaneLocks.TryAcquire("MBB"))
        {
            Assert.NotNull(lease);
            await AssertErrorAsync(await admin.PostAsync("/api/mbb/admin/sync", null), HttpStatusCode.Conflict, "LANE_BUSY");
            await AssertErrorAsync(await admin.PostAsync("/api/mbb/admin/observe?timeframe=1d", null), HttpStatusCode.Conflict, "LANE_BUSY");
            await AssertErrorAsync(await admin.PostAsync("/api/equities/MBB/admin/recompute", null), HttpStatusCode.Conflict, "LANE_BUSY");
        }
    }

    [Fact]
    public async Task ResearchRoutes_AreWired_WithCapabilityErrors()
    {
        var client = _factory.CreateClient();
        var baseRates = await JsonAsync(await client.GetAsync("/api/equities/BID/base-rates"));
        Assert.Equal("equity-base-rates-v1", baseRates.GetProperty("contractVersion").GetString());
        // The shared evidence root may hold a real artifact built from another DB: it must then be
        // stale against this test DB (abstained), otherwise missing (unavailable) — never "ok".
        Assert.Contains(baseRates.GetProperty("status").GetString(), new[] { "unavailable", "abstained" });
        Assert.All(baseRates.GetProperty("configs").EnumerateArray(),
            c => Assert.Equal(JsonValueKind.Null, c.GetProperty("decisionUse").ValueKind));
        var context = await JsonAsync(await client.GetAsync("/api/equities/MBB/context"));
        Assert.Equal("equity-context-v1", context.GetProperty("contractVersion").GetString());
        Assert.Equal(4, context.GetProperty("relativeStrength").GetProperty("benchmarks").GetArrayLength());
        var journal = await JsonAsync(await client.GetAsync("/api/equities/BID/forecast-journal?configId=tb-p5-s4-h20"));
        Assert.Equal("equity-forecast-journal-v1", journal.GetProperty("contractVersion").GetString());
        Assert.Equal("no-data", journal.GetProperty("scoring")[0].GetProperty("status").GetString());

        await AssertErrorAsync(await client.GetAsync("/api/equities/VCB/base-rates"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await client.GetAsync("/api/equities/VNINDEX/context"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await client.GetAsync("/api/equities/NOPE/forecast-journal"), HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");

        foreach (var url in new[]
        {
            "/api/equities/BID/admin/base-rates/rebuild", "/api/equities/BID/admin/context/sync",
            "/api/equities/BID/admin/corporate-actions", "/api/equities/BID/admin/events", "/api/equities/BID/admin/fundamentals",
            "/api/equities/BID/admin/forecasts/record",
        })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, Array.Empty<object>())).StatusCode);

        var admin = Admin();
        await AssertErrorAsync(await admin.PostAsync("/api/equities/VNINDEX/admin/context/sync", null),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await admin.PostAsync("/api/equities/VCB/admin/base-rates/rebuild", null),
            HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await admin.PostAsJsonAsync("/api/equities/VCB/admin/fundamentals",
                new[] { new { period = "2026Q2", metric = "NPL", unit = "percent", source = "x", sourceUrl = "https://x" } }),
            HttpStatusCode.BadRequest, "INVALID_CONTEXT_INPUT");
        var imported = await admin.PostAsJsonAsync("/api/equities/VCB/admin/events",
            new[] { new { eventType = "earnings", eventDate = "2026-10-30", title = "Q3 results", sourceUrl = "https://vietcombank.com.vn/ir" } });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(1, (await JsonAsync(imported)).GetProperty("inserted").GetInt32());
    }

    [Fact]
    public async Task DossierAndLlmRuns_AreWired_GuardedAndLocked()
    {
        var client = _factory.CreateClient();
        var dossier = await client.GetAsync("/api/equities/BID/dossier");
        Assert.Equal(HttpStatusCode.OK, dossier.StatusCode);
        var root = await JsonAsync(dossier);
        Assert.Equal("equity-dossier-v1", root.GetProperty("contractVersion").GetString());
        Assert.False(root.TryGetProperty("position", out _));
        var list = await JsonAsync(await client.GetAsync("/api/equities/BID/llm-runs?take=500"));
        Assert.Equal(JsonValueKind.Array, list.GetProperty("items").ValueKind);
        await AssertErrorAsync(await client.GetAsync("/api/equities/VCB/dossier"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/equities/BID/llm-runs", null)).StatusCode);
        // EQX v2 B: EquityLlm:Enabled defaults to false ⇒ 503 LLM_DISABLED before scope/lock/upstream.
        // The legacy scope/lock behaviour with the lane enabled is covered in EquityLlmDisabledTests.
        var admin = Admin();
        await AssertErrorAsync(await admin.PostAsync("/api/equities/BID/llm-runs", null), HttpStatusCode.ServiceUnavailable, "LLM_DISABLED");
        await AssertErrorAsync(await admin.PostAsync("/api/equities/VCB/llm-runs", null), HttpStatusCode.ServiceUnavailable, "LLM_DISABLED");
        using (var lease = EquityLaneLocks.TryAcquire("BID"))
        {
            Assert.NotNull(lease);
            await AssertErrorAsync(await admin.PostAsync("/api/equities/BID/llm-runs", null), HttpStatusCode.ServiceUnavailable, "LLM_DISABLED");
        }
    }

    [Fact]
    public async Task AdminSync_IngestionDisabled_ReportsDisabledPerSymbol()
    {
        var root = await JsonAsync(await Admin().PostAsync("/api/equities/VNINDEX/admin/sync", null));
        Assert.Equal("VNINDEX", root.GetProperty("symbol").GetString());
        Assert.Equal("Disabled", root.GetProperty("status").GetString());
    }
}

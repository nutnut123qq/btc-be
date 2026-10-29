using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace Backend.Tests;

/// <summary>
/// EQTECH-1 lane T: GET /api/equities/{symbol}/technical (equity-technical-v1). HTTP-level through
/// TestWebApplicationFactory with a per-test EF InMemory database, a fixed clock, the real HOSE calendar file, a fake AI
/// current-conditions service and a temp base-rates artifact root. Each test owns its host (the "expensive" rate
/// limiter allows 10 calls per minute per host). Parsed with Web defaults (camelCase, AGENTS #27). No network, no
/// PostgreSQL.
/// </summary>
public class EquityTechnicalEndpointTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 14); // Wednesday
    private static readonly DateOnly Tomorrow = new(2026, 10, 15);
    private static readonly DateTimeOffset AfterClose = new(2026, 10, 14, 16, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset MidAfternoon = new(2026, 10, 14, 14, 30, 0, TimeSpan.FromHours(7));
    private static readonly DateTime Updated = new(2026, 10, 14, 8, 30, 15, 123, DateTimeKind.Utc);
    private const string Sha64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>1h trading-day indices (of the 30 seeded days) whose 09:00 bar is left out: days[-25], days[-18], days[-5].</summary>
    private static readonly int[] Missing0900DayIndices = [5, 12, 25];

    private static readonly string[] ForbiddenKeys =
    [
        "fundamentals", "fundamentalsSeries", "fundamentalsCoverage", "events", "corporateActions",
        "sourceDocuments", "documents", "news", "context",
    ];

    private static readonly string[] PrivateKeys =
        ["position", "lots", "totalShares", "availableCashVnd", "sharedCashVnd", "buyBudgetVnd", "costPerShareVnd"];

    private readonly ITestOutputHelper _output = output;
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    // --- host -------------------------------------------------------------------------------------------------

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class EmptyEvidence : IResearchEvidenceCatalog
    {
        public ResearchEvidenceCatalogResponse GetCatalog() => new("btc-research-evidence-catalog-v1", "MBB",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), [], new EvidenceIntegritySummaryDto(0, 0, 0), null);

        public ResearchEvidenceDetailDto? GetDetail(string id) => null;
    }

    /// <summary>Fake AI state: latest finalized (open, close) per symbol:timeframe, and per-key failure modes.</summary>
    private sealed class AiState
    {
        public Dictionary<string, (long Open, long Close)> Latest { get; } = new(StringComparer.Ordinal);

        /// <summary>symbol:timeframe → "down" (503) or "invalid" (schema violation).</summary>
        public Dictionary<string, string> Failures { get; } = new(StringComparer.Ordinal);

        public int Calls;
    }

    /// <summary>
    /// Valid current-conditions-v1 for the latest seeded finalized bar. Like the real AI module, every condition carries
    /// a technical <c>context</c> object ({trend, volatility}).
    /// </summary>
    private sealed class FakeAi(AiState state) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = QueryHelpers.ParseQuery(uri.Query);
            var symbol = query.TryGetValue("symbol", out var s) ? s.ToString() : "";
            var timeframe = query.TryGetValue("timeframe", out var t) ? t.ToString() : "";
            var key = $"{symbol}:{timeframe}";
            if (uri.AbsolutePath != "/api/current-conditions"
                || state.Failures.GetValueOrDefault(key) == "down"
                || !state.Latest.TryGetValue(key, out var bar))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
            var calls = Interlocked.Increment(ref state.Calls);
            var payload = new JsonObject
            {
                ["schemaVersion"] = state.Failures.GetValueOrDefault(key) == "invalid" ? "current-conditions-v0" : "current-conditions-v1",
                ["symbol"] = symbol,
                ["timeframe"] = timeframe,
                ["asOfMs"] = bar.Close,
                ["generatedAtMs"] = 1_000_000 + calls,
                ["latestClosedBar"] = new JsonObject
                {
                    ["openTimeMs"] = bar.Open, ["closeTimeMs"] = bar.Close,
                    ["open"] = 1, ["high"] = 1, ["low"] = 1, ["close"] = 1, ["volume"] = 1,
                },
                ["analysisWindow"] = new JsonObject
                    { ["firstOpenTimeMs"] = bar.Open, ["bars"] = 1, ["contiguous"] = true, ["windowBars"] = 2000 },
                ["contract"] = new JsonObject
                    { ["contractVersion"] = "c1", ["definitionsSha256"] = "d", ["rawFileSha256"] = "r" },
                ["conditions"] = new JsonArray(new JsonObject
                {
                    ["module"] = "candlePatterns", ["eventType"] = "DOJI", ["kind"] = "triggeredOnBar",
                    ["direction"] = "neutral", ["eventId"] = "candlePatterns-e1",
                    ["formedTimeMs"] = bar.Close, ["availableTimeMs"] = bar.Close,
                    ["context"] = new JsonObject { ["trend"] = "sideways", ["volatility"] = "normal" },
                    ["details"] = new JsonObject { ["sourceCandleOpenTimesMs"] = new JsonArray(bar.Open) },
                }),
                ["warnings"] = new JsonArray(),
                ["unavailableModules"] = new JsonArray(),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload.ToJsonString()) });
        }
    }

    private sealed class Factory(DateTimeOffset now, string root) : TestWebApplicationFactory
    {
        private readonly string _database = "eqtech-t-" + Guid.NewGuid().ToString("N");
        public AiState Ai { get; } = new();
        public DateTimeOffset Now { get; } = now;
        public string Root { get; } = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["EvidenceCatalog:RootPath"] = Root }));
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList())
                    services.Remove(d);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_database));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock(Now));
                services.RemoveAll<IResearchEvidenceCatalog>();
                services.AddSingleton<IResearchEvidenceCatalog>(new EmptyEvidence());
                services.AddHttpClient("AIService").ConfigurePrimaryHttpMessageHandler(() => new FakeAi(Ai));
            });
        }
    }

    // --- fixture ----------------------------------------------------------------------------------------------

    private sealed record Fixture(
        Factory Factory,
        VietnamMarketCalendar Calendar,
        long[] Mbb1d,
        long[] Mbb1h,
        long[] Bid1d,
        long Future1d,
        long Future1h,
        long[] Missing1h);

    private static InstrumentCandle Candle(VietnamMarketCalendar cal, string symbol, string tf, long open, decimal px) => new()
    {
        Symbol = symbol, Timeframe = tf, OpenTimeMs = open, CloseTimeMs = cal.SessionCloseUtcMs(tf, open),
        Open = px, High = px + 150m, Low = px - 100m, Close = px + 50m, Volume = 1_000_000m + px,
        Provider = "VCI", PriceBasis = symbol == "VNINDEX" ? "raw" : "adjusted", Venue = "HOSE",
        SourceTimezone = "Asia/Ho_Chi_Minh", FetchedAtUtc = Updated, UpdatedAtUtc = Updated,
    };

    private static decimal Px(int i) => 20_000m + i + (i % 13) * 37m;

    private async Task<Fixture> CreateAsync(DateTimeOffset now, params (string Key, string Mode)[] failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "eqtech-t-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var factory = new Factory(now, root);
        foreach (var (key, mode) in failures) factory.Ai.Failures[key] = mode;
        var cal = factory.Services.GetRequiredService<VietnamMarketCalendar>();
        var nowMs = now.ToUnixTimeMilliseconds();

        DateOnly[] Days(int n) => cal.EnumerateTradingDates(Today.AddDays(-n * 2), Today).TakeLast(n).ToArray();
        var mbbDays = Days(600);
        var bidDays = Days(300);
        var hourDays = Days(30);
        Assert.Equal(Today, mbbDays[^1]);

        var mbb1d = mbbDays.Select(VietnamMarketCalendar.DailyOpenUtcMs).ToArray();
        var bid1d = bidDays.Select(VietnamMarketCalendar.DailyOpenUtcMs).ToArray();
        var mbb1h = new List<long>();
        var missing1h = new List<long>();
        for (var i = 0; i < hourDays.Length; i++)
        {
            foreach (var open in VietnamMarketCalendar.HourlyOpensUtcMs(hourDays[i]))
            {
                if (Missing0900DayIndices.Contains(i) && VietnamMarketCalendar.ToLocal(open).Hour == 9)
                    missing1h.Add(open);
                else
                    mbb1h.Add(open);
            }
        }
        var future1d = VietnamMarketCalendar.DailyOpenUtcMs(Tomorrow);
        var future1h = VietnamMarketCalendar.HourlyOpensUtcMs(Tomorrow)[0];

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < mbb1d.Length; i++)
            {
                db.InstrumentCandles.Add(Candle(cal, "MBB", "1d", mbb1d[i], Px(i)));
                db.InstrumentCandles.Add(Candle(cal, "VNINDEX", "1d", mbb1d[i], 1_200m + i));
            }
            for (var i = 0; i < bid1d.Length; i++)
                db.InstrumentCandles.Add(Candle(cal, "BID", "1d", bid1d[i], 40_000m + Px(i)));
            for (var i = 0; i < mbb1h.Count; i++)
                db.InstrumentCandles.Add(Candle(cal, "MBB", "1h", mbb1h[i], Px(i)));
            // Not finalized at either clock: must never be served.
            db.InstrumentCandles.Add(Candle(cal, "MBB", "1d", future1d, 99_999m));
            db.InstrumentCandles.Add(Candle(cal, "MBB", "1h", future1h, 99_999m));

            db.TechnicalIndicators.Add(Indicator("MBB", "1d", mbb1d[^3], 41.5));
            db.TechnicalIndicators.Add(Indicator("MBB", "1d", mbb1d[^2], 52.5));
            db.TechnicalIndicators.Add(Indicator("MBB", "1d", mbb1d[^1], 61.25));
            db.TechnicalIndicators.Add(Indicator("MBB", "1d", future1d, 99));
            db.TechnicalIndicators.Add(Indicator("MBB", "1h", mbb1h[^2], 47.75));

            db.CandlePatterns.Add(Pattern("MBB", "1d", mbb1d[^3], "Hammer", "Single", "Downtrend"));
            db.CandlePatterns.Add(Pattern("MBB", "1d", mbb1d[^3], "MorningStar", "Triple", "Downtrend"));
            db.CandlePatterns.Add(Pattern("MBB", "1d", mbb1d[^2], "BullishEngulfing", "Double", "Sideways"));
            db.CandlePatterns.Add(Pattern("MBB", "1d", mbb1d[^150], "Doji", "Single", "Sideways"));
            db.CandlePatterns.Add(Pattern("MBB", "1d", future1d, "ShootingStar", "Single", "Uptrend"));
            await db.SaveChangesAsync();
        }

        long LatestFinalized(IEnumerable<long> opens, string tf) =>
            opens.Where(o => cal.IsFinalized(tf, o, nowMs)).Max();
        void Latest(string key, string tf, IEnumerable<long> opens)
        {
            var open = LatestFinalized(opens, tf);
            factory.Ai.Latest[key] = (open, cal.SessionCloseUtcMs(tf, open));
        }
        Latest("MBB:1d", "1d", mbb1d);
        Latest("MBB:1h", "1h", mbb1h);
        Latest("BID:1d", "1d", bid1d);

        await WriteArtifactAsync(root, "BID", bid1d.Where(o => cal.IsFinalized("1d", o, nowMs)).ToArray());
        return new Fixture(factory, cal, mbb1d, mbb1h.ToArray(), bid1d, future1d, future1h, missing1h.ToArray());
    }

    private static TechnicalIndicator Indicator(string symbol, string tf, long open, double rsi) => new()
    {
        Symbol = symbol, Timeframe = tf, OpenTimeMs = open, Rsi14 = rsi,
        Ema12 = 21_012.5m, Ema26 = 21_001.25m, Ema50 = 20_950m, Ema200 = 20_500m, Sma50 = 20_940m, Sma200 = 20_480m,
        Macd = 11.25, MacdSignal = 9.5, MacdHistogram = 1.75, MacdNorm = 0.051, MacdSignalNorm = 0.043,
        MacdHistogramNorm = 0.008, BollingerUpper = 21_300m, BollingerMiddle = 21_000m, BollingerLower = 20_700m,
        Atr14 = 220.5, Obv = 123_456_789, ObvEma50 = 120_000_000, Vwap = 21_033.33m, RollingVwap24 = 20_990.5m,
    };

    private static CandlePattern Pattern(string symbol, string tf, long open, string type, string category, string trend) => new()
    {
        Symbol = symbol, Timeframe = tf, OpenTimeMs = open, Open = 1, High = 1, Low = 1, Close = 1, Volume = 1,
        PatternType = type, PatternCategory = category, TrendDirection = trend, CreatedAtUtc = Updated,
    };

    /// <summary>Gate-not-passed artifact (same shape as EquityResearchSnapshotTests), fresh for the latest bar.</summary>
    private static async Task WriteArtifactAsync(string root, string symbol, long[] finalizedOpens)
    {
        var dataAsOf = finalizedOpens[^1];
        JsonObject Config(string id, double t, double s, int h) => new()
        {
            ["configId"] = id, ["targetPct"] = t, ["stopPct"] = s, ["horizonSessions"] = h,
            ["validation"] = new JsonObject
            {
                ["gatePassed"] = false, ["gateReasons"] = new JsonArray("ci90-upper-not-below-zero"),
                ["nOos"] = 900, ["nEffective"] = 40,
            },
            ["current"] = new JsonObject
            {
                ["signalBarOpenTimeMs"] = dataAsOf, ["probTargetFirst"] = 0.3, ["probStopFirst"] = 0.4, ["probTimeout"] = 0.3,
                ["evNetFraction"] = -0.01, ["k"] = 50, ["nEligible"] = 600, ["abstained"] = false,
                ["prior"] = new JsonObject { ["probTargetFirst"] = 0.33, ["probStopFirst"] = 0.4, ["probTimeout"] = 0.27 },
            },
        };
        var artifact = new JsonObject
        {
            ["schemaVersion"] = "equity-base-rates-artifact-v1", ["symbol"] = symbol, ["timeframe"] = "1d",
            ["generatedAtUtc"] = "2026-10-14T10:00:00.000000Z", ["dataAsOfOpenTimeMs"] = dataAsOf, ["priceBasis"] = "adjusted",
            ["provider"] = "VCI",
            ["dataset"] = new JsonObject
            {
                ["barCount"] = finalizedOpens.Length, ["firstOpenTimeMs"] = finalizedOpens[0], ["lastOpenTimeMs"] = dataAsOf,
                ["maxUpdatedAtUtc"] = EquityBaseRatesService.FormatFingerprintTimestamp(Updated), ["sha256"] = Sha64,
            },
            ["method"] = new JsonObject { ["version"] = "equity-knn-v1" },
            ["protocol"] = new JsonObject { ["version"] = "equity-base-rates-protocol-v1" },
            ["configs"] = new JsonArray(Config("tb-p5-s4-h20", 0.05, 0.04, 20), Config("tb-p10-s6-h60", 0.10, 0.06, 60)),
        };
        var path = Path.Combine(root, "equity-base-rates", symbol, "base-rates.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, artifact.ToJsonString());
    }

    // --- helpers ----------------------------------------------------------------------------------------------

    private static async Task<(JsonElement Root, string Body)> OkJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
        return (JsonDocument.Parse(body).RootElement, body);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode} {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static JsonElement Tf(JsonElement root, string timeframe) =>
        root.GetProperty("timeframes").EnumerateArray().Single(t => t.GetProperty("timeframe").GetString() == timeframe);

    private static long[] CandleOpens(JsonElement entry) =>
        entry.GetProperty("candles").EnumerateArray().Select(c => c.GetProperty("openTimeMs").GetInt64()).ToArray();

    private static IEnumerable<(string Path, string Key)> Keys(JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                yield return (path, p.Name);
                foreach (var x in Keys(p.Value, $"{path}/{p.Name}")) yield return x;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in e.EnumerateArray())
            {
                foreach (var x in Keys(item, $"{path}[{i}]")) yield return x;
                i++;
            }
        }
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static double? WilderRsi(IReadOnlyList<double> p, int n)
    {
        if (p.Count <= n) return null;
        double g = 0, l = 0;
        for (var i = 1; i <= n; i++) { var d = p[i] - p[i - 1]; g += Math.Max(d, 0); l += Math.Max(-d, 0); }
        g /= n; l /= n;
        for (var i = n + 1; i < p.Count; i++)
        {
            var d = p[i] - p[i - 1];
            g = (g * (n - 1) + Math.Max(d, 0)) / n;
            l = (l * (n - 1) + Math.Max(-d, 0)) / n;
        }
        return l == 0 ? 100 : 100 - 100 / (1 + g / l);
    }

    private static double CutlerRsi(IReadOnlyList<double> p, int n)
    {
        double g = 0, l = 0;
        for (var i = p.Count - n; i < p.Count; i++) { var d = p[i] - p[i - 1]; g += Math.Max(d, 0); l += Math.Max(-d, 0); }
        return l == 0 ? 100 : 100 - 100 / (1 + (g / n) / (l / n));
    }

    // --- (a) scope + catalog timeframes, (b) forbidden keys, (c) finalized ascending candles, indicators, patterns ----

    [Fact]
    public async Task Mbb_ServesCatalogTimeframes_TechnicalOnly_FinalizedCandles_IndicatorsPatternsAndQuality()
    {
        var fx = await CreateAsync(AfterClose);
        await using var factory = fx.Factory;
        var client = factory.CreateClient();

        var (root, body) = await OkJsonAsync(await client.GetAsync("/api/equities/mbb/technical"));
        _output.WriteLine($"MBB technical payload on the fixture (bars=100, 1h+1d): {Encoding.UTF8.GetByteCount(body)} bytes");

        Assert.Equal("equity-technical-v1", root.GetProperty("contractVersion").GetString());
        Assert.Equal("MBB", root.GetProperty("symbol").GetString());
        Assert.Equal(AfterClose.UtcDateTime, root.GetProperty("asOfUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(("adjusted", "VCI"), (root.GetProperty("priceBasis").GetString(), root.GetProperty("dataProvider").GetString()));
        Assert.Equal((100, 100), (root.GetProperty("barsRequested").GetInt32(), root.GetProperty("barsApplied").GetInt32()));

        // (a) one entry per catalog ActiveTimeframe, in catalog order (never a hard-coded list).
        var catalogMbb = InstrumentCatalog.Default.Get("MBB")!.ActiveTimeframes;
        Assert.Equal(catalogMbb, root.GetProperty("timeframes").EnumerateArray().Select(t => t.GetProperty("timeframe").GetString()!).ToArray());

        // (b) technical-only: no fundamentals/events/documents/news/context key anywhere outside the AI conditions
        // payload; inside it only the module's own conditions[].context ({trend, volatility}) exists.
        var keys = Keys(root, "$").ToList();
        var payloadPath = new Regex(@"^\$/timeframes\[\d+\]/conditions/payload(?:[/\[].*)?$", RegexOptions.CultureInvariant);
        var outside = keys.Where(k => !payloadPath.IsMatch(k.Path)).ToList();
        Assert.Empty(outside.Where(k => ForbiddenKeys.Contains(k.Key)));
        Assert.Empty(keys.Where(k => PrivateKeys.Contains(k.Key)));
        var inside = keys.Where(k => payloadPath.IsMatch(k.Path) && ForbiddenKeys.Contains(k.Key)).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, k =>
        {
            Assert.Equal("context", k.Key);
            Assert.Matches(@"^\$/timeframes\[\d+\]/conditions/payload/conditions\[\d+\]$", k.Path);
        });

        // (c) 1d: the last 100 stored finalized bars, ascending; the stored-but-unfinalized next-session bar is excluded.
        var d1 = Tf(root, "1d");
        Assert.Equal(fx.Mbb1d.TakeLast(100).ToArray(), CandleOpens(d1));
        Assert.DoesNotContain(fx.Future1d, CandleOpens(d1));
        var lastCandle = d1.GetProperty("candles").EnumerateArray().Last();
        Assert.Equal(Px(599) + 50m, lastCandle.GetProperty("close").GetDecimal());
        Assert.Equal(fx.Calendar.SessionCloseUtcMs("1d", fx.Mbb1d[^1]), lastCandle.GetProperty("closeTimeMs").GetInt64());
        var fresh = d1.GetProperty("freshness");
        Assert.Equal(fx.Mbb1d[^1], fresh.GetProperty("latestStoredOpenTimeMs").GetInt64());
        Assert.Equal(fx.Mbb1d[^1], fresh.GetProperty("expectedLatestOpenTimeMs").GetInt64());
        Assert.False(fresh.GetProperty("isStale").GetBoolean());
        Assert.Equal("data-quality", fresh.GetProperty("staleRule").GetString());

        var h1 = Tf(root, "1h");
        var h1Opens = CandleOpens(h1);
        Assert.Equal(fx.Mbb1h.TakeLast(100).ToArray(), h1Opens);
        Assert.True(h1Opens.Zip(h1Opens.Skip(1)).All(p => p.First < p.Second));
        Assert.Equal(VietnamMarketCalendar.HourlyOpensUtcMs(Today)[^1], h1Opens[^1]);
        Assert.DoesNotContain(fx.Future1h, h1Opens);

        // Indicators: the latest stored row at or before the newest candle, every entity value + its formula.
        var ind = d1.GetProperty("indicators");
        Assert.Equal(fx.Mbb1d[^1], ind.GetProperty("openTimeMs").GetInt64());
        Assert.True(ind.GetProperty("isLatestCandle").GetBoolean());
        Assert.Equal(61.25, ind.GetProperty("rsi14").GetDouble());
        Assert.Equal(21_033.33m, ind.GetProperty("vwap").GetDecimal());
        var methods = ind.GetProperty("methods");
        foreach (var prop in typeof(TechnicalIndicator).GetProperties())
        {
            if (prop.Name is "Id" or "Symbol" or "Timeframe") continue;
            var name = Camel(prop.Name);
            Assert.True(ind.TryGetProperty(name, out var value), $"indicators.{name} missing");
            Assert.NotEqual(JsonValueKind.Null, value.ValueKind);
            if (name != "openTimeMs")
                Assert.False(string.IsNullOrWhiteSpace(methods.GetProperty(name).GetString()), $"methods.{name} missing");
        }
        Assert.Contains("Wilder", methods.GetProperty("rsi14").GetString());
        Assert.Contains("Cutler", methods.GetProperty("rsi14").GetString());
        Assert.Contains("Wilder", methods.GetProperty("atr14").GetString());
        // The label describes the stored implementation: the indexer's RSI is Wilder's, not the simple average.
        double[] closes = [100, 102, 101, 104, 103, 105, 107, 106, 108, 110, 109, 111, 113, 112, 114, 113, 115, 111, 112];
        var stored = TechnicalIndicatorIndexer.ComputeRsi(closes.Select(c => (decimal)c).ToList(), 14)[^1]!.Value;
        Assert.Equal(WilderRsi(closes, 14)!.Value, stored, 9);
        Assert.True(Math.Abs(CutlerRsi(closes, 14) - stored) > 1e-3);

        var hInd = h1.GetProperty("indicators");
        Assert.Equal(fx.Mbb1h[^2], hInd.GetProperty("openTimeMs").GetInt64());
        Assert.False(hInd.GetProperty("isLatestCandle").GetBoolean());

        // Candle patterns whose first bar lies in the candle window (Doji at -150 and the unfinalized bar are outside).
        var patterns = d1.GetProperty("candlePatterns").EnumerateArray().Select(p => (
            p.GetProperty("openTimeMs").GetInt64(), p.GetProperty("patternType").GetString(),
            p.GetProperty("spanBars").GetInt32(),
            p.GetProperty("lastBarOpenTimeMs").ValueKind == JsonValueKind.Null ? (long?)null : p.GetProperty("lastBarOpenTimeMs").GetInt64(),
            p.GetProperty("trendDirection").GetString())).ToArray();
        Assert.Equal(new (long, string?, int, long?, string?)[]
        {
            (fx.Mbb1d[^3], "Hammer", 1, fx.Mbb1d[^3], "Downtrend"),
            (fx.Mbb1d[^3], "MorningStar", 3, fx.Mbb1d[^1], "Downtrend"),
            (fx.Mbb1d[^2], "BullishEngulfing", 2, fx.Mbb1d[^1], "Sideways"),
        }, patterns);

        // Conditions per timeframe, aligned with the newest candle.
        foreach (var (entry, tf) in new[] { (d1, "1d"), (h1, "1h") })
        {
            var cond = entry.GetProperty("conditions");
            Assert.Equal("ok", cond.GetProperty("status").GetString());
            Assert.True(cond.GetProperty("alignedWithLatestCandle").GetBoolean());
            Assert.Equal(tf, cond.GetProperty("payload").GetProperty("timeframe").GetString());
        }

        // Data quality: 1h shows the three missing 09:00 bars (two of them inside the 100-bar window).
        var hq = h1.GetProperty("dataQuality");
        Assert.Equal("ok", hq.GetProperty("status").GetString());
        var inCov = hq.GetProperty("inCoverage");
        Assert.Equal(3, inCov.GetProperty("missingBars").GetInt64());
        Assert.Equal(3, inCov.GetProperty("missingByLocalOpen").GetProperty("09:00").GetInt64());
        Assert.Single(inCov.GetProperty("missingByLocalOpen").EnumerateObject());
        Assert.Equal(fx.Missing1h.Reverse().ToArray(),
            inCov.GetProperty("latestMissingOpenTimeMs").EnumerateArray().Select(x => x.GetInt64()).ToArray());
        Assert.Equal(2, inCov.GetProperty("missingInCandleWindow").GetInt64());
        Assert.True(hq.GetProperty("missingBars").GetInt64() >= 3);
        var hCodes = hq.GetProperty("caveats").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToArray();
        Assert.Contains("session-open-1h-bars-missing", hCodes);
        Assert.Contains("missing-bars-in-candle-window", hCodes);
        Assert.Contains("indicators-not-latest", hCodes);
        var dq = d1.GetProperty("dataQuality");
        Assert.Equal(0, dq.GetProperty("inCoverage").GetProperty("missingBars").GetInt64());
        Assert.Equal(JsonValueKind.Null, dq.GetProperty("inCoverage").GetProperty("missingByLocalOpen").ValueKind);

        Assert.Equal("ok", root.GetProperty("relativeStrength").GetProperty("status").GetString());
        Assert.Contains(root.GetProperty("relativeStrength").GetProperty("benchmarks").EnumerateArray(),
            b => b.GetProperty("symbol").GetString() == "VNINDEX" && b.GetProperty("status").GetString() == "ok");
        Assert.NotEmpty(root.GetProperty("limitations").EnumerateArray());

        // BID: only its catalog timeframes; no indicator rows seeded ⇒ honest reason, not an error.
        var (bid, _) = await OkJsonAsync(await client.GetAsync("/api/equities/BID/technical"));
        Assert.Equal(InstrumentCatalog.Default.Get("BID")!.ActiveTimeframes,
            bid.GetProperty("timeframes").EnumerateArray().Select(t => t.GetProperty("timeframe").GetString()!).ToArray());
        var bid1d = Tf(bid, "1d");
        Assert.Equal(fx.Bid1d.TakeLast(100).ToArray(), CandleOpens(bid1d));
        Assert.Equal(JsonValueKind.Null, bid1d.GetProperty("indicators").ValueKind);
        Assert.Equal("no-indicator-row", bid1d.GetProperty("indicatorsUnavailableReason").GetString());

        // Scope errors exactly like the existing equity lane.
        await AssertErrorAsync(await client.GetAsync("/api/equities/VCB/technical"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await client.GetAsync("/api/equities/VNINDEX/technical"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await client.GetAsync("/api/equities/BTCUSDT/technical"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await client.GetAsync("/api/equities/NOPE/technical"), HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");
    }

    // --- (c) bars clamp + finalized-only while a session is still open ----------------------------------------

    [Fact]
    public async Task Bars_AreClampedTo1And500_AndAFormingSessionBarIsNeverServed()
    {
        // 14:30 local: today's 1d bar and today's 14:00 1h bar are stored but still forming.
        var fx = await CreateAsync(MidAfternoon);
        await using var factory = fx.Factory;
        var client = factory.CreateClient();
        var yesterday = fx.Mbb1d[^2];
        var today1300 = VietnamMarketCalendar.HourlyOpensUtcMs(Today)[3];

        foreach (var raw in new[] { "0", "-7" })
        {
            var (root, _) = await OkJsonAsync(await client.GetAsync($"/api/equities/MBB/technical?bars={raw}"));
            Assert.Equal(int.Parse(raw, CultureInfo.InvariantCulture), root.GetProperty("barsRequested").GetInt32());
            Assert.Equal(1, root.GetProperty("barsApplied").GetInt32());
            Assert.Equal(new[] { yesterday }, CandleOpens(Tf(root, "1d")));
            Assert.Equal(new[] { today1300 }, CandleOpens(Tf(root, "1h")));
            Assert.Equal(yesterday, Tf(root, "1d").GetProperty("indicators").GetProperty("openTimeMs").GetInt64());
        }

        var (big, body) = await OkJsonAsync(await client.GetAsync("/api/equities/MBB/technical?bars=9999"));
        _output.WriteLine($"MBB technical payload on the fixture (bars=500): {Encoding.UTF8.GetByteCount(body)} bytes");
        Assert.Equal((9999, 500), (big.GetProperty("barsRequested").GetInt32(), big.GetProperty("barsApplied").GetInt32()));
        var d1 = CandleOpens(Tf(big, "1d"));
        Assert.Equal(fx.Mbb1d[..^1].TakeLast(500).ToArray(), d1);
        var h1 = CandleOpens(Tf(big, "1h"));
        Assert.Equal(fx.Mbb1h.Where(o => o <= today1300).ToArray(), h1); // fewer than 500 stored: all finalized ones
        Assert.Contains(Tf(big, "1d").GetProperty("candlePatterns").EnumerateArray(),
            p => p.GetProperty("patternType").GetString() == "Doji");
    }

    // --- (d) baseRates == GET base-rates on the same fixture ----------------------------------------------------

    [Fact]
    public async Task BaseRates_EqualTheBaseRatesEndpoint_WithGateReasonsAndUnvalidatedLabelsIntact()
    {
        var fx = await CreateAsync(AfterClose);
        await using var factory = fx.Factory;
        var client = factory.CreateClient();

        foreach (var symbol in new[] { "BID", "MBB" })
        {
            var (technical, _) = await OkJsonAsync(await client.GetAsync($"/api/equities/{symbol}/technical"));
            var (baseRates, raw) = await OkJsonAsync(await client.GetAsync($"/api/equities/{symbol}/base-rates"));
            Assert.Equal(baseRates.GetRawText(), technical.GetProperty("baseRates").GetRawText());
            Assert.Equal(JsonValueKind.Null, technical.GetProperty("baseRatesUnavailableReason").ValueKind);
            _output.WriteLine($"{symbol} base-rates: {raw.Length} chars, status {baseRates.GetProperty("status").GetString()}");
        }

        var (bid, _) = await OkJsonAsync(await client.GetAsync("/api/equities/BID/base-rates"));
        Assert.Equal("abstained", bid.GetProperty("status").GetString());
        Assert.Contains("gate-not-passed", bid.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.All(bid.GetProperty("configs").EnumerateArray(), c =>
        {
            Assert.Equal("unvalidated", c.GetProperty("research").GetProperty("label").GetString());
            Assert.Equal(JsonValueKind.Null, c.GetProperty("decisionUse").ValueKind);
            Assert.Contains("ci90-upper-not-below-zero",
                c.GetProperty("validation").GetProperty("gateReasons").EnumerateArray().Select(r => r.GetString()));
        });
    }

    // --- (e) noteBinding round-trip through POST research-notes (forward) + scoring/calendar blocks -------------

    [Fact]
    public async Task NoteBinding_RoundTripsThroughAForwardResearchNote()
    {
        var fx = await CreateAsync(AfterClose);
        await using var factory = fx.Factory;
        var client = factory.CreateClient();

        var (root, _) = await OkJsonAsync(await client.GetAsync("/api/equities/MBB/technical"));
        var calendar = root.GetProperty("calendar");
        Assert.Equal(fx.Mbb1d[^1], calendar.GetProperty("signalBarOpenTimeMs").GetInt64());
        Assert.True(calendar.GetProperty("signalBarStored").GetBoolean());
        Assert.Equal(VietnamMarketCalendar.ToUtcMs(Tomorrow, new TimeOnly(9, 0)), calendar.GetProperty("nextSessionOpenUtcMs").GetInt64());
        Assert.True(calendar.GetProperty("forwardNoteOpen").GetBoolean(), calendar.GetRawText());
        Assert.Equal(JsonValueKind.Null, calendar.GetProperty("reason").ValueKind);
        Assert.Equal(fx.Calendar.CoverageTo!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            calendar.GetProperty("coverageTo").GetString());

        var scoring = root.GetProperty("scoring");
        Assert.Equal("direction-score-v1", scoring.GetProperty("ruleVersion").GetString());
        Assert.Equal("omit", scoring.GetProperty("probabilityPolicy").GetString());
        Assert.Equal("/api/equities/MBB/research-notes/direction-scores", scoring.GetProperty("scoresEndpoint").GetString());
        // Same signed thresholds as direction-scores: down is negative (-0.04 for H=20).
        Assert.Equal(EquityBarrierConfigs.All.Select(c => (c.ConfigId, c.HorizonSessions, c.TargetPct, -c.StopPct)).ToArray(),
            scoring.GetProperty("horizons").EnumerateArray().Select(h => (h.GetProperty("configId").GetString()!,
                h.GetProperty("horizonSessions").GetInt32(), h.GetProperty("upThresholdPct").GetDouble(),
                h.GetProperty("downThresholdPct").GetDouble())).ToArray());

        var binding = root.GetProperty("noteBinding");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("noteBindingUnavailableReason").ValueKind);
        var requestedAsOf = binding.GetProperty("requestedAsOf").GetString()!;
        Assert.True(EquityResearchSnapshotService.TryParseAsOf(requestedAsOf, AfterClose, out var asOf, out _));
        Assert.Equal(AfterClose, asOf);
        Assert.Equal(fx.Mbb1d[^1], binding.GetProperty("signalBarOpenTimeMs").GetInt64());
        var snapshotId = binding.GetProperty("snapshotId").GetString()!;
        Assert.StartsWith("snap/MBB/", snapshotId);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);
        var note = new
        {
            mode = "forward",
            requestedAsOf,
            signalBarOpenTimeMs = binding.GetProperty("signalBarOpenTimeMs").GetInt64(),
            horizonSessions = 20,
            snapshotId,
            snapshotSha256 = binding.GetProperty("snapshotSha256").GetString(),
            skillVersion = "equity-technical-v1",
            methodVersion = "eqtech-t-test",
            direction = "sideways",
            actionView = "none",
            actionBasis = "insufficient-input",
            body = new
            {
                schemaVersion = "equity-research-note-v1",
                thesis = "Technical-only test note.",
                evidence = Array.Empty<object>(),
                counterEvidence = Array.Empty<object>(),
                scenarios = Array.Empty<object>(),
                limitations = Array.Empty<string>(),
                missingData = Array.Empty<string>(),
                outcomeCheck = new { how = "direction-score-v1", dueAfterSessions = 20 },
                probability = (object?)null,
            },
        };
        var created = await admin.PostAsJsonAsync("/api/equities/MBB/research-notes", note, Web);
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"{(int)created.StatusCode} {createdBody}");
        var stored = JsonDocument.Parse(createdBody).RootElement;
        Assert.Equal(snapshotId, stored.GetProperty("snapshotId").GetString());
        Assert.Equal("forward", stored.GetProperty("mode").GetString());
        Assert.Empty(stored.GetProperty("forecastIds").EnumerateArray());
    }

    // --- (f) a conditions failure only marks its own entry --------------------------------------------------

    [Fact]
    public async Task ConditionsFailure_MarksOnlyThatTimeframe_RestOfThePayloadIsServed()
    {
        var fx = await CreateAsync(AfterClose, ("MBB:1h", "down"), ("BID:1d", "invalid"));
        await using var factory = fx.Factory;
        var client = factory.CreateClient();

        var (mbb, _) = await OkJsonAsync(await client.GetAsync("/api/equities/MBB/technical"));
        var h1 = Tf(mbb, "1h");
        var hCond = h1.GetProperty("conditions");
        Assert.Equal("unavailable", hCond.GetProperty("status").GetString());
        Assert.Equal("conditions-source-unavailable", hCond.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, hCond.GetProperty("payload").ValueKind);
        Assert.Equal(100, h1.GetProperty("candles").GetArrayLength());
        Assert.Equal("ok", Tf(mbb, "1d").GetProperty("conditions").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, mbb.GetProperty("noteBinding").ValueKind);
        Assert.Equal(JsonValueKind.Object, mbb.GetProperty("baseRates").ValueKind);

        var (bid, _) = await OkJsonAsync(await client.GetAsync("/api/equities/BID/technical"));
        var bCond = Tf(bid, "1d").GetProperty("conditions");
        Assert.Equal(("unavailable", "conditions-payload-invalid"),
            (bCond.GetProperty("status").GetString(), bCond.GetProperty("reason").GetString()));
        Assert.Equal(100, Tf(bid, "1d").GetProperty("candles").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, bid.GetProperty("noteBinding").ValueKind);
    }
}

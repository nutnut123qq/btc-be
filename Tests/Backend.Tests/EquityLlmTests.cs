using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

/// <summary>
/// EQX-A3: dossier privacy + section isolation, LLM run signal-bar guard, AI status mapping
/// (fake HttpMessageHandler, no network), defensive output validation, llm-judge journal rows
/// only for succeeded runs, list ordering/clamp.
/// </summary>
public class EquityLlmTests : IDisposable
{
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private sealed class FakeAi : HttpMessageHandler
    {
        public Func<HttpRequestMessage, string, HttpResponseMessage> Run { get; set; } =
            (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        public List<string> RunBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/equity-llm/run")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                RunBodies.Add(body);
                return Run(request, body);
            }
            // current-conditions etc.: AI down for everything else in these tests.
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
    }

    private sealed class EmptyEvidence : IResearchEvidenceCatalog
    {
        public ResearchEvidenceCatalogResponse GetCatalog() => new("btc-research-evidence-catalog-v1", "BID", DateTime.UtcNow, [],
            new EvidenceIntegritySummaryDto(0, 0, 0), null);
        public ResearchEvidenceDetailDto? GetDetail(string id) => null;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eqx-a3-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { } }

    private static readonly VietnamMarketCalendar Cal = new([]);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 16, 0, 0, TimeSpan.FromHours(7));
    private static readonly long Wed = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 14));

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static async Task SeedAsync(AppDbContext db, DateOnly last, bool withPosition = true)
    {
        foreach (var d in Cal.EnumerateTradingDates(last.AddDays(-50), last))
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(d);
            db.InstrumentCandles.Add(new InstrumentCandle
            {
                Symbol = "BID", Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = Cal.SessionCloseUtcMs("1d", open),
                Open = 40_000, High = 40_100, Low = 39_900, Close = 40_000, Volume = 1, Provider = "VCI",
                PriceBasis = "adjusted", Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh", UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        if (withPosition)
        {
            db.EquityPositionLots.Add(new EquityPositionLot
                { Symbol = "BID", Quantity = 700, CostPerShareVnd = 41_234, AcquireDate = last.AddDays(-20), Source = "manual" });
            db.EquityPositionAccounts.Add(new EquityPositionAccount { Symbol = "BID", AvailableCashVnd = 9_876_543 });
        }
        await db.SaveChangesAsync();
    }

    private (EquityDossierService Dossier, EquityLlmRunService Runs) Services(AppDbContext db, FakeAi ai, TestTimeProvider tp,
        EquityContextService? context = null)
    {
        var factory = new Factory(ai);
        var baseRates = new EquityBaseRatesService(db, _root, 4096, Cal, factory, tp);
        var journal = new EquityForecastJournalService(db, baseRates, Cal, tp);
        var dossier = new EquityDossierService(db,
            new CurrentConditionsService(factory, new EmptyEvidence(), NullLogger<CurrentConditionsService>.Instance),
            baseRates, context ?? new EquityContextService(db, Cal, tp), journal, tp);
        return (dossier, new EquityLlmRunService(db, dossier, factory, Cal, tp));
    }

    private static readonly string[] PrivateKeys =
    [
        "position", "lots", "totalShares", "settledShares", "pendingShares", "avgCostPerShareVnd", "costPerShareVnd",
        "availableCashVnd", "unrealizedPnlVnd", "acquireDate", "quantity", "sellableSharesNow",
    ];

    private static IEnumerable<string> PropertyNames(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    [Fact]
    public async Task Dossier_IsMarketOnly_AndCarriesAllSections()
    {
        var db = NewDb(nameof(Dossier_IsMarketOnly_AndCarriesAllSections));
        await SeedAsync(db, new DateOnly(2026, 10, 14));
        var (dossier, _) = Services(db, new FakeAi(), new TestTimeProvider(Now));

        var d = await dossier.BuildAsync("BID");

        Assert.Equal("equity-dossier-v1", d.ContractVersion);
        Assert.Equal(Wed, d.SignalBarOpenTimeMs);
        Assert.Equal("unavailable", d.Conditions["status"]!.GetValue<string>());
        Assert.Equal("unavailable", d.BaseRates.Status);
        Assert.Equal("equity-context-v1", d.Context.ContractVersion);
        Assert.Null(d.LatestLlmRun);
        Assert.Equal(2, d.JournalSummary.Count);
        var json = JsonSerializer.Serialize(d, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var names = PropertyNames(JsonDocument.Parse(json).RootElement).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(names, n => PrivateKeys.Contains(n, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("41234", json);
        Assert.DoesNotContain("9876543", json);
        await Assert.ThrowsAsync<ArgumentException>(() => dossier.BuildAsync("VCB"));
    }

    [Fact]
    public async Task Dossier_FailingSection_DegradesToUnavailable()
    {
        var db = NewDb(nameof(Dossier_FailingSection_DegradesToUnavailable));
        await SeedAsync(db, new DateOnly(2026, 10, 14));
        var broken = NewDb("disposed-" + Guid.NewGuid());
        await broken.DisposeAsync();
        var (dossier, _) = Services(db, new FakeAi(), new TestTimeProvider(Now), new EquityContextService(broken, Cal));

        var d = await dossier.BuildAsync("BID");

        Assert.Equal(("unavailable", "section-error"), (d.Context.RelativeStrength.Status, d.Context.RelativeStrength.Reason));
        Assert.Equal("unavailable", d.Context.Fundamentals.Status);
        Assert.Equal(Wed, d.SignalBarOpenTimeMs);       // other sections still served
        Assert.Equal(2, d.JournalSummary.Count);
    }

    [Fact]
    public async Task Run_RefusesWhenLatestExpectedBarNotStored_NoBackdating()
    {
        var db = NewDb(nameof(Run_RefusesWhenLatestExpectedBarNotStored_NoBackdating));
        await SeedAsync(db, new DateOnly(2026, 10, 13)); // Wednesday's finalized bar missing
        var ai = new FakeAi();
        var (_, runs) = Services(db, ai, new TestTimeProvider(Now));

        var r = await runs.RunAsync("BID");

        Assert.Equal(EquityLlmRunService.Outcome.SignalBarNotLatest, r.Outcome);
        Assert.Empty(ai.RunBodies);
        Assert.Empty(await db.EquityLlmRuns.ToListAsync());
    }

    private static string Record(string status, JsonNode? output = null, string? reason = null, string? errorCode = null) =>
        new JsonObject
        {
            ["status"] = status, ["reason"] = reason, ["researchModel"] = "claude-opus-5-5", ["judgeModel"] = "claude-opus-5-5",
            ["promptVersion"] = "equity-llm-prompt-v1", ["schemaVersion"] = "equity-llm-judge-v1",
            ["inputSnapshotSha256"] = new string('b', 64), ["output"] = output,
            ["usage"] = new JsonObject { ["input_tokens"] = 10 }, ["durationMs"] = 1234, ["errorCode"] = errorCode,
        }.ToJsonString();

    private static JsonObject Output(long signal, Action<JsonObject>? edit = null)
    {
        var o = new JsonObject
        {
            ["schemaVersion"] = "equity-llm-judge-v1", ["symbol"] = "BID", ["signalBarOpenTimeMs"] = signal,
            ["evidenceStatus"] = "sourced",
            ["scenarios"] = new JsonArray(new JsonObject
                { ["name"] = "base", ["direction"] = "sideways", ["description"] = "range", ["sourceIds"] = new JsonArray("s1") }),
            ["warnings"] = new JsonArray(),
            ["veto"] = new JsonObject { ["active"] = false, ["reason"] = null, ["sourceIds"] = new JsonArray() },
            ["sources"] = new JsonArray(new JsonObject
                { ["id"] = "s1", ["url"] = "https://example.com/a", ["title"] = "news", ["publishedAt"] = null }),
            ["adjustedForecasts"] = new JsonArray(
                new JsonObject { ["configId"] = "tb-p5-s4-h20", ["probTargetFirst"] = 0.35, ["probStopFirst"] = 0.5, ["rationale"] = "r", ["sourceIds"] = new JsonArray("s1") },
                new JsonObject { ["configId"] = "tb-p10-s6-h60", ["probTargetFirst"] = 0.3, ["probStopFirst"] = 0.45, ["rationale"] = "r", ["sourceIds"] = new JsonArray("s1") }),
        };
        edit?.Invoke(o);
        return o;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task<(EquityLlmRunService.RunResult Result, AppDbContext Db, FakeAi Ai)> RunWith(string name,
        Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var db = NewDb(name);
        await SeedAsync(db, new DateOnly(2026, 10, 14));
        var ai = new FakeAi { Run = respond };
        var (_, runs) = Services(db, ai, new TestTimeProvider(Now));
        return (await runs.RunAsync("BID"), db, ai);
    }

    [Fact]
    public async Task Run_Succeeded_PersistsRunAndUnvalidatedLlmForecasts()
    {
        var (r, db, ai) = await RunWith(nameof(Run_Succeeded_PersistsRunAndUnvalidatedLlmForecasts),
            (_, _) => Json(HttpStatusCode.OK, Record("succeeded", Output(Wed))));

        Assert.Equal(EquityLlmRunService.Outcome.Created, r.Outcome);
        Assert.Equal("succeeded", r.Item!.Status);
        Assert.Equal("equity-llm-judge-v1", r.Item.Output!.Value.GetProperty("schemaVersion").GetString());
        var run = await db.EquityLlmRuns.SingleAsync();
        Assert.Equal((new string('b', 64), 1234L), (run.InputSnapshotSha256, run.DurationMs));
        Assert.Contains("\"configs\"", run.InputSnapshotJson);
        Assert.NotNull(run.OutputJson);
        var rows = await db.EquityForecastRecords.OrderBy(f => f.ConfigId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, f =>
        {
            Assert.Equal(("llm-judge", false, run.Id), (f.Source, f.Validated, f.LlmRunId!.Value));
            Assert.Equal(Wed, f.SignalBarOpenTimeMs);
            Assert.Equal("claude-opus-5-5", f.ModelId);
            Assert.Equal("equity-llm-prompt-v1", f.PromptVersion);
            Assert.True(f.AvailableTimeMs >= f.SignalBarCloseTimeMs);
        });
        Assert.Equal((0.10, 0.06, 60), (rows[0].TargetPct, rows[0].StopPct, rows[0].HorizonSessions));
        Assert.Equal($"llm-judge/BID/tb-p5-s4-h20/{Wed}/{run.Id}", rows[1].ForecastId);

        // The request sent to the AI: symbol, dossier at the latest bar, configs with quant fields; no position data.
        var sent = JsonDocument.Parse(ai.RunBodies.Single()).RootElement;
        Assert.Equal("BID", sent.GetProperty("symbol").GetString());
        Assert.Equal(Wed, sent.GetProperty("dossier").GetProperty("signalBarOpenTimeMs").GetInt64());
        var cfg = sent.GetProperty("configs")[0];
        Assert.Equal(JsonValueKind.Null, cfg.GetProperty("quantProbTargetFirst").ValueKind);
        Assert.False(cfg.GetProperty("quantValidated").GetBoolean());
        Assert.DoesNotContain(PropertyNames(sent), n => PrivateKeys.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("string-prob")]
    [InlineData("unknown-config")]
    [InlineData("sum-above-one")]
    [InlineData("unknown-source")]
    [InlineData("wrong-signal")]
    [InlineData("extra-key")]
    [InlineData("output-array")]
    public async Task Run_MalformedOutput_StoredAsFailed_NoForecasts(string mutation)
    {
        JsonNode output = Output(Wed, o =>
        {
            var fc = (JsonObject)o["adjustedForecasts"]![0]!;
            switch (mutation)
            {
                case "string-prob": fc["probTargetFirst"] = "0.4"; break;
                case "unknown-config": fc["configId"] = "tb-x"; break;
                case "sum-above-one": fc["probTargetFirst"] = 0.7; fc["probStopFirst"] = 0.6; break;
                case "unknown-source": fc["sourceIds"] = new JsonArray("s9"); break;
                case "wrong-signal": o["signalBarOpenTimeMs"] = Wed - 86_400_000; break;
                case "extra-key": o["price"] = 41_000; break;
            }
        });
        if (mutation == "output-array") output = new JsonArray(1, 2);
        var (r, db, _) = await RunWith(nameof(Run_MalformedOutput_StoredAsFailed_NoForecasts) + mutation,
            (_, _) => Json(HttpStatusCode.OK, Record("succeeded", output)));

        Assert.Equal(EquityLlmRunService.Outcome.Created, r.Outcome);
        Assert.Equal("failed", r.Item!.Status);
        Assert.True(r.Item.Reason!.StartsWith("backend-output-invalid") || r.Item.Reason == "ai-succeeded-without-output", r.Item.Reason);
        Assert.Null((await db.EquityLlmRuns.SingleAsync()).OutputJson);
        Assert.Empty(await db.EquityForecastRecords.ToListAsync());
    }

    [Fact]
    public async Task Run_MapsAiStatuses_AndPersistsEveryOutcome()
    {
        var unavailable = await RunWith("ai-503", (_, _) => Json(HttpStatusCode.ServiceUnavailable,
            Record("unavailable", reason: "ANTHROPIC_API_KEY not configured")));
        Assert.Equal(EquityLlmRunService.Outcome.Unavailable, unavailable.Result.Outcome);
        Assert.Equal(("unavailable", "ANTHROPIC_API_KEY not configured"),
            (unavailable.Result.Item!.Status, unavailable.Result.Item.Reason));
        Assert.Single(await unavailable.Db.EquityLlmRuns.ToListAsync());

        var timeout = await RunWith("ai-504", (_, _) => Json(HttpStatusCode.GatewayTimeout,
            Record("failed", reason: "judge-timeout", errorCode: "TIMEOUT")));
        Assert.Equal(EquityLlmRunService.Outcome.Created, timeout.Result.Outcome);
        Assert.Equal(("failed", "llm-timeout:judge-timeout"), (timeout.Result.Item!.Status, timeout.Result.Item.Reason));

        var failed = await RunWith("ai-200-failed", (_, _) => Json(HttpStatusCode.OK, Record("failed", reason: "research-refusal")));
        Assert.Equal(("failed", "research-refusal"), (failed.Result.Item!.Status, failed.Result.Item.Reason));
        Assert.Empty(await failed.Db.EquityForecastRecords.ToListAsync());

        var rejected = await RunWith("ai-400", (_, _) => Json(HttpStatusCode.BadRequest,
            "{\"detail\":{\"code\":\"INVALID_LLM_INPUT\",\"message\":\"configs must be a non-empty list.\"}}"));
        Assert.Equal(EquityLlmRunService.Outcome.UpstreamFailed, rejected.Result.Outcome);
        Assert.StartsWith("ai-rejected-input:configs must be", rejected.Result.Item!.Reason);

        var down = await RunWith("ai-down", (_, _) => throw new HttpRequestException("refused"));
        Assert.Equal(EquityLlmRunService.Outcome.UpstreamFailed, down.Result.Outcome);
        Assert.Equal(("failed", "ai-unreachable"), (down.Result.Item!.Status, down.Result.Item.Reason));
        var row = await down.Db.EquityLlmRuns.SingleAsync();
        Assert.Equal(64, row.InputSnapshotSha256.Length); // recomputed canonically when the AI never answered
        Assert.Equal(EquityLlmRunService.Sha256Hex(row.InputSnapshotJson), row.InputSnapshotSha256);

        var garbage = await RunWith("ai-garbage", (_, _) => Json(HttpStatusCode.OK, "[\"not\",\"a\",\"record\"]"));
        Assert.Equal(("failed", "ai-record-not-json-object"), (garbage.Result.Item!.Status, garbage.Result.Item.Reason));
    }

    [Fact]
    public async Task Run_RefusedOnceNextSessionOpened_ForecastWindowClosed()
    {
        var db = NewDb(nameof(Run_RefusedOnceNextSessionOpened_ForecastWindowClosed));
        await SeedAsync(db, new DateOnly(2026, 10, 14));
        var ai = new FakeAi { Run = (_, _) => Json(HttpStatusCode.OK, Record("succeeded", Output(Wed))) };
        // Thu 09:30 local: Wednesday is still the latest finalized bar, but Thursday's session is open.
        var (_, runs) = Services(db, ai, new TestTimeProvider(new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.FromHours(7))));

        var r = await runs.RunAsync("BID");

        Assert.Equal(EquityLlmRunService.Outcome.ForecastWindowClosed, r.Outcome);
        Assert.Empty(ai.RunBodies);
        Assert.Empty(await db.EquityLlmRuns.ToListAsync());

        // 08:59 the same morning the window is still open.
        var (_, early) = Services(db, ai, new TestTimeProvider(new DateTimeOffset(2026, 10, 15, 8, 59, 0, TimeSpan.FromHours(7))));
        Assert.Equal(EquityLlmRunService.Outcome.Created, (await early.RunAsync("BID")).Outcome);
    }

    [Fact]
    public async Task Run_ClientDisconnectDuringAiCall_StillPersistsRun()
    {
        var db = NewDb(nameof(Run_ClientDisconnectDuringAiCall_StillPersistsRun));
        await SeedAsync(db, new DateOnly(2026, 10, 14));
        using var cts = new CancellationTokenSource();
        var ai = new FakeAi
        {
            Run = (_, _) =>
            {
                cts.Cancel(); // the HTTP client went away while the paid AI call was running
                return Json(HttpStatusCode.ServiceUnavailable, Record("unavailable", reason: "ANTHROPIC_API_KEY not configured"));
            },
        };
        var (_, runs) = Services(db, ai, new TestTimeProvider(Now));

        var r = await runs.RunAsync("BID", cts.Token);

        Assert.Equal(EquityLlmRunService.Outcome.Unavailable, r.Outcome);
        Assert.Equal("unavailable", (await db.EquityLlmRuns.SingleAsync()).Status);
    }

    [Fact]
    public async Task List_NewestFirst_Clamped_AndDossierShowsLatestRun()
    {
        var db = NewDb(nameof(List_NewestFirst_Clamped_AndDossierShowsLatestRun));
        await SeedAsync(db, new DateOnly(2026, 10, 14));
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 60; i++)
            db.EquityLlmRuns.Add(new EquityLlmRun
            {
                Id = Guid.NewGuid(), Symbol = "BID", SignalBarOpenTimeMs = Wed, CreatedAtUtc = t0.AddMinutes(i),
                Status = "unavailable", Reason = $"r{i}", PromptVersion = "p", SchemaVersion = "s",
                InputSnapshotSha256 = new string('0', 64), InputSnapshotJson = "{}",
            });
        await db.SaveChangesAsync();
        var (dossier, runs) = Services(db, new FakeAi(), new TestTimeProvider(Now));

        var all = await runs.ListAsync("BID", 1000);
        Assert.Equal(50, all.Items.Count);
        Assert.Equal("r59", all.Items[0].Reason);
        Assert.True(all.Items.Zip(all.Items.Skip(1)).All(p => p.First.CreatedAtUtc > p.Second.CreatedAtUtc));
        Assert.Single((await runs.ListAsync("BID", 0)).Items);
        Assert.Empty((await runs.ListAsync("MBB", 10)).Items);
        Assert.Equal("r59", (await dossier.BuildAsync("BID")).LatestLlmRun!.Reason);
    }

    [Fact]
    public void CanonicalJson_SortsKeysCompact_LikePython()
    {
        var node = JsonNode.Parse("{\"b\":1,\"a\":{\"d\":[1,2,{\"z\":null,\"y\":\"ạ\"}],\"c\":true}}");
        Assert.Equal("{\"a\":{\"c\":true,\"d\":[1,2,{\"y\":\"ạ\",\"z\":null}]},\"b\":1}", EquityLlmRunService.CanonicalJson(node));
    }
}

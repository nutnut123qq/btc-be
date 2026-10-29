using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit.Abstractions;

namespace Backend.Tests;

/// <summary>
/// EQX v2 §B/§C (worker B): research snapshot determinism + PIT rules, readiness per purpose,
/// session research notes (forward/retrospective guards, snapshot binding, probability →
/// session-research forecast), source-document registration, and HTTP scope/AdminGuard wiring.
/// EF InMemory + temp dirs + fake AI handler; no network, no PostgreSQL.
/// </summary>
// Shares the 2002-2004 btc_eqx_test window with the other Postgres classes; run serialised.
[Collection(PostgresTestDbCollection.Name)]
public class EquityResearchSnapshotTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = output;

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    /// <summary>Fake AI: valid current-conditions-v1 for the latest seeded bar; generatedAtMs varies per call.</summary>
    private sealed class FakeAi(Func<long> latestOpen) : HttpMessageHandler
    {
        public int ConditionsCalls { get; private set; }
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down || request.RequestUri!.AbsolutePath != "/api/current-conditions")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
            ConditionsCalls++;
            var open = latestOpen();
            var payload = new JsonObject
            {
                ["schemaVersion"] = "current-conditions-v1", ["symbol"] = "BID", ["timeframe"] = "1d",
                ["asOfMs"] = Cal.SessionCloseUtcMs("1d", open), ["generatedAtMs"] = 1_000_000 + ConditionsCalls,
                ["latestClosedBar"] = new JsonObject
                {
                    ["openTimeMs"] = open, ["closeTimeMs"] = Cal.SessionCloseUtcMs("1d", open),
                    ["open"] = 1, ["high"] = 1, ["low"] = 1, ["close"] = 1, ["volume"] = 1,
                },
                ["analysisWindow"] = new JsonObject { ["firstOpenTimeMs"] = open, ["bars"] = 1, ["contiguous"] = true, ["windowBars"] = 2000 },
                ["contract"] = new JsonObject { ["contractVersion"] = "c1", ["definitionsSha256"] = "d", ["rawFileSha256"] = "r" },
                ["conditions"] = new JsonArray(), ["warnings"] = new JsonArray(), ["unavailableModules"] = new JsonArray(),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload.ToJsonString()) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
    }

    private sealed class EmptyEvidence : IResearchEvidenceCatalog
    {
        public ResearchEvidenceCatalogResponse GetCatalog() => new("btc-research-evidence-catalog-v1", "BID",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), [], new EvidenceIntegritySummaryDto(0, 0, 0), null);
        public ResearchEvidenceDetailDto? GetDetail(string id) => null;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eqx-v2b-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { } }

    private static readonly VietnamMarketCalendar Cal = new([]);
    private static DateTimeOffset Local(int y, int m, int d, int hh, int mm) => new(y, m, d, hh, mm, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Now = Local(2026, 10, 14, 16, 0); // Wednesday, after the close
    private static readonly long Wed = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 14));
    private static readonly long Tue = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 13));
    private static readonly DateTime Updated = new(2026, 10, 14, 8, 30, 15, 123, DateTimeKind.Utc);
    private const string Sha64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"v2b-{name}").Options);

    private static InstrumentCandle Bar(string symbol, DateOnly d, decimal px, DateTime? updated = null)
    {
        var open = VietnamMarketCalendar.DailyOpenUtcMs(d);
        return new InstrumentCandle
        {
            Symbol = symbol, Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = Cal.SessionCloseUtcMs("1d", open),
            Open = px, High = px + 100.50m, Low = px - 100, Close = px + 25.000m, Volume = 1_000_000.000m,
            Provider = "VCI", PriceBasis = symbol == "VNINDEX" ? "raw" : "adjusted", Venue = "HOSE",
            SourceTimezone = "Asia/Ho_Chi_Minh", FetchedAtUtc = Updated, UpdatedAtUtc = updated ?? Updated,
        };
    }

    private static async Task SeedAsync(AppDbContext db, int count = 260, DateOnly? lastDay = null)
    {
        var last = lastDay ?? new DateOnly(2026, 10, 14);
        var dates = Cal.EnumerateTradingDates(last.AddDays(-count * 2), last).TakeLast(count).ToArray();
        for (var i = 0; i < dates.Length; i++)
        {
            db.InstrumentCandles.Add(Bar("BID", dates[i], 40_000m + i));
            db.InstrumentCandles.Add(Bar("VNINDEX", dates[i], 1_200m + i));
        }
        // Personal data that must never leak into public research payloads.
        db.EquityPositionLots.Add(new EquityPositionLot
            { Symbol = "BID", Quantity = 700, CostPerShareVnd = 41_234, AcquireDate = last.AddDays(-20), Source = "manual" });
        db.EquityPositionAccounts.Add(new EquityPositionAccount { Symbol = "BID", AvailableCashVnd = 9_876_543 });
        await db.SaveChangesAsync();
    }

    private sealed record Rig(AppDbContext Db, TestTimeProvider Clock, FakeAi Ai, EquityResearchSnapshotService Snapshots,
        EquityResearchNoteService Notes, EquityForecastJournalService Journal);

    private Rig Build(AppDbContext db, DateTimeOffset? now = null, VietnamMarketCalendar? calendar = null)
    {
        var cal = calendar ?? Cal;
        var clock = new TestTimeProvider(now ?? Now);
        FakeAi ai = null!;
        ai = new FakeAi(() => db.InstrumentCandles.Where(c => c.Symbol == "BID" && c.Timeframe == "1d"
                && c.CloseTimeMs <= clock.GetUtcNow().ToUnixTimeMilliseconds())
            .Max(c => c.OpenTimeMs));
        var factory = new Factory(ai);
        var snapshots = new EquityResearchSnapshotService(db, cal,
            tp => new EquityBaseRatesService(db, _root, 1 << 20, cal, factory, tp),
            new CurrentConditionsService(factory, new EmptyEvidence(), NullLogger<CurrentConditionsService>.Instance),
            clock);
        var notes = new EquityResearchNoteService(db, snapshots, Cal, clock);
        var journal = new EquityForecastJournalService(db, new EquityBaseRatesService(db, _root, 1 << 20, Cal, factory, clock), Cal, clock);
        return new Rig(db, clock, ai, snapshots, notes, journal);
    }

    private async Task WriteArtifactAsync(AppDbContext db, long dataAsOf)
    {
        var rows = await db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == "BID" && c.Timeframe == "1d" && c.OpenTimeMs <= dataAsOf).OrderBy(c => c.OpenTimeMs).ToListAsync();
        JsonObject Config(string id, double t, double s, int h) => new()
        {
            ["configId"] = id, ["targetPct"] = t, ["stopPct"] = s, ["horizonSessions"] = h,
            ["validation"] = new JsonObject { ["gatePassed"] = false, ["gateReasons"] = new JsonArray("ci90-upper-not-below-zero"), ["nOos"] = 900, ["nEffective"] = 40 },
            ["current"] = new JsonObject
            {
                ["signalBarOpenTimeMs"] = dataAsOf, ["probTargetFirst"] = 0.3, ["probStopFirst"] = 0.4, ["probTimeout"] = 0.3,
                ["evNetFraction"] = -0.01, ["k"] = 50, ["nEligible"] = 600, ["abstained"] = false,
                ["prior"] = new JsonObject { ["probTargetFirst"] = 0.33, ["probStopFirst"] = 0.4, ["probTimeout"] = 0.27 },
            },
        };
        var artifact = new JsonObject
        {
            ["schemaVersion"] = "equity-base-rates-artifact-v1", ["symbol"] = "BID", ["timeframe"] = "1d",
            ["generatedAtUtc"] = "2026-10-14T10:00:00.000000Z", ["dataAsOfOpenTimeMs"] = dataAsOf, ["priceBasis"] = "adjusted",
            ["provider"] = "VCI",
            ["dataset"] = new JsonObject
            {
                ["barCount"] = rows.Count, ["firstOpenTimeMs"] = rows[0].OpenTimeMs, ["lastOpenTimeMs"] = rows[^1].OpenTimeMs,
                ["maxUpdatedAtUtc"] = EquityBaseRatesService.FormatFingerprintTimestamp(rows.Max(r => r.UpdatedAtUtc)), ["sha256"] = Sha64,
            },
            ["method"] = new JsonObject { ["version"] = "equity-knn-v1" },
            ["protocol"] = new JsonObject { ["version"] = "equity-base-rates-protocol-v1" },
            ["configs"] = new JsonArray(Config("tb-p5-s4-h20", 0.05, 0.04, 20), Config("tb-p10-s6-h60", 0.10, 0.06, 60)),
        };
        var path = Path.Combine(_root, "equity-base-rates", "BID", "base-rates.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, artifact.ToJsonString());
    }

    private static readonly string[] PrivateKeys =
    [
        "position", "lots", "totalShares", "settledShares", "avgCostPerShareVnd", "costPerShareVnd",
        "availableCashVnd", "unrealizedPnlVnd", "acquireDate", "quantity", "sharedCashVnd", "buyBudgetVnd",
    ];

    private static IEnumerable<string> PropertyNames(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // --- fingerprint contract (shared with ai/research_snapshot.py) -----------------------------

    [Fact]
    public void SeriesFingerprint_MatchesCrossLanguageVector()
    {
        Assert.Equal("27650.5", EquityResearchSnapshotService.NormalizeDecimal(27650.50m));
        Assert.Equal("40000", EquityResearchSnapshotService.NormalizeDecimal(40000.000m));
        Assert.Equal("0", EquityResearchSnapshotService.NormalizeDecimal(0.00m));
        var rows = new[]
        {
            new InstrumentCandle { OpenTimeMs = 1000, CloseTimeMs = 2000, Open = 27650.50m, High = 27700.00m, Low = 27600.0m, Close = 27650.5m, Volume = 1234567.000m, UpdatedAtUtc = Updated },
            new InstrumentCandle { OpenTimeMs = 3000, CloseTimeMs = 4000, Open = 40000m, High = 40100m, Low = 39900m, Close = 40000m, Volume = 0m, UpdatedAtUtc = Updated },
        };
        var fp = EquityResearchSnapshotService.Fingerprint(rows);
        // Same vector is pinned in ai/tests/unit/test_research_snapshot.py.
        Assert.Equal("a3ad6448775a161503acd32ddbc6ec328472ae4107052eb54c2b86790683932f", fp.Sha256);
        Assert.Equal((2L, 1000L, 3000L), (fp.BarCount, fp.FirstOpenTimeMs!.Value, fp.LastOpenTimeMs!.Value));
    }

    // --- snapshot -----------------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_Latest_IsMarketOnly_Deterministic_AndCarriesConditions()
    {
        var db = NewDb(nameof(Snapshot_Latest_IsMarketOnly_Deterministic_AndCarriesConditions));
        await SeedAsync(db);
        await WriteArtifactAsync(db, Wed);
        var rig = Build(db);

        var a = await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow());
        rig.Clock.Now = Now.AddMinutes(5); // generatedAt + conditions generatedAtMs change; content must not
        var b = await rig.Snapshots.BuildAsync("bid", Now);

        Assert.Equal("equity-research-snapshot-v1", a.ContractVersion);
        Assert.True(a.AsOfIsLatest);
        Assert.Equal(Wed, a.SignalBar!.OpenTimeMs);
        Assert.Equal(Cal.SessionCloseUtcMs("1d", Wed), a.EffectiveCutoffMs);
        Assert.Matches(@"^snap/BID/\d+/[0-9a-f]{12}$", a.SnapshotId);
        Assert.StartsWith($"snap/BID/{a.EffectiveCutoffMs}/{a.SnapshotSha256[..12]}", a.SnapshotId);
        Assert.Equal(a.SnapshotSha256, b.SnapshotSha256);
        Assert.Equal(a.SnapshotId, b.SnapshotId);
        Assert.NotEqual(a.GeneratedAt, b.GeneratedAt);
        Assert.Equal(2, rig.Ai.ConditionsCalls);
        Assert.Equal("ok", a.Conditions.Status);
        Assert.Null(a.Conditions.Payload!["generatedAtMs"]);
        Assert.Equal("partial", a.Pit.Reconstruction);
        Assert.Equal(260, a.Dataset["1d"].BarCount);
        Assert.Equal(a.Dataset["1d"].Sha256, a.Series.Single(s => s.Timeframe == "1d").Fingerprint);
        Assert.Equal(0, a.Coverage.Single(c => c.Timeframe == "1d").MissingBars);
        Assert.True(a.Coverage.Single(c => c.Timeframe == "1d").UpToDate);
        // EQTECH-1 H: BID declares 1h, so the snapshot also fingerprints the (here empty) 1h series.
        Assert.Equal(["1d", "1h"], a.Series.Select(s => s.Timeframe).ToArray());
        Assert.Equal(0, a.Dataset["1h"].BarCount);
        Assert.False(a.Coverage.Single(c => c.Timeframe == "1h").UpToDate);
        Assert.Equal("ok", a.RelativeStrength.Status);
        Assert.Equal(EquityBaseRatesStatuses.Abstained, a.BaseRates.Status); // gate not passed, but artifact is PIT
        Assert.NotEmpty(a.BaseRates.Configs);
        Assert.DoesNotContain(a.Unavailable, u => u.Section == "conditions");

        var json = JsonSerializer.Serialize(a, Web);
        var names = PropertyNames(JsonDocument.Parse(json).RootElement).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(names, n => PrivateKeys.Contains(n, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("41234", json);
        Assert.DoesNotContain("9876543", json);

        // Sha excludes generatedAt only: tampering with content changes it.
        Assert.NotEqual(a.SnapshotSha256, EquityResearchSnapshotService.Sha256Of(a with { EffectiveCutoffMs = 1 }));
        Assert.Equal(a.SnapshotSha256, EquityResearchSnapshotService.Sha256Of(a with { GeneratedAt = DateTime.UnixEpoch }));
    }

    [Fact]
    public async Task Snapshot_PastAsOf_AppliesPointInTimeRules()
    {
        var db = NewDb(nameof(Snapshot_PastAsOf_AppliesPointInTimeRules));
        await SeedAsync(db);
        await WriteArtifactAsync(db, Wed); // built on data after the past asOf
        var asOf = Local(2026, 10, 1, 16, 0);
        var oct1 = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 1));
        db.EquityFundamentals.AddRange(
            new EquityFundamental
            {
                Symbol = "BID", Period = "2026Q2", PeriodEndDate = new DateOnly(2026, 6, 30), Metric = "ROE", Value = 18.2m,
                Unit = "percent", AvailableAtUtc = asOf.UtcDateTime.AddDays(-20), AsOfUtc = asOf.UtcDateTime.AddDays(-20),
                Source = "KBS", SourceUrl = "https://kbs.example/x", Provenance = "provider:KBS",
            },
            new EquityFundamental
            {
                Symbol = "BID", Period = "2026Q3", PeriodEndDate = new DateOnly(2026, 9, 30), Metric = "ROE", Value = 19.9m,
                Unit = "percent", AvailableAtUtc = asOf.UtcDateTime.AddDays(5), AsOfUtc = asOf.UtcDateTime.AddDays(5),
                Source = "KBS", SourceUrl = "https://kbs.example/y", Provenance = "provider:KBS",
            });
        // Bars written/updated after asOf are counted, not hidden (seeded rows are all updated on Oct 14,
        // except one old bar updated before asOf).
        var earlyBar = await db.InstrumentCandles.FirstAsync(c => c.Symbol == "BID" && c.OpenTimeMs == oct1);
        earlyBar.UpdatedAtUtc = asOf.UtcDateTime.AddHours(-1);
        await db.SaveChangesAsync();
        var rig = Build(db);

        var s = await rig.Snapshots.BuildAsync("BID", asOf);

        Assert.False(s.AsOfIsLatest);
        Assert.Equal(oct1, s.SignalBar!.OpenTimeMs);
        Assert.Equal(Cal.SessionCloseUtcMs("1d", oct1), s.EffectiveCutoffMs);
        var expectedBars = await db.InstrumentCandles.CountAsync(c => c.Symbol == "BID" && c.CloseTimeMs <= s.EffectiveCutoffMs);
        Assert.Equal(expectedBars, s.Dataset["1d"].BarCount);
        Assert.Equal(oct1, s.Dataset["1d"].LastOpenTimeMs);
        Assert.Equal(s.Dataset["1d"].BarCount - 1, s.Pit.BarsUpdatedAfterAsOf["1d"]);
        Assert.Equal("unavailable", s.Conditions.Status);
        Assert.Equal(EquityResearchSnapshotService.ReasonConditionsPast, s.Conditions.Reason);
        Assert.Equal(0, rig.Ai.ConditionsCalls);
        Assert.Equal(EquityBaseRatesStatuses.Abstained, s.BaseRates.Status);
        Assert.Equal([EquityResearchSnapshotService.ReasonArtifactNotPit], s.BaseRates.Reasons);
        Assert.Empty(s.BaseRates.Configs);
        var roe = Assert.Single(s.Fundamentals.Items, i => i.Metric == "ROE");
        Assert.Equal("2026Q2", roe.Period);

        var readiness = EquityResearchSnapshotService.Readiness(s);
        Assert.Equal("equity-research-readiness-v1", readiness.ContractVersion);
        Assert.Equal(["fundamental", "portfolio-action", "quantitative", "technical"], readiness.Purposes.Keys.ToArray());
        Assert.Equal(EquityReadinessStatuses.RequiresAdmin, readiness.Purposes["portfolio-action"].Status);
        Assert.Equal(EquityReadinessStatuses.Partial, readiness.Purposes["technical"].Status);
        Assert.Contains("conditions", readiness.Purposes["technical"].Reasons);
        Assert.Contains("pit-prices", readiness.Purposes["technical"].Reasons);
        Assert.Equal(EquityReadinessStatuses.NotReady, readiness.Purposes["quantitative"].Status);
        var fresh = readiness.Purposes["quantitative"].Checks.Single(c => c.Name == "artifact-fresh");
        Assert.Equal("fail", fresh.Status);
        Assert.StartsWith("not evaluated: no point-in-time artifact", fresh.Detail);
        Assert.Contains(s.HowToGoDeeper, h => h.Contains("--include-portfolio") && h.Contains($"--as-of {s.RequestedAsOfMs}"));
        Assert.Equal(EquityReadinessStatuses.Partial, readiness.Purposes["fundamental"].Status);
        var rjson = JsonSerializer.Serialize(readiness, Web);
        Assert.DoesNotContain("\"ready\":", rjson); // no global ready flag
    }

    [Fact]
    public async Task Snapshot_NoData_IsNotReady_WithoutThrowing()
    {
        var db = NewDb(nameof(Snapshot_NoData_IsNotReady_WithoutThrowing));
        var rig = Build(db);
        var s = await rig.Snapshots.BuildAsync("BID", Now);
        Assert.Null(s.SignalBar);
        Assert.Contains(s.Unavailable, u => u.Section == "signalBar");
        Assert.Equal(("unavailable", "market-calendar-days-empty"), (s.Calendar.Status, s.Calendar.Reason));
        Assert.Contains(s.Unavailable, u => u is { Section: "calendar", Reason: "market-calendar-days-empty" });
        var r = EquityResearchSnapshotService.Readiness(s);
        Assert.Equal(EquityReadinessStatuses.NotReady, r.Purposes["technical"].Status);
        Assert.Contains("calendar", r.Purposes["technical"].Reasons);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Snapshots.BuildAsync("VCB", Now));
    }

    [Fact]
    public async Task Snapshot_FundamentalsSeriesCoverageAndReadiness_FromValidatedFacts()
    {
        var db = NewDb(nameof(Snapshot_FundamentalsSeriesCoverageAndReadiness_FromValidatedFacts));
        await SeedAsync(db);
        var rig = Build(db);
        var published = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var doc = new EquitySourceDocument
        {
            Id = Guid.NewGuid(), Symbol = "BID", Entity = "BIDV", DocType = "financial-statement", Title = "BCTC hop nhat Q2 2026",
            SourceUrl = "https://bidv.com.vn/ir/q2-2026.pdf", Provider = "issuer:BIDV", ContentSha256 = Sha64, StoragePath = "BID/x.pdf",
            ByteSize = 10, MimeType = "application/pdf", ReportingPeriod = "2026Q2", Consolidation = "consolidated", AuditStatus = "reviewed",
            PublishedAtUtc = published, FirstSeenAtUtc = published.AddDays(10), FetchedAtUtc = published.AddDays(10), Revision = 1,
            CreatedAtUtc = published.AddDays(10),
        };
        var extraction = new EquityDocumentExtraction
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, ExtractorVersion = "eqx-extract-v1", Method = "mixed", Status = "succeeded",
            PageCount = 60, PagesOcr = 12, StartedAtUtc = published.AddDays(10), FinishedAtUtc = published.AddDays(10).AddMinutes(9),
        };
        db.EquitySourceDocuments.Add(doc);
        db.EquityDocumentExtractions.Add(extraction);
        string[] balance = ["TOTAL_ASSETS", "TOTAL_EQUITY", "CUSTOMER_LOANS_GROSS", "CUSTOMER_DEPOSITS"];
        string[] flows = ["NET_INTEREST_INCOME", "PROFIT_BEFORE_TAX", "PROFIT_AFTER_TAX"];
        var quarters = new List<(string Label, DateOnly End)>();
        for (var (y, q, i) = (2026, 2, 0); i < 8; i++)
        {
            quarters.Add(($"{y}Q{q}", new DateOnly(y, q * 3, 1).AddMonths(1).AddDays(-1)));
            if (--q == 0) { q = 4; y--; }
        }
        EquityFinancialFact F(string metric, string label, DateOnly end, bool pit, string status = "validated") => new()
        {
            Id = Guid.NewGuid(), FactKey = $"BID/{metric}/{label}/{status}", Symbol = "BID", MetricCode = metric, MetricDefinitionVersion = "v1",
            MetricGroup = pit ? "balance-sheet" : "income-statement", Statement = pit ? "balance-sheet" : "income-statement",
            PeriodLabel = label, PeriodType = pit ? "point-in-time" : "quarter", PeriodEnd = end, FiscalYear = end.Year,
            Consolidation = "consolidated", Value = 1000.50m, Unit = "VND", Scale = 1_000_000m, RawValueText = "1.000,5",
            ColumnRole = "current", PageNumber = 5, DocumentId = doc.Id, ExtractionId = extraction.Id, Status = status,
            AvailableAtUtc = published, CreatedAtUtc = published.AddDays(10),
        };
        foreach (var (label, end) in quarters)
        {
            foreach (var m in balance) db.EquityFinancialFacts.Add(F(m, label, end, pit: true));
            foreach (var m in flows) db.EquityFinancialFacts.Add(F(m, label, end, pit: false));
        }
        db.EquityFinancialFacts.Add(F("NPL_GROUP3_5", "2026Q2", new DateOnly(2026, 6, 30), pit: true, status: "quarantined"));
        await db.SaveChangesAsync();

        var a = await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow());
        var b = await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow());
        Assert.Equal(a.SnapshotSha256, b.SnapshotSha256);
        var series = a.FundamentalsSeries!;
        Assert.Equal(("ok", 56, 0), (series.Status, series.FactCount, series.DuplicatesDropped));
        Assert.Equal(balance.Concat(flows).Order(StringComparer.Ordinal).ToArray(), series.Metrics.Select(m => m.MetricCode).ToArray());
        Assert.DoesNotContain(series.Metrics, m => m.MetricCode == "NPL_GROUP3_5"); // quarantined ⇒ not in the validated series
        var point = series.Metrics.Single(m => m.MetricCode == "TOTAL_ASSETS").Points[^1];
        Assert.Equal(("2026Q2", "point-in-time", "consolidated", 1000.50m, doc.Id, 5), (point.Period, point.PeriodType, point.Consolidation, point.Value, point.DocumentId, point.Page!.Value));
        var docRef = Assert.Single(a.SourceDocuments);
        Assert.Equal(("succeeded", "mixed", 57, 56), (docRef.Extraction!.Status, docRef.Extraction.Method!, docRef.Extraction.Facts, docRef.Extraction.ValidatedFacts));
        var coverage = a.FundamentalsCoverage!;
        Assert.Equal(("catalog-unavailable", "2016Q1", "2026Q3"), (coverage.Catalog.Status, coverage.FirstPeriod, coverage.LastPeriod));
        Assert.Contains(a.Unavailable, u => u.Section == "fundamentalsCoverage" && u.Reason.StartsWith("catalog-unavailable", StringComparison.Ordinal));
        Assert.Contains(coverage.Gaps, g => g.Period == "2026Q2" && g.MetricGroup == "balance-sheet" && g.Stage == "validated");
        Assert.True(coverage.GapsTruncated);

        var ready = EquityResearchSnapshotService.Readiness(a).Purposes["fundamental"];
        Assert.Equal("ready", ready.Status);
        Assert.Contains("informational", ready.Checks.Single(c => c.Name == "metrics-unavailable").Detail);
        Assert.Equal("pass", ready.Checks.Single(c => c.Name == "facts-core-quarters").Status);

        // A later quarantine removes the cell from the validated series ⇒ partial with the exact missing cell.
        var pat = await db.EquityFinancialFacts.SingleAsync(f => f.FactKey == "BID/PROFIT_AFTER_TAX/2025Q1/validated");
        pat.Status = "quarantined";
        await db.SaveChangesAsync();
        var partial = EquityResearchSnapshotService.Readiness(await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow())).Purposes["fundamental"];
        Assert.Equal("partial", partial.Status);
        var check = partial.Checks.Single(c => c.Name == "facts-core-quarters");
        Assert.Equal("warn", check.Status);
        Assert.Contains("1 missing validated cell(s): 2025Q1/consolidated/PROFIT_AFTER_TAX", check.Detail);

        // Before the document's availability nothing is visible (document, facts, extraction).
        var early = await rig.Snapshots.BuildAsync("BID", new DateTimeOffset(published.AddDays(-1)));
        Assert.Equal("empty", early.FundamentalsSeries!.Status);
        Assert.Empty(early.SourceDocuments);
        Assert.Contains(early.Unavailable, u => u.Section == "fundamentalsSeries" && u.Reason == "no-validated-facts-at-asof");
        Assert.Equal(0, early.FundamentalsCoverage!.Summary.Facts);
        Assert.Equal("not-ready", EquityResearchSnapshotService.Readiness(early).Purposes["fundamental"].Status);
    }

    [Fact]
    public async Task Snapshot_CarriesBackendCalendarProvenance_AndFlagsDrift()
    {
        var db = NewDb(nameof(Snapshot_CarriesBackendCalendarProvenance_AndFlagsDrift));
        await SeedAsync(db, 60);
        db.MarketCalendarDays.AddRange(
            new MarketCalendarDay { Venue = "HOSE", Date = new DateOnly(2026, 9, 1), Kind = "market-holiday", Reason = "National Day",
                SourceUrl = "https://hsx.vn/x", Verification = "verified", CalendarVersion = "vn-hose-calendar-v1",
                CoverageFromDate = new DateOnly(2020, 1, 1), CoverageToDate = new DateOnly(2026, 12, 31) },
            new MarketCalendarDay { Venue = "HOSE", Date = new DateOnly(2026, 9, 2), Kind = "market-holiday", Reason = "National Day",
                Verification = "unverified-legacy", CalendarVersion = "vn-hose-calendar-v1",
                CoverageFromDate = new DateOnly(2020, 1, 1), CoverageToDate = new DateOnly(2026, 12, 31) });
        await db.SaveChangesAsync();
        var matching = new VietnamMarketCalendar([new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2)]);

        var s = await Build(db, calendar: matching).Snapshots.BuildAsync("BID", Now);
        Assert.Equal(("ok", true), (s.Calendar.Status, s.Calendar.MatchesInMemoryCalendar));
        Assert.Equal(["vn-hose-calendar-v1"], s.Calendar.Versions);
        Assert.Equal((2, 1, 1), (s.Calendar.Count, s.Calendar.VerifiedCount, s.Calendar.UnverifiedLegacyCount));
        Assert.Equal(("2026-09-01", "2026-09-02"), (s.Calendar.FirstDate, s.Calendar.LastDate));
        // Same digest as ai/vietnam_market_calendar.MarketCalendar.sha256 (sorted ISO dates joined by LF).
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("2026-09-01\n2026-09-02"))).ToLowerInvariant(),
            s.Calendar.Sha256);

        var drift = await Build(db, calendar: new VietnamMarketCalendar([new DateOnly(2026, 9, 2)])).Snapshots.BuildAsync("BID", Now);
        Assert.Equal(("mismatch", false), (drift.Calendar.Status, drift.Calendar.MatchesInMemoryCalendar));
        Assert.Contains("calendar", EquityResearchSnapshotService.Readiness(drift).Purposes["technical"].Reasons);
        Assert.NotEqual(s.SnapshotSha256, drift.SnapshotSha256);
    }

    private static MarketCalendarDay CalendarRow(DateOnly date, DateOnly coverageTo) => new()
    {
        Venue = "HOSE", Date = date, Kind = "market-holiday", Reason = "test", SourceUrl = "https://hsx.vn/x",
        Verification = "verified", CalendarVersion = "vn-hose-test", CoverageFromDate = new DateOnly(2020, 1, 1),
        CoverageToDate = coverageTo,
    };

    [Fact]
    public async Task Snapshot_CoverageExpired_WhenNextSessionIsBeyondCoverage_2027()
    {
        var db = NewDb(nameof(Snapshot_CoverageExpired_WhenNextSessionIsBeyondCoverage_2027));
        var lastDay = new DateOnly(2026, 12, 31); // Thursday; the next weekday is 2027-01-01
        await SeedAsync(db, 60, lastDay);
        db.MarketCalendarDays.Add(CalendarRow(new DateOnly(2026, 9, 2), new DateOnly(2026, 12, 31)));
        await db.SaveChangesAsync();
        var cal = new VietnamMarketCalendar([new DateOnly(2026, 9, 2)]);
        var rig = Build(db, Local(2026, 12, 31, 16, 0), cal);

        var s = await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow());
        Assert.Equal(("coverage-expired", "calendar-coverage-expired"), (s.Calendar.Status, s.Calendar.Reason));
        Assert.Equal(("2020-01-01", "2026-12-31", "2027-01-01"), (s.Calendar.CoverageFrom, s.Calendar.CoverageTo, s.Calendar.NextSessionCheckDate));
        Assert.Contains(s.Unavailable, u => u is { Section: "calendar", Reason: "calendar-coverage-expired" });
        Assert.True(s.Calendar.BarsBeforeCoverage!["1d"] == 0);
        var technical = EquityResearchSnapshotService.Readiness(s).Purposes["technical"];
        Assert.Equal(EquityReadinessStatuses.NotReady, technical.Status);
        Assert.Contains("calendar", technical.Reasons);

        var note = await rig.Notes.RecordAsync("BID", Note(s, "forward", Body(Probability())));
        Assert.Equal("CALENDAR_COVERAGE_EXPIRED", note.Code);
        Assert.Empty(await db.EquityResearchNotes.ToListAsync());

        // One session earlier the next session (2026-12-31) is still covered.
        var earlier = await rig.Snapshots.BuildAsync("BID", Local(2026, 12, 30, 16, 0));
        Assert.Equal("ok", earlier.Calendar.Status);
    }

    // --- freshness at requestedAsOf: data cutoff (last stored bar) vs expected data time (calendar) ----

    private static readonly DateOnly Fri16 = new(2026, 10, 16);
    private static long Open(DateOnly d) => VietnamMarketCalendar.DailyOpenUtcMs(d);

    private static EquityReadinessCheck FreshCheck(EquityResearchSnapshot s) =>
        EquityResearchSnapshotService.Readiness(s).Purposes["technical"].Checks.Single(c => c.Name == "latest-expected-bar-stored");

    [Fact]
    public async Task Freshness_TwoMissedSessions_IsNotReady_SameDataEarlierAsOfIsFresh_NoBarsLeak()
    {
        var db = NewDb(nameof(Freshness_TwoMissedSessions_IsNotReady_SameDataEarlierAsOfIsFresh_NoBarsLeak));
        await SeedAsync(db, 260, Fri16); // ingestion stopped after Fri 16/10: Mon 19 + Tue 20 never stored
        var rig = Build(db, Local(2026, 10, 21, 8, 0));

        var stale = await rig.Snapshots.BuildAsync("BID", Local(2026, 10, 20, 21, 0));
        Assert.Equal(Open(Fri16), stale.SignalBar!.OpenTimeMs);
        Assert.Equal(Cal.SessionCloseUtcMs("1d", Open(Fri16)), stale.EffectiveCutoffMs); // data cutoff unchanged
        Assert.Equal(Open(new DateOnly(2026, 10, 20)), stale.LatestExpectedOpenTimeMsAtAsOf);
        var check = FreshCheck(stale);
        Assert.Equal("fail", check.Status);
        Assert.Contains("stale at requestedAsOf 2026-10-20T14:00:00Z", check.Detail);
        Assert.Contains($"2026-10-20 (open {Open(new DateOnly(2026, 10, 20))})", check.Detail);
        Assert.Contains($"last stored 2026-10-16 (open {Open(Fri16)})", check.Detail);
        var technical = EquityResearchSnapshotService.Readiness(stale).Purposes["technical"];
        Assert.Equal(EquityReadinessStatuses.NotReady, technical.Status);
        Assert.Contains("latest-expected-bar-stored", technical.Reasons);
        // Completeness INSIDE the window is a separate question: the stored window itself has no gap.
        var day = stale.Coverage.Single(c => c.Timeframe == "1d");
        Assert.Equal((0L, true), (day.RecentMissingBars, day.UpToDate));

        // Same DB, asOf on the weekend right after the last stored session: nothing is missing yet.
        var weekend = await rig.Snapshots.BuildAsync("BID", Local(2026, 10, 18, 10, 0));
        Assert.Equal(Open(Fri16), weekend.LatestExpectedOpenTimeMsAtAsOf);
        Assert.Equal("pass", FreshCheck(weekend).Status);

        // Bars stored later never leak into an earlier asOf: series stop at that asOf's own cutoff.
        db.InstrumentCandles.AddRange(Bar("BID", new DateOnly(2026, 10, 19), 41_000m), Bar("BID", new DateOnly(2026, 10, 20), 41_100m));
        await db.SaveChangesAsync();
        var past = await rig.Snapshots.BuildAsync("BID", Local(2026, 10, 16, 20, 0));
        Assert.Equal(Open(Fri16), past.SignalBar!.OpenTimeMs);
        Assert.Equal(Open(Fri16), past.Dataset["1d"].LastOpenTimeMs);
        Assert.Equal(await db.InstrumentCandles.CountAsync(c => c.Symbol == "BID" && c.CloseTimeMs <= past.EffectiveCutoffMs),
            past.Dataset["1d"].BarCount);
        Assert.Equal("pass", FreshCheck(past).Status);
        var caughtUp = await rig.Snapshots.BuildAsync("BID", Local(2026, 10, 20, 21, 0));
        Assert.Equal(("pass", Open(new DateOnly(2026, 10, 20))), (FreshCheck(caughtUp).Status, caughtUp.SignalBar!.OpenTimeMs));
    }

    [Theory]
    [InlineData(11, 0, "pass")]  // Mon 19/10 mid-session: Monday is not closed ⇒ Friday is the expected bar
    [InlineData(14, 50, "pass")] // after the 14:45 close but inside the 10-minute publish grace
    [InlineData(14, 56, "fail")] // grace over ⇒ Monday expected, DB still at Friday
    public async Task Freshness_BeforeCloseAndInsidePublishGrace_IsNotStaleEarly(int hh, int mm, string status)
    {
        var db = NewDb($"{nameof(Freshness_BeforeCloseAndInsidePublishGrace_IsNotStaleEarly)}-{hh}{mm}");
        await SeedAsync(db, 260, Fri16);
        var asOf = Local(2026, 10, 19, hh, mm);
        var s = await Build(db, asOf).Snapshots.BuildAsync("BID", asOf);
        Assert.Equal(status, FreshCheck(s).Status);
        Assert.Equal(status == "pass" ? Open(Fri16) : Open(new DateOnly(2026, 10, 19)), s.LatestExpectedOpenTimeMsAtAsOf);
    }

    [Fact]
    public async Task Freshness_Holiday_DoesNotExpectANonexistentBar()
    {
        var db = NewDb(nameof(Freshness_Holiday_DoesNotExpectANonexistentBar));
        await SeedAsync(db, 260, Fri16);
        var asOf = Local(2026, 10, 19, 20, 0);
        var holiday = new VietnamMarketCalendar([new DateOnly(2026, 10, 19)]); // Monday closed
        var s = await Build(db, asOf, holiday).Snapshots.BuildAsync("BID", asOf);
        Assert.Equal((Open(Fri16), "pass"), (s.LatestExpectedOpenTimeMsAtAsOf, FreshCheck(s).Status));
        var trading = await Build(db, asOf).Snapshots.BuildAsync("BID", asOf); // same data, Monday a session
        Assert.Equal("fail", FreshCheck(trading).Status);
    }

    [Fact]
    public async Task Freshness_AsOfBeyondCalendarCoverage_FailsClosed()
    {
        var db = NewDb(nameof(Freshness_AsOfBeyondCalendarCoverage_FailsClosed));
        await SeedAsync(db, 60, new DateOnly(2026, 12, 31));
        db.MarketCalendarDays.Add(CalendarRow(new DateOnly(2026, 9, 2), new DateOnly(2026, 12, 31)));
        await db.SaveChangesAsync();
        var asOf = Local(2027, 1, 5, 10, 0);

        // MarketCalendarDays coverage ends 2026-12-31: 2027 holidays are unknown ⇒ no expected time, not "assume weekdays".
        var s = await Build(db, asOf, new VietnamMarketCalendar([new DateOnly(2026, 9, 2)])).Snapshots.BuildAsync("BID", asOf);
        Assert.Null(s.LatestExpectedOpenTimeMsAtAsOf);
        Assert.Equal(Open(new DateOnly(2026, 12, 31)), s.SignalBar!.OpenTimeMs); // data cutoff still honest
        Assert.Equal(("coverage-expired", "calendar-coverage-expired"), (s.Calendar.Status, s.Calendar.Reason));
        var check = FreshCheck(s);
        Assert.Equal("fail", check.Status);
        Assert.Contains("not evaluable at requestedAsOf 2027-01-05T03:00:00Z: calendar-coverage-expired (coverage to 2026-12-31)", check.Detail);
        Assert.Equal(EquityReadinessStatuses.NotReady, EquityResearchSnapshotService.Readiness(s).Purposes["technical"].Status);

        // The in-memory canonical calendar fails closed on its own coverage too (no MarketCalendarDays row needed).
        var memDb = NewDb(nameof(Freshness_AsOfBeyondCalendarCoverage_FailsClosed) + "-mem");
        await SeedAsync(memDb, 60, new DateOnly(2026, 12, 31));
        var canonical = VietnamMarketCalendar.FromSource(MarketCalendarSource.Default);
        var mem = await Build(memDb, asOf, canonical).Snapshots.BuildAsync("BID", asOf);
        Assert.Null(mem.LatestExpectedOpenTimeMsAtAsOf);
        Assert.Contains("not evaluable", FreshCheck(mem).Detail);
        Assert.Equal("fail", FreshCheck(mem).Status);
        // Before coverage (Tet 2019 holidays are not in the calendar): not evaluable, never a false "stale".
        var tetDb = NewDb(nameof(Freshness_AsOfBeyondCalendarCoverage_FailsClosed) + "-tet");
        await SeedAsync(tetDb, 60, new DateOnly(2019, 2, 1));
        var tet = await Build(tetDb, Local(2019, 2, 6, 20, 0), canonical).Snapshots.BuildAsync("BID", Local(2019, 2, 6, 20, 0));
        Assert.Null(tet.LatestExpectedOpenTimeMsAtAsOf);
        Assert.Contains("not evaluable", FreshCheck(tet).Detail);
    }

    [Fact]
    public async Task Freshness_NoBars_AndRealGap_StayHonest()
    {
        var empty = await Build(NewDb(nameof(Freshness_NoBars_AndRealGap_StayHonest) + "-empty")).Snapshots.BuildAsync("BID", Now);
        Assert.Equal(Wed, empty.LatestExpectedOpenTimeMsAtAsOf);
        Assert.Equal("fail", FreshCheck(empty).Status);
        Assert.Contains("last stored none", FreshCheck(empty).Detail);

        // Fresh at the end, but a real gap inside the window: freshness passes, completeness does not.
        var db = NewDb(nameof(Freshness_NoBars_AndRealGap_StayHonest) + "-gap");
        await SeedAsync(db);
        db.InstrumentCandles.Remove(await db.InstrumentCandles.SingleAsync(c => c.Symbol == "BID" && c.OpenTimeMs == Open(new DateOnly(2026, 10, 7))));
        await db.SaveChangesAsync();
        var s = await Build(db).Snapshots.BuildAsync("BID", Now);
        Assert.Equal("pass", FreshCheck(s).Status);
        var gaps = EquityResearchSnapshotService.Readiness(s).Purposes["technical"].Checks.Single(c => c.Name == "recent-gaps");
        Assert.Equal(("warn", "1 missing of the last 250 expected 1d sessions"), (gaps.Status, gaps.Detail));
    }

    // --- discovery attempts are as-of: bookkeeping after asOf never rewrites a past snapshot ------------

    private static EquitySourceAttempt Attempt(string period, DateTimeOffset at, string outcome = "not-listed") => new()
    {
        Symbol = "BID", Source = "bidv-ir-library", TargetPeriod = period, DocType = "financial-statement",
        Consolidation = "consolidated", AttemptedAtUtc = at.UtcDateTime, Outcome = outcome,
    };

    private static EquitySourceDocument StatementDoc(string period, DateTime? published, DateTime firstSeen, int revision = 1, Guid? supersedes = null) => new()
    {
        Id = Guid.NewGuid(), Symbol = "BID", Entity = "BIDV", DocType = "financial-statement", Title = $"BCTC hop nhat {period} r{revision}",
        SourceUrl = $"https://bidv.com.vn/ir/{period}-r{revision}.pdf", Provider = "issuer:BIDV", ContentSha256 = Sha64, StoragePath = $"BID/{period}-r{revision}.pdf",
        ByteSize = 10, MimeType = "application/pdf", ReportingPeriod = period, Consolidation = "consolidated", AuditStatus = "reviewed",
        PublishedAtUtc = published, FirstSeenAtUtc = firstSeen, FetchedAtUtc = firstSeen, Revision = revision, SupersedesDocumentId = supersedes,
        CreatedAtUtc = firstSeen,
    };

    private static EquityFinancialFact Fact(EquitySourceDocument doc, string metric, decimal value) => new()
    {
        Id = Guid.NewGuid(), FactKey = $"BID/{doc.Id:N}/{metric}", Symbol = "BID", MetricCode = metric, MetricDefinitionVersion = "v1",
        MetricGroup = "balance-sheet", Statement = "balance-sheet", PeriodLabel = doc.ReportingPeriod!, PeriodType = "point-in-time",
        PeriodEnd = new DateOnly(2026, 6, 30), FiscalYear = 2026, Consolidation = "consolidated", Value = value, Unit = "VND",
        Scale = 1_000_000m, RawValueText = value.ToString(System.Globalization.CultureInfo.InvariantCulture), ColumnRole = "current",
        PageNumber = 5, DocumentId = doc.Id, Status = "validated", AvailableAtUtc = doc.PublishedAtUtc ?? doc.FirstSeenAtUtc,
        CreatedAtUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), // extracted long after publication: never hides it
    };

    private static IReadOnlyList<EquityFundamentalsCoverageGap> Q3Gaps(EquityResearchSnapshot s) =>
        s.FundamentalsCoverage!.Gaps.Where(g => g is { Period: "2026Q3", Consolidation: "consolidated" }).ToList();

    [Fact]
    public async Task Snapshot_AttemptsAfterAsOf_KeepEvidenceAndHash_RealChangesStillChangeIt()
    {
        var db = NewDb(nameof(Snapshot_AttemptsAfterAsOf_KeepEvidenceAndHash_RealChangesStillChangeIt));
        await SeedAsync(db);
        var published = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
        var doc = StatementDoc("2026Q2", published, published.AddDays(80));
        var fact = Fact(doc, "TOTAL_ASSETS", 3_440_841m);
        var before = Attempt("2026Q3", Now.AddHours(-2));
        db.EquitySourceDocuments.Add(doc);
        db.EquityFinancialFacts.Add(fact);
        db.EquitySourceAttempts.Add(before);
        await db.SaveChangesAsync();
        var rig = Build(db);

        var a = await rig.Snapshots.BuildAsync("BID", Now);
        Assert.Equal("equity-research-snapshot-method-v2", a.MethodVersion);
        Assert.NotEmpty(Q3Gaps(a));
        Assert.All(Q3Gaps(a), g => Assert.Equal(("not-listed", before.Id), (g.Stage, g.AttemptId!.Value)));
        Assert.Equal(1, a.FundamentalsCoverage!.Summary.Attempts);
        // Before that attempt was made the system had not looked: honest "no-attempt", not a later observation.
        var earlier = await rig.Snapshots.BuildAsync("BID", Now.AddHours(-3));
        Assert.All(Q3Gaps(earlier), g => Assert.Equal(("not-attempted", "no-attempt", (long?)null), (g.Stage, g.Reason, g.AttemptId)));

        // Worker polls after asOf (bookkeeping only; even a later "listed"): same evidence, same id and hash.
        db.EquitySourceAttempts.AddRange(Attempt("2026Q3", Now.AddHours(2)), Attempt("2026Q3", Now.AddDays(1), "listed"));
        // A statement first seen after asOf is excluded as before.
        var later = StatementDoc("2026Q3", null, Now.AddDays(1).UtcDateTime);
        db.EquitySourceDocuments.Add(later);
        db.EquityFinancialFacts.Add(Fact(later, "TOTAL_EQUITY", 198_783m));
        await db.SaveChangesAsync();
        var b = await rig.Snapshots.BuildAsync("BID", Now);
        Assert.Equal((a.SnapshotId, a.SnapshotSha256), (b.SnapshotId, b.SnapshotSha256));
        Assert.Equal(JsonSerializer.Serialize(a.FundamentalsCoverage, Web), JsonSerializer.Serialize(b.FundamentalsCoverage, Web));
        Assert.DoesNotContain(b.SourceDocuments, d => d.DocumentId == later.Id);

        // Real changes still move the hash: a fact value, then a revision available at asOf.
        var stored = await db.EquityFinancialFacts.SingleAsync(f => f.Id == fact.Id);
        stored.Value = 3_440_900m;
        await db.SaveChangesAsync();
        var c = await rig.Snapshots.BuildAsync("BID", Now);
        Assert.NotEqual(a.SnapshotSha256, c.SnapshotSha256);
        var revisionLater = StatementDoc("2026Q2", null, Now.AddHours(1).UtcDateTime, revision: 2, supersedes: doc.Id);
        db.EquitySourceDocuments.Add(revisionLater);
        await db.SaveChangesAsync();
        Assert.Equal(c.SnapshotSha256, (await rig.Snapshots.BuildAsync("BID", Now)).SnapshotSha256); // first seen after asOf
        var revision = StatementDoc("2026Q2", null, Now.AddHours(-1).UtcDateTime, revision: 3, supersedes: doc.Id);
        db.EquitySourceDocuments.Add(revision);
        await db.SaveChangesAsync();
        var d = await rig.Snapshots.BuildAsync("BID", Now);
        Assert.NotEqual(c.SnapshotSha256, d.SnapshotSha256);
        Assert.Contains(d.SourceDocuments, x => x.DocumentId == revision.Id);
    }

    [Fact]
    public async Task ForwardNote_StillVerifies_AfterPostExportBookkeeping()
    {
        var db = NewDb(nameof(ForwardNote_StillVerifies_AfterPostExportBookkeeping));
        await SeedAsync(db);
        db.EquitySourceAttempts.Add(Attempt("2026Q3", Now.AddHours(-2)));
        await db.SaveChangesAsync();
        var rig = Build(db);
        var snap = await rig.Snapshots.BuildAsync("BID", Now);
        db.EquitySourceAttempts.Add(Attempt("2026Q3", Now.AddMinutes(1))); // worker poll between export and record
        await db.SaveChangesAsync();
        rig.Clock.Now = Now.AddMinutes(5);
        var result = await rig.Notes.RecordAsync("BID", Note(snap, "forward", Body()));
        Assert.Equal(EquityResearchNoteService.Outcome.Created, result.Outcome);
        Assert.True(result.Item!.SnapshotVerified);
        Assert.Equal(snap.SnapshotId, result.Item.SnapshotId);
    }

    [Fact]
    public async Task SourceDocuments_ServerStampedAvailability_GatesDocumentsAndTheirMetrics()
    {
        var db = NewDb(nameof(SourceDocuments_ServerStampedAvailability_GatesDocumentsAndTheirMetrics));
        await SeedAsync(db, 60);
        var docsRoot = Path.Combine(_root, "docs2");
        Directory.CreateDirectory(Path.Combine(docsRoot, "BID"));
        async Task<EquitySourceDocumentDto> Register(string text, DateTime? publishedAt, DateTimeOffset at)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(docsRoot, "BID", $"{sha}.pdf"), bytes);
            var r = await new EquitySourceDocumentService(db, docsRoot, new TestTimeProvider(at)).RegisterAsync("BID", new()
            {
                Entity = "BIDV", DocType = "financial-statement", Title = text, SourceUrl = "https://bidv.com.vn/x.pdf",
                Provider = "issuer:BIDV", ContentSha256 = sha, StoragePath = $"BID/{sha}.pdf", MimeType = "application/pdf",
                ReportingPeriod = "2026Q2", PublishedAtUtc = publishedAt,
                FirstSeenAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), // backdating attempt: ignored
                FetchedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            return r.Result!.Item;
        }
        var registeredAt = Local(2026, 10, 10, 10, 0);
        var doc = await Register("%PDF statement A", null, registeredAt);
        Assert.Equal(registeredAt.UtcDateTime, doc.FirstSeenAtUtc);
        Assert.Equal((registeredAt.UtcDateTime, "first-seen"), (doc.AvailableAtUtc, doc.AvailabilityBasis));
        var published = await Register("%PDF statement B", new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc), registeredAt);
        Assert.Equal("issuer-published", published.AvailabilityBasis);

        // Issuer-report metric whose own availableAt predates its document's registration.
        db.EquityFundamentals.Add(new EquityFundamental
        {
            Symbol = "BID", Period = "2026Q2", PeriodEndDate = new DateOnly(2026, 6, 30), Metric = "NET_PROFIT", Value = 1234m,
            Unit = "VND", AvailableAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), AsOfUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            Source = "issuer-report", SourceUrl = "https://bidv.com.vn/x.pdf", Provenance = "issuer:BIDV",
            DocumentId = doc.DocumentId, SourceLocation = "p.3 row 12",
        });
        // A second metric from the same document that only becomes available on 2026-10-13.
        db.EquityFundamentals.Add(new EquityFundamental
        {
            Symbol = "BID", Period = "2026Q2", PeriodEndDate = new DateOnly(2026, 6, 30), Metric = "ROE", Value = 17.5m,
            Unit = "percent", AvailableAtUtc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc), AsOfUtc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
            Source = "issuer-report", SourceUrl = "https://bidv.com.vn/x.pdf", Provenance = "issuer:BIDV",
            DocumentId = doc.DocumentId, SourceLocation = "p.4 row 2",
        });
        await db.SaveChangesAsync();
        var rig = Build(db);

        var before = await rig.Snapshots.BuildAsync("BID", Local(2026, 9, 30, 16, 0));
        Assert.Equal([published.DocumentId], before.SourceDocuments.Select(d => d.DocumentId).ToArray());
        Assert.DoesNotContain(before.Fundamentals.Items, i => i.Metric == "NET_PROFIT" && i.Value is not null);

        var after = await rig.Snapshots.BuildAsync("BID", Local(2026, 10, 12, 16, 0));
        Assert.Contains(after.SourceDocuments, d => d.DocumentId == doc.DocumentId && d.AvailabilityBasis == "first-seen");
        Assert.Contains(after.Fundamentals.Items, i => i.Metric == "NET_PROFIT" && i.Value == 1234m);

        // hasExtraction reflects stored metric values, not "an extraction record exists".
        var refA = after.SourceDocuments.Single(d => d.DocumentId == doc.DocumentId);
        var refB = after.SourceDocuments.Single(d => d.DocumentId == published.DocumentId);
        Assert.Equal((true, 1), (refA.HasExtraction, refA.ExtractedMetricCount)); // ROE not yet available at Oct 12
        var atNow = await rig.Snapshots.BuildAsync("BID", Now);
        Assert.Equal(2, atNow.SourceDocuments.Single(d => d.DocumentId == doc.DocumentId).ExtractedMetricCount);
        Assert.Equal((false, 0), (refB.HasExtraction, refB.ExtractedMetricCount));
        var listed = await new EquitySourceDocumentService(db, docsRoot, new TestTimeProvider(Now)).ListAsync("BID");
        Assert.Equal(2, listed.Items.Single(d => d.DocumentId == doc.DocumentId).ExtractedMetricCount);

        // Write-once fill of an issuer-stated publication DATE on the existing document (A had none).
        var service = new EquitySourceDocumentService(db, docsRoot, new TestTimeProvider(Now));
        var bytesA = Encoding.UTF8.GetBytes("%PDF statement A");
        var shaA = Convert.ToHexString(SHA256.HashData(bytesA)).ToLowerInvariant();
        EquitySourceDocumentRegistration Again(string? date) => new()
        {
            Entity = "BIDV", DocType = "financial-statement", Title = "%PDF statement A", SourceUrl = "https://bidv.com.vn/x.pdf",
            Provider = "issuer:BIDV", ContentSha256 = shaA, StoragePath = $"BID/{shaA}.pdf", MimeType = "application/pdf",
            ReportingPeriod = "2026Q2", PublishedDate = date,
        };
        var fill = await service.RegisterAsync("BID", Again("2026-08-29"));
        Assert.True(fill.Result!.PublishedAtFilled);
        Assert.False(fill.Result.Created);
        // date-only ⇒ 00:00 VN of the NEXT day = 2026-08-29T17:00Z
        Assert.Equal(new DateTime(2026, 8, 29, 17, 0, 0, DateTimeKind.Utc), fill.Result.Item.PublishedAtUtc);
        Assert.Equal(("issuer-published", new DateTime(2026, 8, 29, 17, 0, 0, DateTimeKind.Utc)),
            (fill.Result.Item.AvailabilityBasis, fill.Result.Item.AvailableAtUtc));
        var same = await service.RegisterAsync("BID", Again("2026-08-29"));
        Assert.Equal((EquitySourceDocumentService.Outcome.Ok, false), (same.Outcome, same.Result!.PublishedAtFilled));
        var change = await service.RegisterAsync("BID", Again("2026-08-01"));
        Assert.Equal("DOCUMENT_PUBLISHED_AT_IMMUTABLE", change.Code);
        Assert.Equal("INVALID_SOURCE_DOCUMENT", (await service.RegisterAsync("BID", Again("29/08/2026"))).Code);
        // Now available (with its metric) for an asOf before the original registration.
        var nowVisible = await rig.Snapshots.BuildAsync("BID", Local(2026, 9, 30, 16, 0));
        Assert.Contains(nowVisible.SourceDocuments, d => d.DocumentId == doc.DocumentId);
        Assert.Contains(nowVisible.Fundamentals.Items, i => i.Metric == "NET_PROFIT" && i.Value == 1234m);
    }

    [Fact]
    public void AsOfParsing_IsStrict()
    {
        Assert.True(EquityResearchSnapshotService.TryParseAsOf(null, Now, out var d, out _));
        Assert.Equal(Now.ToUnixTimeMilliseconds(), d.ToUnixTimeMilliseconds());
        Assert.True(EquityResearchSnapshotService.TryParseAsOf("2026-10-01T09:00:00Z", Now, out d, out _));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), d);
        Assert.True(EquityResearchSnapshotService.TryParseAsOf("2026-10-01T16:00:00+07:00", Now, out d, out _));
        Assert.True(EquityResearchSnapshotService.TryParseAsOf("1790000000000", Now, out d, out _));
        Assert.False(EquityResearchSnapshotService.TryParseAsOf("2026-10-01T09:00:00", Now, out _, out _)); // naive
        Assert.False(EquityResearchSnapshotService.TryParseAsOf("2027-01-01T00:00:00Z", Now, out _, out var err));
        Assert.Contains("future", err);
        Assert.False(EquityResearchSnapshotService.TryParseAsOf("yesterday", Now, out _, out _));
    }

    // --- research notes -----------------------------------------------------------------------

    private static JsonElement Body(JsonObject? probability = null)
    {
        var body = new JsonObject
        {
            ["schemaVersion"] = "equity-research-note-v1",
            ["thesis"] = "Range-bound near 40k; RS vs VNINDEX flat.",
            ["evidence"] = new JsonArray(new JsonObject { ["claim"] = "20d RS ~ 0", ["evidenceIds"] = new JsonArray("rs.VNINDEX.20") }),
            ["counterEvidence"] = new JsonArray("Q3 earnings pending"),
            ["scenarios"] = new JsonArray(new JsonObject
            {
                ["name"] = "base", ["horizonSessions"] = 20, ["description"] = "sideways",
                ["invalidation"] = new JsonArray("close < 38k"),
            }),
            ["limitations"] = new JsonArray("latest-adjusted prices"),
            ["missingData"] = new JsonArray("NPL"),
            ["outcomeCheck"] = new JsonObject { ["how"] = "barrier tb-p5-s4-h20", ["dueAfterSessions"] = 20 },
        };
        if (probability is not null) body["probability"] = probability;
        return JsonDocument.Parse(body.ToJsonString()).RootElement.Clone();
    }

    private static JsonObject Probability(string? methodRef = "ai/skills/investment-research/references/method.md") => new()
    {
        ["configId"] = "tb-p5-s4-h20", ["probTargetFirst"] = 0.35, ["probStopFirst"] = 0.4,
        ["method"] = "base-rate-adjusted-judgement", ["methodRef"] = methodRef,
    };

    private static EquityResearchNoteRequest Note(EquityResearchSnapshot s, string mode, JsonElement body, long? signal = null) => new()
    {
        Mode = mode,
        RequestedAsOf = s.RequestedAsOfMs.ToString(),
        SignalBarOpenTimeMs = signal ?? s.SignalBar!.OpenTimeMs,
        HorizonSessions = 20,
        SnapshotId = s.SnapshotId,
        SnapshotSha256 = s.SnapshotSha256,
        SkillVersion = "investment-research/0.1.0",
        MethodVersion = "session-method-v1",
        Direction = "sideways",
        ActionView = "wait",
        ActionBasis = "market-only",
        Body = body,
    };

    [Fact]
    public async Task ForwardNote_WithProbability_AddsSessionResearchForecast_ThatResolves()
    {
        var db = NewDb(nameof(ForwardNote_WithProbability_AddsSessionResearchForecast_ThatResolves));
        await SeedAsync(db);
        var rig = Build(db);
        var snap = await rig.Snapshots.BuildAsync("BID", Now);

        var result = await rig.Notes.RecordAsync("BID", Note(snap, "forward", Body(Probability())));

        Assert.Equal(EquityResearchNoteService.Outcome.Created, result.Outcome);
        var item = result.Item!;
        Assert.Matches(@"^session/BID/\d+/[0-9a-f]{8}$", item.NoteId);
        Assert.True(item.SnapshotVerified);
        Assert.Equal(Wed, item.SignalBarOpenTimeMs);
        var forecast = Assert.Single(await db.EquityForecastRecords.ToListAsync());
        Assert.Equal("session-research", forecast.Source);
        Assert.False(forecast.Validated);
        Assert.Equal("base-rate-adjusted-judgement", forecast.MethodVersion);
        Assert.Equal(0.05, forecast.TargetPct);
        Assert.Equal(20, forecast.HorizonSessions);
        Assert.Equal(snap.SnapshotSha256, forecast.InputSnapshotSha256);
        Assert.Equal(item.ForecastIds.Single(), forecast.ForecastId);
        var stored = await db.EquityResearchNotes.SingleAsync();
        Assert.Equal(forecast.ResearchNoteId, stored.Id);

        var list = await rig.Notes.ListAsync("BID", 10);
        Assert.Equal(item.NoteId, list.Items.Single().NoteId);
        Assert.Equal(forecast.ForecastId, list.Items.Single().ForecastIds.Single());
        Assert.Equal("Range-bound near 40k; RS vs VNINDEX flat.", list.Items.Single().Body!.Value.GetProperty("thesis").GetString());

        // The journal lists it and the outcome resolver labels it like quant/LLM rows.
        var journal = await rig.Journal.GetJournalAsync("BID", null, 50);
        Assert.Contains(journal.Items, i => i.Source == "session-research" && i.ForecastId == forecast.ForecastId);
        var next = Cal.EnumerateTradingDates(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 30)).ToArray();
        db.InstrumentCandles.Add(Bar("BID", next[0], 40_300m));
        var spike = Bar("BID", next[1], 40_300m);
        spike.High = 50_000m;
        db.InstrumentCandles.Add(spike);
        await db.SaveChangesAsync();
        rig.Clock.Now = Local(2026, 10, 16, 16, 0);
        Assert.Equal(1, await rig.Journal.ResolveOutcomesAsync("BID"));
        Assert.Equal("target-first", (await db.EquityForecastRecords.SingleAsync()).OutcomeLabel);
    }

    [Fact]
    public async Task ForwardNote_Guards_SignalBar_Window_Snapshot_AndProbabilityMethod()
    {
        var db = NewDb(nameof(ForwardNote_Guards_SignalBar_Window_Snapshot_AndProbabilityMethod));
        await SeedAsync(db);
        var rig = Build(db);
        var snap = await rig.Snapshots.BuildAsync("BID", Now);

        var notLatest = await rig.Notes.RecordAsync("BID", Note(snap, "forward", Body(), signal: Tue));
        Assert.Equal(("SIGNAL_BAR_NOT_LATEST", EquityResearchNoteService.Outcome.SignalBarNotLatest), (notLatest.Code, notLatest.Outcome));

        var tampered = Note(snap, "forward", Body());
        tampered.SnapshotSha256 = Sha64;
        var mismatch = await rig.Notes.RecordAsync("BID", tampered);
        Assert.Equal("SNAPSHOT_MISMATCH", mismatch.Code);
        Assert.Contains("Snapshot content changed since export (e.g. artifact rebuilt", mismatch.Message);

        var noRef = await rig.Notes.RecordAsync("BID", Note(snap, "forward", Body(Probability(methodRef: " "))));
        Assert.Equal("PROBABILITY_METHOD_REQUIRED", noRef.Code);

        var badBody = Body();
        var withExtra = JsonNode.Parse(badBody.GetRawText())!.AsObject();
        withExtra["position"] = 700;
        var extra = await rig.Notes.RecordAsync("BID", Note(snap, "forward", JsonDocument.Parse(withExtra.ToJsonString()).RootElement.Clone()));
        Assert.Equal("INVALID_NOTE", extra.Code);

        var badBasis = Note(snap, "forward", Body());
        badBasis.ActionBasis = "insufficient-input";
        Assert.Equal("INVALID_NOTE", (await rig.Notes.RecordAsync("BID", badBasis)).Code);

        rig.Clock.Now = Local(2026, 10, 15, 9, 30); // Thu session open passed; Wed is still the latest finalized bar
        var closed = await rig.Notes.RecordAsync("BID", Note(snap, "forward", Body()));
        Assert.Equal("FORECAST_WINDOW_CLOSED", closed.Code);

        Assert.Empty(await db.EquityResearchNotes.ToListAsync());
        Assert.Empty(await db.EquityForecastRecords.ToListAsync());
    }

    [Fact]
    public async Task RetrospectiveNote_IsStored_ButNeverCreatesForecasts()
    {
        var db = NewDb(nameof(RetrospectiveNote_IsStored_ButNeverCreatesForecasts));
        await SeedAsync(db);
        var rig = Build(db);
        var snap = await rig.Snapshots.BuildAsync("BID", Local(2026, 9, 30, 16, 0));

        var result = await rig.Notes.RecordAsync("BID", Note(snap, "retrospective", Body(Probability())));

        Assert.Equal(EquityResearchNoteService.Outcome.Created, result.Outcome);
        Assert.Empty(result.Item!.ForecastIds);
        Assert.Empty(await db.EquityForecastRecords.ToListAsync());
        var note = await db.EquityResearchNotes.SingleAsync();
        Assert.Equal("retrospective", note.Mode);
        Assert.Equal(snap.SignalBar!.OpenTimeMs, note.SignalBarOpenTimeMs);
        Assert.True(note.AvailableTimeMs > note.SignalBarCloseTimeMs);
        Assert.Equal(snap.RequestedAsOf, note.RequestedAsOfUtc);
    }

    // --- source documents ---------------------------------------------------------------------

    [Fact]
    public async Task SourceDocuments_RegisterVerifiesFileUnderRoot_AndIsIdempotent()
    {
        var db = NewDb(nameof(SourceDocuments_RegisterVerifiesFileUnderRoot_AndIsIdempotent));
        var docsRoot = Path.Combine(_root, "docs");
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.7 fake statement");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Directory.CreateDirectory(Path.Combine(docsRoot, "BID"));
        await File.WriteAllBytesAsync(Path.Combine(docsRoot, "BID", $"{sha}.pdf"), bytes);
        var service = new EquitySourceDocumentService(db, docsRoot, new TestTimeProvider(Now));
        EquitySourceDocumentRegistration Reg() => new()
        {
            Entity = "BIDV", DocType = "financial-statement", Title = "BCTC hợp nhất Q2/2026", SourceUrl = "https://bidv.com.vn/x.pdf",
            Provider = "issuer:BIDV", ContentSha256 = sha, StoragePath = $"BID/{sha}.pdf", MimeType = "application/pdf",
            ReportingPeriod = "2026Q2", Consolidation = "consolidated", AuditStatus = "reviewed",
        };

        var created = await service.RegisterAsync("BID", Reg());
        Assert.Equal(EquitySourceDocumentService.Outcome.Ok, created.Outcome);
        Assert.True(created.Result!.Created);
        Assert.Equal((1, (long)bytes.Length), (created.Result.Item.Revision, created.Result.Item.ByteSize));
        var again = await service.RegisterAsync("BID", Reg());
        Assert.False(again.Result!.Created);
        Assert.Equal(created.Result.Item.DocumentId, again.Result.Item.DocumentId);

        var wrongHash = Reg();
        wrongHash.ContentSha256 = Sha64;
        Assert.Equal("DOCUMENT_HASH_MISMATCH", (await service.RegisterAsync("BID", wrongHash)).Code);
        var missing = Reg();
        missing.StoragePath = "BID/nope.pdf";
        Assert.Equal("DOCUMENT_FILE_NOT_FOUND", (await service.RegisterAsync("BID", missing)).Code);
        foreach (var bad in new[] { "../outside.pdf", "BID/../../x.pdf", Path.Combine(docsRoot, "BID", $"{sha}.pdf") })
        {
            var r = Reg();
            r.StoragePath = bad;
            Assert.Equal("INVALID_SOURCE_DOCUMENT", (await service.RegisterAsync("BID", r)).Code);
        }
        var badEnum = Reg();
        badEnum.DocType = "rumor";
        Assert.Equal("INVALID_SOURCE_DOCUMENT", (await service.RegisterAsync("BID", badEnum)).Code);
        var upper = Reg();
        upper.ContentSha256 = sha.ToUpperInvariant();
        Assert.Equal("INVALID_SOURCE_DOCUMENT", (await service.RegisterAsync("BID", upper)).Code);

        var list = await service.ListAsync("BID");
        Assert.Equal("equity-source-documents-v1", list.ContractVersion);
        Assert.Single(list.Items);
        Assert.Equal(EquitySourceDocumentService.Outcome.RootUnavailable,
            (await new EquitySourceDocumentService(db, null).RegisterAsync("BID", Reg())).Outcome);

        // The snapshot lists only documents first seen by asOf.
        var rig = Build(db);
        Assert.Single((await rig.Snapshots.BuildAsync("BID", Now)).SourceDocuments);
        Assert.Empty((await rig.Snapshots.BuildAsync("BID", Local(2026, 9, 1, 12, 0))).SourceDocuments);
    }

    // --- PostgreSQL (BTC_TEST_POSTGRES → btc_eqx_test only; clock pinned to 2003, cleaned up) ----

    [Fact]
    public async Task Postgres_SnapshotInRepeatableRead_AndForwardNoteSatisfiesConstraints()
    {
        if (PostgresTestDb.ConnectionString() is not { } cs) return;
        var windowEnd = new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        AppDbContext Pg() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);
        async Task Cleanup()
        {
            await using var db = Pg();
            await db.EquityForecastRecords.Where(f => f.SignalBarOpenTimeMs < windowEnd).ExecuteDeleteAsync();
            await db.EquityResearchNotes.Where(n => n.SignalBarOpenTimeMs < windowEnd).ExecuteDeleteAsync();
            await db.InstrumentCandles.Where(c => (c.Symbol == "BID" || c.Symbol == "VNINDEX") && c.OpenTimeMs < windowEnd).ExecuteDeleteAsync();
            await db.EquitySourceAttempts.Where(a => a.Symbol == "BID" && a.AttemptedAtUtc < new DateTime(2005, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ExecuteDeleteAsync();
        }
        await Cleanup();
        try
        {
            var last = new DateOnly(2003, 3, 12);
            await using (var seed = Pg())
            {
                foreach (var d in Cal.EnumerateTradingDates(last.AddDays(-60), last))
                    seed.InstrumentCandles.Add(Bar("BID", d, 30_000m, DateTime.SpecifyKind(new DateTime(2003, 3, 1), DateTimeKind.Utc)));
                seed.EquitySourceAttempts.Add(Attempt("2002Q4", Local(2003, 3, 12, 10, 0)));
                await seed.SaveChangesAsync();
            }
            await using var db = Pg();
            var rig = Build(db, Local(2003, 3, 12, 16, 0));
            var snap = await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow());
            Assert.Null(db.Database.CurrentTransaction); // RepeatableRead snapshot transaction closed
            Assert.Equal(VietnamMarketCalendar.DailyOpenUtcMs(last), snap.SignalBar!.OpenTimeMs);
            // Bookkeeping after asOf (a later poll; plus the real 2026 BID attempts already in btc_eqx_test) is not
            // visible at asOf: only the one 10:00 attempt counts and the hash is stable (query path on PostgreSQL).
            Assert.Equal(1, snap.FundamentalsCoverage!.Summary.Attempts);
            await using (var poll = Pg())
            {
                poll.EquitySourceAttempts.Add(Attempt("2002Q4", Local(2003, 3, 12, 18, 0)));
                await poll.SaveChangesAsync();
            }
            var again = await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow());
            Assert.Equal(1, again.FundamentalsCoverage!.Summary.Attempts);
            // Fresh → stale on PostgreSQL inside calendar coverage: Postgres_Freshness_SameStoredData_FreshThenStale_ByRequestedAsOf.

            // A failing calendar read inside the RepeatableRead transaction (missing relation via the test
            // seam; the real table is untouched) must degrade honestly, not abort the snapshot (savepoint).
            rig.Snapshots.CalendarReader = (pgDb, token) => pgDb.MarketCalendarDays
                .FromSqlRaw("SELECT * FROM \"MarketCalendarDays_missing_v2b_test\"").ToListAsync(token);
            var degraded = await rig.Snapshots.BuildAsync("BID", rig.Clock.GetUtcNow());
            Assert.Equal(("unavailable", "market-calendar-days-unreadable"), (degraded.Calendar.Status, degraded.Calendar.Reason));
            Assert.Equal(snap.SignalBar.OpenTimeMs, degraded.SignalBar!.OpenTimeMs); // later queries still ran
            Assert.Equal(snap.Dataset["1d"].Sha256, degraded.Dataset["1d"].Sha256);
            Assert.Null(db.Database.CurrentTransaction);
            rig.Snapshots.CalendarReader = (pgDb, token) => pgDb.MarketCalendarDays.AsNoTracking()
                .Where(d => d.Venue == "HOSE").OrderBy(d => d.Date).ToListAsync(token);
            Assert.Equal(snap.SnapshotSha256, again.SnapshotSha256);

            var result = await rig.Notes.RecordAsync("BID", Note(snap, "forward", Body(Probability())));
            Assert.Equal(EquityResearchNoteService.Outcome.Created, result.Outcome);
            await using var check = Pg();
            var forecast = await check.EquityForecastRecords.SingleAsync(f => f.SignalBarOpenTimeMs < windowEnd);
            Assert.Equal(("session-research", false), (forecast.Source, forecast.Validated));
            var note = await check.EquityResearchNotes.SingleAsync(n => n.SignalBarOpenTimeMs < windowEnd);
            note.Direction = "up";
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => check.SaveChangesAsync()); // immutable trigger
        }
        finally
        {
            await Cleanup();
        }
    }

    // --- PostgreSQL, self-contained: a throwaway database on the BTC_TEST_POSTGRES server, migrated from scratch,
    // seeded inside the canonical HOSE calendar coverage (2026), dropped afterwards. Real BID/MBB rows in btc_eqx_test
    // are never touched. Skipped (returns) only when BTC_TEST_POSTGRES is unset.

    private static readonly VietnamMarketCalendar Hose = VietnamMarketCalendar.FromSource(MarketCalendarSource.Default);
    private static readonly DateOnly Fri28Aug = new(2026, 8, 28); // next HOSE sessions: Thu 03/09, Fri 04/09 (31/08–02/09 closed)

    private async Task WithFreshPostgresDbAsync(Func<Func<AppDbContext>, Task> body)
    {
        if (PostgresTestDb.ConnectionString() is not { } cs) return;
        var name = $"btc_eqx_test_snapfix_{Guid.NewGuid():N}";
        var server = new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString;
        var fixture = new NpgsqlConnectionStringBuilder(cs) { Database = name, CommandTimeout = 300 }.ConnectionString;
        async Task Admin(string sql)
        {
            await using var c = new NpgsqlConnection(server);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, c);
            await cmd.ExecuteNonQueryAsync();
        }
        await Admin($"CREATE DATABASE \"{name}\"");
        try
        {
            AppDbContext Pg() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture).Options);
            await using (var db = Pg())
            {
                var current = await db.Database.SqlQueryRaw<string>("SELECT current_database() AS \"Value\"").SingleAsync();
                Assert.Equal(name, current); // every write below goes to this throwaway database only
                _output.WriteLine($"fixture database: {current}");
                await db.Database.MigrateAsync();
                var sync = await MarketCalendarSync.SyncAsync(db, MarketCalendarSource.Default); // real calendar rows (2020-2026)
                _output.WriteLine($"MarketCalendarDays synced: {sync}");
            }
            await body(Pg);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Admin($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
            _output.WriteLine($"dropped {name}");
        }
    }

    private static async Task SeedHoseBarsAsync(AppDbContext db, DateOnly last, int count = 260)
    {
        foreach (var (d, i) in Hose.EnumerateTradingDates(last.AddDays(-count * 2), last).TakeLast(count).Select((d, i) => (d, i)))
        {
            db.InstrumentCandles.Add(Bar("BID", d, 40_000m + i));
            db.InstrumentCandles.Add(Bar("VNINDEX", d, 1_200m + i));
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Postgres_Freshness_SameStoredData_FreshThenStale_ByRequestedAsOf() => await WithFreshPostgresDbAsync(async pg =>
    {
        await using (var seed = pg()) await SeedHoseBarsAsync(seed, Fri28Aug);
        var dOpen = Open(Fri28Aug);
        var dCutoff = Hose.SessionCloseUtcMs("1d", dOpen);

        // Fri 28/08 15:00: after the 14:45 close + 10 min publish grace ⇒ D expected, D stored ⇒ fresh.
        var freshAt = Local(2026, 8, 28, 15, 0);
        await using var db = pg();
        var fresh = await Build(db, freshAt, Hose).Snapshots.BuildAsync("BID", freshAt);
        Assert.Equal(("ok", true), (fresh.Calendar.Status, fresh.Calendar.MatchesInMemoryCalendar));
        Assert.Equal((dOpen, dOpen, dCutoff), (fresh.LatestExpectedOpenTimeMsAtAsOf!.Value, fresh.SignalBar!.OpenTimeMs, fresh.EffectiveCutoffMs));
        Assert.Equal("pass", FreshCheck(fresh).Status);
        Assert.Equal(EquityReadinessStatuses.Ready, EquityResearchSnapshotService.Readiness(fresh).Purposes["technical"].Status);

        // Wed 02/09 20:00 (31/08–02/09 are HOSE holidays): no new session is due yet ⇒ still fresh.
        var holiday = await Build(db, Local(2026, 9, 2, 20, 0), Hose).Snapshots.BuildAsync("BID", Local(2026, 9, 2, 20, 0));
        Assert.Equal((dOpen, "pass"), (holiday.LatestExpectedOpenTimeMsAtAsOf!.Value, FreshCheck(holiday).Status));

        // Same DB, Fri 04/09 15:00: two HOSE sessions (03/09, 04/09) closed + grace, nothing new stored ⇒ stale.
        var staleAt = Local(2026, 9, 4, 15, 0);
        var stale = await Build(db, staleAt, Hose).Snapshots.BuildAsync("BID", staleAt);
        var expected = Open(new DateOnly(2026, 9, 4));
        Assert.Equal(expected, stale.LatestExpectedOpenTimeMsAtAsOf);
        Assert.Equal((dOpen, dCutoff), (stale.SignalBar!.OpenTimeMs, stale.EffectiveCutoffMs)); // data cutoff still D
        Assert.Equal((dOpen, (long)await db.InstrumentCandles.CountAsync(c => c.Symbol == "BID" && c.Timeframe == "1d")),
            (stale.Dataset["1d"].LastOpenTimeMs!.Value, (long)stale.Dataset["1d"].BarCount)); // nothing after the cutoff exists or is served
        var check = FreshCheck(stale);
        Assert.Equal("fail", check.Status);
        Assert.Equal($"stale at requestedAsOf 2026-09-04T08:00:00Z: expected finalized 1d session 2026-09-04 (open {expected}) but last stored 2026-08-28 (open {dOpen})", check.Detail);
        var technical = EquityResearchSnapshotService.Readiness(stale).Purposes["technical"];
        Assert.Equal(EquityReadinessStatuses.NotReady, technical.Status);
        Assert.Contains("latest-expected-bar-stored", technical.Reasons);
        _output.WriteLine($"fresh {fresh.SnapshotId} | {FreshCheck(fresh).Detail}");
        _output.WriteLine($"holiday {holiday.SnapshotId} | {FreshCheck(holiday).Detail}");
        _output.WriteLine($"stale {stale.SnapshotId} | {check.Detail}");
    });

    [Fact]
    public async Task Postgres_AttemptAfterAsOf_KeepsHashIdAndGapAttemptIds() => await WithFreshPostgresDbAsync(async pg =>
    {
        var asOf = Local(2026, 8, 28, 15, 0);
        // Document published before asOf, OCR'd and validated long after it: retrospective processing stays visible.
        var published = new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);
        var doc = StatementDoc("2026Q1", published, new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc));
        var extraction = new EquityDocumentExtraction
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, ExtractorVersion = "eqx-facts-v4", Method = "ocr", Status = "succeeded",
            PageCount = 60, PagesOcr = 60, StartedAtUtc = new DateTime(2026, 10, 19, 2, 0, 0, DateTimeKind.Utc),
            FinishedAtUtc = new DateTime(2026, 10, 19, 2, 9, 0, DateTimeKind.Utc),
        };
        var fact = Fact(doc, "TOTAL_ASSETS", 3_300_000m);
        fact.PeriodEnd = new DateOnly(2026, 3, 31);
        fact.ExtractionId = extraction.Id;
        var before = Attempt("2026Q2", asOf.AddHours(-3)); // the latest quarter ended before asOf ⇒ newest gaps
        await using (var seed = pg())
        {
            await SeedHoseBarsAsync(seed, Fri28Aug);
            seed.EquitySourceDocuments.Add(doc);
            seed.EquityDocumentExtractions.Add(extraction);
            seed.EquityFinancialFacts.Add(fact);
            seed.EquitySourceAttempts.Add(before);
            await seed.SaveChangesAsync();
        }

        await using var db = pg();
        var a = await Build(db, asOf, Hose).Snapshots.BuildAsync("BID", asOf);
        static bool Target(EquityFundamentalsCoverageGap g) => g is { Period: "2026Q2", PeriodKind: "quarter", Consolidation: "consolidated" };
        var target = a.FundamentalsCoverage!.Gaps.Where(Target).ToList();
        Assert.Equal(7, target.Count); // catalog unavailable in tests ⇒ every metric group is a cell
        Assert.All(target, g => Assert.Equal(("not-listed", before.Id), (g.Stage, g.AttemptId!.Value)));
        Assert.Equal(1, a.FundamentalsCoverage.Summary.Attempts);
        Assert.Equal(("ok", 1), (a.FundamentalsSeries!.Status, a.FundamentalsSeries.FactCount)); // OCR time never hides it
        Assert.Equal("succeeded", Assert.Single(a.SourceDocuments).Extraction!.Status);

        // A later worker poll, written through another connection, stamped after asOf.
        long laterId;
        await using (var poll = pg())
        {
            var later = Attempt("2026Q2", asOf.AddHours(2));
            poll.EquitySourceAttempts.Add(later);
            await poll.SaveChangesAsync();
            laterId = later.Id;
        }
        await using (var check = pg())
            Assert.Equal(2, await check.EquitySourceAttempts.CountAsync(x => x.Symbol == "BID" && x.TargetPeriod == "2026Q2"));

        var b = await Build(db, asOf, Hose).Snapshots.BuildAsync("BID", asOf);
        Assert.Equal((a.SnapshotId, a.SnapshotSha256), (b.SnapshotId, b.SnapshotSha256));
        Assert.Equal(1, b.FundamentalsCoverage!.Summary.Attempts);
        Assert.All(b.FundamentalsCoverage.Gaps.Where(Target), g => Assert.Equal(before.Id, g.AttemptId));
        Assert.DoesNotContain(b.FundamentalsCoverage.Gaps, g => g.AttemptId == laterId);

        // The insert is real and not ignored: an asOf after it sees the new attempt.
        var afterAt = asOf.AddHours(3);
        var c = await Build(db, afterAt, Hose).Snapshots.BuildAsync("BID", afterAt);
        Assert.Equal(2, c.FundamentalsCoverage!.Summary.Attempts);
        Assert.All(c.FundamentalsCoverage.Gaps.Where(Target), g => Assert.Equal(laterId, g.AttemptId));
        _output.WriteLine($"A {a.SnapshotId} == B {b.SnapshotId}; before={before.Id} later={laterId}; after-asOf view {c.SnapshotId}");
    });

    // --- HTTP wiring (scope, asOf validation, AdminGuard) --------------------------------------

    public sealed class ResearchFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddEquityResearch();
                var db = services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList();
                foreach (var d in db) services.Remove(d);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("v2b-http"));
            });
        }
    }

    [Fact]
    public async Task Http_ScopeAsOfAndAdminGuard()
    {
        await using var factory = new ResearchFactory();
        var client = factory.CreateClient();

        var readiness = await client.GetAsync("/api/equities/BID/research-readiness");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        var r = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("requires-admin", r.GetProperty("purposes").GetProperty("portfolio-action").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/equities/BID/research-snapshot?asOf=2026-01-05T09:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/equities/MBB/source-documents")).StatusCode);

        async Task AssertCode(HttpResponseMessage response, HttpStatusCode status, string code)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
        await AssertCode(await client.GetAsync("/api/equities/BID/research-snapshot?asOf=2026-01-05T09:00:00"), HttpStatusCode.BadRequest, "INVALID_AS_OF");
        await AssertCode(await client.GetAsync("/api/equities/NOPE/research-snapshot"), HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");
        await AssertCode(await client.GetAsync("/api/equities/VCB/research-readiness"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertCode(await client.GetAsync("/api/equities/BTCUSDT/source-documents"), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/equities/BID/research-notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/equities/BID/research-notes", new { mode = "forward" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/equities/BID/admin/source-documents", new { title = "x" })).StatusCode);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);
        var list = await admin.GetAsync("/api/equities/BID/research-notes");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        await AssertCode(await admin.PostAsJsonAsync("/api/equities/BID/research-notes", new { mode = "sideways" }),
            HttpStatusCode.BadRequest, "INVALID_NOTE");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

/// <summary>
/// EQX-A2 tests (EF InMemory + temp dirs, no network): barrier labeller parity with
/// ai/equity_base_rates.py, base-rates artifact verification, forecast journal recording /
/// write-once outcomes / scoring, context collector parsing from recorded-shape fixtures,
/// relative-strength alignment, and the context endpoint's unavailable rows.
/// </summary>
public class EquityResearchTests : IDisposable
{
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eqx-a2-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static DateTimeOffset Local(int y, int m, int d, int hh, int mm) => new(y, m, d, hh, mm, 0, TimeSpan.FromHours(7));

    private const double Fee = 0.0015, Tax = 0.001;
    private static double Net(double entry, double exit) => exit * (1 - Fee - Tax) / (entry * (1 + Fee)) - 1;

    // --- barrier labeller: mirrors tests/unit/test_equity_base_rates.py --------------------

    private static EquityBarrierLabeler.Outcome? LabelFirst(params (double O, double H, double L, double C)[] ohlc)
    {
        // Python cases are written with the signal bar at index 0; entry is bar 1.
        var bars = ohlc.Skip(1).Select((b, i) => new EquityBarrierLabeler.Bar(i, b.O, b.H, b.L, b.C)).ToArray();
        return EquityBarrierLabeler.Label(bars, 0.05, 0.04, 3, Fee, Fee, Tax);
    }

    private static readonly (double, double, double, double) Flat = (100.0, 100.5, 99.5, 100.0);

    [Fact]
    public void Labeler_MatchesPythonReferenceCases()
    {
        var target = LabelFirst(Flat, (100, 101, 99, 100), (100.5, 106, 99.5, 104), Flat, Flat)!;
        Assert.Equal(("target-first", 1), (target.Label, target.ResolveIndex)); // python resolve_idx 2 = entry+1
        Assert.Equal(Net(100, 105), target.NetReturn, 12);
        Assert.False(target.GapExit || target.DoubleTouch);

        var stop = LabelFirst(Flat, (100, 101, 95.5, 97), Flat, Flat, Flat)!;
        Assert.Equal(("stop-first", 0), (stop.Label, stop.ResolveIndex));
        Assert.Equal(Net(100, 96), stop.NetReturn, 12);

        var gapDown = LabelFirst(Flat, (100, 101, 99, 100), (93, 99, 92, 98), Flat, Flat)!;
        Assert.True(gapDown is { Label: "stop-first", GapExit: true });
        Assert.Equal(Net(100, 93), gapDown.NetReturn, 12);
        Assert.True(gapDown.NetReturn < Net(100, 96));

        var gapUp = LabelFirst(Flat, (100, 101, 99, 100), (107, 108, 106, 107), Flat, Flat)!;
        Assert.True(gapUp is { Label: "target-first", GapExit: true });
        Assert.Equal(Net(100, 105), gapUp.NetReturn, 12);

        var both = LabelFirst(Flat, (100, 106, 95, 101), Flat, Flat, Flat)!;
        Assert.True(both is { Label: "stop-first", DoubleTouch: true });
        Assert.Equal(Net(100, 96), both.NetReturn, 12);

        var timeout = LabelFirst(Flat, (100, 101, 99, 100), (100, 102, 98, 101), (101, 103, 99, 102.5), Flat)!;
        Assert.Equal(("timeout", 2), (timeout.Label, timeout.ResolveIndex)); // python resolve_idx 3
        Assert.Equal(Net(100, 102.5), timeout.NetReturn, 12);

        Assert.Null(LabelFirst(Flat, (100, 101, 99, 100), (100, 102, 98, 101))); // immature
        Assert.Equal("target-first", LabelFirst(Flat, (100, 106, 99.5, 105))!.Label); // early touch matures
        Assert.Equal(0.9975 / 1.0015 - 1, EquityBarrierLabeler.NetReturn(100, 100, Fee, Fee, Tax), 12);
    }

    // --- base-rates artifact verification ---------------------------------------------------

    private static readonly VietnamMarketCalendar Cal = new([]);
    private static readonly DateTimeOffset Now = Local(2026, 10, 14, 16, 0);
    private static readonly long Wed = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 14));
    private static readonly DateTime Updated = new(2026, 10, 14, 8, 30, 15, 123, DateTimeKind.Utc);

    private static async Task<List<InstrumentCandle>> SeedBarsAsync(AppDbContext db, string symbol, int count,
        DateOnly last, Func<int, decimal>? close = null)
    {
        var dates = Cal.EnumerateTradingDates(last.AddDays(-count * 2), last).TakeLast(count).ToArray();
        var rows = dates.Select((d, i) =>
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(d);
            var px = close?.Invoke(i) ?? 40_000m;
            return new InstrumentCandle
            {
                Symbol = symbol, Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = Cal.SessionCloseUtcMs("1d", open),
                Open = px, High = px + 100, Low = px - 100, Close = px, Volume = 1_000,
                Provider = "VCI", PriceBasis = symbol == "VNINDEX" ? "raw" : "adjusted", Venue = "HOSE",
                SourceTimezone = "Asia/Ho_Chi_Minh", FetchedAtUtc = Updated, UpdatedAtUtc = Updated.AddTicks(-i),
            };
        }).ToList();
        db.InstrumentCandles.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    private static JsonObject Artifact(string symbol, IReadOnlyList<InstrumentCandle> rows, bool gatePassed = true,
        bool currentAbstained = false)
    {
        JsonObject Config(string id, double t, double s, int h) => new()
        {
            ["configId"] = id, ["targetPct"] = t, ["stopPct"] = s, ["horizonSessions"] = h,
            ["validation"] = new JsonObject
            {
                ["nOos"] = 900, ["nEffective"] = 45, ["brierKnn"] = 0.21, ["brierPrior"] = 0.24, ["brierDiffMean"] = -0.03,
                ["brierDiffCi90"] = new JsonArray(-0.05, -0.01), ["ece"] = 0.03,
                ["calibrationBins"] = new JsonArray(new JsonObject
                    { ["lower"] = 0.0, ["upper"] = 0.2, ["n"] = 5, ["meanPredicted"] = 0.1, ["observedRate"] = null }),
                ["coverage"] = 1.0, ["abstentionRate"] = 0.0, ["evNetOosMean"] = 0.001, ["evNetOosWhenPositive"] = null,
                ["gatePassed"] = gatePassed,
                ["gateReasons"] = gatePassed ? new JsonArray() : new JsonArray("ece-above-0.05"),
            },
            ["current"] = new JsonObject
            {
                ["signalBarOpenTimeMs"] = rows[^1].OpenTimeMs,
                ["probTargetFirst"] = currentAbstained ? null : 0.4, ["probStopFirst"] = currentAbstained ? null : 0.45,
                ["probTimeout"] = currentAbstained ? null : 0.15, ["evNetFraction"] = currentAbstained ? null : -0.002,
                ["k"] = 50, ["nEligible"] = 800,
                ["prior"] = new JsonObject { ["probTargetFirst"] = 0.44, ["probStopFirst"] = 0.48, ["probTimeout"] = 0.08 },
                ["abstained"] = currentAbstained, ["abstainReason"] = currentAbstained ? "insufficient-warmup" : null,
            },
        };
        return new JsonObject
        {
            ["schemaVersion"] = "equity-base-rates-artifact-v1", ["symbol"] = symbol, ["timeframe"] = "1d",
            ["generatedAtUtc"] = "2026-10-14T09:00:00.000000Z", ["dataAsOfOpenTimeMs"] = rows[^1].OpenTimeMs,
            ["priceBasis"] = "adjusted", ["provider"] = "VCI",
            ["dataset"] = new JsonObject
            {
                ["barCount"] = rows.Count, ["firstOpenTimeMs"] = rows[0].OpenTimeMs, ["lastOpenTimeMs"] = rows[^1].OpenTimeMs,
                ["maxUpdatedAtUtc"] = EquityBaseRatesService.FormatFingerprintTimestamp(rows.Max(r => r.UpdatedAtUtc)),
                ["maxRevision"] = 0, ["sha256"] = new string('a', 64),
            },
            ["method"] = new JsonObject { ["name"] = "knn-euclidean-standardized", ["version"] = "equity-knn-v1" },
            ["protocol"] = new JsonObject { ["version"] = "equity-base-rates-protocol-v1" },
            ["configs"] = new JsonArray(Config("tb-p5-s4-h20", 0.05, 0.04, 20), Config("tb-p10-s6-h60", 0.10, 0.06, 60)),
        };
    }

    private void WriteArtifact(string dirSymbol, string json)
    {
        var dir = Path.Combine(_root, "equity-base-rates", dirSymbol);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "base-rates.json"), json);
    }

    private EquityBaseRatesService BaseRates(AppDbContext db, TimeProvider? tp = null, long maxBytes = 4 * 1024 * 1024) =>
        new(db, _root, maxBytes, Cal, timeProvider: tp ?? new TestTimeProvider(Now));

    [Fact]
    public async Task BaseRates_FreshGatePassed_ExposesDecisionUse()
    {
        var db = NewDb(nameof(BaseRates_FreshGatePassed_ExposesDecisionUse));
        var rows = await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 14));
        WriteArtifact("BID", Artifact("BID", rows).ToJsonString());

        var r = await BaseRates(db).GetAsync("BID");

        Assert.Equal("equity-base-rates-v1", r.ContractVersion);
        Assert.Equal("ok", r.Status);
        Assert.Empty(r.Reasons);
        Assert.Equal(2, r.Configs.Count);
        var c = r.Configs[0];
        Assert.False(c.Abstained);
        Assert.Equal(0.4, c.DecisionUse!.ProbTargetFirst);
        Assert.Equal("validated", c.Research!.Label);
        Assert.Equal([-0.05, -0.01], c.Validation!.BrierDiffCi90!.ToArray());
        Assert.Equal(new string('a', 64), r.Artifact!.DatasetSha256);
    }

    [Fact]
    public async Task BaseRates_GateFailed_ResearchUnvalidated_NoDecisionUse()
    {
        var db = NewDb(nameof(BaseRates_GateFailed_ResearchUnvalidated_NoDecisionUse));
        var rows = await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 14));
        WriteArtifact("BID", Artifact("BID", rows, gatePassed: false).ToJsonString());

        var r = await BaseRates(db).GetAsync("BID");

        Assert.Equal("abstained", r.Status);
        Assert.Contains("gate-not-passed", r.Reasons);
        Assert.All(r.Configs, c =>
        {
            Assert.Null(c.DecisionUse);
            Assert.False(c.Abstained);
            Assert.Equal("unvalidated", c.Research!.Label);
        });
    }

    [Fact]
    public async Task BaseRates_NewerStoredBar_IsStaleAsOf()
    {
        var db = NewDb(nameof(BaseRates_NewerStoredBar_IsStaleAsOf));
        var rows = await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 13));
        WriteArtifact("BID", Artifact("BID", rows).ToJsonString());
        db.InstrumentCandles.Add(new InstrumentCandle
        {
            Symbol = "BID", Timeframe = "1d", OpenTimeMs = Wed, CloseTimeMs = Cal.SessionCloseUtcMs("1d", Wed),
            Open = 1, High = 1, Low = 1, Close = 1, Volume = 1, Provider = "VCI", PriceBasis = "adjusted",
            Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh", UpdatedAtUtc = Updated,
        });
        await db.SaveChangesAsync();

        var r = await BaseRates(db).GetAsync("BID");

        Assert.Equal("abstained", r.Status);
        Assert.Equal(["artifact-stale-asof"], r.Reasons.ToArray());
        Assert.All(r.Configs, c =>
        {
            Assert.True(c.Abstained);
            Assert.Null(c.DecisionUse);
            Assert.Equal("unvalidated", c.Research!.Label);
        });
    }

    [Fact]
    public async Task BaseRates_RevisedStoredBar_IsStaleRevision_AtMicrosecondPrecision()
    {
        var db = NewDb(nameof(BaseRates_RevisedStoredBar_IsStaleRevision_AtMicrosecondPrecision));
        var rows = await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 14));
        WriteArtifact("BID", Artifact("BID", rows).ToJsonString());
        var bar = await db.InstrumentCandles.SingleAsync(c => c.OpenTimeMs == rows[0].OpenTimeMs);
        bar.UpdatedAtUtc = rows.Max(r => r.UpdatedAtUtc).AddTicks(10); // +1 microsecond
        await db.SaveChangesAsync();

        var r = await BaseRates(db).GetAsync("BID");

        Assert.Contains("artifact-stale-revision", r.Reasons);
        Assert.All(r.Configs, c => Assert.Null(c.DecisionUse));
        Assert.Equal("2026-10-14T08:30:15.123000Z", EquityBaseRatesService.FormatFingerprintTimestamp(Updated.AddTicks(7)));
    }

    [Fact]
    public async Task BaseRates_RejectsWrongSymbolSchemaAndTypes_AndNeverFallsBack()
    {
        var db = NewDb(nameof(BaseRates_RejectsWrongSymbolSchemaAndTypes_AndNeverFallsBack));
        var rows = await SeedBarsAsync(db, "MBB", 30, new DateOnly(2026, 10, 14));
        var svc = BaseRates(db);

        // MBB has a valid artifact; BID has none → BID is unavailable, never served MBB's numbers.
        WriteArtifact("MBB", Artifact("MBB", rows).ToJsonString());
        Assert.Equal("ok", (await svc.GetAsync("MBB")).Status);
        var bid = await svc.GetAsync("BID");
        Assert.Equal(("unavailable", "artifact-missing"), (bid.Status, bid.Reasons.Single()));
        Assert.Empty(bid.Configs);

        async Task Expect(string json, string reason)
        {
            WriteArtifact("MBB", json);
            var r = await svc.GetAsync("MBB");
            Assert.Equal("unavailable", r.Status);
            Assert.Equal(reason, r.Reasons.Single());
            Assert.Null(r.Artifact);
        }

        var wrongSymbol = Artifact("MBB", rows);
        wrongSymbol["symbol"] = "BID";
        await Expect(wrongSymbol.ToJsonString(), "artifact-symbol-mismatch");
        var wrongSchema = Artifact("MBB", rows);
        wrongSchema["schemaVersion"] = "equity-base-rates-artifact-v0";
        await Expect(wrongSchema.ToJsonString(), "artifact-schema-mismatch");
        var wrongBasis = Artifact("MBB", rows);
        wrongBasis["priceBasis"] = "raw";
        await Expect(wrongBasis.ToJsonString(), "artifact-price-basis-mismatch");
        var badType = Artifact("MBB", rows);
        badType["dataset"]!["barCount"] = "30";
        await Expect(badType.ToJsonString(), "artifact-invalid");
        var badCi = Artifact("MBB", rows);
        badCi["configs"]![0]!["validation"]!["brierDiffCi90"] = new JsonArray(-0.1);
        await Expect(badCi.ToJsonString(), "artifact-invalid");
        var badProb = Artifact("MBB", rows);
        badProb["configs"]![0]!["current"]!["probTargetFirst"] = 1.5;
        await Expect(badProb.ToJsonString(), "artifact-invalid");
        await Expect("[1,2,3]", "artifact-invalid");
        await Expect("{\"schemaVersion\":", "artifact-unreadable");

        WriteArtifact("MBB", Artifact("MBB", rows).ToJsonString());
        var tiny = BaseRates(db, maxBytes: 1_024);
        Assert.Equal("artifact-too-large", (await tiny.GetAsync("MBB")).Reasons.Single());
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetAsync("VCB"));
    }

    // --- forecast journal ----------------------------------------------------------------

    private EquityForecastJournalService Journal(AppDbContext db, TestTimeProvider tp) =>
        new(db, BaseRates(db, tp), Cal, tp);

    [Fact]
    public async Task Journal_RecordsLatestBarOnly_Idempotent_FreshVsMissing()
    {
        var db = NewDb(nameof(Journal_RecordsLatestBarOnly_Idempotent_FreshVsMissing));
        var tp = new TestTimeProvider(Now);
        var rows = await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 14));
        await SeedBarsAsync(db, "MBB", 30, new DateOnly(2026, 10, 14));
        WriteArtifact("BID", Artifact("BID", rows, gatePassed: false).ToJsonString());
        var journal = Journal(db, tp);

        var bid = await journal.RecordQuantAsync("BID");
        var again = await journal.RecordQuantAsync("BID");
        var mbb = await journal.RecordQuantAsync("MBB"); // no artifact

        Assert.Equal((2, 0), (bid.Recorded, bid.Abstained));
        Assert.Equal((0, 2), (again.Recorded, again.AlreadyRecorded));
        Assert.Equal((2, 2), (mbb.Recorded, mbb.Abstained));
        var bidRows = await db.EquityForecastRecords.Where(f => f.Symbol == "BID").OrderBy(f => f.ConfigId).ToListAsync();
        Assert.All(bidRows, f =>
        {
            Assert.Equal(Wed, f.SignalBarOpenTimeMs);
            Assert.Equal(0.4, f.ProbTargetFirst);
            Assert.False(f.Validated); // gate failed → research probability, unvalidated
            Assert.True(f.AvailableTimeMs >= f.SignalBarCloseTimeMs);
        });
        Assert.Equal($"quant-knn/BID/tb-p10-s6-h60/{Wed}", bidRows[0].ForecastId);
        var mbbRows = await db.EquityForecastRecords.Where(f => f.Symbol == "MBB").ToListAsync();
        Assert.All(mbbRows, f =>
        {
            Assert.True(f.Abstained);
            Assert.Null(f.ProbTargetFirst);
            Assert.Equal("artifact-missing", f.AbstainReason);
        });
    }

    [Fact]
    public async Task Journal_StaleArtifact_RecordsAbstained()
    {
        var db = NewDb(nameof(Journal_StaleArtifact_RecordsAbstained));
        var tp = new TestTimeProvider(Now);
        var rows = await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 13));
        WriteArtifact("BID", Artifact("BID", rows).ToJsonString());
        db.InstrumentCandles.Add(new InstrumentCandle
        {
            Symbol = "BID", Timeframe = "1d", OpenTimeMs = Wed, CloseTimeMs = Cal.SessionCloseUtcMs("1d", Wed),
            Open = 1, High = 1, Low = 1, Close = 1, Volume = 1, Provider = "VCI", PriceBasis = "adjusted",
            Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh", UpdatedAtUtc = Updated,
        });
        await db.SaveChangesAsync();

        await Journal(db, tp).RecordQuantAsync("BID");

        var all = await db.EquityForecastRecords.ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, f =>
        {
            Assert.True(f.Abstained);
            Assert.Contains("artifact-stale-asof", f.AbstainReason);
            Assert.False(f.Validated);
        });
    }

    private static EquityForecastRecord Forecast(string source, long signal, double? pt, string? label = null,
        string config = "tb-p5-s4-h20", int h = 20) => new()
    {
        Id = Guid.NewGuid(),
        ForecastId = EquityForecastJournalService.ForecastIdFor(source, "BID", config, signal,
            source == "llm-judge" ? Guid.NewGuid() : null),
        Source = source, Symbol = "BID", Timeframe = "1d", ConfigId = config, TargetPct = 0.05, StopPct = 0.04,
        HorizonSessions = h, SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = signal + 27_900_000,
        AvailableTimeMs = signal + 30_000_000, CreatedAtUtc = DateTime.UtcNow,
        ProbTargetFirst = pt, ProbStopFirst = pt is null ? null : 0.3, Abstained = pt is null,
        AbstainReason = pt is null ? "artifact-missing" : null, MethodVersion = "equity-knn-v1",
        InputSnapshotSha256 = new string('0', 64), LlmRunId = source == "llm-judge" ? Guid.NewGuid() : null,
        OutcomeLabel = label,
    };

    [Fact]
    public async Task Journal_ResolvesOutcome_WriteOnce_PendingStaysNull()
    {
        var db = NewDb(nameof(Journal_ResolvesOutcome_WriteOnce_PendingStaysNull));
        var tp = new TestTimeProvider(Local(2026, 10, 20, 16, 0));
        var signal = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 13));
        var resolvable = Forecast("quant-knn", signal, 0.4, h: 20);
        var pending = Forecast("quant-knn", signal, 0.4, config: "tb-p10-s6-h60", h: 60);
        pending.TargetPct = 0.10;
        pending.StopPct = 0.06;
        db.EquityForecastRecords.AddRange(resolvable, pending);
        // Entry 10-14 open 100; 10-15 high 106 ⇒ +5% target first (pending: +10% never touched, < 60 bars).
        var px = new (double O, double H, double L, double C)[] { (100, 101, 99, 100), (101, 106, 100, 105), (105, 106, 104, 105), (105, 106, 104, 105), (105, 106, 104, 105) };
        var days = Cal.EnumerateTradingDates(new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20)).ToArray();
        for (var i = 0; i < px.Length; i++)
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(days[i]);
            db.InstrumentCandles.Add(new InstrumentCandle
            {
                Symbol = "BID", Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = Cal.SessionCloseUtcMs("1d", open),
                Open = (decimal)px[i].O, High = (decimal)px[i].H, Low = (decimal)px[i].L, Close = (decimal)px[i].C,
                Volume = 1, Provider = "VCI", PriceBasis = "adjusted", Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh",
            });
        }
        await db.SaveChangesAsync();
        var journal = Journal(db, tp);

        Assert.Equal(1, await journal.ResolveOutcomesAsync("BID"));
        var row = await db.EquityForecastRecords.SingleAsync(f => f.Id == resolvable.Id);
        Assert.Equal("target-first", row.OutcomeLabel);
        Assert.Equal(VietnamMarketCalendar.DailyOpenUtcMs(days[0]), row.EntryBarOpenTimeMs);
        Assert.Equal(100m, row.EntryPrice);
        Assert.Equal(VietnamMarketCalendar.DailyOpenUtcMs(days[1]), row.ResolvedBarOpenTimeMs);
        Assert.Equal(Net(100, 105), row.OutcomeReturnNet!.Value, 12);
        Assert.Null((await db.EquityForecastRecords.SingleAsync(f => f.Id == pending.Id)).OutcomeLabel);

        // A later re-adjustment of the entry bar never rewrites a resolved outcome.
        var entryBar = await db.InstrumentCandles.SingleAsync(c => c.OpenTimeMs == row.EntryBarOpenTimeMs);
        entryBar.Open = 90m;
        await db.SaveChangesAsync();
        await journal.ResolveOutcomesAsync("BID"); // may resolve the other (pending) row; never this one again
        Assert.Equal(100m, (await db.EquityForecastRecords.AsNoTracking().SingleAsync(f => f.Id == resolvable.Id)).EntryPrice);
    }

    private static long Window(long signal) => EquityForecastJournalService.NextSessionOpenUtcMs(Cal, signal);

    [Fact]
    public void Journal_Scoring_ExcludesLookAhead_AndUsesEarliestLlmRun()
    {
        var signal = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 13)); // Tue
        var close = Window(signal);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(7)).ToUnixTimeMilliseconds(), close);
        var index = new Dictionary<long, int> { [signal] = 0 };
        EquityForecastRecord At(EquityForecastRecord r, long available, int createdMinute)
        {
            r.AvailableTimeMs = available;
            r.CreatedAtUtc = new DateTime(2026, 10, 13, 9, createdMinute, 0, DateTimeKind.Utc);
            return r;
        }
        var quant = At(Forecast("quant-knn", signal, 0.5, "target-first"), close - 3_600_000, 0);
        var earlyLlm = At(Forecast("llm-judge", signal, 0.6, "target-first"), close - 1_800_000, 1);
        var rerun = At(Forecast("llm-judge", signal, 0.99, "target-first"), close - 600_000, 2); // later re-run: never scored

        var s = EquityForecastJournalService.Score("tb-p5-s4-h20", [quant, earlyLlm, rerun], index, Window);
        Assert.Equal(1, s.PairedN);
        Assert.Equal(Math.Pow(0.6 - 1, 2), s.BrierLlm!.Value, 12);

        // LLM forecast made at/after the next session open → no pair (but still counted for coverage).
        var lateLlm = At(Forecast("llm-judge", signal, 0.6, "target-first"), close, 1);
        Assert.Equal("no-data", EquityForecastJournalService.Score("tb-p5-s4-h20", [quant, lateLlm], index, Window).Status);
        // Quant recorded after the window → listed, not paired.
        var lateQuant = At(Forecast("quant-knn", signal, 0.5, "target-first"), close + 1, 0);
        Assert.Equal(0, EquityForecastJournalService.Score("tb-p5-s4-h20", [lateQuant, earlyLlm], index, Window).PairedN);
    }

    [Fact]
    public void Journal_Scoring_SessionResearch_PairedPerSource_SameRules()
    {
        var signal = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 13));
        var close = Window(signal);
        var index = new Dictionary<long, int> { [signal] = 0, [signal + 86_400_000L] = 1 };
        EquityForecastRecord At(EquityForecastRecord r, long available)
        {
            r.AvailableTimeMs = available;
            return r;
        }
        var quant = At(Forecast("quant-knn", signal, 0.5, "target-first"), close - 3_600_000);
        var llm = At(Forecast("llm-judge", signal, 0.6, "target-first"), close - 1_800_000);
        var session = At(Forecast("session-research", signal, 0.7, "target-first"), close - 1_200_000);
        var sessionLater = At(Forecast("session-research", signal, 0.99, "target-first"), close - 600_000); // never scored
        var sessionLate = At(Forecast("session-research", signal + 86_400_000L, 0.9), Window(signal + 86_400_000L)); // after next open
        var quant2 = At(Forecast("quant-knn", signal + 86_400_000L, 0.4, "stop-first"), Window(signal + 86_400_000L) - 1);

        var s = EquityForecastJournalService.Score("tb-p5-s4-h20", [quant, llm, session, sessionLater, sessionLate, quant2], index, Window);

        // Top-level fields unchanged: quant vs llm-judge.
        Assert.Equal((1, Math.Pow(0.6 - 1, 2)), (s.PairedN, s.BrierLlm!.Value));
        var pairs = s.Pairs!.ToDictionary(p => p.Source);
        Assert.Equal(["llm-judge", "session-research"], s.Pairs!.Select(p => p.Source).ToArray());
        Assert.Equal(s.BrierLlm, pairs["llm-judge"].BrierChallenger);
        var sr = pairs["session-research"];
        Assert.Equal((1, 1, "insufficient-sample"), (sr.PairedN, sr.NEffective, sr.Status));
        Assert.Equal(Math.Pow(0.7 - 1, 2), sr.BrierChallenger!.Value, 12); // earliest in-window note only
        Assert.Equal(Math.Pow(0.5 - 1, 2), sr.BrierQuant!.Value, 12);
        Assert.Equal(sr.BrierChallenger - sr.BrierQuant, sr.BrierDiff);
        Assert.Equal(1.0, sr.CoverageChallenger!.Value, 12);

        // Abstained quant ⇒ no pair for either source.
        var abst = Forecast("quant-knn", signal, null);
        var none = EquityForecastJournalService.Score("tb-p5-s4-h20", [abst, session], index, Window);
        Assert.Equal("no-data", none.Pairs!.Single(p => p.Source == "session-research").Status);
    }

    [Fact]
    public void Journal_Scoring_PairedBrier_NonOverlapping_InsufficientSample()
    {
        var index = Enumerable.Range(0, 200).ToDictionary(i => 1_000_000L + i * 86_400_000L, i => i);
        long Bar(int i) => 1_000_000L + i * 86_400_000L;
        var rows = new List<EquityForecastRecord>
        {
            Forecast("quant-knn", Bar(0), 0.6, "target-first"),
            Forecast("llm-judge", Bar(0), 0.8, "target-first"),
            Forecast("quant-knn", Bar(5), 0.4, "stop-first"),   // overlaps bar 0 (H=20)
            Forecast("llm-judge", Bar(5), 0.1, "stop-first"),
            Forecast("quant-knn", Bar(25), 0.5, "timeout"),
            Forecast("llm-judge", Bar(25), 0.5, "timeout"),
            Forecast("quant-knn", Bar(30), null),                // abstained: not paired
            Forecast("llm-judge", Bar(30), 0.7),
            Forecast("quant-knn", Bar(40), 0.5),                 // unresolved: not paired
        };

        var s = EquityForecastJournalService.Score("tb-p5-s4-h20", rows, index, Window);

        Assert.Equal(3, s.PairedN);
        Assert.Equal(2, s.NEffective); // bars 0 and 25 (5 overlaps 0)
        Assert.Equal("insufficient-sample", s.Status);
        var bq = (Math.Pow(0.6 - 1, 2) + Math.Pow(0.4, 2) + Math.Pow(0.5, 2)) / 3;
        var bl = (Math.Pow(0.8 - 1, 2) + Math.Pow(0.1, 2) + Math.Pow(0.5, 2)) / 3;
        Assert.Equal(bq, s.BrierQuant!.Value, 12);
        Assert.Equal(bl, s.BrierLlm!.Value, 12);
        Assert.Equal(bl - bq, s.BrierDiff!.Value, 12);
        Assert.Equal(4.0 / 5, s.CoverageQuant!.Value, 12);
        Assert.Equal(1.0, s.CoverageLlm!.Value, 12);

        var none = EquityForecastJournalService.Score("tb-p5-s4-h20", [Forecast("quant-knn", Bar(0), 0.5)], index, Window);
        Assert.Equal(("no-data", 0), (none.Status, none.PairedN));
        Assert.Null(none.BrierQuant);

        var many = Enumerable.Range(0, 31).SelectMany(i => new[]
        {
            Forecast("quant-knn", Bar(i * 5), 0.5, "stop-first", h: 5),
            Forecast("llm-judge", Bar(i * 5), 0.5, "stop-first", h: 5),
        }).ToArray();
        Assert.Equal("ok", EquityForecastJournalService.Score("tb-p5-s4-h20", many, index, Window).Status);
    }

    // --- AI rebuild proxy + worker seam ------------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
    }

    private static HttpResponseMessage Json(System.Net.HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Rebuild_ProxiesAi_AndMapsFailures()
    {
        var db = NewDb(nameof(Rebuild_ProxiesAi_AndMapsFailures));
        var handler = new StubHandler(_ => Json(System.Net.HttpStatusCode.OK, "{\"status\":\"built\",\"symbol\":\"BID\"}"));
        var ok = await new EquityBaseRatesService(db, _root, 4096, Cal, new StubFactory(handler)).RebuildAsync("BID");
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal("built", ok.Summary!.Value.GetProperty("status").GetString());
        Assert.Equal("POST /api/equity-base-rates/build?symbol=BID", handler.Requests.Single());

        var rejected = await new EquityBaseRatesService(db, _root, 4096, Cal, new StubFactory(new StubHandler(_ =>
            Json(System.Net.HttpStatusCode.UnprocessableEntity,
                "{\"detail\":{\"code\":\"INSUFFICIENT_DATA\",\"reason\":\"insufficient-bars\",\"message\":\"few\"}}"))))
            .RebuildAsync("BID");
        Assert.Equal((422, "BASE_RATES_BUILD_REJECTED"), (rejected.StatusCode, rejected.Code));
        Assert.Contains("insufficient-bars", rejected.Message);

        var down = await new EquityBaseRatesService(db, _root, 4096, Cal, new StubFactory(new StubHandler(_ =>
            throw new HttpRequestException("refused")))).RebuildAsync("MBB");
        Assert.Equal((503, "BASE_RATES_SOURCE_UNAVAILABLE"), (down.StatusCode, down.Code));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new EquityBaseRatesService(db, _root, 4096, Cal, new StubFactory(handler)).RebuildAsync("VNINDEX"));
    }

    [Fact]
    public async Task PostProcessor_FailSoft_ContextOncePerSession()
    {
        EquitySessionPostProcessor.ResetContextSyncMemory();
        var db = NewDb(nameof(PostProcessor_FailSoft_ContextOncePerSession));
        var tp = new TestTimeProvider(Now);
        await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 14));
        var baseRates = new EquityBaseRatesService(db, _root, 4096, Cal,
            new StubFactory(new StubHandler(_ => throw new HttpRequestException("AI down"))), tp);
        var source = new FakeSource(VciFixture, KbsFixture);
        var post = new EquitySessionPostProcessor(baseRates, new EquityForecastJournalService(db, baseRates, Cal, tp),
            new EquityContextCollector(db, source, tp),
            Microsoft.Extensions.Options.Options.Create(new Backend.Options.EquityIngestionOptions()));
        var bid = InstrumentCatalog.Default.Get("BID")!;

        var first = await post.RunAsync(bid, Wed, default);
        var second = await post.RunAsync(bid, Wed, default);

        Assert.Contains("base-rates BASE_RATES_SOURCE_UNAVAILABLE", first);
        Assert.Contains("forecast recorded 2 (abstained 2)", first); // artifact missing ⇒ abstained rows, still journaled
        Assert.Contains("context succeeded", first);
        Assert.DoesNotContain("context", second);
        Assert.Equal(2, source.Urls.Count); // one VCI + one KBS call for the session
        Assert.Equal(2, await db.EquityForecastRecords.CountAsync());
        Assert.Null(await post.RunAsync(InstrumentCatalog.Default.Get("VCB")!, Wed, default)); // context-only: no-op
    }

    [Fact]
    public async Task PostProcessor_SkipsRebuildOnlyWhileArtifactMatchesStoredBars()
    {
        EquitySessionPostProcessor.ResetContextSyncMemory();
        var db = NewDb(nameof(PostProcessor_SkipsRebuildOnlyWhileArtifactMatchesStoredBars));
        var tp = new TestTimeProvider(Now);
        var rows = await SeedBarsAsync(db, "BID", 30, new DateOnly(2026, 10, 14));
        WriteArtifact("BID", Artifact("BID", rows).ToJsonString());
        var handler = new StubHandler(_ => Json(System.Net.HttpStatusCode.OK, "{\"status\":\"built\",\"symbol\":\"BID\"}"));
        var baseRates = new EquityBaseRatesService(db, _root, 4096, Cal, new StubFactory(handler), tp);
        var post = new EquitySessionPostProcessor(baseRates, new EquityForecastJournalService(db, baseRates, Cal, tp),
            new EquityContextCollector(db, new FakeSource(VciFixture, KbsFixture), tp),
            Microsoft.Extensions.Options.Options.Create(new Backend.Options.EquityIngestionOptions()));
        var bid = InstrumentCatalog.Default.Get("BID")!;

        Assert.Contains("base-rates up-to-date", await post.RunAsync(bid, Wed, default));
        Assert.Empty(handler.Requests);

        // A revised stored bar breaks the fingerprint: the next run rebuilds.
        var bar = await db.InstrumentCandles.OrderByDescending(c => c.OpenTimeMs).FirstAsync(c => c.Symbol == "BID");
        bar.Close += 1;
        bar.UpdatedAtUtc = bar.UpdatedAtUtc.AddSeconds(1);
        await db.SaveChangesAsync();
        Assert.Contains("base-rates rebuilt", await post.RunAsync(bid, Wed, default));
        Assert.Equal("POST /api/equity-base-rates/build?symbol=BID", handler.Requests.Single());
    }

    // --- context collector (recorded-shape fixtures, no network) -------------------------

    private const string VciFixture = """
        {"status":200,"msg":"success","data":{"totalElements":7,"content":[
          {"id":"1","ticker":"BID","eventCode":"ISS","eventTitleVi":"Phát hành cổ phiếu - Cổ phiếu thưởng tỉ lệ 6.8%","publicDate":"2026-08-12T00:00:00","recordDate":"2026-08-18T00:00:00","exrightDate":"2026-08-17T00:00:00","exerciseRatio":0.068433},
          {"id":"2","ticker":"BID","eventCode":"DIV","eventTitleVi":"Trả cổ tức bằng tiền mặt - Cả năm 2025 - 450 VND","publicDate":"2026-07-10T00:00:00","recordDate":"2026-07-20T00:00:00","exrightDate":"2026-07-17T00:00:00","exerciseRatio":0.045,"valuePerShare":450.0},
          {"id":"3","ticker":"BID","eventCode":"ISS","eventTitleVi":"Phát hành cổ phiếu - Phát hành riêng lẻ tỉ lệ 3.8%","publicDate":"2026-03-25T00:00:00","exrightDate":"2026-03-24T00:00:00","exerciseRatio":0.0375},
          {"id":"4","ticker":"BID","eventCode":"AGME","eventTitleVi":"BID - Tổ chức ĐHĐCĐ thường niên 2026","publicDate":"2026-03-05T00:00:00","issueDate":"2026-04-24T00:00:00"},
          {"id":"5","ticker":"BID","eventCode":"AIS","eventTitleVi":"BID - Niêm yết bổ sung 498.164.263 cổ phiếu","publicDate":"2026-09-25T00:00:00","issueDate":"2026-09-23T00:00:00"},
          {"id":"6","ticker":"BID","eventCode":"DDIND","eventTitleVi":"Giao dịch nội bộ","publicDate":"2026-09-01T00:00:00"},
          {"id":"7","ticker":"BID","eventCode":"DIV","eventTitleVi":"Không có ngày công bố","exrightDate":"2026-01-10T00:00:00","valuePerShare":100}
        ]}}
        """;

    private const string KbsFixture = """
        {"Audit":[],"Unit":[],"Head":[
          {"YearPeriod":2026,"TermCode":"Q2","PeriodEnd":"202606","ReportDate":"2026-07-30T00:00:00","DatePubDepartment":"2026-07-31T00:00:00"},
          {"YearPeriod":2026,"TermCode":"Q1","PeriodEnd":"202603","ReportDate":"2026-04-24T00:00:00","DatePubDepartment":"2026-04-24T00:00:00"}],
         "Content":{
          "Nhóm chỉ số Định giá":[
            {"ReportNormID":54,"Name":"BVPS","Unit":"VNĐ","Value1":27304.43,"Value2":27149.03},
            {"ReportNormID":55,"Name":"P/E","Unit":"Lần","Value1":9.27,"Value2":8.95},
            {"ReportNormID":57,"Name":"P/B","Unit":"Lần","Value1":1.55,"Value2":1.45}],
          "Nhóm chỉ số Sinh lợi":[
            {"ReportNormID":45,"Name":"ROEA","Unit":"%","Value1":4.31},
            {"ReportNormID":108,"Name":"NIM","Unit":"%","Value1":0.54},
            {"ReportNormID":122,"Name":"ROE Trailling","Unit":"%","Value1":18.24}],
          "Nhóm chỉ số Thanh khoản":[{"ReportNormID":80,"Name":"LDR","Unit":"%","Value1":78.87}],
          "Nhóm chỉ số Chất lượng tài sản":[{"ReportNormID":117,"Name":"LLR","Unit":"%","Value1":-1.41}]}}
        """;

    private sealed class FakeSource(string vci, string kbs) : IEquityContextSource
    {
        public List<string> Urls { get; } = [];
        public string VciEventsUrl(string symbol, DateOnly fromLocal, DateOnly toLocal, int page, int size) =>
            $"https://iq.vietcap.com.vn/api/iq-insight-service/v1/events?ticker={symbol}&page={page}&size={size}";
        public string KbsFinanceInfoUrl(string symbol) =>
            $"https://kbbuddywts.kbsec.com.vn/iis-server/investment/stock/finance-info/{symbol}?type=CSTC&termtype=2";
        public Task<string> GetAsync(string url, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            return Task.FromResult(url.Contains("vietcap") ? vci : kbs);
        }
    }

    [Fact]
    public void Collector_ParsesVciEvents_WithPointInTimeAvailability()
    {
        var nowUtc = Now.UtcDateTime;
        var parsed = EquityContextCollector.ParseVciEvents("BID", VciFixture, "https://iq.vietcap.com.vn/x", nowUtc);

        Assert.Equal(7, parsed.ItemsOnPage);
        Assert.Equal(2, parsed.Skipped); // insider deal + missing publicDate
        var actions = parsed.Actions.OrderByDescending(a => a.ExDate).ToArray();
        Assert.Equal(["bonus-shares", "cash-dividend", "other"], actions.Select(a => a.ActionType).ToArray());
        Assert.Equal(450m, actions[1].CashPerShareVnd);
        Assert.Equal("450.0 VND/share", actions[1].RatioText);
        Assert.All(actions, a =>
        {
            Assert.Null(a.AdjustmentFactor);
            Assert.Equal("unverified", a.VerificationStatus);
            Assert.Equal("provider:VCI", a.Provenance);
        });
        // publicDate 2026-08-12 (date-only) ⇒ available 2026-08-13 00:00 VN = 2026-08-12T17:00Z.
        Assert.Equal(new DateTime(2026, 8, 12, 17, 0, 0, DateTimeKind.Utc), actions[0].AvailableAtUtc);
        var events = parsed.Events.OrderBy(e => e.EventDate).ToArray();
        Assert.Equal([("agm", new DateOnly(2026, 4, 24)), ("additional-listing", new DateOnly(2026, 9, 23))],
            events.Select(e => (e.EventType, e.EventDate)).ToArray());
        Assert.Throws<MarketDataPayloadException>(() =>
            EquityContextCollector.ParseVciEvents("BID", "{\"data\":[]}", "https://x", nowUtc));
    }

    [Fact]
    public void Collector_ParsesKbsCstc_LatestQuarterOnly_PublishedUnits()
    {
        var parsed = EquityContextCollector.ParseKbsFinanceInfo("BID", KbsFixture, "https://kbs/x", Now.UtcDateTime);

        var byMetric = parsed.Rows.ToDictionary(r => r.Metric);
        Assert.Equal(["BVPS", "LDR", "LLR", "NIM", "PB", "ROE"], byMetric.Keys.Order().ToArray());
        Assert.All(parsed.Rows, r =>
        {
            Assert.Equal("2026Q2", r.Period);
            Assert.Equal(new DateOnly(2026, 6, 30), r.PeriodEndDate);
            Assert.Equal(new DateOnly(2026, 7, 31), r.PublishedDate);
            Assert.Equal(new DateTime(2026, 7, 31, 17, 0, 0, DateTimeKind.Utc), r.AvailableAtUtc);
            Assert.Equal("provider:KBS", r.Provenance);
        });
        Assert.Equal((27304.43m, "VND"), (byMetric["BVPS"].Value!.Value, byMetric["BVPS"].Unit));
        Assert.Equal((1.55m, "ratio"), (byMetric["PB"].Value!.Value, byMetric["PB"].Unit));
        Assert.Equal((18.24m, "percent"), (byMetric["ROE"].Value!.Value, byMetric["ROE"].Unit)); // trailing, not ROEA
        Assert.Equal(-1.41m, byMetric["LLR"].Value); // sign kept as published

        var badUnit = KbsFixture.Replace("\"ReportNormID\":57,\"Name\":\"P/B\",\"Unit\":\"Lần\"", "\"ReportNormID\":57,\"Name\":\"P/B\",\"Unit\":\"%\"");
        var skipped = EquityContextCollector.ParseKbsFinanceInfo("BID", badUnit, "https://kbs/x", Now.UtcDateTime);
        Assert.DoesNotContain(skipped.Rows, r => r.Metric == "PB");
        Assert.Contains(skipped.Warnings, w => w.Contains("PB unit"));
    }

    [Fact]
    public async Task Collector_SyncIsIdempotent_AndNeverOverwritesAdminRows()
    {
        var db = NewDb(nameof(Collector_SyncIsIdempotent_AndNeverOverwritesAdminRows));
        var source = new FakeSource(VciFixture, KbsFixture);
        var collector = new EquityContextCollector(db, source, new TestTimeProvider(Now));

        await collector.ImportCorporateActionsAsync("BID", [new EquityCorporateActionImport
        {
            ExDate = "2026-07-17", ActionType = "cash-dividend", CashPerShareVnd = 450, SourceUrl = "https://hose.vn/a",
            VerificationStatus = "verified", AnnouncedDate = "2026-07-10",
        }], default);
        var first = await collector.SyncAsync("BID");
        var second = await collector.SyncAsync("BID");

        Assert.Equal("succeeded", first.Status);
        Assert.Equal(2, first.ProviderRequests);
        Assert.Equal((2, 2, 6), (first.CorporateActionsUpserted, first.EventsUpserted, first.FundamentalsUpserted));
        Assert.Equal((0, 0, 0), (second.CorporateActionsUpserted, second.EventsUpserted, second.FundamentalsUpserted));
        var div = await db.EquityCorporateActions.SingleAsync(a => a.ActionType == "cash-dividend");
        Assert.Equal(("admin-manual", "verified"), (div.Provenance, div.VerificationStatus));
        Assert.Equal(3, await db.EquityCorporateActions.CountAsync());
        Assert.Throws<ArgumentException>(() => collector.RequireContextEquity("VNINDEX"));
    }

    [Fact]
    public async Task Collector_AdminImportValidation_FailsClosed()
    {
        var db = NewDb(nameof(Collector_AdminImportValidation_FailsClosed));
        var collector = new EquityContextCollector(db, new FakeSource("{}", "{}"), new TestTimeProvider(Now));

        async Task Bad(Func<Task> act) => await Assert.ThrowsAsync<ArgumentException>(act);
        await Bad(() => collector.ImportCorporateActionsAsync("BID", [new() { ExDate = "2026-07-17", ActionType = "cash-dividend", SourceUrl = "ftp://x" }], default));
        await Bad(() => collector.ImportCorporateActionsAsync("BID", [new() { ExDate = "2026-07-17", ActionType = "dividend", SourceUrl = "https://x" }], default));
        await Bad(() => collector.ImportCorporateActionsAsync("BID", [new() { ExDate = "2026-07-17", ActionType = "split", SourceUrl = "https://x", AnnouncedDate = "2027-01-01" }], default));
        await Bad(() => collector.ImportEventsAsync("BID", [new() { EventType = "earnings", EventDate = "2026-10-30", Title = "Q3", SourceUrl = "https://x", AvailableAtUtc = Now.UtcDateTime.AddDays(1) }], default));
        await Bad(() => collector.ImportFundamentalsAsync("BID", [new() { Period = "2026Q2", Metric = "NPL", Unit = "percent", Source = "BID IR", SourceUrl = "https://x" }], default));
        await Bad(() => collector.ImportFundamentalsAsync("BID", [new() { Period = "2026Q2", Metric = "NPL", Unit = "percent", Value = 1.2m, UnavailableReason = "x", Source = "BID IR", SourceUrl = "https://x" }], default));
        await Bad(() => collector.ImportFundamentalsAsync("BID", [new() { Period = "2026-Q2", Metric = "NPL", Unit = "percent", Value = 1.2m, Source = "BID IR", SourceUrl = "https://x" }], default));
        await Bad(() => collector.ImportFundamentalsAsync("BID", [], default));
        Assert.Empty(await db.EquityCorporateActions.ToListAsync());
        Assert.Empty(await db.EquityFundamentals.ToListAsync());

        var ok = await collector.ImportFundamentalsAsync("BID", [new() { Period = "2026Q2", Metric = "NPL", Unit = "percent", Value = 1.2m, Source = "BID IR", SourceUrl = "https://bidv.com.vn/ir", PublishedDate = "2026-07-30" }], default);
        Assert.Equal((1, 0), (ok.Inserted, ok.Updated));
    }

    [Fact]
    public async Task Collector_IssuerReportFundamentals_DocumentRef_SeparateFromKbs()
    {
        var db = NewDb(nameof(Collector_IssuerReportFundamentals_DocumentRef_SeparateFromKbs));
        var tp = new TestTimeProvider(Now);
        var doc = new EquitySourceDocument
        {
            Id = Guid.NewGuid(), Symbol = "BID", Entity = "BIDV", DocType = "financial-statement", Title = "BCTC hợp nhất Q2/2026",
            SourceUrl = "https://bidv.com.vn/ir/q2.pdf", Provider = "issuer:BIDV", ContentSha256 = new string('a', 64),
            StoragePath = "BID/a.pdf", ByteSize = 10, MimeType = "application/pdf", ReportingPeriod = "2026Q2",
            Consolidation = "consolidated", AuditStatus = "reviewed", FirstSeenAtUtc = Now.UtcDateTime, FetchedAtUtc = Now.UtcDateTime,
            CreatedAtUtc = Now.UtcDateTime,
        };
        var mbbDoc = new EquitySourceDocument
        {
            Id = Guid.NewGuid(), Symbol = "MBB", Entity = "MB", DocType = "financial-statement", Title = "x", SourceUrl = "https://mbbank.com.vn/x",
            Provider = "issuer:MB", ContentSha256 = new string('b', 64), StoragePath = "MBB/b.pdf", ByteSize = 1, MimeType = "application/pdf",
            FirstSeenAtUtc = Now.UtcDateTime, FetchedAtUtc = Now.UtcDateTime, CreatedAtUtc = Now.UtcDateTime,
        };
        db.EquitySourceDocuments.AddRange(doc, mbbDoc);
        // Same metric name from KBS for the same period: must stay a separate row.
        db.EquityFundamentals.Add(new EquityFundamental
        {
            Symbol = "BID", Period = "2026Q2", PeriodEndDate = new DateOnly(2026, 6, 30), Metric = "ROE", Value = 18.2m, Unit = "percent",
            Source = EquityContextRules.KbsSource, SourceUrl = "https://kbs/x", Provenance = EquityContextRules.ProvenanceKbs,
            AvailableAtUtc = Now.UtcDateTime.AddDays(-3), AsOfUtc = Now.UtcDateTime.AddDays(-3),
        });
        await db.SaveChangesAsync();
        var collector = new EquityContextCollector(db, new FakeSource("{}", "{}"), tp);

        EquityFundamentalImport Row(string metric, decimal value, string unit, Guid? documentId = null, string? location = "p.12, B02/TCTD dòng 300") => new()
        {
            Period = "2026Q2", Metric = metric, Value = value, Unit = unit, Source = EquityContextRules.IssuerReportSource,
            SourceUrl = doc.SourceUrl, PublishedDate = "2026-07-30", DocumentId = documentId ?? doc.Id, SourceLocation = location,
        };
        var ok = await collector.ImportFundamentalsAsync("BID", [
            Row("TOTAL_ASSETS", 2_900_000_000_000_000m, "VND"), Row("NET_PROFIT", 7_000_000_000_000m, "VND"),
            Row("EQUITY", 150_000_000_000_000m, "VND"), Row("ROE", 19.1m, "percent")], default);
        Assert.Equal((4, 0), (ok.Inserted, ok.Updated));

        async Task Bad(EquityFundamentalImport r) =>
            await Assert.ThrowsAsync<ArgumentException>(() => collector.ImportFundamentalsAsync("BID", [r], default));
        await Bad(Row("NET_PROFIT", 1m, "VND", documentId: Guid.NewGuid()));          // unknown document
        await Bad(Row("NET_PROFIT", 1m, "VND", documentId: mbbDoc.Id));               // other symbol's document
        await Bad(Row("NET_PROFIT", 1m, "VND", location: new string('x', 501)));      // location too long
        await Bad(Row("EQUITY", 1m, "ratio"));                                         // amounts are VND
        var noDoc = Row("NET_PROFIT", 1m, "VND"); noDoc.DocumentId = null;            // issuer-report needs documentId
        await Bad(noDoc);
        await Bad(Row("NET_PROFIT", 1m, "VND", location: " "));                         // ... and sourceLocation

        var ctx = await new EquityContextService(db, Cal, tp).GetAsync("BID");
        var roe = ctx.Fundamentals.Items.Where(f => f.Metric == "ROE" && f.Period == "2026Q2").ToArray();
        Assert.Equal(2, roe.Length);
        var issuer = roe.Single(f => f.Source == EquityContextRules.IssuerReportSource);
        var kbs = roe.Single(f => f.Source == EquityContextRules.KbsSource);
        Assert.Equal((19.1m, doc.Id, "p.12, B02/TCTD dòng 300"), (issuer.Value!.Value, issuer.DocumentId!.Value, issuer.SourceLocation));
        Assert.Contains("issuer", issuer.Note);
        Assert.Equal((18.2m, (Guid?)null, (string?)null), (kbs.Value!.Value, kbs.DocumentId, kbs.SourceLocation));
        Assert.Equal(doc.Id, ctx.Fundamentals.Items.Single(f => f.Metric == "TOTAL_ASSETS").DocumentId);
    }

    [Fact]
    public async Task Context_ImplausiblePublishDate_UsesFirstSeen_AndPastAsOfDoesNotSeeFutureListing()
    {
        var db = NewDb(nameof(Context_ImplausiblePublishDate_UsesFirstSeen_AndPastAsOfDoesNotSeeFutureListing));
        var firstSeen = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        // Real btc_eqx_test case: BID additional listing on 2024-11-06 carrying VCI publish date 2019-12-11.
        db.EquityEvents.Add(new EquityEvent
        {
            Symbol = "BID", EventType = "additional-listing", EventDate = new DateOnly(2024, 11, 6), Title = "Niêm yết bổ sung",
            SourceUrl = "https://iq.vietcap.com.vn/x", Provenance = "provider:VCI",
            AvailableAtUtc = new DateTime(2019, 12, 10, 17, 0, 0, DateTimeKind.Utc), CreatedAtUtc = firstSeen,
        });
        db.EquityEvents.Add(new EquityEvent
        {
            Symbol = "BID", EventType = "agm", EventDate = new DateOnly(2026, 4, 25), Title = "ĐHCĐ 2026",
            SourceUrl = "https://iq.vietcap.com.vn/y", Provenance = "provider:VCI",
            AvailableAtUtc = new DateTime(2026, 3, 31, 17, 0, 0, DateTimeKind.Utc), CreatedAtUtc = firstSeen,
        });
        // Corporate action: record date is the item's own date when earlier than the ex-date.
        db.EquityCorporateActions.Add(new EquityCorporateAction
        {
            Symbol = "BID", ExDate = new DateOnly(2024, 7, 10), RecordDate = new DateOnly(2024, 7, 9), ActionType = "stock-dividend",
            SourceUrl = "https://iq.vietcap.com.vn/z", Provenance = "provider:VCI", VerificationStatus = "unverified",
            AvailableAtUtc = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), CreatedAtUtc = firstSeen,
        });
        await db.SaveChangesAsync();

        var now = await new EquityContextService(db, Cal, new TestTimeProvider(Now)).GetAsync("BID");
        var listing = now.Events.Items.Single(e => e.EventType == "additional-listing");
        Assert.Equal((EquityAvailabilityBases.FirstSeen, EquityAvailabilityBases.ImplausibleReason), (listing.AvailabilityBasis, listing.AvailabilityReason));
        Assert.Equal(firstSeen, listing.AvailableAtUtc);
        Assert.Equal(new DateTime(2019, 12, 10, 17, 0, 0, DateTimeKind.Utc), listing.PublishedAvailableAtUtc);
        var agm = now.Events.Items.Single(e => e.EventType == "agm");
        Assert.Equal((EquityAvailabilityBases.ProviderPublishDate, (string?)null), (agm.AvailabilityBasis, agm.AvailabilityReason));
        Assert.Equal(new DateTime(2026, 3, 31, 17, 0, 0, DateTimeKind.Utc), agm.AvailableAtUtc);
        var ca = Assert.Single(now.CorporateActions.Items);
        Assert.Equal((EquityAvailabilityBases.FirstSeen, firstSeen), (ca.AvailabilityBasis, ca.AvailableAtUtc));

        // A past as-of (2024-01-01) must not "know" the 2024-11-06 listing nor the 2024-07 dividend.
        var past = await new EquityContextService(db, Cal, new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero))).GetAsync("BID");
        Assert.DoesNotContain(past.Events.Items, e => e.EventType == "additional-listing");
        Assert.Empty(past.CorporateActions.Items);

        // Stored rows are untouched.
        Assert.Equal(new DateTime(2019, 12, 10, 17, 0, 0, DateTimeKind.Utc),
            (await db.EquityEvents.SingleAsync(e => e.EventType == "additional-listing")).AvailableAtUtc);
    }

    // --- context endpoint -------------------------------------------------------------------

    [Fact]
    public void RelativeStrength_UsesIntersectedSessions()
    {
        // Instrument misses session 3, benchmark misses session 10 → 23 aligned of 25.
        var inst = new Dictionary<long, decimal>();
        var bench = new Dictionary<long, decimal>();
        for (var i = 0; i < 25; i++)
        {
            if (i != 3) inst[i] = 100m + i;
            if (i != 10) bench[i] = 200m + 2 * i;
        }

        var b = EquityContextService.Compare("VNINDEX", "index", inst, bench);

        Assert.Equal(("ok", 23), (b.Status, b.AlignedSessions));
        Assert.Equal(24, b.LastAlignedOpenTimeMs);
        var aligned = inst.Keys.Where(bench.ContainsKey).OrderBy(k => k).ToArray();
        var start = aligned[^21];
        var w = b.Returns["20"]!;
        Assert.Equal(Math.Round(124.0 / (100 + start) - 1, 6), w.Instrument);
        Assert.Equal(Math.Round(248.0 / (200 + 2 * start) - 1, 6), w.Benchmark);
        Assert.Equal(Math.Round(w.Instrument - w.Benchmark, 6), w.Excess, 6);
        Assert.Null(b.Returns["60"]); // not enough aligned sessions for 60

        var shortBench = EquityContextService.Compare("CTG", "peer", inst, bench.Take(10).ToDictionary());
        Assert.Equal("unavailable", shortBench.Status);
        Assert.StartsWith("insufficient-aligned-sessions", shortBench.Reason);
        Assert.Equal("benchmark-not-ingested", EquityContextService.Compare("VCB", "peer", inst, new Dictionary<long, decimal>()).Reason);
    }

    [Fact]
    public async Task Context_ReportsBenchmarks_UnavailableMetrics_AndHidesFutureRows()
    {
        var db = NewDb(nameof(Context_ReportsBenchmarks_UnavailableMetrics_AndHidesFutureRows));
        var last = new DateOnly(2026, 10, 14);
        await SeedBarsAsync(db, "BID", 80, last, i => 40_000m + i * 10);
        await SeedBarsAsync(db, "VNINDEX", 80, last, i => 1_600m + i);
        await SeedBarsAsync(db, "VCB", 15, last);
        var collector = new EquityContextCollector(db, new FakeSource(VciFixture, KbsFixture), new TestTimeProvider(Now));
        await collector.SyncAsync("BID");
        await collector.ImportEventsAsync("BID", [new() { EventType = "earnings", EventDate = "2026-10-30", Title = "Q3 results", SourceUrl = "https://bidv.com.vn/ir" }], default);
        // A provider row published today becomes available only tomorrow 00:00 VN → hidden now.
        db.EquityEvents.Add(new EquityEvent
        {
            Symbol = "BID", EventType = "other", EventDate = new DateOnly(2026, 10, 20), Title = "future", SourceUrl = "https://x",
            Provenance = "provider:VCI", AvailableAtUtc = Now.UtcDateTime.AddHours(9),
        });
        await db.SaveChangesAsync();

        var ctx = await new EquityContextService(db, Cal, new TestTimeProvider(Now)).GetAsync("BID");

        Assert.Equal("equity-context-v1", ctx.ContractVersion);
        var rs = ctx.RelativeStrength.Benchmarks.ToDictionary(b => b.Symbol);
        Assert.Equal(["VNINDEX", "MBB", "VCB", "CTG"], ctx.RelativeStrength.Benchmarks.Select(b => b.Symbol).ToArray());
        Assert.Equal(("index", "ok", 80), (rs["VNINDEX"].Kind, rs["VNINDEX"].Status, rs["VNINDEX"].AlignedSessions));
        Assert.NotNull(rs["VNINDEX"].Returns["60"]);
        Assert.Equal("benchmark-not-ingested", rs["MBB"].Reason);
        Assert.Equal("unavailable", rs["VCB"].Status);
        Assert.Equal("ok", ctx.CorporateActions.Status);
        Assert.Equal(3, ctx.CorporateActions.Items.Count);
        Assert.DoesNotContain(ctx.Events.Items, e => e.Title == "future");
        Assert.Contains(ctx.Events.Items, e => e.Title == "Q3 results");
        var f = ctx.Fundamentals.Items.ToDictionary(x => x.Metric);
        Assert.Equal("quarterly NIM as published by KBS, NOT annualized", f["NIM"].Note);
        foreach (var metric in new[] { "NPL", "COVERAGE", "CAR" })
        {
            Assert.Null(f[metric].Value);
            Assert.Contains("no point-in-time source verified", f[metric].UnavailableReason);
            Assert.Equal("2026Q2", f[metric].Period);
        }

        var empty = await new EquityContextService(NewDb("ctx-empty"), Cal, new TestTimeProvider(Now)).GetAsync("MBB");
        Assert.Equal(("unavailable", "instrument-not-ingested"), (empty.RelativeStrength.Status, empty.RelativeStrength.Reason));
        Assert.Equal(("unavailable", "context-not-synced"), (empty.Fundamentals.Status, empty.Fundamentals.Reason));
    }
}

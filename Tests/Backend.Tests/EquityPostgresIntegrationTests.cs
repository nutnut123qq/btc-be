using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

/// <summary>
/// EQX-A1 PostgreSQL integration (EF InMemory proves neither CHECK constraints nor unique
/// indexes). Runs only when BTC_TEST_POSTGRES is set — point it at the throwaway test DB
/// (btc_eqx_test, migrated to EquityExpansionLane), never at bitcoin_analyst. Every test pins
/// its clock in 2002-2003 so its rows can never collide with real ingested history (2009+),
/// and deletes exactly that window afterwards.
/// </summary>
[Collection(PostgresTestDbCollection.Name)]
public class EquityPostgresIntegrationTests
{
    private static readonly long WindowEndMs = new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        // Npgsql only writes offset-0 DateTimeOffset values (real TimeProvider.System is UTC).
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private static string? ConnectionString() => Environment.GetEnvironmentVariable("BTC_TEST_POSTGRES");

    private static AppDbContext NewDb(string cs) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    private static DateTimeOffset Local(int y, int m, int d, int hh, int mm) =>
        new(y, m, d, hh, mm, 0, TimeSpan.FromHours(7));

    private static async Task CleanupAsync(string cs)
    {
        await using var db = NewDb(cs);
        string[] symbols = ["MBB", "BID"];
        await db.MbbSignalObservations
            .Where(o => symbols.Contains(o.Symbol) && o.SignalBarOpenTimeMs < WindowEndMs).ExecuteDeleteAsync();
        await db.TechnicalIndicators
            .Where(t => symbols.Contains(t.Symbol) && t.OpenTimeMs < WindowEndMs).ExecuteDeleteAsync();
        await db.InstrumentCandles
            .Where(c => symbols.Contains(c.Symbol) && c.OpenTimeMs < WindowEndMs).ExecuteDeleteAsync();
        await db.EquityForecastRecords.Where(f => f.SignalBarOpenTimeMs < WindowEndMs).ExecuteDeleteAsync();
        await db.EquityLlmRuns.Where(r => r.SignalBarOpenTimeMs < WindowEndMs).ExecuteDeleteAsync();
        var windowEnd = new DateOnly(2005, 1, 1);
        await db.EquityCorporateActions.Where(a => a.ExDate < windowEnd).ExecuteDeleteAsync();
        await db.EquityEvents.Where(e => e.EventDate < windowEnd).ExecuteDeleteAsync();
        await db.EquityFundamentals.Where(f => f.PeriodEndDate < windowEnd).ExecuteDeleteAsync();
    }

    private static InstrumentCandle Candle(string symbol, long openMs, long closeMs, decimal px) => new()
    {
        Symbol = symbol, Timeframe = "1d", OpenTimeMs = openMs, CloseTimeMs = closeMs,
        Open = px, High = px + 100m, Low = px - 100m, Close = px, Volume = 1_000m,
        Provider = "VCI", PriceBasis = "adjusted", Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
    };

    private static async Task SeedSignalBarsAsync(string cs, VietnamMarketCalendar cal, long signal, params string[] symbols)
    {
        await using var db = NewDb(cs);
        var signalLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime);
        foreach (var symbol in symbols)
        {
            foreach (var d in cal.EnumerateTradingDates(signalLocal.AddDays(-10), signalLocal))
            {
                var open = VietnamMarketCalendar.DailyOpenUtcMs(d);
                db.InstrumentCandles.Add(Candle(symbol, open, cal.SessionCloseUtcMs("1d", open), 30_000m));
            }
        }
        await db.SaveChangesAsync();
    }

    private static MbbSignalObservationService Observations(AppDbContext db, VietnamMarketCalendar cal, TimeProvider tp) =>
        new(db, new MbbDecisionService(db, cal, timeProvider: tp), cal, tp);

    private static MbbSignalObservation Row(string symbol, string tf, long signal, long closeMs, DateTimeOffset local)
    {
        var at = local.ToUniversalTime();
        return new MbbSignalObservation
        {
        Id = Guid.NewGuid(),
        DecisionId = MbbSignalObservationService.DecisionIdFor(symbol, tf, signal),
        RecorderVersion = MbbSignalObservationService.RecorderVersion,
        Symbol = symbol, Timeframe = tf,
        SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = closeMs,
        ObservedAtUtc = at, AvailableTimeMs = at.ToUnixTimeMilliseconds(),
        Decision = "sell", PriceBasis = "adjusted", DecisionPayloadJson = "{}", CreatedAtUtc = at,
        };
    }

    [Fact]
    public async Task ScopeConstraint_AllowsMbbAndBidDaily_RejectsContextOnlyAndBid1h()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var cal = new VietnamMarketCalendar();
        var at = Local(2003, 3, 12, 16, 0);
        var signal = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2003, 3, 12));
        var close = cal.SessionCloseUtcMs("1d", signal);
        try
        {
            await using (var ok = NewDb(cs))
            {
                ok.MbbSignalObservations.Add(Row("BID", "1d", signal, close, at));
                ok.MbbSignalObservations.Add(Row("MBB", "1d", signal, close, at));
                await ok.SaveChangesAsync();
            }
            foreach (var (symbol, tf) in new[] { ("VCB", "1d"), ("VNINDEX", "1d"), ("BID", "1h") })
            {
                await using var bad = NewDb(cs);
                bad.MbbSignalObservations.Add(Row(symbol, tf, signal - 86_400_000L, close - 86_400_000L, at));
                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => bad.SaveChangesAsync());
                Assert.Contains("CK_MbbSignalObservations_Scope", ex.InnerException?.Message ?? ex.Message);
            }
        }
        finally { await CleanupAsync(cs); }
    }

    [Fact]
    public async Task Observe_ConcurrentPolls_OneRowPerSymbol_DistinctIdsAcrossSymbols()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2003, 3, 12, 16, 0));
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        try
        {
            await SeedSignalBarsAsync(cs, cal, signal, "MBB", "BID");
            var reports = await Task.WhenAll(
                Enumerable.Range(0, 6).Select(async i =>
                {
                    await using var db = NewDb(cs);
                    return await Observations(db, cal, tp).RecordAsync(i % 2 == 0 ? "BID" : "MBB", "1d");
                }));

            Assert.All(reports, r => Assert.Contains(r.Status, new[] { "recorded", "already-recorded" }));
            Assert.Equal(2, reports.Count(r => r.Status == "recorded"));
            await using var check = NewDb(cs);
            var rows = await check.MbbSignalObservations.AsNoTracking()
                .Where(o => o.SignalBarOpenTimeMs == signal).OrderBy(o => o.Symbol).ToListAsync();
            Assert.Equal(["BID", "MBB"], rows.Select(r => r.Symbol).ToArray());
            Assert.Equal([$"equity-observe/BID/1d/{signal}", $"equity-observe/MBB/1d/{signal}"],
                rows.Select(r => r.DecisionId).ToArray());
        }
        finally { await CleanupAsync(cs); }
    }

    [Fact]
    public async Task Observe_LegacyMbbRowOnPostgres_BlocksDuplicateUnderNewId()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2003, 3, 12, 16, 0));
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        try
        {
            await SeedSignalBarsAsync(cs, cal, signal, "MBB");
            await using (var db = NewDb(cs))
            {
                var legacy = Row("MBB", "1d", signal, cal.SessionCloseUtcMs("1d", signal), tp.Now.AddMinutes(-1));
                legacy.DecisionId = $"mbb-observe/1d/{signal}";
                legacy.RecorderVersion = MbbSignalObservationService.LegacyRecorderVersion;
                legacy.Decision = "wait";
                db.MbbSignalObservations.Add(legacy);
                await db.SaveChangesAsync();
            }
            await using (var db = NewDb(cs))
                Assert.Equal("already-recorded", (await Observations(db, cal, tp).RecordAsync("MBB", "1d")).Status);

            // Even bypassing the service check, the natural-key unique index refuses a second row.
            await using (var db = NewDb(cs))
            {
                db.MbbSignalObservations.Add(Row("MBB", "1d", signal, cal.SessionCloseUtcMs("1d", signal), tp.Now));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
        }
        finally { await CleanupAsync(cs); }
    }

    [Fact]
    public async Task FillAndOutcome_AreWriteOnce_AndChronologyIsEnforcedByPostgres()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2003, 3, 11, 16, 0));
        var signal = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2003, 3, 11));
        var next = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2003, 3, 12));
        try
        {
            Guid id;
            await using (var db = NewDb(cs))
            {
                var row = Row("BID", "1d", signal, cal.SessionCloseUtcMs("1d", signal), tp.Now);
                id = row.Id;
                db.MbbSignalObservations.Add(row);
                db.InstrumentCandles.Add(Candle("BID", next, cal.SessionCloseUtcMs("1d", next), 31_000m));
                await db.SaveChangesAsync();
            }

            tp.Now = Local(2003, 3, 12, 16, 0);
            await using (var db = NewDb(cs))
            {
                var report = await Observations(db, cal, tp).RecordAsync("BID", "1d");
                Assert.Equal(1, report.FillsResolved);
                Assert.Equal(1, report.OutcomesResolved);
            }

            await using (var db = NewDb(cs))
            {
                var bar = await db.InstrumentCandles.SingleAsync(c => c.Symbol == "BID" && c.OpenTimeMs == next);
                bar.Open = 29_000m; // provider re-adjusts after the fact
                await db.SaveChangesAsync();
            }
            tp.Now = Local(2003, 3, 13, 16, 0);
            await using (var db = NewDb(cs))
                await Observations(db, cal, tp).RecordAsync("BID", "1d");

            await using (var db = NewDb(cs))
            {
                var row = await db.MbbSignalObservations.SingleAsync(o => o.Id == id);
                Assert.Equal(31_000m, row.FillPrice);
                Assert.Equal(next, row.FillBarOpenTimeMs);
                Assert.NotNull(row.OutcomeReturn);

                // Rewriting an already-set fill is refused by the write-once trigger (EQX v2 A2), which fires
                // before the CHECK; the CHECK itself is covered on INSERT in EquityJournalPostgresTests.
                row.FillBarOpenTimeMs = row.AvailableTimeMs - 1;
                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Contains("write-once column FillBarOpenTimeMs already set", ex.InnerException?.Message ?? ex.Message);
            }
        }
        finally { await CleanupAsync(cs); }
    }

    private sealed class SeriesVci : IVciOhlcvClient
    {
        public List<ProviderOhlcvBar> Bars { get; set; } = [];

        public Task<IReadOnlyList<ProviderOhlcvBar>> GetBarsAsync(string symbol, string timeframe,
            long toUnixSeconds, int countBack, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderOhlcvBar> slice = Bars
                .Where(b => b.OpenTimeMs <= toUnixSeconds * 1000L)
                .OrderBy(b => b.OpenTimeMs).TakeLast(countBack).ToArray();
            return Task.FromResult(slice);
        }
    }

    [Fact]
    public async Task Revision_TriggersFullHistoryRewrite_OnPostgres()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2003, 3, 14, 16, 0));
        List<ProviderOhlcvBar> Series(decimal factor) => cal
            .EnumerateTradingDates(new DateOnly(2002, 6, 3), new DateOnly(2003, 3, 14))
            .Select((d, i) =>
            {
                var px = Math.Round((30_000m + i * 5m) * factor, 2);
                return new ProviderOhlcvBar(VietnamMarketCalendar.DailyOpenUtcMs(d), px, px + 50, px - 50, px, 1_000m);
            }).ToList();
        var options = new MbbIngestionOptions
        {
            Enabled = true, Timeframes = ["1d"], LatestCountBack = 10, BatchCountBack = 100, RequestDelayMs = 0,
            GapFillStartLocal = "2002-01-01", NonTradingDates = [],
            DailyBackfillStartBySymbol = new(StringComparer.OrdinalIgnoreCase) { ["BID"] = "2002-06-03" },
        };
        var vci = new SeriesVci { Bars = Series(1m) };
        try
        {
            await using (var db = NewDb(cs))
            {
                var svc = new MbbIngestionService(db, vci, options, cal, tp).ForSymbol("BID");
                Assert.Null((await svc.BackfillAsync("1d")).Error);
            }
            vci.Bars = Series(0.9m);
            await using (var db = NewDb(cs))
            {
                var report = await new MbbIngestionService(db, vci, options, cal, tp).ForSymbol("BID").SyncAsync();
                Assert.Equal(RevisionRefetchStatuses.Completed, Assert.Single(report.Timeframes).RevisionRefetchStatus);
            }
            await using (var check = NewDb(cs))
            {
                var stored = await check.InstrumentCandles.AsNoTracking()
                    .Where(c => c.Symbol == "BID" && c.Timeframe == "1d" && c.OpenTimeMs < WindowEndMs)
                    .OrderBy(c => c.OpenTimeMs).ToListAsync();
                Assert.Equal(vci.Bars.Count, stored.Count);
                Assert.All(stored.Zip(vci.Bars), p =>
                {
                    Assert.Equal(p.Second.Close, p.First.Close);
                    Assert.Equal(1, p.First.Revision);
                });
            }
        }
        finally { await CleanupAsync(cs); }
    }

    private static EquityForecastRecord ForecastRow(string source, string symbol, long signal, Action<EquityForecastRecord>? edit = null)
    {
        var row = new EquityForecastRecord
        {
            Id = Guid.NewGuid(),
            ForecastId = $"{source}/{symbol}/tb-p5-s4-h20/{signal}/{Guid.NewGuid():N}",
            Source = source, Symbol = symbol, Timeframe = "1d", ConfigId = "tb-p5-s4-h20",
            TargetPct = 0.05, StopPct = 0.04, HorizonSessions = 20,
            SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = signal + 27_900_000, AvailableTimeMs = signal + 30_000_000,
            CreatedAtUtc = DateTime.UtcNow, ProbTargetFirst = 0.4, ProbStopFirst = 0.5, Abstained = false,
            Validated = false, MethodVersion = "equity-knn-v1", InputSnapshotSha256 = new string('0', 64),
            LlmRunId = source == "llm-judge" ? Guid.NewGuid() : null,
        };
        edit?.Invoke(row);
        return row;
    }

    [Fact]
    public async Task ForecastConstraints_AreEnforcedByPostgres()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var signal = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2003, 3, 12));
        try
        {
            await using (var ok = NewDb(cs))
            {
                ok.EquityForecastRecords.Add(ForecastRow("quant-knn", "BID", signal));
                ok.EquityForecastRecords.Add(ForecastRow("quant-knn", "MBB", signal, r =>
                {
                    r.Abstained = true; r.ProbTargetFirst = null; r.ProbStopFirst = null; r.AbstainReason = "artifact-missing";
                }));
                ok.EquityForecastRecords.Add(ForecastRow("llm-judge", "BID", signal));
                await ok.SaveChangesAsync();
            }

            var violations = new (string Constraint, Action<EquityForecastRecord> Edit, string Source)[]
            {
                ("CK_EquityForecastRecords_Abstention", r => { r.Abstained = true; r.AbstainReason = "x"; }, "quant-knn"),
                ("CK_EquityForecastRecords_Abstention", r => { r.Abstained = true; r.ProbTargetFirst = null; r.ProbStopFirst = null; }, "quant-knn"),
                ("CK_EquityForecastRecords_LlmUnvalidated", r => r.Validated = true, "llm-judge"),
                ("CK_EquityForecastRecords_LlmUnvalidated", r => r.LlmRunId = null, "llm-judge"),
                ("CK_EquityForecastRecords_OutcomeCoherence", r => r.OutcomeLabel = "target-first", "quant-knn"),
                ("CK_EquityForecastRecords_OutcomeCoherence", r =>
                {
                    r.OutcomeLabel = "target-first"; r.EntryPrice = 100; r.OutcomeReturnNet = 0.04; r.OutcomeResolvedAtUtc = DateTime.UtcNow;
                    r.EntryBarOpenTimeMs = r.SignalBarOpenTimeMs; r.ResolvedBarOpenTimeMs = r.SignalBarOpenTimeMs;
                }, "quant-knn"),
                // NULL bar timestamps used to slip through (NULL comparison = pass); TightenEquityForecastOutcomeCoherence.
                ("CK_EquityForecastRecords_OutcomeCoherence", r =>
                {
                    r.OutcomeLabel = "timeout"; r.EntryPrice = 100; r.OutcomeReturnNet = 0.01; r.OutcomeResolvedAtUtc = DateTime.UtcNow;
                    r.EntryBarOpenTimeMs = null; r.ResolvedBarOpenTimeMs = null;
                }, "quant-knn"),
                ("CK_EquityForecastRecords_Probability", r => { r.ProbTargetFirst = 0.7; r.ProbStopFirst = 0.6; }, "quant-knn"),
                ("CK_EquityForecastRecords_Chronology", r => r.AvailableTimeMs = r.SignalBarCloseTimeMs - 1, "quant-knn"),
                ("CK_EquityForecastRecords_Scope", r => r.Symbol = "VCB", "quant-knn"),
            };
            foreach (var (constraint, edit, source) in violations)
            {
                await using var bad = NewDb(cs);
                bad.EquityForecastRecords.Add(ForecastRow(source, "BID", signal - 86_400_000L, edit));
                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => bad.SaveChangesAsync());
                Assert.Contains(constraint, ex.InnerException?.Message ?? ex.Message);
            }
        }
        finally { await CleanupAsync(cs); }
    }

    [Fact]
    public async Task Journal_RecordAndResolve_OnPostgres_WriteOnce()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2003, 3, 12, 16, 0));
        var root = Path.Combine(Path.GetTempPath(), "eqx-pg", Guid.NewGuid().ToString("N")); // no artifact ⇒ abstained
        try
        {
            await SeedSignalBarsAsync(cs, cal, VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2003, 3, 12)), "BID");
            await using (var db = NewDb(cs))
            {
                var journal = new EquityForecastJournalService(db, new EquityBaseRatesService(db, root, 4096, cal, timeProvider: tp), cal, tp);
                var first = await journal.RecordQuantAsync("BID");
                var again = await journal.RecordQuantAsync("BID");
                Assert.Equal((2, 2), (first.Recorded, first.Abstained));
                Assert.Equal(2, again.AlreadyRecorded);
            }
            // Next sessions: +6% on day 2 ⇒ the 5% config resolves target-first; the 10% config stays pending.
            await using (var db = NewDb(cs))
            {
                var px = new[] { 30_000m, 31_800m };
                var days = cal.EnumerateTradingDates(new DateOnly(2003, 3, 13), new DateOnly(2003, 3, 20)).Take(2).ToArray();
                for (var i = 0; i < 2; i++)
                {
                    var open = VietnamMarketCalendar.DailyOpenUtcMs(days[i]);
                    var c = Candle("BID", open, cal.SessionCloseUtcMs("1d", open), 30_000m);
                    c.High = px[i] + 100m;
                    db.InstrumentCandles.Add(c);
                }
                await db.SaveChangesAsync();
            }
            tp.Now = Local(2003, 3, 20, 16, 0);
            await using (var db = NewDb(cs))
            {
                var journal = new EquityForecastJournalService(db, new EquityBaseRatesService(db, root, 4096, cal, timeProvider: tp), cal, tp);
                Assert.Equal(1, await journal.ResolveOutcomesAsync("BID"));
                Assert.Equal(0, await journal.ResolveOutcomesAsync("BID"));
                var rows = await db.EquityForecastRecords.AsNoTracking()
                    .Where(f => f.Symbol == "BID" && f.SignalBarOpenTimeMs < WindowEndMs).OrderBy(f => f.ConfigId).ToListAsync();
                Assert.Null(rows[0].OutcomeLabel); // tb-p10-s6-h60
                Assert.Equal("target-first", rows[1].OutcomeLabel);
                Assert.Equal(30_000m, rows[1].EntryPrice);
            }
        }
        finally { await CleanupAsync(cs); }
    }

    [Fact]
    public async Task ContextRows_UpsertAndConstraints_OnPostgres()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var tp = new TestTimeProvider(Local(2003, 3, 12, 16, 0));
        try
        {
            await using (var db = NewDb(cs))
            {
                var collector = new EquityContextCollector(db, new NoSource(), tp);
                var body = new List<EquityCorporateActionImport>
                {
                    new() { ExDate = "2003-03-10", ActionType = "cash-dividend", CashPerShareVnd = 500, SourceUrl = "https://hose.vn/x", AnnouncedDate = "2003-03-01" },
                };
                var first = await collector.ImportCorporateActionsAsync("CTG", body, default);
                var second = await collector.ImportCorporateActionsAsync("CTG", body, default);
                Assert.Equal((1, 0), (first.Inserted, first.Updated));
                Assert.Equal((0, 0), (second.Inserted, second.Updated));
                await collector.ImportFundamentalsAsync("CTG", [new() { Period = "2002Q4", Metric = "CAR", Unit = "percent",
                    UnavailableReason = "not disclosed", Source = "CTG IR", SourceUrl = "https://vietinbank.vn/ir" }], default);
            }
            await using (var bad = NewDb(cs))
            {
                bad.EquityFundamentals.Add(new EquityFundamental
                {
                    Symbol = "CTG", Period = "2002Q4", PeriodEndDate = new DateOnly(2002, 12, 31), Metric = "NPL", Unit = "percent",
                    Source = "x", SourceUrl = "https://x", Provenance = "admin-manual", AvailableAtUtc = DateTime.UtcNow, AsOfUtc = DateTime.UtcNow,
                });
                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => bad.SaveChangesAsync());
                Assert.Contains("CK_EquityFundamentals_ValueOrReason", ex.InnerException?.Message ?? ex.Message);
            }
        }
        finally { await CleanupAsync(cs); }
    }

    private sealed class NoSource : IEquityContextSource
    {
        public string VciEventsUrl(string symbol, DateOnly fromLocal, DateOnly toLocal, int page, int size) => "https://x";
        public string KbsFinanceInfoUrl(string symbol) => "https://x";
        public Task<string> GetAsync(string url, CancellationToken cancellationToken) => throw new InvalidOperationException("no network in tests");
    }

    private sealed class FakeLlmAi(Func<string, (System.Net.HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/equity-llm/run")
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            var (code, body) = respond(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://ai.test") };
    }

    private sealed class NoEvidence : IResearchEvidenceCatalog
    {
        public ResearchEvidenceCatalogResponse GetCatalog() => new("btc-research-evidence-catalog-v1", "BID", DateTime.UtcNow, [],
            new EvidenceIntegritySummaryDto(0, 0, 0), null);
        public ResearchEvidenceDetailDto? GetDetail(string id) => null;
    }

    [Fact]
    public async Task LlmRun_SucceededAndUnavailable_PersistOnPostgres_PassChecks()
    {
        var cs = ConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return;
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2003, 3, 12, 16, 0));
        var signal = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2003, 3, 12));
        var root = Path.Combine(Path.GetTempPath(), "eqx-pg-llm", Guid.NewGuid().ToString("N"));
        string Output(long sig) => $$$"""
            {"status":"succeeded","reason":null,"researchModel":"m","judgeModel":"judge-m","promptVersion":"equity-llm-prompt-v1",
             "schemaVersion":"equity-llm-judge-v1","inputSnapshotSha256":null,"usage":{"input_tokens":1},"durationMs":5,"errorCode":null,
             "output":{"schemaVersion":"equity-llm-judge-v1","symbol":"BID","signalBarOpenTimeMs":{{{sig}}},"evidenceStatus":"sourced",
               "scenarios":[],"warnings":[],"veto":{"active":false,"reason":null,"sourceIds":[]},
               "sources":[{"id":"s1","url":"https://example.com/a","title":"t","publishedAt":"2003-03-11"}],
               "adjustedForecasts":[{"configId":"tb-p5-s4-h20","probTargetFirst":0.3,"probStopFirst":0.5,"rationale":"r","sourceIds":["s1"]}]}}
            """;
        try
        {
            await SeedSignalBarsAsync(cs, cal, signal, "BID");
            var responses = new Queue<(System.Net.HttpStatusCode, string)>([
                (System.Net.HttpStatusCode.OK, Output(signal)),
                (System.Net.HttpStatusCode.ServiceUnavailable,
                    "{\"status\":\"unavailable\",\"reason\":\"ANTHROPIC_API_KEY not configured\",\"promptVersion\":\"equity-llm-prompt-v1\",\"schemaVersion\":\"equity-llm-judge-v1\",\"output\":null,\"durationMs\":0,\"code\":\"LLM_UNAVAILABLE\"}"),
            ]);
            var factory = new FakeFactory(new FakeLlmAi(_ => responses.Dequeue()));
            for (var i = 0; i < 2; i++)
            {
                await using var db = NewDb(cs);
                var baseRates = new EquityBaseRatesService(db, root, 4096, cal, factory, tp);
                var journal = new EquityForecastJournalService(db, baseRates, cal, tp);
                var dossier = new EquityDossierService(db,
                    new CurrentConditionsService(factory, new NoEvidence(),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<CurrentConditionsService>.Instance),
                    baseRates, new EquityContextService(db, cal, tp), journal, tp);
                var result = await new EquityLlmRunService(db, dossier, factory, cal, tp).RunAsync("BID");
                Assert.Equal(i == 0 ? EquityLlmRunService.Outcome.Created : EquityLlmRunService.Outcome.Unavailable, result.Outcome);
            }

            await using var check = NewDb(cs);
            var runs = await check.EquityLlmRuns.AsNoTracking().Where(r => r.SignalBarOpenTimeMs == signal)
                .OrderBy(r => r.CreatedAtUtc).ToListAsync();
            Assert.Equal(["succeeded", "unavailable"], runs.Select(r => r.Status).ToArray());
            Assert.NotNull(runs[0].OutputJson);
            Assert.Null(runs[1].OutputJson);
            var fc = await check.EquityForecastRecords.AsNoTracking().SingleAsync(f => f.SignalBarOpenTimeMs == signal && f.Source == "llm-judge");
            Assert.Equal((runs[0].Id, false, "judge-m"), (fc.LlmRunId!.Value, fc.Validated, fc.ModelId));
        }
        finally { await CleanupAsync(cs); }
    }
}

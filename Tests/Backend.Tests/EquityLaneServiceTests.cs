using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

/// <summary>
/// EQX-A1 service-level tests (EF InMemory, fake providers): multi-symbol scope of the
/// generalized Mbb* services, observation ids/legacy compatibility, BID daily-only freshness,
/// and the revision → full-history refetch data-correctness rule.
/// </summary>
public class EquityLaneServiceTests
{
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static DateTimeOffset Local(int y, int m, int d, int hh, int mm) =>
        new(y, m, d, hh, mm, 0, TimeSpan.FromHours(7));

    private static InstrumentCandle Candle(string symbol, long openMs, long closeMs, decimal px, string tf = "1d") => new()
    {
        Symbol = symbol,
        Timeframe = tf,
        OpenTimeMs = openMs,
        CloseTimeMs = closeMs,
        Open = px, High = px + 100m, Low = px - 100m, Close = px,
        Volume = 1_000_000m,
        Provider = "VCI",
        PriceBasis = "adjusted",
        Venue = "HOSE",
        SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>Daily bars for <paramref name="symbol"/> through the signal open + an indicator row on it.</summary>
    private static async Task SeedDailyAsync(AppDbContext db, VietnamMarketCalendar cal, string symbol,
        long signalOpenMs, decimal px)
    {
        var signalLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signalOpenMs).DateTime);
        foreach (var d in cal.EnumerateTradingDates(signalLocal.AddDays(-40), signalLocal))
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(d);
            db.InstrumentCandles.Add(Candle(symbol, open, cal.SessionCloseUtcMs("1d", open), px));
        }
        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = symbol, Timeframe = "1d", OpenTimeMs = signalOpenMs,
            Sma50 = px, Rsi14 = 55, Atr14 = 500,
        });
        await db.SaveChangesAsync();
    }

    private static MbbSignalObservationService NewObservations(AppDbContext db, VietnamMarketCalendar cal, TimeProvider tp) =>
        new(db, new MbbDecisionService(db, cal, timeProvider: tp), cal, tp);

    // --- observations -------------------------------------------------------------

    [Fact]
    public async Task Observe_MbbAndBid_SameSignalBar_GetDistinctDecisionIds()
    {
        var db = NewDb(nameof(Observe_MbbAndBid_SameSignalBar_GetDistinctDecisionIds));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2026, 10, 14, 16, 0));
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        await SeedDailyAsync(db, cal, "MBB", signal, 20_000m);
        await SeedDailyAsync(db, cal, "BID", signal, 40_000m);
        var svc = NewObservations(db, cal, tp);

        var mbb = await svc.RecordAsync("MBB", "1d");
        var bid = await svc.RecordAsync("BID", "1d");

        Assert.Equal("recorded", mbb.Status);
        Assert.Equal("recorded", bid.Status);
        Assert.Equal("BID", bid.Symbol);
        Assert.Equal($"equity-observe/MBB/1d/{signal}", mbb.DecisionId);
        Assert.Equal($"equity-observe/BID/1d/{signal}", bid.DecisionId);
        var rows = await db.MbbSignalObservations.OrderBy(o => o.Symbol).ToListAsync();
        Assert.Equal(["BID", "MBB"], rows.Select(r => r.Symbol).ToArray());
        Assert.All(rows, r => Assert.Equal("equity-observe-v2", r.RecorderVersion));
        Assert.Equal(40_000m, rows[0].MarketCloseVnd);
    }

    [Fact]
    public async Task Observe_LegacyMbbRow_CountsAsAlreadyRecorded_AndStaysUntouched()
    {
        var db = NewDb(nameof(Observe_LegacyMbbRow_CountsAsAlreadyRecorded_AndStaysUntouched));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2026, 10, 14, 16, 0));
        var nowMs = tp.Now.ToUnixTimeMilliseconds();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", nowMs)!.Value;
        await SeedDailyAsync(db, cal, "MBB", signal, 20_000m);
        db.MbbSignalObservations.Add(new MbbSignalObservation
        {
            Id = Guid.NewGuid(),
            DecisionId = $"mbb-observe/1d/{signal}",
            RecorderVersion = MbbSignalObservationService.LegacyRecorderVersion,
            Symbol = "MBB", Timeframe = "1d",
            SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = cal.SessionCloseUtcMs("1d", signal),
            ObservedAtUtc = tp.Now.AddMinutes(-5), AvailableTimeMs = nowMs - 300_000,
            Decision = "wait", PriceBasis = "adjusted", DecisionPayloadJson = "{}",
            CreatedAtUtc = tp.Now.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var report = await NewObservations(db, cal, tp).RecordAsync("1d");

        Assert.Equal("already-recorded", report.Status);
        var row = await db.MbbSignalObservations.SingleAsync();
        Assert.Equal($"mbb-observe/1d/{signal}", row.DecisionId);
        Assert.Equal("mbb-observe-v1", row.RecorderVersion);
    }

    [Fact]
    public async Task Observe_RejectsContextOnlySymbol_AndUndeclaredTimeframe()
    {
        var db = NewDb(nameof(Observe_RejectsContextOnlySymbol_AndUndeclaredTimeframe));
        var cal = new VietnamMarketCalendar();
        var svc = NewObservations(db, cal, new TestTimeProvider(Local(2026, 10, 14, 16, 0)));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync("VCB", "1d"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync("VNINDEX", "1d"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync("BTCUSDT", "1d"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync("BID", "1h"));
        Assert.Empty(await db.MbbSignalObservations.ToListAsync());
    }

    [Fact]
    public async Task Observe_ResolvesPendingFills_OnlyForThePolledSymbol_WriteOnce()
    {
        var db = NewDb(nameof(Observe_ResolvesPendingFills_OnlyForThePolledSymbol_WriteOnce));
        var cal = new VietnamMarketCalendar();
        var day1 = Local(2026, 10, 13, 16, 0);
        var tp = new TestTimeProvider(day1);
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        var nowMs = tp.Now.ToUnixTimeMilliseconds();
        foreach (var symbol in new[] { "MBB", "BID" })
        {
            db.MbbSignalObservations.Add(new MbbSignalObservation
            {
                Id = Guid.NewGuid(),
                DecisionId = MbbSignalObservationService.DecisionIdFor(symbol, "1d", signal),
                RecorderVersion = MbbSignalObservationService.RecorderVersion,
                Symbol = symbol, Timeframe = "1d",
                SignalBarOpenTimeMs = signal, SignalBarCloseTimeMs = cal.SessionCloseUtcMs("1d", signal),
                ObservedAtUtc = tp.Now, AvailableTimeMs = nowMs,
                Decision = "sell", PriceBasis = "adjusted", DecisionPayloadJson = "{}",
                CreatedAtUtc = tp.Now,
            });
        }
        // Next session (2026-10-14) bar stored for both symbols.
        var nextOpen = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 10, 14));
        var nextClose = cal.SessionCloseUtcMs("1d", nextOpen);
        db.InstrumentCandles.Add(Candle("MBB", nextOpen, nextClose, 19_000m));
        db.InstrumentCandles.Add(Candle("BID", nextOpen, nextClose, 41_000m));
        await db.SaveChangesAsync();
        tp.Now = Local(2026, 10, 14, 16, 0);
        var svc = NewObservations(db, cal, tp);

        var report = await svc.RecordAsync("BID", "1d");

        Assert.Equal(1, report.FillsResolved);
        Assert.Equal(1, report.OutcomesResolved);
        var bid = await db.MbbSignalObservations.SingleAsync(o => o.Symbol == "BID" && o.SignalBarOpenTimeMs == signal);
        var mbb = await db.MbbSignalObservations.SingleAsync(o => o.Symbol == "MBB" && o.SignalBarOpenTimeMs == signal);
        Assert.Equal(41_000m, bid.FillPrice);
        Assert.Equal(nextOpen, bid.FillBarOpenTimeMs);
        Assert.Null(mbb.FillPrice); // other symbol's pending row is not this poll's business

        // Write-once: a later re-adjusted price never rewrites a resolved fill/outcome.
        var fillAt = bid.FillObservedAtUtc;
        var bar = await db.InstrumentCandles.SingleAsync(c => c.Symbol == "BID" && c.OpenTimeMs == nextOpen);
        bar.Open = 39_000m;
        await db.SaveChangesAsync();
        tp.Now = tp.Now.AddDays(1);
        await svc.RecordAsync("BID", "1d");
        bid = await db.MbbSignalObservations.AsNoTracking().SingleAsync(o => o.Id == bid.Id);
        Assert.Equal(41_000m, bid.FillPrice);
        Assert.Equal(fillAt, bid.FillObservedAtUtc);
    }

    // --- decision -----------------------------------------------------------------

    [Fact]
    public async Task Decide_Bid_DailyOnly_IsNotStaleForMissing1h()
    {
        var db = NewDb(nameof(Decide_Bid_DailyOnly_IsNotStaleForMissing1h));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2026, 10, 14, 16, 0));
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        await SeedDailyAsync(db, cal, "BID", signal, 40_000m);
        var svc = new MbbDecisionService(db, cal, timeProvider: tp);

        var decision = await svc.DecideAsync("BID");

        Assert.Equal("BID", decision.Symbol);
        var freshness = Assert.Single(decision.DataFreshness);
        Assert.Equal("1d", freshness.Timeframe);
        Assert.False(freshness.IsStale);
        Assert.DoesNotContain("stale-data", decision.Reasons);
        Assert.Null(decision.Market.LatestHourlyCloseVnd);

        // MBB keeps both timeframes, 1d first (alias parity): missing 1h IS stale for MBB.
        await SeedDailyAsync(db, cal, "MBB", signal, 20_000m);
        var mbb = await svc.DecideAsync("MBB");
        Assert.Equal(["1d", "1h"], mbb.DataFreshness.Select(f => f.Timeframe).ToArray());
        Assert.True(mbb.DataFreshness[1].IsStale);
    }

    [Fact]
    public async Task Decision_PositionsArePerSymbol_AndContextOnlySymbolsFailClosed()
    {
        var db = NewDb(nameof(Decision_PositionsArePerSymbol_AndContextOnlySymbolsFailClosed));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2026, 10, 14, 16, 0));
        var svc = new MbbDecisionService(db, cal, timeProvider: tp);

        await svc.ReplacePositionAsync(new UpdateMbbPositionRequest(
            [new UpdateMbbPositionLotItem(100, 40_000m, "2026-09-01")], 1_000_000m), "BID");

        Assert.Equal(100, (await svc.GetPositionAsync("BID")).TotalShares);
        Assert.Equal(0, (await svc.GetPositionAsync("MBB")).TotalShares);
        foreach (var symbol in new[] { "VCB", "CTG", "VNINDEX", "BTCUSDT", "NOPE" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => svc.DecideAsync(symbol));
            await Assert.ThrowsAsync<ArgumentException>(() => svc.GetPositionAsync(symbol));
        }
    }

    // --- recompute / data quality ---------------------------------------------------

    [Fact]
    public async Task Recompute_AllowsTaEquities_RejectsContextOnly()
    {
        var db = NewDb(nameof(Recompute_AllowsTaEquities_RejectsContextOnly));
        var opts = OptionsFactory.Create(new IndexingOptions());
        var svc = new MbbTechnicalRecomputeService(
            db,
            new CandlePatternIndexer(db, new FakeBinanceKlinesService(), opts),
            new WindowVectorIndexer(db, opts),
            new CandleVolumeIndexer(db, NullLogger<CandleVolumeIndexer>.Instance, opts),
            new TechnicalIndicatorIndexer(db, NullLogger<TechnicalIndicatorIndexer>.Instance, opts),
            new CandlePatternSequenceIndexer(db, NullLogger<CandlePatternSequenceIndexer>.Instance, opts),
            new VietnamMarketCalendar());

        var bid = await svc.RecomputeAsync("BID", "1d");
        Assert.Equal("BID", bid.Symbol);
        Assert.Equal(MbbRecomputeStatuses.NoData, bid.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("BID", "1h"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("VCB", "1d"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecomputeAsync("VNINDEX", "1d"));
    }

    [Fact]
    public async Task DataQuality_PerSymbol_VnindexSkipsCrossCheck()
    {
        var db = NewDb(nameof(DataQuality_PerSymbol_VnindexSkipsCrossCheck));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider(Local(2026, 10, 14, 16, 0));
        var mbb = new MbbDataQualityService(db, new MbbIngestionOptions(), cal, timeProvider: tp);

        var vnindex = mbb.ForSymbol("VNINDEX");
        var report = await vnindex.BuildReportAsync();
        Assert.Equal("VNINDEX", report.Symbol);
        Assert.Equal("", report.CrossCheckProvider);
        Assert.Equal(["1d"], report.Timeframes.Select(t => t.Timeframe).ToArray());
        Assert.Contains(report.Limitations, l => l.Contains("volume is never cross-checked"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vnindex.CrossCheckAsync("1d", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14)));

        var bid = await mbb.ForSymbol("BID").BuildReportAsync();
        Assert.Equal(["1d"], bid.Timeframes.Select(t => t.Timeframe).ToArray());
        Assert.Same(mbb, mbb.ForSymbol("MBB"));
        Assert.Throws<ArgumentException>(() => mbb.ForSymbol("BTCUSDT"));
        Assert.Throws<ArgumentException>(() => mbb.ForSymbol("NOPE"));
    }

    [Fact]
    public void IngestionOptions_ForSymbol_UsesPerSymbolDailyStart_AndKeepsMbb()
    {
        var options = new MbbIngestionOptions();
        Assert.Equal("2010-01-01", options.ForSymbol("VNINDEX").DailyBackfillStartLocal);
        Assert.Equal("2014-01-24", options.ForSymbol("BID").DailyBackfillStartLocal);
        Assert.Equal(options.DailyBackfillStartLocal, options.ForSymbol("MBB").DailyBackfillStartLocal);

        var db = NewDb(nameof(IngestionOptions_ForSymbol_UsesPerSymbolDailyStart_AndKeepsMbb));
        var ingestion = new MbbIngestionService(db, new SeriesVci(), options);
        Assert.Equal(["1d"], ingestion.ForSymbol("BID").EffectiveTimeframes().ToArray());
        Assert.Equal(["1d", "1h"], ingestion.EffectiveTimeframes().ToArray());
        Assert.Same(ingestion, ingestion.ForSymbol("MBB"));
        Assert.Throws<ArgumentException>(() => ingestion.ForSymbol("BTCUSDT"));
    }

    [Fact]
    public async Task Backfill_Vnindex_NeverStoresBarsBeforeHistoryStart_AndRejectsInvalidOhlc()
    {
        var db = NewDb(nameof(Backfill_Vnindex_NeverStoresBarsBeforeHistoryStart_AndRejectsInvalidOhlc));
        var cal = new VietnamMarketCalendar([]);
        var tp = new TestTimeProvider(Local(2010, 3, 1, 16, 0));
        var vci = new SeriesVci();
        foreach (var d in cal.EnumerateTradingDates(new DateOnly(2009, 11, 2), new DateOnly(2010, 2, 26)))
            vci.Bars.Add(new ProviderOhlcvBar(VietnamMarketCalendar.DailyOpenUtcMs(d), 500m, 510m, 495m, 505m, 1_000m));
        var broken = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2010, 2, 1));
        vci.Bars[vci.Bars.FindIndex(b => b.OpenTimeMs == broken)] = new ProviderOhlcvBar(broken, 500m, 490m, 495m, 505m, 1m);
        var options = new MbbIngestionOptions { Enabled = true, RequestDelayMs = 0, BatchCountBack = 500, NonTradingDates = [] };
        var svc = new MbbIngestionService(db, vci, options, cal, tp).ForSymbol("VNINDEX");

        var report = await svc.BackfillAsync("1d");
        await svc.SyncAsync();

        Assert.Equal(1, report.RejectedInvalidBars);
        var start = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2010, 1, 1));
        var stored = await db.InstrumentCandles.Where(c => c.Symbol == "VNINDEX").ToListAsync();
        Assert.NotEmpty(stored);
        Assert.All(stored, c => Assert.True(c.OpenTimeMs >= start));
        Assert.DoesNotContain(stored, c => c.OpenTimeMs == broken);
        Assert.All(stored, c => Assert.Equal("raw", c.PriceBasis));
    }

    // --- revision → full refetch ----------------------------------------------------

    /// <summary>Fake VCI over a mutable daily series: last countBack bars with open ≤ to.</summary>
    private sealed class SeriesVci : IVciOhlcvClient
    {
        public List<ProviderOhlcvBar> Bars { get; set; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ProviderOhlcvBar>> GetBarsAsync(string symbol, string timeframe,
            long toUnixSeconds, int countBack, CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<ProviderOhlcvBar> slice = Bars
                .Where(b => b.OpenTimeMs <= toUnixSeconds * 1000L)
                .OrderBy(b => b.OpenTimeMs)
                .TakeLast(countBack)
                .ToArray();
            return Task.FromResult(slice);
        }
    }

    private static MbbIngestionOptions RevisionOptions(int maxPages = 12) => new()
    {
        Enabled = true,
        Timeframes = ["1d"],
        LatestCountBack = 10,
        BatchCountBack = 100,
        MaxRequestsPerCycle = 10,
        RequestDelayMs = 0,
        GapFillStartLocal = "2023-01-01",
        DailyBackfillStartBySymbol = new(StringComparer.OrdinalIgnoreCase) { ["BID"] = "2023-06-01" },
        NonTradingDates = [],
        RevisionRefetchMaxPages = maxPages,
    };

    private static List<ProviderOhlcvBar> Series(VietnamMarketCalendar cal, decimal factor)
    {
        var bars = new List<ProviderOhlcvBar>();
        var i = 0;
        foreach (var d in cal.EnumerateTradingDates(new DateOnly(2023, 6, 1), new DateOnly(2024, 6, 28)))
        {
            var px = Math.Round((40_000m + i++ * 10m) * factor, 2);
            bars.Add(new ProviderOhlcvBar(VietnamMarketCalendar.DailyOpenUtcMs(d), px, px + 50, px - 50, px, 1_000m));
        }
        return bars;
    }

    private static async Task<(AppDbContext Db, SeriesVci Vci, MbbIngestionService Svc, VietnamMarketCalendar Cal)>
        SeededBidAsync(string name, MbbIngestionOptions options)
    {
        var cal = new VietnamMarketCalendar(options.NonTradingDates.Select(DateOnly.Parse).ToHashSet());
        var tp = new TestTimeProvider(Local(2024, 6, 28, 16, 0));
        var vci = new SeriesVci { Bars = Series(cal, 1m) };
        var db = NewDb(name);
        var svc = new MbbIngestionService(db, vci, options, cal, tp).ForSymbol("BID");
        var seeded = await svc.BackfillAsync("1d");
        Assert.Null(seeded.Error);
        Assert.Equal(vci.Bars.Count, await db.InstrumentCandles.CountAsync(c => c.Symbol == "BID"));
        return (db, vci, svc, cal);
    }

    [Fact]
    public async Task Sync_StoredBarRevised_RefetchesWholeHistory_SingleAdjustmentBasis()
    {
        var (db, vci, svc, cal) = await SeededBidAsync(
            nameof(Sync_StoredBarRevised_RefetchesWholeHistory_SingleAdjustmentBasis), RevisionOptions());
        // Ex-date: provider back-adjusts EVERY historical bar by the same factor.
        vci.Bars = Series(cal, 0.95m);
        var callsBefore = vci.Calls;

        var report = await svc.SyncAsync();

        var tf = Assert.Single(report.Timeframes);
        Assert.Null(tf.Error);
        Assert.Equal(RevisionRefetchStatuses.Completed, tf.RevisionRefetchStatus);
        Assert.True(tf.RevisionRefetchRequests >= 2, $"expected paging, got {tf.RevisionRefetchRequests}");
        Assert.True(vci.Calls - callsBefore > 1);
        var stored = await db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == "BID").OrderBy(c => c.OpenTimeMs).ToListAsync();
        Assert.Equal(vci.Bars.Count, stored.Count);
        // Every stored bar — including those far outside the latest window — is on the new basis.
        Assert.All(stored.Zip(vci.Bars), pair =>
        {
            Assert.Equal(pair.Second.Close, pair.First.Close);
            Assert.Equal(1, pair.First.Revision);
        });

        // Idempotent: a pristine re-run detects no revision and writes nothing.
        var again = await svc.SyncAsync();
        Assert.Null(Assert.Single(again.Timeframes).RevisionRefetchStatus);
        Assert.Equal(0, again.Timeframes[0].UpdatedBars);
    }

    [Fact]
    public async Task Sync_RevisionRefetchIncomplete_WritesNothing_NoMixedBasis()
    {
        var (db, vci, svc, cal) = await SeededBidAsync(
            nameof(Sync_RevisionRefetchIncomplete_WritesNothing_NoMixedBasis), RevisionOptions(maxPages: 1));
        var before = await db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == "BID").OrderBy(c => c.OpenTimeMs).Select(c => c.Close).ToListAsync();
        vci.Bars = Series(cal, 0.95m);

        var report = await svc.SyncAsync();

        Assert.Equal(MbbSyncStatuses.Failed, report.Status);
        var tf = Assert.Single(report.Timeframes);
        Assert.Equal(RevisionRefetchStatuses.Incomplete, tf.RevisionRefetchStatus);
        Assert.StartsWith("revision-refetch-incomplete", tf.Error);
        var after = await db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == "BID").OrderBy(c => c.OpenTimeMs).Select(c => c.Close).ToListAsync();
        Assert.Equal(before, after); // old basis kept intact, never half-rewritten
    }

    [Theory]
    [InlineData("2024-01-02", null)]          // partial-range backfill overlapping stored rows
    [InlineData("2023-06-01", "2023-08-31")]  // older range ending in the past: the latest probe detects it
    public async Task Backfill_StoredBarsOnOldBasis_RebasesWholeHistory_Atomically(string from, string? to)
    {
        var (db, vci, svc, cal) = await SeededBidAsync(
            nameof(Backfill_StoredBarsOnOldBasis_RebasesWholeHistory_Atomically) + from, RevisionOptions());
        vci.Bars = Series(cal, 0.95m); // ex-date: provider re-based every bar

        var report = await svc.BackfillAsync("1d", DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to));

        Assert.Null(report.Error);
        Assert.Equal(RevisionRefetchStatuses.Completed, report.RevisionRefetchStatus);
        var stored = await db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == "BID").OrderBy(c => c.OpenTimeMs).ToListAsync();
        Assert.Equal(vci.Bars.Count, stored.Count);
        Assert.All(stored.Zip(vci.Bars), p => Assert.Equal(p.Second.Close, p.First.Close)); // single basis
    }

    [Fact]
    public async Task Backfill_RevisionRefetchIncomplete_WritesNothing()
    {
        var (db, vci, svc, cal) = await SeededBidAsync(
            nameof(Backfill_RevisionRefetchIncomplete_WritesNothing), RevisionOptions(maxPages: 1));
        var before = await db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == "BID").OrderBy(c => c.OpenTimeMs).Select(c => c.Close).ToListAsync();
        vci.Bars = Series(cal, 0.95m);

        var report = await svc.BackfillAsync("1d", new DateOnly(2024, 1, 2));

        Assert.Equal(RevisionRefetchStatuses.Incomplete, report.RevisionRefetchStatus);
        Assert.StartsWith("revision-refetch-incomplete", report.Error);
        var after = await db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == "BID").OrderBy(c => c.OpenTimeMs).Select(c => c.Close).ToListAsync();
        Assert.Equal(before, after); // old basis intact, never half-rewritten
    }

    [Fact]
    public async Task Sync_VolumeOnlyChange_PatchesInPlace_WithoutFullRefetch()
    {
        var (db, vci, svc, _) = await SeededBidAsync(
            nameof(Sync_VolumeOnlyChange_PatchesInPlace_WithoutFullRefetch), RevisionOptions());
        var last = vci.Bars[^1];
        vci.Bars[^1] = last with { Volume = 2_000m };

        var report = await svc.SyncAsync();

        var tf = Assert.Single(report.Timeframes);
        Assert.Null(tf.RevisionRefetchStatus);
        Assert.Equal(1, tf.UpdatedBars);
        Assert.Equal(2_000m, (await db.InstrumentCandles.SingleAsync(c => c.Symbol == "BID" && c.OpenTimeMs == last.OpenTimeMs)).Volume);
    }
}

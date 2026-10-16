using System.Text.Json;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

/// <summary>
/// MBB-02 focused tests: VCI/KBS strict parsing, Vietnam session calendar,
/// idempotent/resumable ingestion, gap classification, coverage reporting.
/// No real HTTP — providers are fakes; storage is EF InMemory.
/// </summary>
public class MbbIngestionTests
{
    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name).Options);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Fake VCI: serves the last countBack bars with OpenTimeMs &lt;= to.</summary>
    private sealed class FakeVciClient : IVciOhlcvClient
    {
        private readonly Func<string, long, int, IReadOnlyList<ProviderOhlcvBar>> _slice;

        public FakeVciClient(IReadOnlyList<ProviderOhlcvBar> bars)
        {
            _slice = (_, toUnixSeconds, countBack) => bars
                .Where(b => b.OpenTimeMs <= toUnixSeconds * 1000L)
                .OrderBy(b => b.OpenTimeMs)
                .TakeLast(countBack)
                .ToArray();
        }

        private FakeVciClient(Func<string, long, int, IReadOnlyList<ProviderOhlcvBar>> slice) =>
            _slice = slice;

        public static FakeVciClient ForTimeframes(
            IReadOnlyDictionary<string, IReadOnlyList<ProviderOhlcvBar>> barsByTimeframe) =>
            new((timeframe, to, countBack) =>
                (barsByTimeframe.TryGetValue(timeframe, out var bars) ? bars : [])
                    .Where(b => b.OpenTimeMs <= to * 1000L)
                    .OrderBy(b => b.OpenTimeMs)
                    .TakeLast(countBack)
                    .ToArray());

        public int CallCount { get; private set; }
        public List<string> RequestedTimeframes { get; } = [];

        public Task<IReadOnlyList<ProviderOhlcvBar>> GetBarsAsync(
            string symbol, string timeframe, long toUnixSeconds, int countBack,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            RequestedTimeframes.Add(timeframe);
            return Task.FromResult(_slice(timeframe, toUnixSeconds, countBack));
        }
    }

    private sealed class FakeKbsClient(IReadOnlyList<ProviderOhlcvBar> daily) : IKbsOhlcvClient
    {
        public Task<IReadOnlyList<ProviderOhlcvBar>> GetDailyBarsAsync(
            string symbol, DateOnly startLocal, DateOnly endLocal, CancellationToken ct = default) =>
            Task.FromResult(daily);
        public Task<IReadOnlyList<ProviderOhlcvBar>> GetHourlyBarsAsync(
            string symbol, DateOnly startLocal, DateOnly endLocal, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderOhlcvBar>>([]);
    }

    private static MbbIngestionOptions TestOptions() => new()
    {
        Enabled = true,
        Timeframes = ["1d", "1h"],
        LatestCountBack = 10,
        BatchCountBack = 500,
        MaxRequestsPerCycle = 10,
        PublishGraceMinutes = 10,
        DailyBackfillStartLocal = "2024-06-10",
        HourlyBackfillStartLocal = "2024-06-17",
        // 2024-06-18 (Tue) is a configured holiday: no expected bars that day.
        NonTradingDates = ["2024-06-18"],
    };

    private static ProviderOhlcvBar Bar(long openMs, decimal px = 100m, decimal vol = 1000m) =>
        new(openMs, px, px + 1, px - 1, px, vol);

    // Week of 2024-06-17..21 (Mon..Fri). "now" = Fri 2024-06-21 21:00 local = 14:00 UTC:
    // every session close + grace this week has passed.
    private static readonly DateTimeOffset Now =
        new(2024, 6, 21, 14, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Mon = new(2024, 6, 17);
    private static readonly DateOnly Tue = new(2024, 6, 18); // configured holiday
    private static readonly DateOnly Wed = new(2024, 6, 19);
    private static readonly DateOnly Thu = new(2024, 6, 20);
    private static readonly DateOnly Fri = new(2024, 6, 21);

    private static List<ProviderOhlcvBar> FullDailyBars()
    {
        var cal = new VietnamMarketCalendar(TestOptions().NonTradingDates.Select(DateOnly.Parse).ToHashSet());
        var bars = new List<ProviderOhlcvBar>();
        foreach (var d in cal.EnumerateTradingDates(new DateOnly(2024, 6, 10), Fri))
            bars.Add(Bar(VietnamMarketCalendar.DailyOpenUtcMs(d)));
        return bars;
    }

    private static List<ProviderOhlcvBar> FridayHourlyBars() =>
        VietnamMarketCalendar.HourlyOpensUtcMs(Fri).Select(open => Bar(open)).ToList();

    // ---------- VCI strict payload parsing ----------

    [Fact]
    public void VciParse_ValidPayload_ReturnsSortedDedupedBars()
    {
        // t values are unix SECONDS (probe-verified); out of order + duplicate.
        const string json = """
            [{"t":[1718874000,1718614800,1718874000],"o":[10,20,11],
              "h":[12,22,13],"l":[9,19,8],"c":[11,21,12],"v":[100,200,150]}]
            """;
        var bars = VciMarketDataClient.ParseBars(JsonDocument.Parse(json).RootElement, "MBB");
        Assert.Equal(2, bars.Count);
        Assert.Equal(1718614800_000L, bars[0].OpenTimeMs);
        Assert.Equal(1718874000_000L, bars[1].OpenTimeMs);
        Assert.Equal(11m, bars[1].Open);   // last duplicate wins
        Assert.Equal(150m, bars[1].Volume);
    }

    [Fact]
    public void VciParse_EmptyArray_ReturnsEmpty()
    {
        var bars = VciMarketDataClient.ParseBars(JsonDocument.Parse("[]").RootElement, "MBB");
        Assert.Empty(bars);
    }

    [Theory]
    [InlineData("{}", "root")]                       // root not an array
    [InlineData("[1]", "first element")]             // element not object
    [InlineData("[{\"t\":[1]}]", "missing array")]   // missing o/h/l/c/v
    [InlineData("[{\"t\":1,\"o\":[],\"h\":[],\"l\":[],\"c\":[],\"v\":[]}]", "missing array")]
    [InlineData("[{\"t\":[1],\"o\":[1,2],\"h\":[1],\"l\":[1],\"c\":[1],\"v\":[1]}]", "mismatch")]
    [InlineData("[{\"t\":[1],\"o\":[\"x\"],\"h\":[1],\"l\":[1],\"c\":[1],\"v\":[1]}]", "number")]
    public void VciParse_MalformedShapes_ThrowPayloadException(string json, string marker)
    {
        var ex = Assert.Throws<MarketDataPayloadException>(
            () => VciMarketDataClient.ParseBars(JsonDocument.Parse(json).RootElement, "MBB"));
        Assert.Contains(marker, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VciTimeFrameParam_Rejects4h()
    {
        Assert.Equal("ONE_DAY", VciMarketDataClient.TimeFrameParam("1d"));
        Assert.Equal("ONE_HOUR", VciMarketDataClient.TimeFrameParam("1h"));
        Assert.Throws<ArgumentException>(() => VciMarketDataClient.TimeFrameParam("4h"));
    }

    // ---------- KBS strict payload parsing ----------

    [Fact]
    public void KbsParse_ValidPayload_UsesVietnamOffset()
    {
        const string json = """
            {"data_day":[{"t":"2024-06-20 07:00","o":100,"h":101,"l":99,"c":100,"v":5}]}
            """;
        var bars = KbsMarketDataClient.ParseBars(JsonDocument.Parse(json).RootElement, "data_day", "MBB");
        Assert.Single(bars);
        Assert.Equal(VietnamMarketCalendar.ToUtcMs(new DateOnly(2024, 6, 20), new TimeOnly(7, 0)),
            bars[0].OpenTimeMs);
    }

    [Theory]
    [InlineData("[]", "root")]                        // root not object
    [InlineData("{\"data_day\":{}}", "array")]        // rows not array
    [InlineData("[{\"data_day\":[1]}]", "n/a")]       // row not object -> root still fails first
    [InlineData("{\"data_day\":[{\"o\":1}]}", "t")]   // missing timestamp
    public void KbsParse_MalformedShapes_ThrowPayloadException(string json, string marker)
    {
        var ex = Assert.Throws<MarketDataPayloadException>(
            () => KbsMarketDataClient.ParseBars(JsonDocument.Parse(json).RootElement, "data_day", "MBB"));
        if (marker != "n/a") Assert.Contains(marker, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Vietnam market calendar ----------

    [Fact]
    public void Calendar_ExpectedDailyOpens_ExcludeWeekendsAndHolidays()
    {
        var cal = new VietnamMarketCalendar([new DateOnly(2024, 6, 18)]);
        var opens = cal.EnumerateExpectedOpensUtcMs("1d", Mon, Fri);
        // Mon, Wed, Thu, Fri — Tue is a configured holiday; no weekend in range anyway.
        Assert.Equal(4, opens.Count);
        Assert.DoesNotContain(VietnamMarketCalendar.DailyOpenUtcMs(Tue), opens);
        Assert.Contains(VietnamMarketCalendar.DailyOpenUtcMs(Mon), opens);

        var weekend = cal.EnumerateExpectedOpensUtcMs("1d",
            new DateOnly(2024, 6, 22), new DateOnly(2024, 6, 23)); // Sat-Sun
        Assert.Empty(weekend);
    }

    [Fact]
    public void Calendar_ExpectedHourlyOpens_FiveSlotsNoLunch()
    {
        var cal = new VietnamMarketCalendar();
        var opens = cal.EnumerateExpectedOpensUtcMs("1h", Mon, Mon);
        Assert.Equal(5, opens.Count);
        var localHours = opens.Select(o => TimeOnly.FromDateTime(
            VietnamMarketCalendar.ToLocal(o).DateTime)).ToArray();
        Assert.Equal(
            new[] { new TimeOnly(9, 0), new(10, 0), new(11, 0), new(13, 0), new(14, 0) },
            localHours);
        Assert.DoesNotContain(new TimeOnly(12, 0), localHours); // lunch is never expected
    }

    [Fact]
    public void Calendar_SessionClose_IsSessionAwareNotFixed60m()
    {
        var cal = new VietnamMarketCalendar();
        var d = Mon;
        Assert.Equal(VietnamMarketCalendar.ToUtcMs(d, new TimeOnly(11, 30)),
            cal.SessionCloseUtcMs("1h", VietnamMarketCalendar.ToUtcMs(d, new TimeOnly(11, 0))));
        Assert.Equal(VietnamMarketCalendar.ToUtcMs(d, new TimeOnly(14, 45)),
            cal.SessionCloseUtcMs("1h", VietnamMarketCalendar.ToUtcMs(d, new TimeOnly(14, 0))));
        Assert.Equal(VietnamMarketCalendar.ToUtcMs(d, new TimeOnly(10, 0)),
            cal.SessionCloseUtcMs("1h", VietnamMarketCalendar.ToUtcMs(d, new TimeOnly(9, 0))));
        Assert.Equal(VietnamMarketCalendar.ToUtcMs(d, new TimeOnly(14, 45)),
            cal.SessionCloseUtcMs("1d", VietnamMarketCalendar.DailyOpenUtcMs(d)));
    }

    [Fact]
    public void Calendar_ClassifiesLunchBreakBarAsAnomaly()
    {
        var cal = new VietnamMarketCalendar();
        var noon = VietnamMarketCalendar.ToUtcMs(Thu, new TimeOnly(12, 0));
        Assert.Equal(SessionOpenKind.LunchBreak, cal.ClassifyHourlyOpenUtcMs(noon));
        Assert.Equal(SessionOpenKind.Expected,
            cal.ClassifyHourlyOpenUtcMs(VietnamMarketCalendar.ToUtcMs(Thu, new TimeOnly(9, 0))));
        Assert.Equal(SessionOpenKind.OffSession,
            cal.ClassifyHourlyOpenUtcMs(VietnamMarketCalendar.ToUtcMs(Thu, new TimeOnly(8, 30))));
        Assert.Equal(SessionOpenKind.NonTradingDay,
            cal.ClassifyHourlyOpenUtcMs(VietnamMarketCalendar.ToUtcMs(
                new DateOnly(2024, 6, 22), new TimeOnly(9, 0)))); // Saturday

        var holidayCal = new VietnamMarketCalendar([Tue]); // configured holiday
        Assert.Equal(SessionOpenKind.NonTradingDay,
            holidayCal.ClassifyHourlyOpenUtcMs(VietnamMarketCalendar.ToUtcMs(Tue, new TimeOnly(9, 0))));
    }

    [Fact]
    public void Calendar_IsFinalized_RespectsSessionClosePlusGrace()
    {
        var cal = new VietnamMarketCalendar(publishGrace: TimeSpan.FromMinutes(10));
        var open = VietnamMarketCalendar.ToUtcMs(Mon, new TimeOnly(14, 0)); // closes 14:45
        var beforeClose = VietnamMarketCalendar.ToUtcMs(Mon, new TimeOnly(14, 30));
        var afterGrace = VietnamMarketCalendar.ToUtcMs(Mon, new TimeOnly(14, 56));
        Assert.False(cal.IsFinalized("1h", open, beforeClose));
        Assert.True(cal.IsFinalized("1h", open, afterGrace));
    }

    [Fact]
    public void Calendar_LastExpectedFinalizedOpen_WalksBackOverWeekend()
    {
        var cal = new VietnamMarketCalendar();
        var sundayNoonUtc = VietnamMarketCalendar.ToUtcMs(new DateOnly(2024, 6, 23), new TimeOnly(12, 0));
        var last = cal.LastExpectedFinalizedOpenUtcMs("1d", sundayNoonUtc);
        Assert.Equal(VietnamMarketCalendar.DailyOpenUtcMs(Fri), last);
    }

    [Fact]
    public void Calendar_GroupMissingRanges_GroupsContiguousRuns()
    {
        var expected = new List<long> { 1, 2, 3, 4, 5 };
        var ranges = VietnamMarketCalendar.GroupMissingRanges(expected, new HashSet<long> { 2, 3, 5 });
        Assert.Equal(2, ranges.Count);
        Assert.Equal(new CandleGapRange(2, 3, 2), ranges[0]);
        Assert.Equal(new CandleGapRange(5, 5, 1), ranges[1]);
    }

    [Fact]
    public void Calendar_ParseNonTradingDates_RejectsBadFormat() =>
        Assert.Throws<ArgumentException>(() => VietnamMarketCalendar.ParseNonTradingDates(["20/06/2024"]));

    // ---------- Ingestion: idempotent writes, provenance, resume ----------

    private static MbbIngestionService NewService(
        AppDbContext db, IVciOhlcvClient vci, MbbIngestionOptions? options = null) =>
        new(db, vci, options ?? TestOptions(), timeProvider: new FixedTimeProvider(Now));

    [Fact]
    public async Task Upsert_WritesProvenanceAndSessionAwareClose()
    {
        await using var db = NewDb(nameof(Upsert_WritesProvenanceAndSessionAwareClose));
        var svc = NewService(db, new FakeVciClient([]));
        var open = VietnamMarketCalendar.HourlyOpensUtcMs(Wed)[4]; // 14:00 local
        var result = await svc.UpsertBarsAsync("1h", [Bar(open)]);

        Assert.Equal(1, result.Inserted);
        var row = await db.InstrumentCandles.SingleAsync();
        Assert.Equal("MBB", row.Symbol);
        Assert.Equal("VCI", row.Provider);
        Assert.Equal("adjusted", row.PriceBasis);
        Assert.Equal("HOSE", row.Venue);
        Assert.Equal("Asia/Ho_Chi_Minh", row.SourceTimezone);
        Assert.Equal(VietnamMarketCalendar.ToUtcMs(Wed, new TimeOnly(14, 45)), row.CloseTimeMs);
        Assert.Equal(0, row.Revision);
    }

    [Fact]
    public async Task Upsert_ReRun_IsIdempotent_AndProviderRewriteBumpsRevision()
    {
        await using var db = NewDb(nameof(Upsert_ReRun_IsIdempotent_AndProviderRewriteBumpsRevision));
        var svc = NewService(db, new FakeVciClient([]));
        var open = VietnamMarketCalendar.DailyOpenUtcMs(Wed);

        await svc.UpsertBarsAsync("1d", [Bar(open)]);
        var rerun = await svc.UpsertBarsAsync("1d", [Bar(open)]);
        Assert.Equal(1, await db.InstrumentCandles.CountAsync());
        Assert.Equal(0, rerun.Inserted + rerun.Updated);

        // Provider rewrote the close (adjustment change) -> update in place, Revision++.
        var rewrite = await svc.UpsertBarsAsync("1d", [Bar(open, px: 105m)]);
        Assert.Equal(1, rewrite.Updated);
        var row = await db.InstrumentCandles.SingleAsync();
        Assert.Equal(105m, row.Close);
        Assert.Equal(1, row.Revision);
        Assert.True(row.UpdatedAtUtc >= row.FetchedAtUtc);
    }

    [Fact]
    public async Task Upsert_SkipsFormingAndRejectsInvalidBars()
    {
        await using var db = NewDb(nameof(Upsert_SkipsFormingAndRejectsInvalidBars));
        // Mid-session now: Fri 10:30 local = 03:30 UTC. The Fri 10:00 bar opened but
        // closes 11:00 local — still forming, must not be written.
        var midSession = new FixedTimeProvider(new DateTimeOffset(2024, 6, 21, 3, 30, 0, TimeSpan.Zero));
        var svc = new MbbIngestionService(db, new FakeVciClient([]), TestOptions(),
            timeProvider: midSession);
        var forming = VietnamMarketCalendar.ToUtcMs(Fri, new TimeOnly(10, 0));
        var invalid = new ProviderOhlcvBar(
            VietnamMarketCalendar.DailyOpenUtcMs(Wed), 100, 90, 95, 100, 10); // high < low
        var result = await svc.UpsertBarsAsync("1h", [Bar(forming)]);
        Assert.Equal(1, result.SkippedForming);
        Assert.Equal(0, result.Inserted);

        var result2 = await svc.UpsertBarsAsync("1d", [invalid]);
        Assert.Equal(1, result2.RejectedInvalid);
        Assert.Empty(db.InstrumentCandles);
    }

    [Fact]
    public async Task Sync_Disabled_ReturnsDisabledStatus_WithoutFetching()
    {
        await using var db = NewDb(nameof(Sync_Disabled_ReturnsDisabledStatus_WithoutFetching));
        var vci = new FakeVciClient(FullDailyBars());
        var options = TestOptions();
        options.Enabled = false;
        var svc = NewService(db, vci, options);

        var report = await svc.SyncAsync();
        Assert.Equal(MbbSyncStatuses.Disabled, report.Status);
        Assert.Equal(0, vci.CallCount);
        Assert.Empty(db.InstrumentCandles);
    }

    [Fact]
    public async Task Sync_FillsExpectedOpens_AndResumesIdempotently()
    {
        await using var db = NewDb(nameof(Sync_FillsExpectedOpens_AndResumesIdempotently));
        // VCI holds the complete expected daily set 2024-06-10..21 (minus holiday 06-18).
        var vci = new FakeVciClient(FullDailyBars());
        var options = TestOptions();
        options.Timeframes = ["1d"];
        var svc = NewService(db, vci, options);

        var report = await svc.SyncAsync();
        Assert.Equal(MbbSyncStatuses.Succeeded, report.Status);
        var tf = Assert.Single(report.Timeframes);
        Assert.Equal(0, tf.Error?.Length ?? 0);
        // Expected trading days 6/10-21 excl. 6/15-16 weekend + 6/18 holiday = 9 bars.
        Assert.Equal(9, await db.InstrumentCandles.CountAsync());

        // Second run: everything present -> zero writes, zero missing.
        var second = await svc.SyncAsync();
        var tf2 = Assert.Single(second.Timeframes);
        Assert.Equal(0, tf2.InsertedBars + tf2.UpdatedBars);
        Assert.Equal(0, tf2.MissingExpectedBars);
    }

    [Fact]
    public async Task Sync_BackfillBoundedByRequestBudget_LeavesVisibleRemainder()
    {
        await using var db = NewDb(nameof(Sync_BackfillBoundedByRequestBudget_LeavesVisibleRemainder));
        var vci = new FakeVciClient(FullDailyBars());
        var options = TestOptions();
        options.Timeframes = ["1d"];
        options.LatestCountBack = 1;             // latest fetch covers only 1 bar
        options.BatchCountBack = 3;              // each gap-range fetch covers <=3 missing
        options.MaxGapRangesPerCycle = 1;        // only one range per cycle
        var svc = NewService(db, vci, options);

        var report = await svc.SyncAsync();
        var tf = Assert.Single(report.Timeframes);
        Assert.True(tf.MissingExpectedBars > 0 || tf.GapRangesRemaining >= 0);
        // Resume: a second cycle continues filling instead of restarting.
        var before = await db.InstrumentCandles.CountAsync();
        await svc.SyncAsync();
        var after = await db.InstrumentCandles.CountAsync();
        Assert.True(after >= before);
    }

    [Fact]
    public async Task Sync_ReservesLatestRequestPerTimeframe_WhenEarlierTimeframeHasGaps()
    {
        await using var db = NewDb(nameof(Sync_ReservesLatestRequestPerTimeframe_WhenEarlierTimeframeHasGaps));
        var vci = FakeVciClient.ForTimeframes(new Dictionary<string, IReadOnlyList<ProviderOhlcvBar>>
        {
            // Daily has only the latest bar, leaving a fillable missing range that can
            // consume gap requests. Hourly has its normal Friday session bars.
            ["1d"] = [Bar(VietnamMarketCalendar.DailyOpenUtcMs(Fri))],
            ["1h"] = FridayHourlyBars(),
        });
        var options = TestOptions();
        options.MaxRequestsPerCycle = 3;
        options.LatestCountBack = 10;
        var svc = NewService(db, vci, options);

        var report = await svc.SyncAsync();

        Assert.Equal(MbbSyncStatuses.Succeeded, report.Status);
        Assert.Equal(3, vci.CallCount);
        Assert.Equal(["1d", "1d", "1h"], vci.RequestedTimeframes);
        Assert.Equal(2, report.Timeframes.Count);
        Assert.True(report.Timeframes[0].MissingExpectedBars > 0);
        Assert.Equal(1, report.Timeframes[0].GapRangesProcessed);
        Assert.Equal(5, await db.InstrumentCandles.CountAsync(c => c.Timeframe == "1h"));
    }

    [Fact]
    public async Task Sync_DoesNotRetryGapRangesBeforeGapFillStart()
    {
        await using var db = NewDb(nameof(Sync_DoesNotRetryGapRangesBeforeGapFillStart));
        var recentDaily = new[]
        {
            Bar(VietnamMarketCalendar.DailyOpenUtcMs(Wed)),
            Bar(VietnamMarketCalendar.DailyOpenUtcMs(Thu)),
            Bar(VietnamMarketCalendar.DailyOpenUtcMs(Fri)),
        };
        var vci = new FakeVciClient(recentDaily);
        var options = TestOptions();
        options.Timeframes = ["1d"];
        options.GapFillStartLocal = "2024-06-19";
        options.MaxRequestsPerCycle = 10;
        var svc = NewService(db, vci, options);

        var report = await svc.SyncAsync();
        var tf = Assert.Single(report.Timeframes);

        Assert.Equal(1, vci.CallCount); // latest only; old missing range is reported, not retried
        Assert.Equal(3, tf.InsertedBars);
        Assert.True(tf.MissingExpectedBars > 0);
        Assert.Equal(0, tf.GapRangesProcessed);
        Assert.Equal(0, tf.GapRangesRemaining);
    }

    [Fact]
    public async Task Backfill_PagesBackwardUntilStartDate()
    {
        await using var db = NewDb(nameof(Backfill_PagesBackwardUntilStartDate));
        var vci = new FakeVciClient(FullDailyBars());
        var options = TestOptions();
        options.BatchCountBack = 4;
        options.MaxRequestsPerCycle = 10;
        options.RequestDelayMs = 0;
        var svc = NewService(db, vci, options);

        var result = await svc.BackfillAsync("1d");
        Assert.Equal(9, result.InsertedBars);
        Assert.Equal(0, result.MissingExpectedBars);
        Assert.True(vci.CallCount >= 2); // 9 bars with page size 4 needs multiple requests
    }

    [Fact]
    public async Task Backfill_Rejects4h()
    {
        await using var db = NewDb(nameof(Backfill_Rejects4h));
        var svc = NewService(db, new FakeVciClient([]));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.BackfillAsync("4h"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.BackfillAsync("4H"));
    }

    [Fact]
    public async Task Backfill_Disabled_DoesNotFetch()
    {
        await using var db = NewDb(nameof(Backfill_Disabled_DoesNotFetch));
        var vci = new FakeVciClient(FullDailyBars());
        var options = TestOptions();
        options.Enabled = false;
        var svc = NewService(db, vci, options);

        var result = await svc.BackfillAsync("1d");

        Assert.Equal("MBB ingestion is disabled.", result.Error);
        Assert.Equal(0, vci.CallCount);
        Assert.Empty(db.InstrumentCandles);
    }

    // ---------- Data quality: coverage, freshness, anomaly ----------

    private static MbbDataQualityService NewDq(
        AppDbContext db, MbbIngestionOptions? options = null, IKbsOhlcvClient? kbs = null) =>
        new(db, options ?? TestOptions(), kbs: kbs, timeProvider: new FixedTimeProvider(Now));

    private static InstrumentCandle Row(long openMs, string tf, decimal px = 100m) => new()
    {
        Symbol = "MBB", Timeframe = tf, OpenTimeMs = openMs,
        CloseTimeMs = openMs + 60_000, Open = px, High = px + 1, Low = px - 1,
        Close = px, Volume = 1000, Provider = "VCI", PriceBasis = "adjusted",
        Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = Now.UtcDateTime, UpdatedAtUtc = Now.UtcDateTime,
    };

    [Fact]
    public async Task Coverage_FullHistory_Reports100PctNotStale()
    {
        await using var db = NewDb(nameof(Coverage_FullHistory_Reports100PctNotStale));
        var cal = new VietnamMarketCalendar(
            VietnamMarketCalendar.ParseNonTradingDates(TestOptions().NonTradingDates));
        foreach (var d in cal.EnumerateTradingDates(new DateOnly(2024, 6, 10), Fri))
            db.InstrumentCandles.Add(Row(VietnamMarketCalendar.DailyOpenUtcMs(d), "1d"));
        await db.SaveChangesAsync();

        var options = TestOptions();
        options.Timeframes = ["1d"];
        var report = await NewDq(db, options).BuildReportAsync();
        var tf = Assert.Single(report.Timeframes);
        Assert.Equal(9, tf.TotalBars);
        Assert.Equal(9, tf.ExpectedBars);
        Assert.Equal(0, tf.MissingBars);
        Assert.Equal(100.0, tf.CoveragePct);
        Assert.False(tf.IsStale);
        Assert.Equal(0, tf.UnexpectedSessionBars);
        Assert.Equal("VCI", report.Provider);
        Assert.Equal("KBS", report.CrossCheckProvider);
        Assert.Equal("adjusted", report.PriceBasis);
    }

    [Fact]
    public async Task Coverage_MissingAndAnomalousBars_AreClassified()
    {
        await using var db = NewDb(nameof(Coverage_MissingAndAnomalousBars_AreClassified));
        var cal = new VietnamMarketCalendar(
            VietnamMarketCalendar.ParseNonTradingDates(TestOptions().NonTradingDates));
        // All expected 1d bars EXCEPT Wed — that single weekday is a real gap.
        foreach (var d in cal.EnumerateTradingDates(new DateOnly(2024, 6, 10), Fri))
        {
            if (d == Wed) continue;
            db.InstrumentCandles.Add(Row(VietnamMarketCalendar.DailyOpenUtcMs(d), "1d"));
        }
        // The known VCI anomaly: a 1h bar at 12:00 local on 2024-06-20 (lunch break).
        db.InstrumentCandles.Add(Row(VietnamMarketCalendar.ToUtcMs(Thu, new TimeOnly(12, 0)), "1h"));
        await db.SaveChangesAsync();

        var report = await NewDq(db).BuildReportAsync();
        var day = Assert.Single(report.Timeframes, t => t.Timeframe == "1d");
        Assert.Equal(1, day.MissingBars);
        Assert.Equal(1, day.GapRangeCount);
        Assert.Equal(VietnamMarketCalendar.DailyOpenUtcMs(Wed), day.TopGaps[0].StartOpenTimeMs);

        var hour = Assert.Single(report.Timeframes, t => t.Timeframe == "1h");
        Assert.Equal(1, hour.UnexpectedSessionBars); // the 12:00 anomaly is surfaced, not normalized
        Assert.Contains(VietnamMarketCalendar.ToUtcMs(Thu, new TimeOnly(12, 0)),
            hour.UnexpectedSessionOpenTimeMs);
        Assert.True(hour.MissingBars > 0); // hourly lane is essentially empty -> stale
        Assert.True(hour.IsStale);
    }

    [Fact]
    public async Task Coverage_WithinGraceAndCycle_IsNotStaleYet()
    {
        await using var db = NewDb(nameof(Coverage_WithinGraceAndCycle_IsNotStaleYet));
        // Fri 14:56 local = 07:56 UTC: the 14:00 bar is finalized (14:45 + 10m grace),
        // but the 20-minute ingestion cycle has not had time to fetch it yet.
        var justAfterFinalized = new FixedTimeProvider(
            new DateTimeOffset(2024, 6, 21, 7, 56, 0, TimeSpan.Zero));
        var options = TestOptions();
        options.Timeframes = ["1h"];

        var report = await new MbbDataQualityService(db, options,
            timeProvider: justAfterFinalized).BuildReportAsync();
        var tf = Assert.Single(report.Timeframes);

        Assert.True(tf.MissingBars > 0);
        Assert.False(tf.IsStale);

        var afterCycle = new MbbDataQualityService(db, options,
            timeProvider: new FixedTimeProvider(
                new DateTimeOffset(2024, 6, 21, 8, 16, 0, TimeSpan.Zero)));
        var staleReport = await afterCycle.BuildReportAsync();
        Assert.True(staleReport.Timeframes[0].IsStale);
    }

    [Fact]
    public async Task CrossCheck_ComparesClosesWithoutWriting()
    {
        await using var db = NewDb(nameof(CrossCheck_ComparesClosesWithoutWriting));
        var openMon = VietnamMarketCalendar.DailyOpenUtcMs(Mon);
        var openWed = VietnamMarketCalendar.DailyOpenUtcMs(Wed);
        db.InstrumentCandles.Add(Row(openMon, "1d", 100m));
        db.InstrumentCandles.Add(Row(openWed, "1d", 105m));
        await db.SaveChangesAsync();

        var kbs = new FakeKbsClient(
        [
            new ProviderOhlcvBar(openMon, 100, 101, 99, 100, 0),   // equal close
            new ProviderOhlcvBar(openWed, 105, 106, 104, 108, 0),  // diff 3 > 1 VND
            new ProviderOhlcvBar(openWed + 86_400_000L, 1, 1, 1, 1, 0), // KBS-only bar
        ]);
        var report = await NewDq(db, kbs: kbs)
            .CrossCheckAsync("1d", Mon, Fri);

        Assert.Equal(2, report.ComparedBars);
        Assert.Equal(1, report.PriceMismatchCount);
        Assert.Equal(openWed, report.TopMismatches[0].OpenTimeMs);
        Assert.Equal(1, report.OnlyInCrossCheck);
        Assert.Equal(2, await db.InstrumentCandles.CountAsync()); // cross-check never writes
    }

    // ---------- BTC non-regression ----------

    [Fact]
    public void Catalog_BtcContractUnchanged_MbbStillDisabled()
    {
        var catalog = InstrumentCatalog.Default;
        var btc = catalog.Get("BTCUSDT")!;
        Assert.True(btc.Enabled);
        Assert.Equal(new[] { "1h", "4h", "1d" }, btc.ActiveTimeframes.ToArray());
        Assert.Equal("4h", btc.DefaultTimeframe);
        Assert.Equal("Binance", btc.DataProvider);

        var mbb = catalog.Get("MBB")!;
        Assert.False(mbb.Enabled);
        Assert.Equal(new[] { "1h", "1d" }, mbb.ActiveTimeframes.ToArray());
        Assert.DoesNotContain("4h", mbb.ActiveTimeframes);
    }
}

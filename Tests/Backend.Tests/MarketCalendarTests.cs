using System.Net;
using System.Text.Json;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Backend.Tests;

/// <summary>
/// EQX v2 A1: canonical HOSE calendar file (official sources), loader validation, config override
/// reporting, idempotent DB sync, the public endpoint, data-quality gap classification, and the
/// indicator-continuity regression across the 2026-08-31..09-02 National Day closure.
/// </summary>
public class MarketCalendarTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    public MarketCalendarTests(TestWebApplicationFactory factory) => _factory = factory;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static DateOnly D(string s) => DateOnly.Parse(s);

    // --- canonical file -------------------------------------------------------------

    [Fact]
    public void CanonicalFile_LoadsWithOfficialNationalDay2026_AndDropsContradictedLegacyDates()
    {
        var src = MarketCalendarSource.Default;
        var byDate = src.Days.ToDictionary(d => d.Date);

        foreach (var day in new[] { "2026-08-31", "2026-09-01", "2026-09-02", "2026-01-02" })
        {
            var e = byDate[D(day)];
            Assert.Equal(MarketCalendarVerification.Verified, e.Verification);
            Assert.StartsWith("https://staticfile.hsx.vn/", e.SourceUrl);
            Assert.Contains("TB-SGDHCM", e.SourceTitle);
        }
        Assert.Contains("2294/TB-SGDHCM", byDate[D("2026-08-31")].SourceTitle);
        Assert.Contains("2410/TB-SGDHCM", byDate[D("2026-01-02")].SourceTitle);

        // Old config dates that the official 2025 notice / market-wide bars contradict are gone.
        Assert.False(byDate.ContainsKey(D("2025-02-03")));
        Assert.False(byDate.ContainsKey(D("2021-12-31")));
        // 2021–2023 stay closed but are not claimed as verified.
        Assert.Equal(MarketCalendarVerification.UnverifiedLegacy, byDate[D("2022-02-01")].Verification);
        Assert.All(src.Days.Where(d => d.Verification == MarketCalendarVerification.Verified),
            d => Assert.StartsWith("https://staticfile.hsx.vn/", d.SourceUrl));
        Assert.All(src.Days, d => Assert.True(d.Date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday));

        var cal = VietnamMarketCalendar.CreateDefault();
        Assert.False(cal.IsTradingDay(D("2026-09-01")));
        Assert.True(cal.IsTradingDay(D("2026-09-03")));
        Assert.True(cal.IsTradingDay(D("2025-02-03")));
        Assert.False(cal.IsUnverifiedNonTradingDate(D("2026-09-01")));
        Assert.True(cal.IsUnverifiedNonTradingDate(D("2022-02-01")));
        Assert.Empty(cal.Source!.ConfigOverrideDates);
    }

    [Fact]
    public void ConfigOverride_IsUnionedAndReported_NotSilentlyMerged()
    {
        var cal = VietnamMarketCalendar.CreateDefault(["2026-10-15", "2026-09-01"]);
        Assert.False(cal.IsTradingDay(D("2026-10-15")));
        Assert.Equal([D("2026-10-15")], cal.Source!.ConfigOverrideDates); // 09-01 is already canonical
        Assert.True(cal.IsUnverifiedNonTradingDate(D("2026-10-15")));
    }

    [Theory]
    [InlineData("""{"schemaVersion":"other","venue":"HOSE","coverage":{"fromDate":"2026-01-01","toDate":"2026-12-31"},"days":[]}""")]
    [InlineData("""{"schemaVersion":"vn-hose-calendar-v1","venue":"HOSE","coverage":{"fromDate":"2026-01-01","toDate":"2026-12-31"},"days":[{"date":"2026-08-29","kind":"market-holiday","reason":"x","verification":"unverified-legacy"}]}""")]
    [InlineData("""{"schemaVersion":"vn-hose-calendar-v1","venue":"HOSE","coverage":{"fromDate":"2026-01-01","toDate":"2026-12-31"},"days":[{"date":"2026-09-01","kind":"market-holiday","reason":"x","verification":"verified"}]}""")]
    [InlineData("""{"schemaVersion":"vn-hose-calendar-v1","venue":"HOSE","coverage":{"fromDate":"2026-01-01","toDate":"2026-12-31"},"days":[{"date":"2026-09-01","kind":"market-holiday","reason":"x","verification":"unverified-legacy"},{"date":"2026-09-01","kind":"market-holiday","reason":"y","verification":"unverified-legacy"}]}""")]
    [InlineData("""{"schemaVersion":"vn-hose-calendar-v1","venue":"HOSE","coverage":{"fromDate":"2026-01-01","toDate":"2026-12-31"},"days":[{"date":"2026-09-01","kind":"holiday","reason":"x","verification":"unverified-legacy"}]}""")]
    [InlineData("""{"schemaVersion":"vn-hose-calendar-v1","venue":"HOSE","coverage":{"fromDate":"2026-01-01","toDate":"2026-12-31"},"days":[{"date":"2027-01-01","kind":"market-holiday","reason":"x","verification":"unverified-legacy"}]}""")]
    [InlineData("not json")]
    public void Parse_FailsClosed_OnInvalidDocument(string json) =>
        Assert.Throws<InvalidDataException>(() => MarketCalendarSource.Parse(json));

    [Fact]
    public void LoadFile_MissingFile_Throws() =>
        Assert.Throws<FileNotFoundException>(() => MarketCalendarSource.LoadFile(
            Path.Combine(Path.GetTempPath(), "no-such-calendar-" + Guid.NewGuid().ToString("N") + ".json")));

    // --- DB sync ------------------------------------------------------------------

    [Fact]
    public async Task Sync_IsIdempotent_UpdatesDeletes_AndMirrorsOverrides()
    {
        await using var db = NewDb(nameof(Sync_IsIdempotent_UpdatesDeletes_AndMirrorsOverrides));
        var src = MarketCalendarSource.Default.WithOverrides([D("2026-10-15")]);

        var first = await MarketCalendarSync.SyncAsync(db, src);
        Assert.Equal(src.Days.Count + 1, first.Inserted);
        var second = await MarketCalendarSync.SyncAsync(db, src);
        Assert.Equal((0, 0, 0, src.Days.Count + 1), (second.Inserted, second.Updated, second.Deleted, second.Unchanged));

        var over = await db.MarketCalendarDays.SingleAsync(d => d.Date == D("2026-10-15"));
        Assert.Equal(MarketCalendarKinds.MarketClosure, over.Kind);
        Assert.Equal(MarketCalendarVerification.UnverifiedLegacy, over.Verification);

        // A stale DB row (e.g. the old wrong 2025-02-03) is removed; an edited row is updated.
        db.MarketCalendarDays.Add(new MarketCalendarDay
        {
            Venue = "HOSE", Date = D("2025-02-03"), Kind = "market-holiday", Reason = "legacy",
            Verification = "unverified-legacy", CalendarVersion = "old", UpdatedAtUtc = DateTime.UtcNow,
        });
        (await db.MarketCalendarDays.SingleAsync(d => d.Date == D("2026-09-01"))).Reason = "tampered";
        await db.SaveChangesAsync();
        var third = await MarketCalendarSync.SyncAsync(db, src);
        Assert.Equal((0, 1, 1), (third.Inserted, third.Updated, third.Deleted));
        Assert.False(await db.MarketCalendarDays.AnyAsync(d => d.Date == D("2025-02-03")));
    }

    // --- endpoint -------------------------------------------------------------------

    [Fact]
    public async Task Endpoint_VnHose_IsPublic_AndListsSources()
    {
        var response = await _factory.CreateClient().GetAsync("/api/market-calendar/vn-hose");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("vn-hose-calendar-v1", root.GetProperty("contractVersion").GetString());
        Assert.Equal("HOSE", root.GetProperty("venue").GetString());
        var days = root.GetProperty("days").EnumerateArray().ToArray();
        var nd = days.Single(d => d.GetProperty("date").GetString() == "2026-09-01");
        Assert.Equal("verified", nd.GetProperty("verification").GetString());
        Assert.StartsWith("https://staticfile.hsx.vn/", nd.GetProperty("sourceUrl").GetString());
        Assert.True(root.GetProperty("verifiedDays").GetInt32() > 0);
        Assert.True(root.GetProperty("unverifiedLegacyDays").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("configOverrideDates").ValueKind);
    }

    // --- indicator continuity regression ------------------------------------------------

    private static async Task SeedDailyAsync(AppDbContext db, VietnamMarketCalendar cal, DateOnly from, DateOnly to,
        params string[] skipDates)
    {
        var skip = skipDates.Select(D).ToHashSet();
        decimal px = 20_000m;
        var i = 0;
        foreach (var date in cal.EnumerateTradingDates(from, to))
        {
            i++;
            if (skip.Contains(date)) continue;
            var open = VietnamMarketCalendar.DailyOpenUtcMs(date);
            var c = px + (i % 7 - 3) * 25m;
            db.InstrumentCandles.Add(new InstrumentCandle
            {
                Symbol = "MBB", Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = cal.SessionCloseUtcMs("1d", open),
                Open = px, High = Math.Max(px, c) + 50m, Low = Math.Min(px, c) - 50m, Close = c,
                Volume = 1_000_000m + i, Provider = "VCI", PriceBasis = "adjusted", Venue = "HOSE",
                SourceTimezone = "Asia/Ho_Chi_Minh", FetchedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            px = c;
        }
        await db.SaveChangesAsync();
    }

    private static MbbTechnicalRecomputeService Recompute(AppDbContext db, VietnamMarketCalendar cal)
    {
        var opts = OptionsFactory.Create(new IndexingOptions
        {
            CandlePatternBatchSize = 100, VolumeStatsBatchSize = 100, TechnicalIndicatorsBatchSize = 100,
            PatternSequenceBatchSize = 100, WindowVectorBatchSize = 100,
        });
        return new MbbTechnicalRecomputeService(db,
            new CandlePatternIndexer(db, new FakeBinanceKlinesService(), opts),
            new WindowVectorIndexer(db, opts),
            new CandleVolumeIndexer(db, NullLogger<CandleVolumeIndexer>.Instance, opts),
            new TechnicalIndicatorIndexer(db, NullLogger<TechnicalIndicatorIndexer>.Instance, opts),
            new CandlePatternSequenceIndexer(db, NullLogger<CandlePatternSequenceIndexer>.Instance, opts),
            cal);
    }

    private static async Task<List<TechnicalIndicator>> IndicatorsAsync(AppDbContext db) =>
        await db.TechnicalIndicators.Where(x => x.Symbol == "MBB" && x.Timeframe == "1d")
            .OrderBy(x => x.OpenTimeMs).ToListAsync();

    [Fact]
    public async Task Sma50Warmup_DoesNotReset_AcrossOfficialNationalDayClosure()
    {
        var canonical = VietnamMarketCalendar.CreateDefault();
        await using var db = NewDb(nameof(Sma50Warmup_DoesNotReset_AcrossOfficialNationalDayClosure));
        // Real trading days only (bars exist exactly where HOSE traded): 2026-06-01 .. 2026-10-16.
        await SeedDailyAsync(db, canonical, D("2026-06-01"), D("2026-10-16"));

        var report = await Recompute(db, canonical).RecomputeAsync("MBB", "1d");
        Assert.Equal(MbbRecomputeStatuses.Succeeded, report.Status);
        var rows = await IndicatorsAsync(db);
        var firstSma = rows.FindIndex(r => r.Sma50.HasValue);
        Assert.Equal(49, firstSma); // one warmup only, from the first bar
        Assert.All(rows.Skip(firstSma), r => Assert.NotNull(r.Sma50));
        var sep3 = rows.Single(r => r.OpenTimeMs == VietnamMarketCalendar.DailyOpenUtcMs(D("2026-09-03")));
        Assert.NotNull(sep3.Sma50);

        // Pre-v2 calendar (no 2026-08-31 / 2026-09-01): the closure looked like 2 missing bars and
        // restarted every indicator at 2026-09-03 — the bug this wave fixes.
        var legacy = new VietnamMarketCalendar(canonical.Source!.Days.Select(d => d.Date)
            .Where(d => d != D("2026-08-31") && d != D("2026-09-01")));
        await Recompute(db, legacy).RecomputeAsync("MBB", "1d");
        var legacyRows = await IndicatorsAsync(db);
        Assert.Null(legacyRows.Single(r => r.OpenTimeMs == sep3.OpenTimeMs).Sma50);
    }

    [Fact]
    public async Task RealGap_StillRestartsWarmup_WithCanonicalCalendar()
    {
        var canonical = VietnamMarketCalendar.CreateDefault();
        await using var db = NewDb(nameof(RealGap_StillRestartsWarmup_WithCanonicalCalendar));
        await SeedDailyAsync(db, canonical, D("2026-06-01"), D("2026-10-16"), "2026-09-10");

        await Recompute(db, canonical).RecomputeAsync("MBB", "1d");
        var rows = await IndicatorsAsync(db);
        var after = rows.Single(r => r.OpenTimeMs == VietnamMarketCalendar.DailyOpenUtcMs(D("2026-09-11")));
        var before = rows.Single(r => r.OpenTimeMs == VietnamMarketCalendar.DailyOpenUtcMs(D("2026-09-09")));
        Assert.NotNull(before.Sma50);
        Assert.Null(after.Sma50); // missing expected 2026-09-10 bar ⇒ segment break

        var adjacency = MbbTechnicalRecomputeService.BuildSessionAdjacency(canonical, "1d",
            await db.InstrumentCandles.Where(c => c.Symbol == "MBB").OrderBy(c => c.OpenTimeMs)
                .Select(c => new OhlcvBar(c.OpenTimeMs, c.CloseTimeMs, c.Open, c.High, c.Low, c.Close, c.Volume))
                .ToListAsync());
        Assert.True(adjacency(VietnamMarketCalendar.DailyOpenUtcMs(D("2026-08-28")), VietnamMarketCalendar.DailyOpenUtcMs(D("2026-09-03"))));
        Assert.False(adjacency(before.OpenTimeMs, after.OpenTimeMs));
    }

    // --- data-quality gap classification --------------------------------------------

    [Fact]
    public async Task DataQuality_ClassifiesMarketWideVsSymbolMissing()
    {
        var canonical = VietnamMarketCalendar.CreateDefault();
        await using var db = NewDb(nameof(DataQuality_ClassifiesMarketWideVsSymbolMissing));
        void Seed(string symbol, params string[] skip)
        {
            var s = skip.Select(D).ToHashSet();
            foreach (var date in canonical.EnumerateTradingDates(D("2026-08-03"), D("2026-09-25")))
            {
                if (s.Contains(date)) continue;
                var open = VietnamMarketCalendar.DailyOpenUtcMs(date);
                db.InstrumentCandles.Add(new InstrumentCandle
                {
                    Symbol = symbol, Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = canonical.SessionCloseUtcMs("1d", open),
                    Open = 100, High = 110, Low = 90, Close = 105, Volume = 1000, Provider = "VCI", PriceBasis = "adjusted",
                    Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh", FetchedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
                });
            }
        }
        Seed("MBB", "2026-09-10", "2026-09-15");
        Seed("BID", "2026-09-15");
        Seed("VNINDEX", "2026-09-15");
        await db.SaveChangesAsync();

        var options = new MbbIngestionOptions { Symbol = "MBB", Timeframes = ["1d"], DailyBackfillStartLocal = "2026-08-03" };
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var report = await new MbbDataQualityService(db, options, canonical, timeProvider: new FixedTime(now)).BuildReportAsync();

        var daily = report.Timeframes.Single(t => t.Timeframe == "1d");
        Assert.Equal(2, daily.MissingBars); // 2026-08-31..09-02 are NOT gaps
        Assert.Equal(1, daily.SymbolMissingBars);
        Assert.Equal(1, daily.MarketWideMissingBars);
        Assert.Contains(daily.TopClassifiedGaps!, g =>
            g.Classification == MbbGapClassifications.MarketWideMissing
            && g.StartOpenTimeMs == VietnamMarketCalendar.DailyOpenUtcMs(D("2026-09-15")));
        Assert.Contains(daily.TopClassifiedGaps!, g =>
            g.Classification == MbbGapClassifications.SymbolMissing
            && g.StartOpenTimeMs == VietnamMarketCalendar.DailyOpenUtcMs(D("2026-09-10")));
        Assert.Equal("canonical-file", report.Calendar!.Source);
        Assert.Contains("VNINDEX", report.Calendar.PeerSymbols);
        // A market-wide-missing date is reported only — never added to the calendar.
        Assert.True(canonical.IsTradingDay(D("2026-09-15")));
    }
}

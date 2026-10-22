using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Tests;

/// <summary>
/// EQX v2 review #1: the canonical calendar covers 2020-01-01..2026-12-31. Past coverage.toDate nothing may
/// assume "weekday = trading day": data quality / decision freshness flag calendar-coverage-expired, the decision
/// waits, observation and forecast recording refuse, and forecast windows beyond coverage are never computed.
/// Before coverage.fromDate missing bars are below-calendar-coverage, not real gaps.
/// </summary>
public class MarketCalendarCoverageTests
{
    private static readonly VietnamMarketCalendar Cal = VietnamMarketCalendar.CreateDefault();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static DateTimeOffset Local(int y, int m, int d, int hh) => new(y, m, d, hh, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateTimeOffset Beyond = Local(2027, 1, 6, 16);   // after coverage.toDate
    private static readonly DateTimeOffset Inside = Local(2026, 12, 30, 16); // inside coverage

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static void SeedDaily(AppDbContext db, string symbol, DateOnly from, DateOnly to, params string[] skip)
    {
        var s = skip.Select(DateOnly.Parse).ToHashSet();
        // Weekdays only (incl. dates after coverage, which the calendar cannot judge).
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || s.Contains(d)) continue;
            if (!Cal.IsBeyondCoverage(d) && !Cal.IsBelowCoverage(d) && !Cal.IsTradingDay(d)) continue;
            var open = VietnamMarketCalendar.DailyOpenUtcMs(d);
            db.InstrumentCandles.Add(new InstrumentCandle
            {
                Symbol = symbol, Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = Cal.SessionCloseUtcMs("1d", open),
                Open = 100, High = 110, Low = 90, Close = 105, Volume = 1000, Provider = "VCI", PriceBasis = "adjusted",
                Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh", FetchedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
        }
    }

    [Fact]
    public void Coverage_Bounds_AndNextSessionWindow()
    {
        Assert.Equal((new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 31)), (Cal.CoverageFrom!.Value, Cal.CoverageTo!.Value));
        Assert.True(Cal.IsBeyondCoverage(new DateOnly(2027, 1, 4)));
        Assert.False(Cal.IsBeyondCoverage(new DateOnly(2026, 12, 31)));
        Assert.True(Cal.IsBelowCoverage(new DateOnly(2019, 12, 31)));
        Assert.True(Cal.Coverage(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).FullyCovered);
        var st = Cal.Coverage(new DateOnly(2019, 6, 1), new DateOnly(2027, 2, 1));
        Assert.True(st.BelowCoverage && st.BeyondCoverage);
        Assert.False(new VietnamMarketCalendar().IsBeyondCoverage(new DateOnly(2099, 1, 1))); // explicit test calendar: unbounded

        // Inside: 2026-09-03's window closes at the 2026-09-04 09:00 open.
        Assert.Equal(Local(2026, 9, 4, 9).ToUnixTimeMilliseconds(),
            EquityForecastJournalService.NextSessionOpenUtcMs(Cal, VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 9, 3))));
        // 2026-12-31's next session is in 2027: unknown ⇒ fail closed.
        var dec31 = VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 12, 31));
        var ex = Assert.Throws<CalendarCoverageExpiredException>(() => EquityForecastJournalService.NextSessionOpenUtcMs(Cal, dec31));
        Assert.Equal("CALENDAR_COVERAGE_EXPIRED", ex.Code);
        Assert.Null(EquityForecastJournalService.TryNextSessionOpenUtcMs(Cal, dec31));
    }

    [Fact]
    public async Task DataQuality_BeyondCoverage_FlagsExpired_AndBelowCoverageIsNotARealGap()
    {
        await using var db = NewDb(nameof(DataQuality_BeyondCoverage_FlagsExpired_AndBelowCoverageIsNotARealGap));
        // 2019-12-24 missing (below coverage), 2027-01-04 missing (beyond coverage): neither is a real gap.
        SeedDaily(db, "MBB", new DateOnly(2019, 12, 2), new DateOnly(2027, 1, 6), "2019-12-24", "2027-01-04");
        SeedDaily(db, "BID", new DateOnly(2019, 12, 2), new DateOnly(2027, 1, 6), "2019-12-24", "2027-01-04");
        await db.SaveChangesAsync();
        var options = new MbbIngestionOptions { Symbol = "MBB", Timeframes = ["1d"], DailyBackfillStartLocal = "2019-12-02" };

        var beyond = (await new MbbDataQualityService(db, options, Cal, timeProvider: new FixedTime(Beyond)).BuildReportAsync())
            .Timeframes.Single();
        Assert.Contains(VietnamMarketCalendar.CoverageExpiredReason, beyond.CalendarFlags!);
        Assert.Contains(VietnamMarketCalendar.BelowCoverageReason, beyond.CalendarFlags!);
        Assert.Equal(VietnamMarketCalendar.CoverageExpiredReason, beyond.FreshnessReason);
        Assert.True(beyond.IsStale);
        Assert.Equal(1, beyond.BelowCalendarCoverageMissingBars);
        Assert.Equal(1, beyond.CalendarCoverageExpiredMissingBars);
        Assert.Equal(0, beyond.MarketWideMissingBars);
        Assert.Equal(0, beyond.SymbolMissingBars);

        var inside = (await new MbbDataQualityService(db, options, Cal, timeProvider: new FixedTime(Inside)).BuildReportAsync())
            .Timeframes.Single();
        Assert.DoesNotContain(VietnamMarketCalendar.CoverageExpiredReason, inside.CalendarFlags!);
        Assert.Null(inside.FreshnessReason);
        Assert.False(inside.IsStale);
        Assert.Equal(0, inside.CalendarCoverageExpiredMissingBars);
    }

    [Fact]
    public async Task Decision_BeyondCoverage_Waits_WithReason_InsideCoverageDoesNot()
    {
        await using var db = NewDb(nameof(Decision_BeyondCoverage_Waits_WithReason_InsideCoverageDoesNot));
        SeedDaily(db, "MBB", new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 6));
        await db.SaveChangesAsync();

        var d = await new MbbDecisionService(db, Cal, timeProvider: new FixedTime(Beyond)).DecideAsync("MBB");
        Assert.Equal(MbbDecisionActions.Wait, d.Action);
        Assert.Equal(VietnamMarketCalendar.CoverageExpiredReason, d.Reasons[0]);
        Assert.All(d.DataFreshness, f => Assert.Equal((true, VietnamMarketCalendar.CoverageExpiredReason), (f.IsStale, f.Reason)));

        var ok = await new MbbDecisionService(db, Cal, timeProvider: new FixedTime(Inside)).DecideAsync("MBB");
        Assert.DoesNotContain(VietnamMarketCalendar.CoverageExpiredReason, ok.Reasons);
        Assert.All(ok.DataFreshness, f => Assert.Null(f.Reason));
    }

    [Fact]
    public async Task ObservationAndForecastRecording_RefuseBeyondCoverage()
    {
        await using var db = NewDb(nameof(ObservationAndForecastRecording_RefuseBeyondCoverage));
        SeedDaily(db, "BID", new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 6));
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "eqx-cov", Guid.NewGuid().ToString("N"));

        EquityForecastJournalService Journal(TimeProvider tp) =>
            new(db, new EquityBaseRatesService(db, root, 4096, Cal, timeProvider: tp), Cal, tp);
        MbbSignalObservationService Obs(TimeProvider tp) =>
            new(db, new MbbDecisionService(db, Cal, timeProvider: tp), Cal, tp);

        var beyond = new FixedTime(Beyond);
        Assert.Equal(VietnamMarketCalendar.CoverageExpiredReason, (await Obs(beyond).RecordAsync("BID", "1d")).Status);
        Assert.Equal(VietnamMarketCalendar.CoverageExpiredReason, (await Journal(beyond).RecordQuantAsync("BID")).Status);

        // Signal bar 2026-12-31 is inside coverage, but its next session is not ⇒ the forecast window is unknown.
        var dec31 = new FixedTime(Local(2026, 12, 31, 16));
        var report = await Journal(dec31).RecordQuantAsync("BID");
        Assert.Equal(VietnamMarketCalendar.CoverageExpiredReason, report.Status);
        Assert.Equal(VietnamMarketCalendar.DailyOpenUtcMs(new DateOnly(2026, 12, 31)), report.SignalBarOpenTimeMs);
        Assert.Empty(await db.EquityForecastRecords.ToListAsync());
        Assert.Empty(await db.MbbSignalObservations.ToListAsync());

        // Inside coverage the recorder proceeds (abstained rows: no artifact in the temp root).
        var inside = await Journal(new FixedTime(Inside)).RecordQuantAsync("BID");
        Assert.NotEqual(VietnamMarketCalendar.CoverageExpiredReason, inside.Status);
        Assert.True(inside.Recorded > 0);
    }

    [Fact]
    public async Task Sync_WritesCoverage_OnEveryRow()
    {
        await using var db = NewDb(nameof(Sync_WritesCoverage_OnEveryRow));
        var src = MarketCalendarSource.Default.WithOverrides([new DateOnly(2027, 1, 4)]);
        await MarketCalendarSync.SyncAsync(db, src);
        var rows = await db.MarketCalendarDays.ToListAsync();
        Assert.Equal(src.Days.Count + 1, rows.Count);
        Assert.All(rows.Where(r => r.Date.Year < 2027), r =>
            Assert.Equal((src.CoverageFrom, src.CoverageTo), (r.CoverageFromDate, r.CoverageToDate)));
        var over = rows.Single(r => r.Date == new DateOnly(2027, 1, 4)); // override outside coverage widens its own row
        Assert.True(over.CoverageFromDate <= over.Date && over.Date <= over.CoverageToDate);
        // Re-sync is a no-op.
        var again = await MarketCalendarSync.SyncAsync(db, src);
        Assert.Equal((0, 0, 0), (again.Inserted, again.Updated, again.Deleted));
    }
}

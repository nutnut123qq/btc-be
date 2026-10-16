using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

/// <summary>
/// MBB-07 forward observation honesty tests:
/// - records only for the LATEST expected finalized open — an older finalized
///   bar can never receive a row (no backfill as live decision);
/// - idempotent by deterministic DecisionId + unique (symbol, tf, signal open);
/// - waits for the finalized bar to be INGESTED rather than recording on stale;
/// - fills resolve only for action-bearing decisions, from the next stored bar;
/// - outcomes resolve only once the fill bar itself is finalized;
/// - wait/hold never produce fills.
/// </summary>
public class MbbSignalObservationTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static InstrumentCandle Candle(long openMs, long closeMs, decimal px, string tf = "1d") => new()
    {
        Symbol = "MBB",
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

    private static MbbSignalObservationService NewService(
        AppDbContext db, VietnamMarketCalendar cal, TestTimeProvider tp) =>
        new(db, new MbbDecisionService(db, cal, timeProvider: tp), cal, tp);

    /// <summary>
    /// Seeds daily candles + an indicator row ending exactly at the latest
    /// expected finalized 1d open, with the final close and previous close
    /// chosen by the test. Returns the signal bar's open ms.
    /// </summary>
    private static async Task<long> SeedDailyThroughSignalAsync(
        AppDbContext db, VietnamMarketCalendar cal, long signalOpenMs,
        decimal signalClose, decimal prevClose)
    {
        var signalLocal = DateOnly.FromDateTime(
            VietnamMarketCalendar.ToLocal(signalOpenMs).DateTime);
        var dates = cal.EnumerateTradingDates(signalLocal.AddDays(-40), signalLocal).ToArray();
        for (var i = 0; i < dates.Length - 2; i++)
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(dates[i]);
            db.InstrumentCandles.Add(Candle(open, cal.SessionCloseUtcMs("1d", open), prevClose));
        }
        // Previous bar sets the ±7% band reference.
        var prevOpen = VietnamMarketCalendar.DailyOpenUtcMs(dates[^2]);
        db.InstrumentCandles.Add(Candle(prevOpen, cal.SessionCloseUtcMs("1d", prevOpen), prevClose));
        db.InstrumentCandles.Add(Candle(signalOpenMs, cal.SessionCloseUtcMs("1d", signalOpenMs), signalClose));
        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = "MBB",
            Timeframe = "1d",
            OpenTimeMs = signalOpenMs,
            Sma50 = prevClose,
            Rsi14 = 55,
            Atr14 = 500,
        });
        await db.SaveChangesAsync();
        return signalOpenMs;
    }

    [Fact]
    public async Task Record_WritesRowForLatestFinalizedOpen_Only()
    {
        var db = NewDb(nameof(Record_WritesRowForLatestFinalizedOpen_Only));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        var now = tp.Now.ToUnixTimeMilliseconds();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", now)!.Value;
        await SeedDailyThroughSignalAsync(db, cal, signal, signalClose: 20_000m, prevClose: 20_100m);

        var report = await NewService(db, cal, tp).RecordAsync("1d");

        Assert.Equal("recorded", report.Status);
        Assert.Equal(signal, report.SignalBarOpenTimeMs);
        var row = await db.MbbSignalObservations.SingleAsync();
        Assert.Equal("MBB", row.Symbol);
        Assert.Equal("1d", row.Timeframe);
        Assert.Equal(signal, row.SignalBarOpenTimeMs);
        Assert.True(row.AvailableTimeMs >= row.SignalBarCloseTimeMs);
        Assert.Equal($"mbb-observe/1d/{signal}", row.DecisionId);
        Assert.Equal("wait", row.Decision); // no position seeded → wait/no-position
        Assert.Null(row.FillPrice);
        Assert.False(string.IsNullOrWhiteSpace(row.DecisionPayloadJson));
    }

    [Fact]
    public async Task Record_IsIdempotent_SecondPollIsNoOp()
    {
        var db = NewDb(nameof(Record_IsIdempotent_SecondPollIsNoOp));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        await SeedDailyThroughSignalAsync(db, cal, signal, 20_000m, 20_100m);
        var svc = NewService(db, cal, tp);

        await svc.RecordAsync("1d");
        var second = await svc.RecordAsync("1d");

        Assert.Equal("already-recorded", second.Status);
        Assert.Equal(1, await db.MbbSignalObservations.CountAsync());
    }

    [Fact]
    public async Task Record_RefusesOnMissingBar_AndNeverBackfillsOldBars()
    {
        var db = NewDb(nameof(Record_RefusesOnMissingBar_AndNeverBackfillsOldBars));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        // Seed ONLY an older finalized bar — the latest expected open is missing.
        var now = tp.Now.ToUnixTimeMilliseconds();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", now)!.Value;
        var oldLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(-10);
        var oldOpen = VietnamMarketCalendar.DailyOpenUtcMs(oldLocal);
        db.InstrumentCandles.Add(Candle(oldOpen, cal.SessionCloseUtcMs("1d", oldOpen), 20_000m));
        await db.SaveChangesAsync();

        var report = await NewService(db, cal, tp).RecordAsync("1d");

        Assert.Equal("finalized-bar-not-ingested", report.Status);
        // The older finalized bar was stored, yet NO observation exists —
        // record can never attach a decision to a backdated signal bar.
        Assert.Equal(0, await db.MbbSignalObservations.CountAsync());
    }

    [Fact]
    public async Task Record_RejectsUnsupportedTimeframe()
    {
        var db = NewDb(nameof(Record_RejectsUnsupportedTimeframe));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        await Assert.ThrowsAsync<ArgumentException>(() => NewService(db, cal, tp).RecordAsync("4h"));
    }

    [Fact]
    public async Task WaitDecision_NeverResolvesFill_EvenWithLaterBars()
    {
        var db = NewDb(nameof(WaitDecision_NeverResolvesFill_EvenWithLaterBars));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        await SeedDailyThroughSignalAsync(db, cal, signal, 20_000m, 20_100m);
        var svc = NewService(db, cal, tp);
        await svc.RecordAsync("1d"); // wait (no position)

        // A later stored bar appears — wait is a non-transaction, no fill.
        var nextLocal = DateOnly.FromDateTime(
            VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(7);
        var nextTrading = cal.EnumerateTradingDates(nextLocal, nextLocal.AddDays(14)).First();
        var nextOpen = VietnamMarketCalendar.DailyOpenUtcMs(nextTrading);
        db.InstrumentCandles.Add(Candle(nextOpen, cal.SessionCloseUtcMs("1d", nextOpen), 20_500m));
        await db.SaveChangesAsync();
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(
            cal.SessionCloseUtcMs("1d", nextOpen) + 60_000);

        await svc.RecordAsync("1d");
        var row = await db.MbbSignalObservations
            .SingleAsync(o => o.SignalBarOpenTimeMs == signal);
        Assert.Equal("wait", row.Decision);
        Assert.Null(row.FillPrice);
        Assert.Null(row.OutcomeReturn);
    }

    [Fact]
    public async Task ActionableDecision_ResolvesFillAtNextOpen_ThenOutcome()
    {
        var db = NewDb(nameof(ActionableDecision_ResolvesFillAtNextOpen_ThenOutcome));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        // Stop-loss setup: avgCost 25,000 → stop 23,000; signal close 22,000 hits it;
        // prevClose 22,500 keeps 22,000 inside the ±7% band → decision = sell.
        await SeedDailyThroughSignalAsync(db, cal, signal, signalClose: 22_000m, prevClose: 22_500m);
        var acquireLocal = DateOnly.FromDateTime(
            VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(-15);
        db.EquityPositionLots.Add(new EquityPositionLot
        {
            Symbol = "MBB", Quantity = 200, CostPerShareVnd = 25_000m,
            AcquireDate = acquireLocal, Source = "manual",
        });
        await db.SaveChangesAsync();
        var svc = NewService(db, cal, tp);

        var report = await svc.RecordAsync("1d");
        Assert.Equal("sell", report.Decision);
        var row = await db.MbbSignalObservations.SingleAsync();
        Assert.Null(row.FillPrice);

        // Next session's bar is ingested (open 21,800); poll again at +3 days.
        var nextTrading = cal.EnumerateTradingDates(
            DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(1),
            DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(10)).First();
        var nextOpen = VietnamMarketCalendar.DailyOpenUtcMs(nextTrading);
        var nextClose = cal.SessionCloseUtcMs("1d", nextOpen);
        db.InstrumentCandles.Add(new InstrumentCandle
        {
            Symbol = "MBB", Timeframe = "1d",
            OpenTimeMs = nextOpen, CloseTimeMs = nextClose,
            Open = 21_800m, High = 22_300m, Low = 21_700m, Close = 22_200m,
            Volume = 1_000_000m, Provider = "VCI", PriceBasis = "adjusted",
            Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh",
        });
        await db.SaveChangesAsync();
        // Deterministic clock: just past the fill bar's session close, so the
        // fill bar itself is finalized and the outcome can resolve.
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(nextClose + 60_000);

        var second = await svc.RecordAsync("1d");
        Assert.Equal(1, second.FillsResolved);
        var filled = await db.MbbSignalObservations
            .SingleAsync(o => o.SignalBarOpenTimeMs == signal);
        Assert.Equal(21_800m, filled.FillPrice);
        Assert.Equal(nextOpen, filled.FillBarOpenTimeMs);
        Assert.Equal(MbbSignalObservationService.FillSourceNextBarOpen, filled.FillSource);
        Assert.True(filled.FillObservedAtUtc >= filled.ObservedAtUtc);
        // Fill bar is finalized by the advanced clock → outcome resolved too:
        // (22,200 close - 21,800 fill) / 21,800 ≈ +1.835% raw instrument return.
        Assert.Equal(1, second.OutcomesResolved);
        Assert.Equal((double)((22_200m - 21_800m) / 21_800m), filled.OutcomeReturn!.Value, 6);
        Assert.Equal(MbbSignalObservationService.OutcomeHorizonNextBarClose, filled.OutcomeHorizon);
    }
}

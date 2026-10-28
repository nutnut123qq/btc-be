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
/// - fills resolve only for action-bearing decisions, at the first stored open
///   AT/AFTER the observation moment — an open that already existed when the
///   decision was recorded is never claimed as a fill;
/// - conditional-sell fills only after a declared close-below trigger actually
///   fires on a finalized bar, then at the next session open;
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

    // Pins must land AFTER the calendar's publish grace (10 min) for a bar to be
    // the "latest expected finalized" one — pinning inside the grace makes the
    // recorder observe the PREVIOUS bar instead.
    private const long PastPublishGraceMs = 11 * 60_000;

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
        decimal signalClose, decimal prevClose, double rsi = 55, decimal? sma50 = null)
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
            Sma50 = sma50 ?? prevClose,
            Rsi14 = rsi,
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
        // EQX: new rows use the multi-symbol id + recorder v2 (legacy mbb-observe rows untouched).
        Assert.Equal($"equity-observe/MBB/1d/{signal}", row.DecisionId);
        Assert.Equal("equity-observe-v2", row.RecorderVersion);
        // Rules changed in MBBSIZE-1/MBBSTALE-1: new rows must not share the v1 label.
        Assert.Equal("declared-rules-v2", MbbDecisionService.RulesVersion);
        Assert.Equal(MbbDecisionService.RulesVersion, row.RulesVersion);
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
        // Pin the observation clock to just after the signal close: the fill must
        // land at the first stored open ≥ AvailableTimeMs (the observation moment).
        var signalClose = cal.SessionCloseUtcMs("1d", signal);
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(signalClose + PastPublishGraceMs);
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
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(nextClose + PastPublishGraceMs);

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

    [Fact]
    public async Task LateDecision_FillSkipsOpensBeforeTheObservationMoment()
    {
        var db = NewDb(nameof(LateDecision_FillSkipsOpensBeforeTheObservationMoment));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        // Same sell setup; the signal bar closed yesterday or earlier today.
        await SeedDailyThroughSignalAsync(db, cal, signal, signalClose: 22_000m, prevClose: 22_500m);
        db.EquityPositionLots.Add(new EquityPositionLot
        {
            Symbol = "MBB", Quantity = 200, CostPerShareVnd = 25_000m,
            AcquireDate = DateOnly.FromDateTime(
                VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(-15),
            Source = "manual",
        });
        // The NEXT session's bar is already stored (it opened this morning).
        var nextTrading = cal.EnumerateTradingDates(
            DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(1),
            DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(10)).First();
        var skippedOpen = VietnamMarketCalendar.DailyOpenUtcMs(nextTrading);
        db.InstrumentCandles.Add(Candle(skippedOpen, cal.SessionCloseUtcMs("1d", skippedOpen), 21_800m));
        // DecideAsync prices the LATEST stored daily bar — the skipped one —
        // and refuses ("wait") when its indicator row is missing.
        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = "MBB", Timeframe = "1d", OpenTimeMs = skippedOpen,
            Sma50 = 25_000m, Rsi14 = 45, Atr14 = 500,
        });
        await db.SaveChangesAsync();

        // The observe call arrives mid-session on the NEXT day — after the
        // skipped bar's open but before its close. Recording a fill at that
        // already-past open would fabricate an execution that never could have
        // happened; the honest fill is the following session's open.
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(skippedOpen + 3_600_000); // ~10:00 local: past the 09:00 open, pre-close
        var svc = NewService(db, cal, tp);
        var report = await svc.RecordAsync("1d");
        Assert.Equal("sell", report.Decision);

        var row = await db.MbbSignalObservations.SingleAsync();
        Assert.True(row.AvailableTimeMs > skippedOpen); // observed after that open existed
        Assert.Null(row.FillPrice); // skipped open predates the decision — never a fill

        // Once the following session's bar lands, THAT open becomes the fill.
        var afterTrading = cal.EnumerateTradingDates(nextTrading.AddDays(1), nextTrading.AddDays(10)).First();
        var fillOpen = VietnamMarketCalendar.DailyOpenUtcMs(afterTrading);
        db.InstrumentCandles.Add(Candle(fillOpen, cal.SessionCloseUtcMs("1d", fillOpen), 21_600m));
        await db.SaveChangesAsync();
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(
            cal.SessionCloseUtcMs("1d", fillOpen) + PastPublishGraceMs);

        var second = await svc.RecordAsync("1d");
        Assert.Equal(1, second.FillsResolved);
        var filled = await db.MbbSignalObservations
            .SingleAsync(o => o.SignalBarOpenTimeMs == signal);
        Assert.Equal(21_600m, filled.FillPrice);
        Assert.Equal(fillOpen, filled.FillBarOpenTimeMs);
        Assert.True(filled.FillBarOpenTimeMs >= filled.AvailableTimeMs);
    }

    [Fact]
    public async Task ConditionalSell_Untriggered_NeverResolvesFill()
    {
        var db = NewDb(nameof(ConditionalSell_Untriggered_NeverResolvesFill));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        // Bearish but ABOVE the reduce level: avgCost 25,000 → reduce 24,000 /
        // stop 23,000; signal close 24,500 < SMA50 25,000 with RSI 45 →
        // conditional-sell (hold unless a later 1d close breaks a level).
        await SeedDailyThroughSignalAsync(db, cal, signal, signalClose: 24_500m,
            prevClose: 25_000m, rsi: 45, sma50: 25_000m);
        db.EquityPositionLots.Add(new EquityPositionLot
        {
            Symbol = "MBB", Quantity = 200, CostPerShareVnd = 25_000m,
            AcquireDate = DateOnly.FromDateTime(
                VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(-15),
            Source = "manual",
        });
        await db.SaveChangesAsync();
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(
            cal.SessionCloseUtcMs("1d", signal) + PastPublishGraceMs);
        var svc = NewService(db, cal, tp);

        var report = await svc.RecordAsync("1d");
        Assert.Equal("conditional-sell", report.Decision);
        var row = await db.MbbSignalObservations.SingleAsync(o => o.SignalBarOpenTimeMs == signal);
        Assert.Contains("close-below", row.DecisionPayloadJson); // trigger is evaluable

        // Later sessions close ABOVE every trigger level — condition never met.
        var signalLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime);
        foreach (var d in cal.EnumerateTradingDates(signalLocal.AddDays(1), signalLocal.AddDays(6)).Take(2))
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(d);
            db.InstrumentCandles.Add(Candle(open, cal.SessionCloseUtcMs("1d", open), 24_800m));
        }
        await db.SaveChangesAsync();
        var lastOpen = VietnamMarketCalendar.DailyOpenUtcMs(
            cal.EnumerateTradingDates(signalLocal.AddDays(1), signalLocal.AddDays(6)).Skip(1).First());
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(
            cal.SessionCloseUtcMs("1d", lastOpen) + PastPublishGraceMs);

        await svc.RecordAsync("1d");
        var filled = await db.MbbSignalObservations
            .SingleAsync(o => o.SignalBarOpenTimeMs == signal);
        Assert.Null(filled.FillPrice);      // trigger never fired → not a fill
        Assert.Null(filled.OutcomeReturn);  // price movement alone is not an execution
    }

    [Fact]
    public async Task ConditionalSell_Triggered_FillsAtNextSessionOpen()
    {
        var db = NewDb(nameof(ConditionalSell_Triggered_FillsAtNextSessionOpen));
        var cal = new VietnamMarketCalendar();
        var tp = new TestTimeProvider();
        var signal = cal.LastExpectedFinalizedOpenUtcMs("1d", tp.Now.ToUnixTimeMilliseconds())!.Value;
        await SeedDailyThroughSignalAsync(db, cal, signal, signalClose: 24_500m,
            prevClose: 25_000m, rsi: 45, sma50: 25_000m);
        db.EquityPositionLots.Add(new EquityPositionLot
        {
            Symbol = "MBB", Quantity = 200, CostPerShareVnd = 25_000m,
            AcquireDate = DateOnly.FromDateTime(
                VietnamMarketCalendar.ToLocal(signal).DateTime).AddDays(-15),
            Source = "manual",
        });
        await db.SaveChangesAsync();
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(
            cal.SessionCloseUtcMs("1d", signal) + PastPublishGraceMs);
        var svc = NewService(db, cal, tp);

        var report = await svc.RecordAsync("1d");
        Assert.Equal("conditional-sell", report.Decision);

        // Next session closes 23,800 — below reduce level 24,000 → conditional
        // order activates; execution is the FOLLOWING session's open.
        var signalLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signal).DateTime);
        var triggerDate = cal.EnumerateTradingDates(signalLocal.AddDays(1), signalLocal.AddDays(6)).First();
        var triggerOpen = VietnamMarketCalendar.DailyOpenUtcMs(triggerDate);
        var triggerClose = cal.SessionCloseUtcMs("1d", triggerOpen);
        db.InstrumentCandles.Add(Candle(triggerOpen, triggerClose, 23_800m));
        // The following session's open — the fill bar.
        var fillDate = cal.EnumerateTradingDates(triggerDate.AddDays(1), triggerDate.AddDays(10)).First();
        var fillOpen = VietnamMarketCalendar.DailyOpenUtcMs(fillDate);
        var fillClose = cal.SessionCloseUtcMs("1d", fillOpen);
        db.InstrumentCandles.Add(new InstrumentCandle
        {
            Symbol = "MBB", Timeframe = "1d",
            OpenTimeMs = fillOpen, CloseTimeMs = fillClose,
            Open = 23_700m, High = 23_900m, Low = 23_400m, Close = 23_500m,
            Volume = 1_000_000m, Provider = "VCI", PriceBasis = "adjusted",
            Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh",
        });
        await db.SaveChangesAsync();
        tp.Now = DateTimeOffset.FromUnixTimeMilliseconds(fillClose + PastPublishGraceMs);

        var second = await svc.RecordAsync("1d");
        Assert.Equal(1, second.FillsResolved);
        var filled = await db.MbbSignalObservations
            .SingleAsync(o => o.SignalBarOpenTimeMs == signal);
        Assert.Equal(23_700m, filled.FillPrice);            // next session open, not trigger close
        Assert.Equal(fillOpen, filled.FillBarOpenTimeMs);
        Assert.Equal(MbbSignalObservationService.FillSourcePostTriggerNextOpen, filled.FillSource);
        Assert.True(filled.FillBarOpenTimeMs >= filled.AvailableTimeMs);
        Assert.Equal(1, second.OutcomesResolved);
        Assert.Equal((double)((23_500m - 23_700m) / 23_700m), filled.OutcomeReturn!.Value, 6);
    }
}

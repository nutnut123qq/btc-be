using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Tests;

/// <summary>
/// MBB-05 tests: VN execution math must mirror ai/vn_execution_model.py
/// (fee 0.15%/side, sell tax 0.1%, lot 100, T+2 trading days, ±7% band reject),
/// settlement across weekends/holidays, and the declared conservative decision
/// rules (hold/wait/buy-more/reduce/sell/conditional-sell) — never selling more
/// than settled shares, never buying beyond cash, wait on insufficient data.
/// EF InMemory only; TimeProvider.System with dates derived from real today.
/// </summary>
public class MbbPositionDecisionTests
{
    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name).Options);

    private static MbbDecisionService NewService(AppDbContext db, VietnamMarketCalendar? cal = null) =>
        new(db, cal ?? new VietnamMarketCalendar());

    private static DateOnly TodayLocal()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(nowMs).DateTime);
    }

    private static InstrumentCandle Candle(long openMs, long closeMs,
        decimal o, decimal h, decimal l, decimal c, string tf = "1d") => new()
    {
        Symbol = "MBB",
        Timeframe = tf,
        OpenTimeMs = openMs,
        CloseTimeMs = closeMs,
        Open = o, High = h, Low = l, Close = c,
        Volume = 1_000_000m,
        Provider = "VCI",
        PriceBasis = "adjusted",
        Venue = "HOSE",
        SourceTimezone = "Asia/Ho_Chi_Minh",
        FetchedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>
    /// Seed the last <paramref name="count"/> trading-day 1d candles ending today
    /// plus an indicator row on the last bar. closes[i] = close of bar i
    /// (last = current price, second-to-last = band reference).
    /// </summary>
    private static async Task SeedDailyAsync(
        AppDbContext db, IReadOnlyList<decimal> closes,
        decimal? sma50 = null, double? rsi14 = null, double? atr14 = 500,
        decimal? ema50 = null)
    {
        var cal = new VietnamMarketCalendar();
        var today = TodayLocal();
        var dates = cal.EnumerateTradingDates(today.AddDays(-90), today)
            .TakeLast(closes.Count).ToArray();
        for (var i = 0; i < dates.Length; i++)
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(dates[i]);
            var c = closes[i];
            db.InstrumentCandles.Add(Candle(open, cal.SessionCloseUtcMs("1d", open),
                c, c + 200m, c - 200m, c));
        }
        var lastOpen = VietnamMarketCalendar.DailyOpenUtcMs(dates[^1]);
        db.TechnicalIndicators.Add(new TechnicalIndicator
        {
            Symbol = "MBB",
            Timeframe = "1d",
            OpenTimeMs = lastOpen,
            Sma50 = sma50,
            Rsi14 = rsi14,
            Atr14 = atr14,
            Ema50 = ema50,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLotAsync(
        AppDbContext db, int qty, decimal cost, DateOnly acquire)
    {
        db.EquityPositionLots.Add(new EquityPositionLot
        {
            Symbol = "MBB",
            Quantity = qty,
            CostPerShareVnd = cost,
            AcquireDate = acquire,
            Source = "manual",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedCashAsync(AppDbContext db, decimal cash)
    {
        db.EquityPositionAccounts.Add(new EquityPositionAccount
        {
            Symbol = "MBB", AvailableCashVnd = cash, UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // --- execution-rule math (mirrors vn_execution_model.py) ---------------

    [Fact]
    public void QuoteBuy_FeeMatchesVnExecutionModel()
    {
        var q = VietnamExecutionRules.QuoteBuy(200, 20_000m);
        Assert.Equal(4_000_000m, q.NotionalVnd);
        Assert.Equal(6_000m, q.FeeVnd);            // 4,000,000 * 0.0015
        Assert.Equal(4_006_000m, q.TotalCostVnd);
    }

    [Fact]
    public void QuoteSell_FeeAndTaxMatchVnExecutionModel()
    {
        var q = VietnamExecutionRules.QuoteSell(200, 21_000m);
        Assert.Equal(4_200_000m, q.GrossVnd);
        Assert.Equal(6_300m, q.FeeVnd);            // 4,200,000 * 0.0015
        Assert.Equal(4_200m, q.TaxVnd);            // 4,200,000 * 0.001
        Assert.Equal(4_189_500m, q.NetVnd);
    }

    [Fact]
    public void BuySharesForBudget_RoundsDownToWholeLot_AndIncludesFee()
    {
        // per-share cost = 20,000 * 1.0015 = 20,030 -> floor(4,000,000/20,030)=199 -> 100
        Assert.Equal(100, VietnamExecutionRules.BuySharesForBudget(4_000_000m, 20_000m));
        Assert.Equal(200, VietnamExecutionRules.BuySharesForBudget(4_006_000m, 20_000m));
        Assert.Equal(0, VietnamExecutionRules.BuySharesForBudget(1_000_000m, 20_000m));
    }

    [Fact]
    public void BandBounds_And_ClassifyBand_PlusMinus7Pct_Reject()
    {
        var (floor, ceiling) = VietnamExecutionRules.BandBounds(20_000m);
        Assert.Equal(18_600m, floor);
        Assert.Equal(21_400m, ceiling);
        Assert.Equal(VnBandStates.WithinBand, VietnamExecutionRules.ClassifyBand(21_400m, 20_000m));
        Assert.Equal(VnBandStates.WithinBand, VietnamExecutionRules.ClassifyBand(18_600m, 20_000m));
        Assert.Equal(VnBandStates.OpenAboveCeiling, VietnamExecutionRules.ClassifyBand(21_401m, 20_000m));
        Assert.Equal(VnBandStates.OpenBelowFloor, VietnamExecutionRules.ClassifyBand(18_599m, 20_000m));
        var (ok, reason) = VietnamExecutionRules.CanSellAt(21_500m, 20_000m);
        Assert.False(ok);
        Assert.Equal(VnBandStates.OpenAboveCeiling, reason);
    }

    [Fact]
    public void SettlementDate_T2_SkipsWeekendAndConfiguredHoliday()
    {
        var cal = new VietnamMarketCalendar();
        var fri = new DateOnly(2024, 6, 14);   // Friday
        Assert.Equal(new DateOnly(2024, 6, 18), cal.AddTradingDays(fri, 2)); // Tue

        var withHoliday = new VietnamMarketCalendar(new[] { new DateOnly(2024, 6, 17) });
        Assert.Equal(new DateOnly(2024, 6, 19), withHoliday.AddTradingDays(fri, 2)); // Wed
        Assert.Equal(fri, cal.AddTradingDays(fri, 0));
    }

    // --- position service ---------------------------------------------------

    [Fact]
    public async Task GetPosition_DerivesSettlement_AndAvgCost()
    {
        await using var db = NewDb(nameof(GetPosition_DerivesSettlement_AndAvgCost));
        var today = TodayLocal();
        await SeedLotAsync(db, 200, 20_000m, today.AddDays(-14)); // settled
        await SeedLotAsync(db, 100, 22_000m, today);              // pending T+2
        await SeedCashAsync(db, 5_000_000m);
        var svc = NewService(db);

        var p = await svc.GetPositionAsync();

        Assert.Equal("MBB", p.Symbol);
        Assert.Equal(300, p.TotalShares);
        Assert.Equal(200, p.SettledShares);
        Assert.Equal(100, p.PendingShares);
        Assert.Equal(5_000_000m, p.AvailableCashVnd);
        Assert.Equal((200m * 20_000m + 100m * 22_000m) / 300m, p.AvgCostPerShareVnd);
        Assert.Equal(2, p.Lots.Count);
        Assert.True(p.Lots[0].Settled);
        Assert.False(p.Lots[1].Settled);
    }

    [Fact]
    public async Task ReplacePosition_IsIdempotent_FullReplace()
    {
        await using var db = NewDb(nameof(ReplacePosition_IsIdempotent_FullReplace));
        var svc = NewService(db);
        var today = TodayLocal().ToString("yyyy-MM-dd");
        var body = new UpdateMbbPositionRequest(
            [new UpdateMbbPositionLotItem(200, 20_000m, today)],
            3_000_000m);

        var first = await svc.ReplacePositionAsync(body);
        var second = await svc.ReplacePositionAsync(body);

        Assert.Equal(first.TotalShares, second.TotalShares);
        Assert.Equal(1, await db.EquityPositionLots.CountAsync());
        Assert.Single(await db.EquityPositionAccounts.ToListAsync());
        Assert.Equal(3_000_000m, second.AvailableCashVnd);

        // Replace converges: drop the lot entirely.
        var cleared = await svc.ReplacePositionAsync(
            new UpdateMbbPositionRequest([], 500_000m));
        Assert.Equal(0, cleared.TotalShares);
        Assert.Equal(0, await db.EquityPositionLots.CountAsync());
        Assert.Equal(500_000m, cleared.AvailableCashVnd);
    }

    [Fact]
    public async Task ReplacePosition_RejectsInvalidInput_FailClosed()
    {
        await using var db = NewDb(nameof(ReplacePosition_RejectsInvalidInput_FailClosed));
        var svc = NewService(db);
        var today = TodayLocal().ToString("yyyy-MM-dd");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ReplacePositionAsync(new UpdateMbbPositionRequest(
                [new UpdateMbbPositionLotItem(0, 20_000m, today)], 0m)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ReplacePositionAsync(new UpdateMbbPositionRequest(
                [new UpdateMbbPositionLotItem(100, -1m, today)], 0m)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ReplacePositionAsync(new UpdateMbbPositionRequest([], -1m)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ReplacePositionAsync(new UpdateMbbPositionRequest(
                [new UpdateMbbPositionLotItem(100, 20_000m, "not-a-date")], 0m)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ReplacePositionAsync(new UpdateMbbPositionRequest(
                [new UpdateMbbPositionLotItem(100, 20_000m,
                    TodayLocal().AddDays(1).ToString("yyyy-MM-dd"))], 0m)));

        // Nothing persisted on rejected input.
        Assert.Equal(0, await db.EquityPositionLots.CountAsync());
    }

    [Fact]
    public async Task Service_FailsClosed_OnNonMbbOrUnknownSymbol()
    {
        await using var db = NewDb(nameof(Service_FailsClosed_OnNonMbbOrUnknownSymbol));
        var svc = NewService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetPositionAsync("BTCUSDT"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetPositionAsync("XXX"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.DecideAsync("BTCUSDT"));
    }

    // --- decision engine -----------------------------------------------------

    [Fact]
    public async Task Decide_NoCandles_Waits_WithInsufficientData()
    {
        await using var db = NewDb(nameof(Decide_NoCandles_Waits_WithInsufficientData));
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Wait, d.Action);
        Assert.Contains("insufficient-data", d.Reasons);
        Assert.Equal(MbbEvidenceStatuses.RulesOnly, d.EvidenceStatus);
        Assert.Equal("adjusted", d.PriceBasis);
        Assert.Equal(0, d.Position.TotalShares);
    }

    [Fact]
    public async Task Decide_NoIndicatorRow_Waits_WithInsufficientData()
    {
        await using var db = NewDb(nameof(Decide_NoIndicatorRow_Waits_WithInsufficientData));
        var cal = new VietnamMarketCalendar();
        var today = TodayLocal();
        foreach (var dte in cal.EnumerateTradingDates(today.AddDays(-10), today))
        {
            var open = VietnamMarketCalendar.DailyOpenUtcMs(dte);
            db.InstrumentCandles.Add(Candle(open, cal.SessionCloseUtcMs("1d", open),
                20_000m, 20_200m, 19_800m, 20_000m));
        }
        await db.SaveChangesAsync(); // candles only, no TechnicalIndicators
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Wait, d.Action);
        Assert.Contains("insufficient-data", d.Reasons);
    }

    [Fact]
    public async Task Decide_Hold_WhenNoTriggerFired()
    {
        await using var db = NewDb(nameof(Decide_Hold_WhenNoTriggerFired));
        await SeedLotAsync(db, 200, 20_000m, TodayLocal().AddDays(-14));
        // closes: prev 20,100 -> last 20,300 (~+1.4% vs cost; no stop/reduce).
        // trend down (close < sma50) but RSI >= mid -> not bearish, not bullish.
        await SeedDailyAsync(db, [20_100m, 20_300m], sma50: 20_500m, rsi14: 55);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Hold, d.Action);
        Assert.Contains("no-trigger", d.Reasons);
        Assert.Equal(200, d.Position.TotalShares);
        Assert.Equal(200, d.Position.SellableSharesNow);
        Assert.Equal(20_300m, d.Position.LastPriceVnd);
        Assert.Equal(60_000m, d.Position.UnrealizedPnlVnd);   // 200 * (20,300-20,000)
        Assert.NotNull(d.Position.NetProceedsIfSellableNowVnd);
    }

    [Fact]
    public async Task Decide_Sell_WhenStopHit_AndFullySettled_AndInBand()
    {
        await using var db = NewDb(nameof(Decide_Sell_WhenStopHit_AndFullySettled_AndInBand));
        await SeedLotAsync(db, 200, 25_000m, TodayLocal().AddDays(-14));
        // stop = 25,000*0.92 = 23,000; last close 22,000 -> stop hit.
        // prev close 22,500 -> floor 20,925 / ceiling 24,075 -> 22,000 in band.
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Sell, d.Action);
        Assert.Contains("stop-loss-hit", d.Reasons);
        // net proceeds at 22,000: gross 4,400,000 - fee 6,600 - tax 4,400
        Assert.Equal(4_389_000m, d.Position.NetProceedsIfSellableNowVnd);
        Assert.Equal(-600_000m, d.Position.UnrealizedPnlVnd);
    }

    [Fact]
    public async Task Decide_StopHit_ButNothingSettled_Waits_InsteadOfSelling()
    {
        await using var db = NewDb(nameof(Decide_StopHit_ButNothingSettled_Waits_InsteadOfSelling));
        await SeedLotAsync(db, 200, 25_000m, TodayLocal()); // acquired today -> T+2 pending
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Wait, d.Action);
        Assert.Contains("stop-loss-hit", d.Reasons);
        Assert.Contains("no-settled-shares", d.Reasons);
        Assert.Equal(0, d.Position.SellableSharesNow);
        Assert.Null(d.Position.NetProceedsIfSellableNowVnd);
    }

    [Fact]
    public async Task Decide_StopHit_PartiallySettled_ReducesOnlySettledPortion()
    {
        await using var db = NewDb(nameof(Decide_StopHit_PartiallySettled_ReducesOnlySettledPortion));
        await SeedLotAsync(db, 100, 25_000m, TodayLocal().AddDays(-14)); // settled
        await SeedLotAsync(db, 100, 25_000m, TodayLocal());              // pending
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Reduce, d.Action);
        Assert.Contains("partial-settlement-sell-settled-only", d.Reasons);
        Assert.Equal(100, d.Position.SellableSharesNow); // never more than settled
        // net proceeds only on the 100 settled shares: 2,200,000 - 3,300 - 2,200
        Assert.Equal(2_194_500m, d.Position.NetProceedsIfSellableNowVnd);
    }

    [Fact]
    public async Task Decide_BearishTrend_ReduceThresholdHit_Reduces()
    {
        await using var db = NewDb(nameof(Decide_BearishTrend_ReduceThresholdHit_Reduces));
        await SeedLotAsync(db, 200, 25_000m, TodayLocal().AddDays(-14));
        // reduce = 24,000; last 23,500 <= 24,000 but > stop 23,000 -> reduce.
        // bearish: close < sma50 (24,500), rsi 40 < 50.
        await SeedDailyAsync(db, [23_900m, 23_500m], sma50: 24_500m, rsi14: 40);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Reduce, d.Action);
        Assert.Contains("trend-bearish", d.Reasons);
        Assert.Contains("reduce-threshold-hit", d.Reasons);
    }

    [Fact]
    public async Task Decide_BearishTrend_NoThresholdHit_ConditionalSell()
    {
        await using var db = NewDb(nameof(Decide_BearishTrend_NoThresholdHit_ConditionalSell));
        await SeedLotAsync(db, 200, 25_000m, TodayLocal().AddDays(-14));
        // last 24,500 < sma50 25,000, rsi 40 -> bearish; reduce lvl 24,000 not hit.
        await SeedDailyAsync(db, [24_700m, 24_500m], sma50: 25_000m, rsi14: 40);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.ConditionalSell, d.Action);
        Assert.Contains("trend-bearish", d.Reasons);
        Assert.Equal(2, d.Triggers.Count);
        Assert.All(d.Triggers, t => Assert.Null(t.NonFillRisk));
        Assert.All(d.Triggers, t => Assert.True(t.WithinPriceBand));
        Assert.Equal(24_000m, d.Triggers[0].PriceLevelVnd); // reduce level
        Assert.Equal(23_000m, d.Triggers[1].PriceLevelVnd); // stop level
        Assert.NotNull(d.Invalidation);
    }

    [Fact]
    public async Task Decide_TriggerBeyondPriceBand_GetsNonFillRisk()
    {
        await using var db = NewDb(nameof(Decide_TriggerBeyondPriceBand_GetsNonFillRisk));
        await SeedLotAsync(db, 200, 19_000m, TodayLocal().AddDays(-14));
        // prev close 20,000 -> floor 18,600. avgCost 19,000 -> reduce lvl 18,240 < floor.
        // last 19,900 > reduce lvl (not hit) and < sma50 20,500, rsi 40 -> conditional-sell.
        await SeedDailyAsync(db, [20_000m, 19_900m], sma50: 20_500m, rsi14: 40);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.ConditionalSell, d.Action);
        var reduceTrigger = d.Triggers[0];
        Assert.Equal(18_240m, reduceTrigger.PriceLevelVnd);
        Assert.False(reduceTrigger.WithinPriceBand);
        Assert.Contains("non-fill risk", reduceTrigger.NonFillRisk);
    }

    [Fact]
    public async Task Decide_Bullish_EnoughCash_BuyMore_RespectsBudget()
    {
        await using var db = NewDb(nameof(Decide_Bullish_EnoughCash_BuyMore_RespectsBudget));
        await SeedLotAsync(db, 200, 18_000m, TodayLocal().AddDays(-14));
        await SeedCashAsync(db, 5_000_000m);
        // bullish: close 20,000 > sma50 19,000, rsi 60 in [50,70).
        await SeedDailyAsync(db, [19_700m, 20_000m], sma50: 19_000m, rsi14: 60);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.BuyMore, d.Action);
        // per-share 20,030 -> floor(5,000,000/20,030)=249 -> 200 shares
        Assert.NotNull(d.BuyMore);
        Assert.Equal(200, d.BuyMore!.MaxBuyableShares);
        Assert.Equal(2, d.BuyMore.MaxBuyableLots);
        Assert.Equal(4_006_000m, d.BuyMore.EstimatedCostVnd); // incl. 0.15% fee
    }

    [Fact]
    public async Task Decide_Bullish_CashBelowOneLot_HoldsInstead()
    {
        await using var db = NewDb(nameof(Decide_Bullish_CashBelowOneLot_HoldsInstead));
        await SeedLotAsync(db, 200, 18_000m, TodayLocal().AddDays(-14));
        await SeedCashAsync(db, 1_000_000m); // < one lot (needs ~2,003,000)
        await SeedDailyAsync(db, [19_700m, 20_000m], sma50: 19_000m, rsi14: 60);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Hold, d.Action);
        Assert.Contains("cash-below-one-lot", d.Reasons);
        Assert.Null(d.BuyMore);
    }

    [Fact]
    public async Task Decide_NoPosition_Waits()
    {
        await using var db = NewDb(nameof(Decide_NoPosition_Waits));
        await SeedDailyAsync(db, [19_700m, 20_000m], sma50: 19_000m, rsi14: 60);
        var svc = NewService(db);

        var d = await svc.DecideAsync();

        Assert.Equal(MbbDecisionActions.Wait, d.Action);
        Assert.Contains("no-position", d.Reasons);
    }
}

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

    /// <summary>EQX v2 A3: shared pool = cash and MBB declares the whole pool as its buy budget.</summary>
    private static async Task SeedCashAsync(AppDbContext db, decimal cash, decimal? budget = -1m)
    {
        db.EquityCashAccounts.Add(new EquityCashAccount
        {
            AccountId = MbbDecisionService.CashAccountId, AvailableCashVnd = cash, UpdatedAtUtc = DateTime.UtcNow,
        });
        db.EquityPositionAccounts.Add(new EquityPositionAccount
        {
            Symbol = "MBB", BuyBudgetVnd = budget == -1m ? cash : budget, UpdatedAtUtc = DateTime.UtcNow,
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
    public async Task Decide_StopHit_AfterCorporateActionSinceAcquisition_WaitsInsteadOfSelling()
    {
        await using var db = NewDb(nameof(Decide_StopHit_AfterCorporateActionSinceAcquisition_WaitsInsteadOfSelling));
        var acquired = TodayLocal().AddDays(-14);
        await SeedLotAsync(db, 200, 25_000m, acquired);
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);
        // A rights issue went ex after the lot was bought: it is never auto-converted (subscription
        // unknown), so the real cost basis is not comparable and the stop-loss sell is suppressed
        // (spec §6, EQX v2 A3). A cash dividend alone no longer blocks (see the cash-dividend test).
        db.EquityCorporateActions.Add(new EquityCorporateAction
        {
            Symbol = "MBB", ExDate = acquired.AddDays(3), ActionType = "rights-issue",
            SourceUrl = "https://iq.vietcap.com.vn/x", Provenance = "provider:VCI",
            VerificationStatus = "verified", ShareRatio = 0.2m, AvailableAtUtc = DateTime.UtcNow.AddDays(-10),
        });
        // Rejected rows and ex-dates before the acquisition never count.
        db.EquityCorporateActions.Add(new EquityCorporateAction
        {
            Symbol = "MBB", ExDate = acquired.AddDays(4), ActionType = "other",
            SourceUrl = "https://x", Provenance = "admin-manual", VerificationStatus = "rejected",
        });
        await db.SaveChangesAsync();

        var d = await NewService(db).DecideAsync();

        Assert.Equal(MbbDecisionActions.Wait, d.Action);
        Assert.Contains(MbbDecisionService.CorporateActionReason, d.Reasons);
        Assert.DoesNotContain("stop-loss-hit", d.Reasons); // the stop level is cost-derived: not evaluated
        Assert.Empty(d.Triggers);
        Assert.Null(d.Position.UnrealizedPnlVnd);
        Assert.Null(d.Position.UnrealizedPnlPct);
        Assert.False(d.Position.CostBasisComparable);
        Assert.Equal(MbbDecisionService.CostBasisNotComparableNote, d.Position.CostBasisNote);
        Assert.Contains("corporate-action-unconverted:rights-issue", d.Reasons);
        Assert.Null(d.Position.NetProceedsIfSellableNowVnd); // proceeds withheld too (review NEW-2)
        Assert.Equal(MbbCorporateActionStatuses.Unconverted, Assert.Single(d.Position.CorporateActions!).Status);
        Assert.Equal(22_000m, d.Position.LastPriceVnd); // price-only facts remain

        // Same setup without an effective corporate action still sells.
        db.EquityCorporateActions.RemoveRange(db.EquityCorporateActions);
        db.EquityCorporateActions.Add(new EquityCorporateAction
        {
            Symbol = "MBB", ExDate = acquired.AddDays(-3), ActionType = "cash-dividend",
            SourceUrl = "https://x", Provenance = "provider:VCI", VerificationStatus = "verified",
        });
        await db.SaveChangesAsync();
        Assert.Equal(MbbDecisionActions.Sell, (await NewService(db).DecideAsync()).Action);
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
    public async Task Decide_BearishAfterCorporateAction_EmitsNoCostDerivedTriggers()
    {
        await using var db = NewDb(nameof(Decide_BearishAfterCorporateAction_EmitsNoCostDerivedTriggers));
        var acquired = TodayLocal().AddDays(-14);
        await SeedLotAsync(db, 200, 25_000m, acquired);
        await SeedDailyAsync(db, [24_700m, 24_500m], sma50: 25_000m, rsi14: 40); // bearish, as in the conditional-sell case
        db.EquityCorporateActions.Add(new EquityCorporateAction
        {
            Symbol = "MBB", ExDate = acquired.AddDays(2), ActionType = "stock-dividend",
            SourceUrl = "https://iq.vietcap.com.vn/x", Provenance = "provider:VCI", VerificationStatus = "unverified",
        });
        await db.SaveChangesAsync();

        var d = await NewService(db).DecideAsync();

        Assert.Equal(MbbDecisionActions.Wait, d.Action);
        Assert.Contains("trend-bearish", d.Reasons); // trend-only information remains
        Assert.Contains(MbbDecisionService.CorporateActionReason, d.Reasons);
        Assert.Empty(d.Triggers);                    // no reduce-below-cost / stop-loss levels
        Assert.Null(d.Invalidation);
        Assert.False(d.Position.CostBasisComparable);
        Assert.Null(d.Position.UnrealizedPnlVnd);
        Assert.Equal("down", d.Market.Trend);
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

    // --- EQX v2 A3: corporate-action conversion -------------------------------------

    private static async Task SeedActionAsync(AppDbContext db, DateOnly exDate, string type, string verification,
        decimal? shareRatio = null)
    {
        db.EquityCorporateActions.Add(new EquityCorporateAction
        {
            Symbol = "MBB", ExDate = exDate, ActionType = type, ShareRatio = shareRatio,
            SourceUrl = "https://example.com/ca", Provenance = "admin-manual", VerificationStatus = verification,
            AvailableAtUtc = DateTime.UtcNow.AddDays(-20),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Decide_VerifiedStockDividend_ConvertsSharesAndCost_ProceedsUseConvertedShares()
    {
        await using var db = NewDb(nameof(Decide_VerifiedStockDividend_ConvertsSharesAndCost_ProceedsUseConvertedShares));
        var acquired = TodayLocal().AddDays(-20);
        await SeedLotAsync(db, 200, 25_000m, acquired);
        await SeedActionAsync(db, acquired.AddDays(5), "stock-dividend", "verified", 0.15m); // 100:15
        db.EquityEvents.Add(new EquityEvent
        {
            Symbol = "MBB", EventType = "additional-listing", EventDate = acquired.AddDays(9), Title = "Niêm yết bổ sung",
            SourceUrl = "https://example.com/listing", Provenance = "admin-manual", AvailableAtUtc = DateTime.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync();
        // bearish (22,000 < SMA50 23,000, RSI 38) but neither stop (20,000) nor reduce (~20,870) on the converted cost.
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);

        var d = await NewService(db).DecideAsync();

        Assert.True(d.Position.CostBasisComparable);
        Assert.Equal(230, d.Position.TotalShares);        // floor(200 × 1.15)
        Assert.Equal(230, d.Position.SellableSharesNow);  // additional listing recorded
        Assert.Equal(25_000m / 1.15m, d.Position.AvgCostPerShareVnd!.Value, 6);
        Assert.Equal(60_000m, Math.Round(d.Position.UnrealizedPnlVnd!.Value, 2)); // 230×22,000 − 5,000,000
        Assert.Equal(VietnamExecutionRules.QuoteSell(230, 22_000m).NetVnd, d.Position.NetProceedsIfSellableNowVnd);
        Assert.Contains("corporate-action-converted:stock-dividend", d.Reasons);
        Assert.DoesNotContain(MbbDecisionService.CorporateActionReason, d.Reasons);
        Assert.Equal(MbbDecisionActions.ConditionalSell, d.Action);
        Assert.Contains(d.Triggers, t => t.Name == "stop-loss" && Math.Round(t.PriceLevelVnd!.Value) == 20_000m);
        var app = Assert.Single(d.Position.CorporateActions!);
        Assert.Equal((MbbCorporateActionStatuses.Converted, 1, 0.15m), (app.Status, app.AffectedLots, app.ShareRatio!.Value));

        // The stored lot stays the user's fact (raw).
        var stored = await db.EquityPositionLots.SingleAsync();
        Assert.Equal((200, 25_000m), (stored.Quantity, stored.CostPerShareVnd));
    }

    [Fact]
    public async Task Decide_VerifiedBonusShares_WithoutListingEvent_AddedSharesStayPending()
    {
        await using var db = NewDb(nameof(Decide_VerifiedBonusShares_WithoutListingEvent_AddedSharesStayPending));
        var acquired = TodayLocal().AddDays(-20);
        await SeedLotAsync(db, 200, 25_000m, acquired);
        await SeedActionAsync(db, acquired.AddDays(5), "bonus-shares", "verified", 0.5m);
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);

        var d = await NewService(db).DecideAsync();

        Assert.Equal(300, d.Position.TotalShares);
        Assert.Equal(200, d.Position.SellableSharesNow);
        Assert.Equal(100, d.Position.PendingShares);
        Assert.Equal(VietnamExecutionRules.QuoteSell(200, 22_000m).NetVnd, d.Position.NetProceedsIfSellableNowVnd);
        Assert.Contains(MbbDecisionService.PendingBonusSharesNote, d.Position.CostBasisNote);
    }

    [Fact]
    public async Task Decide_UnverifiedStockDividend_Abstains_WithUnconvertedReason()
    {
        await using var db = NewDb(nameof(Decide_UnverifiedStockDividend_Abstains_WithUnconvertedReason));
        var acquired = TodayLocal().AddDays(-20);
        await SeedLotAsync(db, 200, 25_000m, acquired);
        await SeedActionAsync(db, acquired.AddDays(5), "stock-dividend", "unverified", 0.15m);
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);

        var d = await NewService(db).DecideAsync();

        Assert.False(d.Position.CostBasisComparable);
        Assert.Contains("corporate-action-unconverted:stock-dividend", d.Reasons);
        Assert.Equal(200, d.Position.TotalShares); // raw, not converted
        Assert.Null(d.Position.UnrealizedPnlVnd);
        Assert.Null(d.Position.NetProceedsIfSellableNowVnd);
        Assert.Empty(d.Triggers);
        Assert.Equal("not verified", Assert.Single(d.Position.CorporateActions!).Reason);
    }

    [Theory]
    [InlineData("other", "verified", 0.1)]
    [InlineData("rights-issue", "verified", 0.2)]
    [InlineData("split", "verified", null)] // verified but no share ratio
    public async Task Decide_NonConvertibleActions_AreRecognized_AndWithheld(string type, string verification, double? ratio)
    {
        await using var db = NewDb(nameof(Decide_NonConvertibleActions_AreRecognized_AndWithheld) + type);
        var acquired = TodayLocal().AddDays(-20);
        await SeedLotAsync(db, 200, 25_000m, acquired);
        await SeedActionAsync(db, acquired.AddDays(5), type, verification, ratio is null ? null : (decimal)ratio.Value);
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38);

        var d = await NewService(db).DecideAsync();

        Assert.Contains($"corporate-action-unconverted:{type}", d.Reasons);
        Assert.Null(d.Position.UnrealizedPnlVnd);
        Assert.Null(d.Position.NetProceedsIfSellableNowVnd);
        Assert.DoesNotContain(d.Triggers, t => t.Name is "stop-loss" or "reduce-below-cost");
    }

    [Fact]
    public async Task Decide_CashDividend_NoShareChange_StillSellsOnStop()
    {
        await using var db = NewDb(nameof(Decide_CashDividend_NoShareChange_StillSellsOnStop));
        var acquired = TodayLocal().AddDays(-20);
        await SeedLotAsync(db, 200, 25_000m, acquired);
        await SeedActionAsync(db, acquired.AddDays(5), "cash-dividend", "unverified");
        await SeedDailyAsync(db, [22_500m, 22_000m], sma50: 23_000m, rsi14: 38); // stop 23,000 hit

        var d = await NewService(db).DecideAsync();

        Assert.Equal(MbbDecisionActions.Sell, d.Action);
        Assert.True(d.Position.CostBasisComparable);
        Assert.Equal(200, d.Position.TotalShares);
        Assert.Equal(-600_000m, d.Position.UnrealizedPnlVnd);
        Assert.Equal(MbbDecisionService.CashDividendNote, d.Position.CostBasisNote);
        Assert.Equal(MbbCorporateActionStatuses.NoShareChange, Assert.Single(d.Position.CorporateActions!).Status);
    }

    // --- EQX v2 A3: shared cash pool + buy budget ----------------------------------

    [Fact]
    public async Task Decide_Bullish_CashUnknown_NoBuyQuantity()
    {
        await using var db = NewDb(nameof(Decide_Bullish_CashUnknown_NoBuyQuantity));
        await SeedLotAsync(db, 200, 18_000m, TodayLocal().AddDays(-14));
        // Legacy per-symbol cash is ignored for buying power.
        db.EquityPositionAccounts.Add(new EquityPositionAccount { Symbol = "MBB", AvailableCashVnd = 50_000_000m, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await SeedDailyAsync(db, [19_700m, 20_000m], sma50: 19_000m, rsi14: 60);

        var d = await NewService(db).DecideAsync();

        Assert.Equal(MbbDecisionActions.Hold, d.Action);
        Assert.Contains(MbbDecisionService.ReasonCashUnknown, d.Reasons);
        Assert.Contains(MbbDecisionService.ReasonBuyBudgetUndeclared, d.Reasons);
        Assert.DoesNotContain("cash-below-one-lot", d.Reasons);
        Assert.Null(d.BuyMore);
        Assert.Equal(EquityCashSources.Unknown, d.Cash!.CashSource);
    }

    [Fact]
    public async Task Decide_Bullish_BudgetUndeclared_NoBuyQuantity()
    {
        await using var db = NewDb(nameof(Decide_Bullish_BudgetUndeclared_NoBuyQuantity));
        await SeedLotAsync(db, 200, 18_000m, TodayLocal().AddDays(-14));
        await SeedCashAsync(db, 5_000_000m, budget: null);
        await SeedDailyAsync(db, [19_700m, 20_000m], sma50: 19_000m, rsi14: 60);

        var d = await NewService(db).DecideAsync();

        Assert.Equal(MbbDecisionActions.Hold, d.Action);
        Assert.Contains(MbbDecisionService.ReasonBuyBudgetUndeclared, d.Reasons);
        Assert.DoesNotContain(MbbDecisionService.ReasonCashUnknown, d.Reasons);
        Assert.DoesNotContain("cash-below-one-lot", d.Reasons);
        Assert.Null(d.BuyMore);
        Assert.Equal((5_000_000m, (decimal?)null), (d.Cash!.SharedCashVnd!.Value, d.Cash.BuyBudgetVnd));
    }

    [Fact]
    public async Task Decide_Bullish_BudgetsExceedLoweredPool_NoBuyQuantity()
    {
        await using var db = NewDb(nameof(Decide_Bullish_BudgetsExceedLoweredPool_NoBuyQuantity));
        await SeedLotAsync(db, 200, 18_000m, TodayLocal().AddDays(-14));
        await SeedCashAsync(db, 3_000_000m, budget: 3_000_000m);
        db.EquityPositionAccounts.Add(new EquityPositionAccount { Symbol = "BID", BuyBudgetVnd = 2_000_000m, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await SeedDailyAsync(db, [19_700m, 20_000m], sma50: 19_000m, rsi14: 60);

        var d = await NewService(db).DecideAsync();

        Assert.Equal(MbbDecisionActions.Hold, d.Action);
        Assert.Contains(MbbDecisionService.ReasonBudgetExceedsCash, d.Reasons);
        Assert.Null(d.BuyMore);
        Assert.Equal(5_000_000m, d.Cash!.TotalDeclaredBudgetsVnd);
        Assert.Null(d.Cash.SpendableVnd);
    }

    [Fact]
    public async Task Decide_Bullish_BuyQuantityUsesBudget_NotWholePool()
    {
        await using var db = NewDb(nameof(Decide_Bullish_BuyQuantityUsesBudget_NotWholePool));
        await SeedLotAsync(db, 200, 18_000m, TodayLocal().AddDays(-14));
        await SeedCashAsync(db, 50_000_000m, budget: 4_100_000m);
        await SeedDailyAsync(db, [19_700m, 20_000m], sma50: 19_000m, rsi14: 60);

        var d = await NewService(db).DecideAsync();

        Assert.Equal(MbbDecisionActions.BuyMore, d.Action);
        Assert.Equal(200, d.BuyMore!.MaxBuyableShares); // 4,100,000 / 20,030 = 204 -> 2 lots
        Assert.Equal(4_100_000m, d.Cash!.SpendableVnd);
    }

    [Fact]
    public async Task ReplacePosition_SharedPool_NoDoubleCounting_AndBudgetValidation()
    {
        await using var db = NewDb(nameof(ReplacePosition_SharedPool_NoDoubleCounting_AndBudgetValidation));
        var svc = NewService(db);
        var day = TodayLocal().AddDays(-7).ToString("yyyy-MM-dd");

        // Budget before any pool exists ⇒ CASH_UNKNOWN.
        var unknown = await Assert.ThrowsAsync<EquityCashValidationException>(() => svc.ReplacePositionAsync(
            new UpdateMbbPositionRequest([new UpdateMbbPositionLotItem(100, 20_000m, day)], buyBudgetVnd: 1_000_000m)));
        Assert.Equal("CASH_UNKNOWN", unknown.Code);

        // Legacy availableCashVnd on MBB writes the SHARED pool, documented in the response.
        var mbb = await svc.ReplacePositionAsync(new UpdateMbbPositionRequest(
            [new UpdateMbbPositionLotItem(100, 20_000m, day)], availableCashVnd: 10_000_000m, buyBudgetVnd: 6_000_000m));
        Assert.Equal((10_000_000m, 6_000_000m, EquityCashSources.SharedPool), (mbb.SharedCashVnd!.Value, mbb.BuyBudgetVnd!.Value, mbb.CashSource));
        Assert.Contains(MbbDecisionService.LegacyCashWriteNote, mbb.CashNotes!);

        // The same 10M is NOT available again for BID: 6M + 5M > 10M.
        var exceeds = await Assert.ThrowsAsync<EquityCashValidationException>(() => svc.ReplacePositionAsync(
            new UpdateMbbPositionRequest([], buyBudgetVnd: 5_000_000m), "BID"));
        Assert.Equal("BUDGET_EXCEEDS_CASH", exceeds.Code);
        var bid = await svc.ReplacePositionAsync(new UpdateMbbPositionRequest([], buyBudgetVnd: 4_000_000m), "BID");
        Assert.Equal((10_000_000m, 4_000_000m, 10_000_000m), (bid.AvailableCashVnd, bid.BuyBudgetVnd!.Value, bid.TotalDeclaredBudgetsVnd!.Value));
        Assert.DoesNotContain(MbbDecisionService.LegacyCashWriteNote, bid.CashNotes!);
        Assert.Single(await db.EquityCashAccounts.ToListAsync());

        // Lowering the pool below the budgets is allowed and reported.
        var cash = await svc.SetSharedCashAsync(8_000_000m);
        Assert.True(cash.BudgetsExceedCash);
        Assert.Equal(10_000_000m, cash.TotalDeclaredBudgetsVnd);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetSharedCashAsync(-1m));

        // Full replace without buyBudgetVnd clears this symbol's budget (undeclared).
        var cleared = await svc.ReplacePositionAsync(new UpdateMbbPositionRequest([new UpdateMbbPositionLotItem(100, 20_000m, day)]));
        Assert.Null(cleared.BuyBudgetVnd);
        Assert.Equal(8_000_000m, cleared.SharedCashVnd);
    }
}

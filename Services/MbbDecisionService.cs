using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// MBB position + decision service (MBB-05). Scope is MBB-only in v1: the symbol
/// is validated against <see cref="InstrumentCatalog"/> and locked to
/// <see cref="SupportedSymbol"/> — this lane deliberately does not consult
/// <see cref="ProductionSymbolPolicy"/> enabled-traffic gates (MBB stays a
/// known-but-disabled instrument), but fails closed on any other symbol.
///
/// Decision rules are declared up-front and conservative:
/// - "sell" only when the position is FULLY settled, the declared hard stop
///   (-8% vs average cost) is hit, and net proceeds after fee+tax are positive.
/// - "reduce" when only part of the position is settled but the stop/reduce
///   threshold is hit — never sells more than settled shares.
/// - "conditional-sell" when the trend has deteriorated (1d close below SMA50
///   and RSI below midline) but no stop/reduce trigger has been touched; the
///   trigger levels are emitted for the user to arm manually.
/// - "buy-more" only when trend is constructive and cash covers at least one
///   whole lot including the buy fee — never more than cash allows.
/// - "wait"/"hold" are first-class outcomes with reasons; nothing forces a
///   BUY/SELL. Missing candles or artifacts -> "wait" + "insufficient-data".
///
/// Every emitted trigger price is checked against the HOSE ±7% band around the
/// previous daily close; a trigger outside the band carries a nonFillRisk note
/// because the band-breach policy is "reject", not clamp.
///
/// Price basis caveat: candles and levels are on the VCI "adjusted" basis —
/// absolute levels may diverge from real matched prices around corporate
/// actions. Declared, not hidden.
/// </summary>
public sealed class MbbDecisionService
{
    public const string SupportedSymbol = "MBB";

    // Declared rule parameters (contract constants, not data-tuned):
    private const decimal StopLossFraction = 0.08m;   // hard stop at -8% vs avg cost
    private const decimal ReduceLossFraction = 0.04m; // de-risk threshold at -4% vs avg cost
    private const double RsiMid = 50.0;               // midline separating constructive/weak
    private const double RsiOverbought = 70.0;        // buy-more suppressed above this
    private const double RsiOversold = 30.0;
    private const int DailyLookbackBars = 130;        // ~6 months of HOSE sessions
    private const int HourlyLookbackBars = 30;

    private const string TrendUp = "up";
    private const string TrendDown = "down";
    private const string TrendUnknown = "unknown";
    private const string RsiZoneOverbought = "overbought";
    private const string RsiZoneOversold = "oversold";
    private const string RsiZoneNeutral = "neutral";
    private const string RsiZoneUnknown = "unknown";

    private readonly AppDbContext _db;
    private readonly VietnamMarketCalendar _calendar;
    private readonly InstrumentCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly VnEquityCostSpec _costs;

    public MbbDecisionService(
        AppDbContext db,
        VietnamMarketCalendar calendar,
        InstrumentCatalog? catalog = null,
        TimeProvider? timeProvider = null,
        VnEquityCostSpec? costs = null)
    {
        _db = db;
        _calendar = calendar;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _costs = costs ?? VietnamExecutionRules.DefaultCosts;
        _costs.Validate();
    }

    /// <summary>Local Vietnam "today" used for settlement/freshness comparisons.</summary>
    public DateOnly TodayLocal()
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(nowMs).DateTime);
    }

    /// <summary>Fail-closed symbol validation: must be the known catalog instrument MBB.</summary>
    public string EnsureSupportedSymbol(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException(
                $"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'. Known instruments: " +
                string.Join(", ", _catalog.Instruments.Select(x => x.Symbol)),
                nameof(symbol));
        if (!string.Equals(instrument.Symbol, SupportedSymbol, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Symbol '{instrument.Symbol}' is not supported by this lane; v1 serves {SupportedSymbol} only.",
                nameof(symbol));
        return instrument.Symbol;
    }

    /// <summary>Current position state with derived per-lot settlement.</summary>
    public async Task<MbbPositionDto> GetPositionAsync(
        string symbol = SupportedSymbol, CancellationToken cancellationToken = default)
    {
        symbol = EnsureSupportedSymbol(symbol);
        var today = TodayLocal();
        var lots = await _db.EquityPositionLots.AsNoTracking()
            .Where(x => x.Symbol == symbol)
            .OrderBy(x => x.AcquireDate).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var account = await _db.EquityPositionAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == symbol, cancellationToken);

        var lotDtos = lots.Select(x =>
        {
            var settle = VietnamExecutionRules.SettlementDate(
                _calendar, x.AcquireDate, _costs.SettlementTradingDays);
            return new MbbPositionLotDto(
                x.Id, x.Quantity, x.CostPerShareVnd,
                x.AcquireDate.ToString("yyyy-MM-dd"),
                settle.ToString("yyyy-MM-dd"),
                settle <= today,
                x.Source);
        }).ToArray();

        var total = lotDtos.Sum(x => x.Quantity);
        var settled = lotDtos.Where(x => x.Settled).Sum(x => x.Quantity);
        var avgCost = ComputeAvgCost(lots);
        return new MbbPositionDto(
            symbol, total, settled, total - settled, avgCost,
            account?.AvailableCashVnd ?? 0m, account?.UpdatedAtUtc, lotDtos);
    }

    /// <summary>
    /// Replace-all write of lots + cash for the symbol. Validation is fail-closed:
    /// quantity &gt; 0, cost &gt; 0, cash &gt;= 0, acquireDate a valid non-future
    /// local date. Re-running the same body converges to identical state.
    /// </summary>
    public async Task<MbbPositionDto> ReplacePositionAsync(
        UpdateMbbPositionRequest request,
        string symbol = SupportedSymbol,
        CancellationToken cancellationToken = default)
    {
        symbol = EnsureSupportedSymbol(symbol);
        ArgumentNullException.ThrowIfNull(request);
        var lots = request.Lots ?? [];
        if (request.AvailableCashVnd < 0)
            throw new ArgumentException("availableCashVnd cannot be negative.", nameof(request));
        var today = TodayLocal();
        var parsed = new List<(int Qty, decimal Cost, DateOnly Date)>(lots.Count);
        for (var i = 0; i < lots.Count; i++)
        {
            var item = lots[i];
            if (item.Quantity <= 0)
                throw new ArgumentException($"lots[{i}].quantity must be > 0.");
            if (item.CostPerShareVnd <= 0)
                throw new ArgumentException($"lots[{i}].costPerShareVnd must be > 0.");
            if (!DateOnly.TryParseExact(item.AcquireDate, "yyyy-MM-dd", out var d))
                throw new ArgumentException($"lots[{i}].acquireDate must be yyyy-MM-dd.");
            if (d > today)
                throw new ArgumentException($"lots[{i}].acquireDate cannot be in the future.");
            parsed.Add((item.Quantity, item.CostPerShareVnd, d));
        }

        var existing = await _db.EquityPositionLots
            .Where(x => x.Symbol == symbol).ToListAsync(cancellationToken);
        _db.EquityPositionLots.RemoveRange(existing);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (qty, cost, d) in parsed)
        {
            _db.EquityPositionLots.Add(new EquityPositionLot
            {
                Symbol = symbol,
                Quantity = qty,
                CostPerShareVnd = cost,
                AcquireDate = d,
                Source = "manual",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        var account = await _db.EquityPositionAccounts
            .FirstOrDefaultAsync(x => x.Symbol == symbol, cancellationToken);
        if (account is null)
        {
            account = new EquityPositionAccount { Symbol = symbol };
            _db.EquityPositionAccounts.Add(account);
        }
        account.AvailableCashVnd = request.AvailableCashVnd;
        account.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(cancellationToken);
        return await GetPositionAsync(symbol, cancellationToken);
    }

    /// <summary>Compute the position-aware decision for MBB.</summary>
    public async Task<MbbDecisionDto> DecideAsync(
        string symbol = SupportedSymbol, CancellationToken cancellationToken = default)
    {
        symbol = EnsureSupportedSymbol(symbol);
        var nowUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var generatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        // --- inputs --------------------------------------------------------
        var position = await GetPositionAsync(symbol, cancellationToken);

        var daily = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == EquityCandleTimeframes.Day)
            .OrderByDescending(c => c.OpenTimeMs)
            .Take(DailyLookbackBars)
            .ToListAsync(cancellationToken);
        daily.Reverse(); // ascending

        var hourly = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == EquityCandleTimeframes.Hour)
            .OrderByDescending(c => c.OpenTimeMs)
            .Take(HourlyLookbackBars)
            .ToListAsync(cancellationToken);
        hourly.Reverse();

        var freshness = BuildFreshness(daily, hourly, nowUtcMs);

        if (daily.Count == 0)
        {
            return WaitDecision(symbol, position, freshness, generatedAt,
                "insufficient-data", "no MBB 1d candles in InstrumentCandles");
        }

        var lastDaily = daily[^1];
        var prevDaily = daily.Count >= 2 ? daily[^2] : null;

        var lastIndicator = await _db.TechnicalIndicators.AsNoTracking()
            .Where(t => t.Symbol == symbol
                        && t.Timeframe == EquityCandleTimeframes.Day
                        && t.OpenTimeMs == lastDaily.OpenTimeMs)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastIndicator is null)
        {
            return WaitDecision(symbol, position, freshness, generatedAt,
                "insufficient-data",
                "no TechnicalIndicators row for the latest 1d bar — recompute has not run");
        }

        var lastPrice = lastDaily.Close;
        var prevClose = prevDaily?.Close;
        decimal? ceiling = null, floor = null;
        if (prevClose is > 0)
            (floor, ceiling) = VietnamExecutionRules.BandBounds(prevClose.Value, _costs.PriceBandFraction);

        var market = BuildMarketContext(lastIndicator, lastDaily, prevDaily, hourly, floor, ceiling);

        // --- position summary ----------------------------------------------
        var total = position.TotalShares;
        var settled = position.SettledShares;
        var avgCost = position.AvgCostPerShareVnd;
        var unrealized = avgCost is > 0 && total > 0 ? (lastPrice - avgCost.Value) * total : (decimal?)null;
        var unrealizedPct = avgCost is > 0 && total > 0
            ? (double?)((lastPrice - avgCost.Value) / avgCost.Value * 100m)
            : null;
        var sellQuote = settled > 0 ? VietnamExecutionRules.QuoteSell(settled, lastPrice, _costs) : null;
        var summary = new MbbPositionSummary(
            total, settled, total - settled, settled,
            avgCost, lastPrice, unrealized, unrealizedPct, sellQuote?.NetVnd);

        var reasons = new List<string>();
        var triggers = new List<MbbDecisionTrigger>();
        if (freshness.Any(f => f.IsStale)) reasons.Add("stale-data");

        // --- declared rules ------------------------------------------------
        if (total == 0)
        {
            reasons.Add("no-position");
            return Decide(symbol, MbbDecisionActions.Wait, reasons, triggers, null,
                summary, null, market, freshness, generatedAt);
        }

        var indicatorsReady = market.Trend != TrendUnknown && market.RsiZone != RsiZoneUnknown;
        if (!indicatorsReady) reasons.Add("indicators-warming-up");

        var bearish = market.Trend == TrendDown && lastIndicator.Rsi14 is < RsiMid;
        var bullish = market.Trend == TrendUp
                      && lastIndicator.Rsi14 is >= RsiMid and < RsiOverbought;

        var stopLossPrice = avgCost is > 0 ? avgCost.Value * (1m - StopLossFraction) : (decimal?)null;
        var reducePrice = avgCost is > 0 ? avgCost.Value * (1m - ReduceLossFraction) : (decimal?)null;
        var stopHit = stopLossPrice is not null && lastPrice <= stopLossPrice;
        var reduceHit = reducePrice is not null && lastPrice <= reducePrice;

        if (stopHit)
        {
            reasons.Add("stop-loss-hit");
            var (allowed, bandReason) = prevClose is > 0
                ? VietnamExecutionRules.CanSellAt(lastPrice, prevClose.Value, _costs.PriceBandFraction)
                : (false, "band-reference-missing");
            if (!allowed)
            {
                reasons.Add(bandReason!);
                triggers.Add(BandCheckedTrigger("sell-at-market", EquityCandleTimeframes.Day,
                    lastPrice, "sell order would execute at/after next session open",
                    prevClose));
            }
            if (settled == total && sellQuote is { NetVnd: > 0 } && allowed)
            {
                return Decide(symbol, MbbDecisionActions.Sell, reasons, triggers,
                    "stop-loss level reclaimed: 1d close back above "
                    + FormatLevel(stopLossPrice!.Value),
                    summary, null, market, freshness, generatedAt);
            }
            if (settled > 0 && sellQuote is { NetVnd: > 0 } && allowed)
            {
                reasons.Add("partial-settlement-sell-settled-only");
                return Decide(symbol, MbbDecisionActions.Reduce, reasons, triggers,
                    "stop-loss level reclaimed or remaining lots settled",
                    summary, null, market, freshness, generatedAt);
            }
            reasons.Add(settled == 0 ? "no-settled-shares" : "sell-not-fillable");
            return Decide(symbol, MbbDecisionActions.Wait, reasons, triggers, null,
                summary, null, market, freshness, generatedAt);
        }

        if (bearish)
        {
            reasons.Add("trend-bearish");
            triggers.Add(BandCheckedTrigger("reduce-below-cost", EquityCandleTimeframes.Day,
                reducePrice,
                "reduce the settled portion on a 1d close at/below this level",
                prevClose, MbbTriggerDirections.CloseBelow));
            triggers.Add(BandCheckedTrigger("stop-loss", EquityCandleTimeframes.Day,
                stopLossPrice,
                "exit the position on a 1d close at/below this level",
                prevClose, MbbTriggerDirections.CloseBelow));
            if (reduceHit)
            {
                reasons.Add("reduce-threshold-hit");
                if (settled > 0 && sellQuote is { NetVnd: > 0 })
                {
                    return Decide(symbol, MbbDecisionActions.Reduce, reasons, triggers,
                        "1d close back above SMA50 and RSI >= 50",
                        summary, null, market, freshness, generatedAt);
                }
                reasons.Add(settled == 0 ? "no-settled-shares" : "sell-not-fillable");
                return Decide(symbol, MbbDecisionActions.Wait, reasons, triggers, null,
                    summary, null, market, freshness, generatedAt);
            }
            return Decide(symbol, MbbDecisionActions.ConditionalSell, reasons, triggers,
                "1d close back above SMA50 and RSI >= 50",
                summary, null, market, freshness, generatedAt);
        }

        if (bullish)
        {
            var maxShares = VietnamExecutionRules.BuySharesForBudget(
                position.AvailableCashVnd, lastPrice, _costs);
            if (maxShares >= _costs.LotSize)
            {
                reasons.Add("trend-constructive");
                var quote = VietnamExecutionRules.QuoteBuy(maxShares, lastPrice, _costs);
                var capacity = new MbbBuyMoreCapacity(
                    maxShares, maxShares / _costs.LotSize, quote.TotalCostVnd);
                return Decide(symbol, MbbDecisionActions.BuyMore, reasons, triggers,
                    "1d close back below SMA50",
                    summary, capacity, market, freshness, generatedAt);
            }
            reasons.Add("cash-below-one-lot");
        }

        reasons.Add("no-trigger");
        return Decide(symbol, MbbDecisionActions.Hold, reasons, triggers, null,
            summary, null, market, freshness, generatedAt);
    }

    // --- helpers -----------------------------------------------------------

    private static decimal? ComputeAvgCost(IReadOnlyList<EquityPositionLot> lots)
    {
        var totalQty = lots.Sum(x => x.Quantity);
        if (totalQty <= 0) return null;
        return lots.Sum(x => x.Quantity * x.CostPerShareVnd) / totalQty;
    }

    private IReadOnlyList<MbbTimeframeFreshness> BuildFreshness(
        IReadOnlyList<InstrumentCandle> daily,
        IReadOnlyList<InstrumentCandle> hourly,
        long nowUtcMs)
    {
        var result = new List<MbbTimeframeFreshness>(2);
        foreach (var (tf, bars) in new (string, IReadOnlyList<InstrumentCandle>)[]
        {
            (EquityCandleTimeframes.Day, daily),
            (EquityCandleTimeframes.Hour, hourly),
        })
        {
            var latest = bars.Count > 0 ? bars[^1].OpenTimeMs : (long?)null;
            var expected = _calendar.LastExpectedFinalizedOpenUtcMs(tf, nowUtcMs);
            var stale = latest is null || expected is null || latest < expected;
            result.Add(new MbbTimeframeFreshness(tf, latest, expected, stale));
        }
        return result;
    }

    private static MbbMarketContext BuildMarketContext(
        TechnicalIndicator ind,
        InstrumentCandle lastDaily,
        InstrumentCandle? prevDaily,
        IReadOnlyList<InstrumentCandle> hourly,
        decimal? floor,
        decimal? ceiling)
    {
        var trend = ind.Sma50 is null ? TrendUnknown
            : lastDaily.Close > ind.Sma50 ? TrendUp
            : lastDaily.Close < ind.Sma50 ? TrendDown
            : TrendUnknown;
        var rsiZone = ind.Rsi14 is null ? RsiZoneUnknown
            : ind.Rsi14 >= RsiOverbought ? RsiZoneOverbought
            : ind.Rsi14 <= RsiOversold ? RsiZoneOversold
            : RsiZoneNeutral;
        return new MbbMarketContext(
            trend, rsiZone, ind.Rsi14, ind.Atr14, ind.Sma50, ind.Ema50,
            lastDaily.Close, prevDaily?.Close,
            hourly.Count > 0 ? hourly[^1].Close : null,
            ceiling, floor);
    }

    /// <summary>Attach the price-band verdict to a trigger level; breach -> nonFillRisk.</summary>
    private MbbDecisionTrigger BandCheckedTrigger(
        string name, string timeframe, decimal? priceLevel, string condition, decimal? prevClose,
        string? direction = null)
    {
        bool? within = null;
        string? risk = null;
        if (priceLevel is not null)
        {
            if (prevClose is > 0)
            {
                var band = VietnamExecutionRules.ClassifyBand(
                    priceLevel.Value, prevClose.Value, _costs.PriceBandFraction);
                within = band == VnBandStates.WithinBand;
                if (within == false)
                    risk = $"non-fill risk: {band} — band-breach policy is reject, not clamp";
            }
            else
            {
                risk = "non-fill risk: band-reference-missing (no previous 1d close)";
            }
        }
        return new MbbDecisionTrigger(name, timeframe, priceLevel, condition, within, risk, direction);
    }

    private MbbDecisionDto WaitDecision(
        string symbol, MbbPositionDto position,
        IReadOnlyList<MbbTimeframeFreshness> freshness, DateTime generatedAt,
        params string[] reasons)
    {
        var summary = new MbbPositionSummary(
            position.TotalShares, position.SettledShares, position.PendingShares,
            position.SettledShares, position.AvgCostPerShareVnd,
            null, null, null, null);
        var market = new MbbMarketContext(
            TrendUnknown, RsiZoneUnknown, null, null, null, null,
            null, null, null, null, null);
        return Decide(symbol, MbbDecisionActions.Wait, reasons,
            Array.Empty<MbbDecisionTrigger>(), null, summary, null, market,
            freshness, generatedAt);
    }

    private static MbbDecisionDto Decide(
        string symbol, string action, IReadOnlyList<string> reasons,
        IReadOnlyList<MbbDecisionTrigger> triggers, string? invalidation,
        MbbPositionSummary position, MbbBuyMoreCapacity? buyMore,
        MbbMarketContext market, IReadOnlyList<MbbTimeframeFreshness> freshness,
        DateTime generatedAt) =>
        new(symbol, action, reasons, triggers, invalidation, position, buyMore,
            market, MbbEvidenceStatuses.RulesOnly, VietnamExecutionRules.PriceBasis,
            freshness, generatedAt);

    private static string FormatLevel(decimal vnd) => $"{vnd:N0} VND";
}

using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Equity position + decision service (MBB-05, generalized in EQX). The symbol is
/// validated against <see cref="InstrumentCatalog"/> and must carry the Decision
/// capability (MBB, BID) — this lane deliberately does not consult
/// <see cref="ProductionSymbolPolicy"/> enabled-traffic gates (equities stay
/// known-but-disabled instruments), but fails closed on any other symbol.
/// Positions are keyed per symbol.
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
///   whole lot including the buy fee at the NEXT session's ceiling price (the
///   worst possible open) — never more than the budget allows even then.
/// - Stop-loss / reduce levels sit on the HOSE tick grid, rounded up (never a
///   deeper loss than declared).
/// - "wait"/"hold" are first-class outcomes with reasons; nothing forces a
///   BUY/SELL. Missing candles or artifacts -> "wait" + "insufficient-data".
///   A stale 1d bar turns any sell/reduce/conditional-sell/buy-more into "wait"
///   + "stale-data-blocked:&lt;action&gt;" (MBBSTALE-1).
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
    /// <summary>
    /// The /api/mbb alias symbol and the default for symbol-less calls. Since EQX the
    /// service serves every catalog equity with the Decision capability (MBB, BID); the
    /// VND cost/lot/band logic below only ever runs for those instruments.
    /// </summary>
    public const string SupportedSymbol = "MBB";

    /// <summary>
    /// Version of the declared decision rules, stamped on every journal row
    /// (<c>MbbSignalObservation.RulesVersion</c>). Bump it whenever a rule, threshold,
    /// sizing or blocking behaviour changes, so rows decided under different rules stay
    /// separable. v1 = every row recorded before these changes. v2: stop/reduce levels
    /// rounded up onto the HOSE tick grid and buy-more sized at the next session ceiling
    /// (272ec30, MBBSIZE-1); a stale 1d close turns sell/reduce/conditional-sell/buy-more
    /// into wait (ad30023, MBBSTALE-1).
    /// </summary>
    public const string RulesVersion = "declared-rules-v2";

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

    /// <summary>Fail-closed symbol validation: a known catalog equity with the Decision capability.</summary>
    public string EnsureSupportedSymbol(string symbol) => RequireDecisionInstrument(symbol).Symbol;

    private InstrumentDescriptor RequireDecisionInstrument(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException(
                $"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'. Known instruments: " +
                string.Join(", ", _catalog.Instruments.Select(x => x.Symbol)),
                nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException(
                $"Symbol '{instrument.Symbol}' is not supported by this lane: capability 'decision' is required.",
                nameof(symbol));
        return instrument;
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
        var cash = await LoadCashContextAsync(symbol, cancellationToken);
        return new MbbPositionDto(
            symbol, total, settled, total - settled, avgCost,
            cash.SharedCashVnd ?? 0m, account?.UpdatedAtUtc, lotDtos,
            cash.SharedCashVnd, cash.BuyBudgetVnd, cash.CashSource, cash.TotalDeclaredBudgetsVnd,
            CashNotes(symbol, cash));
    }

    // --- shared cash pool + per-symbol buy budget (EQX v2 A3) -----------------

    /// <summary>The single shared brokerage cash pool row.</summary>
    public const string CashAccountId = "default";

    public const string ReasonCashUnknown = "cash-unknown";
    public const string ReasonBuyBudgetUndeclared = "buy-budget-undeclared";
    public const string ReasonBudgetExceedsCash = "budget-exceeds-cash";
    /// <summary>Buy quantity/cost priced at the next session's ceiling (reference = latest 1d close): cost is an upper bound.</summary>
    public const string ReasonBuySizedAtNextCeiling = "buy-sized-at-next-session-ceiling";
    public const string SharedPoolNote =
        "Cash is ONE shared pool for every equity symbol (EquityCashAccounts 'default'); availableCashVnd mirrors that pool and is not per-symbol. Per-symbol buying power is buyBudgetVnd.";
    public const string LegacyCashWriteNote =
        "availableCashVnd in this PUT body was written to the SHARED cash pool (it applies to every symbol, not only this one).";

    private async Task<MbbCashContext> LoadCashContextAsync(string symbol, CancellationToken ct)
    {
        var pool = await _db.EquityCashAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AccountId == CashAccountId, ct);
        var budgets = await _db.EquityPositionAccounts.AsNoTracking()
            .Where(x => x.BuyBudgetVnd != null)
            .Select(x => new { x.Symbol, x.BuyBudgetVnd })
            .ToListAsync(ct);
        var shared = pool?.AvailableCashVnd;
        var budget = budgets.FirstOrDefault(b => b.Symbol == symbol)?.BuyBudgetVnd;
        var total = budgets.Sum(b => b.BuyBudgetVnd ?? 0m);
        decimal? spendable = shared is null || budget is null || total > shared ? null : Math.Min(budget.Value, shared.Value);
        return new MbbCashContext(shared, budget, total,
            shared is null ? EquityCashSources.Unknown : EquityCashSources.SharedPool, spendable);
    }

    private static IReadOnlyList<string> CashNotes(string symbol, MbbCashContext cash)
    {
        var notes = new List<string> { SharedPoolNote };
        if (cash.SharedCashVnd is null)
            notes.Add("The shared cash pool has not been entered (PUT /api/equities/cash): buying power is unknown.");
        if (cash.BuyBudgetVnd is null)
            notes.Add($"No buyBudgetVnd is declared for {symbol}: no buy quantity is proposed.");
        if (cash.SharedCashVnd is not null && cash.TotalDeclaredBudgetsVnd > cash.SharedCashVnd)
            notes.Add("The sum of declared buy budgets exceeds the shared cash pool: no buy quantity is proposed until it is fixed.");
        return notes;
    }

    /// <summary>Shared cash pool + every declared budget.</summary>
    public async Task<EquityCashDto> GetCashAsync(CancellationToken cancellationToken = default)
    {
        var pool = await _db.EquityCashAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AccountId == CashAccountId, cancellationToken);
        var budgets = await _db.EquityPositionAccounts.AsNoTracking()
            .OrderBy(x => x.Symbol)
            .Select(x => new EquityBuyBudgetDto(x.Symbol, x.BuyBudgetVnd))
            .ToListAsync(cancellationToken);
        var total = budgets.Sum(b => b.BuyBudgetVnd ?? 0m);
        var exceeds = pool?.AvailableCashVnd is { } c && total > c;
        var notes = new List<string> { SharedPoolNote };
        if (pool?.AvailableCashVnd is null) notes.Add("The shared cash pool has not been entered: buying power is unknown.");
        if (exceeds) notes.Add("The sum of declared buy budgets exceeds the shared cash pool: decisions report budget-exceeds-cash and propose no buy quantity.");
        return new EquityCashDto(CashAccountId, pool?.AvailableCashVnd, pool?.UpdatedAtUtc, total, exceeds, budgets, notes);
    }

    /// <summary>
    /// Set the shared cash pool. Lowering it below the sum of budgets is allowed (cash really moved);
    /// decisions then report budget-exceeds-cash instead of proposing quantities.
    /// </summary>
    public async Task<EquityCashDto> SetSharedCashAsync(decimal? availableCashVnd, CancellationToken cancellationToken = default)
    {
        if (availableCashVnd is < 0)
            throw new ArgumentException("availableCashVnd cannot be negative.", nameof(availableCashVnd));
        await UpsertPoolAsync(availableCashVnd, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return await GetCashAsync(cancellationToken);
    }

    private async Task UpsertPoolAsync(decimal? cash, DateTime now, CancellationToken ct)
    {
        var pool = await _db.EquityCashAccounts.FirstOrDefaultAsync(x => x.AccountId == CashAccountId, ct);
        if (pool is null)
        {
            pool = new EquityCashAccount { AccountId = CashAccountId };
            _db.EquityCashAccounts.Add(pool);
        }
        pool.AvailableCashVnd = cash;
        pool.UpdatedAtUtc = now;
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
        if (request.AvailableCashVnd is < 0)
            throw new ArgumentException("availableCashVnd cannot be negative.", nameof(request));
        if (request.BuyBudgetVnd is < 0)
            throw new ArgumentException("buyBudgetVnd cannot be negative.", nameof(request));
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

        // Validate cash/budget BEFORE any mutation so a rejected PUT leaves nothing tracked/written.
        // Legacy availableCashVnd => the ONE shared pool (never a per-symbol sleeve, so the same broker cash
        // cannot back buys on two symbols).
        var poolCash = request.AvailableCashVnd
            ?? (await _db.EquityCashAccounts.AsNoTracking()
                .FirstOrDefaultAsync(x => x.AccountId == CashAccountId, cancellationToken))?.AvailableCashVnd;
        if (request.BuyBudgetVnd is { } budget)
        {
            if (poolCash is null)
                throw new EquityCashValidationException("CASH_UNKNOWN",
                    "buyBudgetVnd needs the shared cash pool: set it first (PUT /api/equities/cash or availableCashVnd in this body).");
            var others = (await _db.EquityPositionAccounts.AsNoTracking()
                    .Where(x => x.Symbol != symbol && x.BuyBudgetVnd != null)
                    .Select(x => x.BuyBudgetVnd)
                    .ToListAsync(cancellationToken))
                .Sum(x => x ?? 0m);
            if (others + budget > poolCash.Value)
                throw new EquityCashValidationException("BUDGET_EXCEEDS_CASH",
                    $"buyBudgetVnd {budget:0} + other symbols' budgets {others:0} exceed the shared cash pool {poolCash.Value:0} VND.");
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

        if (request.AvailableCashVnd is { } legacyCash)
            await UpsertPoolAsync(legacyCash, now, cancellationToken);

        var account = await _db.EquityPositionAccounts
            .FirstOrDefaultAsync(x => x.Symbol == symbol, cancellationToken);
        if (account is null)
        {
            account = new EquityPositionAccount { Symbol = symbol };
            _db.EquityPositionAccounts.Add(account);
        }
        // Per-symbol AvailableCashVnd is legacy and no longer written or read for buying power.
        account.BuyBudgetVnd = request.BuyBudgetVnd;
        account.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(cancellationToken);
        var dto = await GetPositionAsync(symbol, cancellationToken);
        return request.AvailableCashVnd is null
            ? dto
            : dto with { CashNotes = [.. dto.CashNotes ?? [], LegacyCashWriteNote] };
    }

    /// <summary>Compute the position-aware decision for MBB.</summary>
    public async Task<MbbDecisionDto> DecideAsync(
        string symbol = SupportedSymbol, CancellationToken cancellationToken = default)
    {
        var instrument = RequireDecisionInstrument(symbol);
        var cash = await LoadCashContextAsync(instrument.Symbol, cancellationToken);
        var decision = await DecideCoreAsync(instrument, cash, cancellationToken);
        return decision with { Cash = cash };
    }

    private async Task<MbbDecisionDto> DecideCoreAsync(
        InstrumentDescriptor instrument, MbbCashContext cash, CancellationToken cancellationToken)
    {
        var symbol = instrument.Symbol;
        var hourlyActive = instrument.ActiveTimeframes.Contains(EquityCandleTimeframes.Hour, StringComparer.Ordinal);
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

        // Hourly bars only for instruments that declare 1h (BID is daily-only). No rule
        // depends on 1h today — it feeds the display-only LastHourlyCloseVnd, which stays
        // null for daily-only instruments instead of being synthesised.
        var hourly = hourlyActive
            ? await _db.InstrumentCandles.AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == EquityCandleTimeframes.Hour)
                .OrderByDescending(c => c.OpenTimeMs)
                .Take(HourlyLookbackBars)
                .ToListAsync(cancellationToken)
            : new List<InstrumentCandle>();
        hourly.Reverse();

        var freshness = BuildFreshness(instrument, daily, hourly, nowUtcMs);

        // EQX v2 review #1: past the calendar coverage no session can be trusted to exist or not: wait.
        if (freshness.Any(f => f.Reason == VietnamMarketCalendar.CoverageExpiredReason))
        {
            return WaitDecision(symbol, position, freshness, generatedAt,
                VietnamMarketCalendar.CoverageExpiredReason,
                $"today is after the HOSE calendar coverage ({_calendar.CoverageTo:yyyy-MM-dd}); extend {MarketCalendarSource.DefaultRelativePath}");
        }

        if (daily.Count == 0)
        {
            return WaitDecision(symbol, position, freshness, generatedAt,
                "insufficient-data", $"no {symbol} 1d candles in InstrumentCandles");
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
        // EQX v2 A3: corporate actions that went ex after a lot was bought (and have taken effect) are
        // applied to that lot. Verified stock-dividend / bonus-shares / split with ShareRatio convert shares
        // and cost/share; a cash dividend changes neither (the cash is not in P&L); anything else
        // (rights issue, other, unverified, ratio missing) leaves the real cost basis incomparable with the
        // price, so NOTHING cost-derived is emitted: no P&L, no proceeds, no stop-loss / reduce levels.
        var ca = await ApplyCorporateActionsAsync(symbol, position, cancellationToken);
        var costComparable = ca.Unconverted.Count == 0;
        var total = ca.TotalShares;
        var settled = ca.SettledShares;
        var avgCost = ca.AvgCostPerShareVnd;

        var unrealized = costComparable && avgCost is > 0 && total > 0 ? (lastPrice - avgCost.Value) * total : (decimal?)null;
        var unrealizedPct = costComparable && avgCost is > 0 && total > 0
            ? (double?)((lastPrice - avgCost.Value) / avgCost.Value * 100m)
            : null;
        var sellQuote = costComparable && settled > 0 ? VietnamExecutionRules.QuoteSell(settled, lastPrice, _costs) : null;
        var summary = new MbbPositionSummary(
            total, settled, total - settled, settled,
            avgCost, lastPrice, unrealized, unrealizedPct, sellQuote?.NetVnd,
            costComparable,
            costComparable ? ca.Note : CostBasisNotComparableNote,
            ca.Applications.Count == 0 ? null : ca.Applications);

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

        foreach (var type in ca.Converted) reasons.Add($"corporate-action-converted:{type}");
        if (!costComparable)
        {
            reasons.Add(CorporateActionReason);
            foreach (var type in ca.Unconverted) reasons.Add($"{UnconvertedReasonPrefix}{type}");
        }

        var indicatorsReady = market.Trend != TrendUnknown && market.RsiZone != RsiZoneUnknown;
        if (!indicatorsReady) reasons.Add("indicators-warming-up");

        var bearish = market.Trend == TrendDown && lastIndicator.Rsi14 is < RsiMid;
        var bullish = market.Trend == TrendUp
                      && lastIndicator.Rsi14 is >= RsiMid and < RsiOverbought;

        // Levels on the HOSE tick grid, rounded UP: an order can be placed there and the declared loss is never exceeded.
        var stopLossPrice = costComparable && avgCost is > 0 ? VietnamExecutionRules.RoundUpToTick(avgCost.Value * (1m - StopLossFraction)) : (decimal?)null;
        var reducePrice = costComparable && avgCost is > 0 ? VietnamExecutionRules.RoundUpToTick(avgCost.Value * (1m - ReduceLossFraction)) : (decimal?)null;
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

        if (bearish && !costComparable)
        {
            // Trend information stays; every conditional-sell level here would be cost-derived.
            reasons.Add("trend-bearish");
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
            // EQX v2 A3: buy quantity only from a DECLARED per-symbol budget backed by the shared pool.
            var blockers = new List<string>();
            if (cash.SharedCashVnd is null) blockers.Add(ReasonCashUnknown);
            if (cash.BuyBudgetVnd is null) blockers.Add(ReasonBuyBudgetUndeclared);
            if (cash.SharedCashVnd is { } pool && cash.TotalDeclaredBudgetsVnd > pool) blockers.Add(ReasonBudgetExceedsCash);
            reasons.AddRange(blockers);
            // The order fills at the next session open, which can be anywhere up to that session's ceiling:
            // size at the ceiling so the cost stays within the budget even at the worst open.
            var (_, nextCeiling) = VietnamExecutionRules.BandBounds(lastPrice, _costs.PriceBandFraction);
            var maxShares = blockers.Count == 0 && cash.SpendableVnd is { } spendable
                ? VietnamExecutionRules.BuySharesForBudget(spendable, nextCeiling, _costs)
                : 0;
            if (blockers.Count == 0) reasons.Add(ReasonBuySizedAtNextCeiling);
            if (blockers.Count == 0 && maxShares >= _costs.LotSize)
            {
                reasons.Add("trend-constructive");
                var quote = VietnamExecutionRules.QuoteBuy(maxShares, nextCeiling, _costs);
                var capacity = new MbbBuyMoreCapacity(
                    maxShares, maxShares / _costs.LotSize, quote.TotalCostVnd);
                return Decide(symbol, MbbDecisionActions.BuyMore, reasons, triggers,
                    "1d close back below SMA50",
                    summary, capacity, market, freshness, generatedAt);
            }
            if (blockers.Count == 0) reasons.Add("cash-below-one-lot");
        }

        reasons.Add("no-trigger");
        return Decide(symbol, MbbDecisionActions.Hold, reasons, triggers, null,
            summary, null, market, freshness, generatedAt);
    }

    public const string CorporateActionReason = "corporate-action-since-acquisition";
    public const string UnconvertedReasonPrefix = "corporate-action-unconverted:";
    public const string CostBasisNotComparableNote =
        "A corporate action that cannot be converted (rights issue, other, unverified, or no verified share ratio) went ex after a lot was bought: " +
        "the cost basis is the real, unconverted purchase price, so cost-vs-price figures (P&L, proceeds, stop-loss/reduce levels) are withheld.";
    public const string CashDividendNote =
        "Cash dividends since acquisition do not change shares or cost; the dividend cash received is not included in P&L.";
    public const string PendingBonusSharesNote =
        "Shares added by a converted stock dividend / bonus issue / split count as pending (not sellable) until an additional-listing event on/after the ex-date is recorded.";

    private static readonly IReadOnlySet<string> ConvertibleActionTypes =
        new HashSet<string>(StringComparer.Ordinal) { "stock-dividend", "bonus-shares", "split" };

    internal sealed record CorporateActionResult(
        int TotalShares,
        int SettledShares,
        decimal? AvgCostPerShareVnd,
        IReadOnlyList<MbbCorporateActionApplication> Applications,
        IReadOnlyList<string> Converted,
        IReadOnlyList<string> Unconverted,
        string? Note);

    /// <summary>
    /// Apply effective (ex-date on/before today, not rejected) corporate actions to every lot bought
    /// BEFORE the ex-date, in ex-date order. Conversion (verified stock-dividend / bonus-shares / split
    /// with ShareRatio r): shares := floor(shares × (1 + r)) (fractional entitlements are paid in cash and not
    /// tracked), cost/share := cost/share ÷ (1 + r). Added shares stay pending until an additional-listing
    /// event on/after the ex-date exists. Stored lots are never rewritten (they remain the user's facts).
    /// </summary>
    internal async Task<CorporateActionResult> ApplyCorporateActionsAsync(
        string symbol, MbbPositionDto position, CancellationToken cancellationToken)
    {
        var lots = position.Lots
            .Select(l => (Acquire: DateOnly.ParseExact(l.AcquireDate, "yyyy-MM-dd"), l.Quantity, l.CostPerShareVnd, l.Settled))
            .ToArray();
        if (lots.Length == 0)
            return new CorporateActionResult(0, 0, null, [], [], [], null);

        var earliest = lots.Min(l => l.Acquire);
        var today = TodayLocal();
        var actions = await _db.EquityCorporateActions.AsNoTracking()
            .Where(a => a.Symbol == symbol
                        && a.VerificationStatus != "rejected"
                        && a.ExDate > earliest
                        && a.ExDate <= today)
            .OrderBy(a => a.ExDate).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        if (actions.Count == 0)
            return new CorporateActionResult(position.TotalShares, position.SettledShares,
                position.AvgCostPerShareVnd, [], [], [], null);

        var listings = await _db.EquityEvents.AsNoTracking()
            .Where(e => e.Symbol == symbol && e.EventType == "additional-listing" && e.EventDate <= today)
            .Select(e => e.EventDate)
            .ToListAsync(cancellationToken);

        var qty = lots.Select(l => (decimal)l.Quantity).ToArray();
        var cost = lots.Select(l => l.CostPerShareVnd).ToArray();
        var pendingExtra = new int[lots.Length];
        var applications = new List<MbbCorporateActionApplication>();
        var converted = new List<string>();
        var unconverted = new List<string>();
        var cashDividend = false;
        var anyPendingExtra = false;
        foreach (var a in actions)
        {
            var affected = Enumerable.Range(0, lots.Length).Where(i => lots[i].Acquire < a.ExDate).ToArray();
            if (affected.Length == 0) continue;
            string status;
            string? reason = null;
            if (a.ActionType == "cash-dividend")
            {
                status = MbbCorporateActionStatuses.NoShareChange;
                reason = "cash dividend: no share/cost change; dividend cash not included in P&L";
                cashDividend = true;
            }
            else if (!ConvertibleActionTypes.Contains(a.ActionType))
            {
                status = MbbCorporateActionStatuses.Unconverted;
                reason = $"type '{a.ActionType}' is not auto-converted (subscription/terms unknown)";
            }
            else if (a.VerificationStatus != "verified")
            {
                status = MbbCorporateActionStatuses.Unconverted;
                reason = "not verified";
            }
            else if (a.ShareRatio is not > 0)
            {
                status = MbbCorporateActionStatuses.Unconverted;
                reason = "verified share ratio missing";
            }
            else
            {
                status = MbbCorporateActionStatuses.Converted;
                var factor = 1m + a.ShareRatio.Value;
                var listed = listings.Any(d => d >= a.ExDate);
                foreach (var i in affected)
                {
                    var before = (int)Math.Floor(qty[i]);
                    qty[i] = Math.Floor(qty[i] * factor);
                    cost[i] /= factor;
                    if (!listed)
                    {
                        pendingExtra[i] += (int)qty[i] - before;
                        anyPendingExtra = true;
                    }
                }
            }
            if (status == MbbCorporateActionStatuses.Converted && !converted.Contains(a.ActionType)) converted.Add(a.ActionType);
            if (status == MbbCorporateActionStatuses.Unconverted && !unconverted.Contains(a.ActionType)) unconverted.Add(a.ActionType);
            applications.Add(new MbbCorporateActionApplication(
                a.ExDate.ToString("yyyy-MM-dd"), a.ActionType, a.VerificationStatus, a.ShareRatio, status, affected.Length, reason));
        }

        if (unconverted.Count > 0)
        {
            // Raw (unconverted) figures; cost-derived outputs are withheld by the caller.
            return new CorporateActionResult(position.TotalShares, position.SettledShares,
                position.AvgCostPerShareVnd, applications, converted, unconverted, null);
        }

        var total = (int)qty.Sum();
        var settled = Enumerable.Range(0, lots.Length)
            .Where(i => lots[i].Settled)
            .Sum(i => (int)qty[i] - pendingExtra[i]);
        decimal? avg = total > 0 ? Enumerable.Range(0, lots.Length).Sum(i => qty[i] * cost[i]) / total : null;
        var notes = new List<string>();
        if (cashDividend) notes.Add(CashDividendNote);
        if (anyPendingExtra) notes.Add(PendingBonusSharesNote);
        return new CorporateActionResult(total, settled, avg, applications, converted, unconverted,
            notes.Count == 0 ? null : string.Join(" ", notes));
    }

    // --- helpers -----------------------------------------------------------

    private static decimal? ComputeAvgCost(IReadOnlyList<EquityPositionLot> lots)
    {
        var totalQty = lots.Sum(x => x.Quantity);
        if (totalQty <= 0) return null;
        return lots.Sum(x => x.Quantity * x.CostPerShareVnd) / totalQty;
    }

    /// <summary>
    /// Freshness over the instrument's ActiveTimeframes only (1d first, then 1h when declared):
    /// a daily-only instrument such as BID can never be stale for a timeframe it does not have.
    /// </summary>
    private IReadOnlyList<MbbTimeframeFreshness> BuildFreshness(
        InstrumentDescriptor instrument,
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
            if (!instrument.ActiveTimeframes.Contains(tf, StringComparer.Ordinal)) continue;
            var latest = bars.Count > 0 ? bars[^1].OpenTimeMs : (long?)null;
            if (_calendar.IsBeyondCoverage(DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(nowUtcMs).DateTime)))
            {
                result.Add(new MbbTimeframeFreshness(tf, latest, null, true, VietnamMarketCalendar.CoverageExpiredReason));
                continue;
            }
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

    /// <summary>MBBSTALE-1: a trading action the rules reached on a stale 1d bar, downgraded to wait (suffix = that action).</summary>
    public const string ReasonStaleDataBlockedPrefix = "stale-data-blocked:";

    private static readonly IReadOnlySet<string> TradingActions = new HashSet<string>(StringComparer.Ordinal)
    {
        MbbDecisionActions.Sell, MbbDecisionActions.Reduce, MbbDecisionActions.ConditionalSell, MbbDecisionActions.BuyMore,
    };

    private static MbbDecisionDto Decide(
        string symbol, string action, IReadOnlyList<string> reasons,
        IReadOnlyList<MbbDecisionTrigger> triggers, string? invalidation,
        MbbPositionSummary position, MbbBuyMoreCapacity? buyMore,
        MbbMarketContext market, IReadOnlyList<MbbTimeframeFreshness> freshness,
        DateTime generatedAt)
    {
        // Every rule reads the latest 1d close: when that bar is stale no trade is proposed (contract §4, no forced
        // BUY/SELL without evidence). Rule reasons/triggers stay so the user sees what the old close touched; 1h is not read.
        if (TradingActions.Contains(action)
            && freshness.Any(f => f.Timeframe == EquityCandleTimeframes.Day && f.IsStale))
        {
            reasons = [.. reasons.Where(r => r != ReasonBuySizedAtNextCeiling), ReasonStaleDataBlockedPrefix + action];
            action = MbbDecisionActions.Wait;
            invalidation = null;
            buyMore = null;
        }
        return new(symbol, action, reasons, triggers, invalidation, position, buyMore,
            market, MbbEvidenceStatuses.RulesOnly, VietnamExecutionRules.PriceBasis,
            freshness, generatedAt);
    }

    private static string FormatLevel(decimal vnd) => $"{vnd:N0} VND";
}

/// <summary>Cash/budget validation failure with a stable API error code (EQX v2 A3).</summary>
public sealed class EquityCashValidationException : ArgumentException
{
    public EquityCashValidationException(string code, string message) : base(message) => Code = code;

    /// <summary>CASH_UNKNOWN | BUDGET_EXCEEDS_CASH.</summary>
    public string Code { get; }
}

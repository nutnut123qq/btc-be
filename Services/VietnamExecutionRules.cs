namespace Backend.Services;

/// <summary>Band classification of a candidate fill/trigger price vs the reference close.</summary>
public static class VnBandStates
{
    public const string WithinBand = "within-band";
    public const string OpenAboveCeiling = "open-above-ceiling";
    public const string OpenBelowFloor = "open-below-floor";
}

/// <summary>
/// HOSE cash-equity cost/constraint bundle. Declared defaults mirror
/// ai/vn_execution_model.py (VnEquityCostSpec) exactly — these are contract
/// constants, not parameters tuned on data.
/// </summary>
public sealed record VnEquityCostSpec
{
    public decimal BrokerFeeFraction { get; init; } = VietnamExecutionRules.DefaultBrokerFeeFraction;
    public decimal SellTaxFraction { get; init; } = VietnamExecutionRules.DefaultSellTaxFraction;
    public decimal PriceBandFraction { get; init; } = VietnamExecutionRules.DefaultPriceBandFraction;
    public int LotSize { get; init; } = VietnamExecutionRules.HoseLotSize;
    public int SettlementTradingDays { get; init; } = VietnamExecutionRules.DefaultSettlementTradingDays;
    public string PriceBasis { get; init; } = VietnamExecutionRules.PriceBasis;

    public void Validate()
    {
        if (BrokerFeeFraction < 0 || SellTaxFraction < 0)
            throw new ArgumentException("Fee/tax fractions cannot be negative.");
        if (PriceBandFraction <= 0 || PriceBandFraction >= 1)
            throw new ArgumentException("priceBandFraction must be in (0, 1).");
        if (LotSize < 1)
            throw new ArgumentException("lotSize must be at least one share.");
        if (SettlementTradingDays < 0)
            throw new ArgumentException("settlementTradingDays cannot be negative.");
    }
}

/// <summary>Buy-side cost breakdown at a given price (fee on notional, no buy tax).</summary>
public sealed record VnBuyQuote(int Shares, decimal Price, decimal NotionalVnd, decimal FeeVnd, decimal TotalCostVnd);

/// <summary>Sell-side proceeds breakdown (broker fee + 0.1% sell tax on gross).</summary>
public sealed record VnSellQuote(int Shares, decimal Price, decimal GrossVnd, decimal FeeVnd, decimal TaxVnd, decimal NetVnd);

/// <summary>
/// C# mirror of ai/vn_execution_model.py — HOSE execution semantics for MBB:
/// lot 100, ±7% price band around the previous session close (breach = reject,
/// never clamp), sell tax 0.1% of gross, broker fee default 0.15% per side,
/// T+2 trading-day settlement for shares and cash, no shorting.
///
/// Fills happen at the next expected session open per VietnamMarketCalendar
/// (never open+interval). Prices are on the "adjusted" basis of the
/// InstrumentCandles series — a declared limitation, stamped on outputs.
/// </summary>
public static class VietnamExecutionRules
{
    public const string PriceBasis = "adjusted";
    public const int HoseLotSize = 100;
    public const decimal DefaultPriceBandFraction = 0.07m;
    public const decimal DefaultBrokerFeeFraction = 0.0015m;  // 0.15% per side
    public const decimal DefaultSellTaxFraction = 0.001m;     // 0.1% of gross, sell side only
    public const int DefaultSettlementTradingDays = 2;
    public const string FillPolicy = "next-session-open";
    public const string BandBreachPolicy = "reject";

    public static readonly VnEquityCostSpec DefaultCosts = new();

    /// <summary>(floor, ceiling) of the HOSE ±7% band around the reference (prev session) close.</summary>
    public static (decimal Floor, decimal Ceiling) BandBounds(
        decimal referenceClose, decimal bandFraction = DefaultPriceBandFraction)
    {
        if (referenceClose <= 0) throw new ArgumentException("referenceClose must be positive.", nameof(referenceClose));
        return (referenceClose * (1m - bandFraction), referenceClose * (1m + bandFraction));
    }

    /// <summary>Classify a candidate execution price against the band. Breach = reject.</summary>
    public static string ClassifyBand(
        decimal price, decimal referenceClose, decimal bandFraction = DefaultPriceBandFraction)
    {
        var (floor, ceiling) = BandBounds(referenceClose, bandFraction);
        if (price > ceiling) return VnBandStates.OpenAboveCeiling;
        if (price < floor) return VnBandStates.OpenBelowFloor;
        return VnBandStates.WithinBand;
    }

    /// <summary>
    /// T+N settlement date counted in trading days after the trade date via the
    /// Vietnam calendar (weekends + configured non-trading dates skipped).
    /// </summary>
    public static DateOnly SettlementDate(
        VietnamMarketCalendar calendar, DateOnly tradeLocalDate,
        int tradingDays = DefaultSettlementTradingDays) =>
        calendar.AddTradingDays(tradeLocalDate, tradingDays);

    /// <summary>True when the lot's T+N settlement date is on/before <paramref name="asOfLocalDate"/>.</summary>
    public static bool IsSettled(
        VietnamMarketCalendar calendar, DateOnly acquireLocalDate, DateOnly asOfLocalDate,
        int tradingDays = DefaultSettlementTradingDays) =>
        SettlementDate(calendar, acquireLocalDate, tradingDays) <= asOfLocalDate;

    /// <summary>Shares from these lots that are settled (sellable) as of <paramref name="asOfLocalDate"/>.</summary>
    public static int SettledShares(
        VietnamMarketCalendar calendar,
        IEnumerable<(int Quantity, DateOnly AcquireDate)> lots,
        DateOnly asOfLocalDate,
        int tradingDays = DefaultSettlementTradingDays)
    {
        var settled = 0;
        foreach (var (qty, acquired) in lots)
            if (IsSettled(calendar, acquired, asOfLocalDate, tradingDays)) settled += qty;
        return settled;
    }

    /// <summary>Max whole-lot share count affordable from settled cash, buy fee included.</summary>
    public static int BuySharesForBudget(decimal settledCashVnd, decimal price, VnEquityCostSpec? costs = null)
    {
        costs ??= DefaultCosts;
        costs.Validate();
        if (price <= 0) throw new ArgumentException("price must be positive.", nameof(price));
        var perShareCost = price * (1m + costs.BrokerFeeFraction);
        if (perShareCost <= 0) return 0;
        var shares = (int)Math.Floor(settledCashVnd / perShareCost);
        if (shares < 0) shares = 0;
        return shares - (shares % costs.LotSize);
    }

    /// <summary>Cost breakdown for buying <paramref name="shares"/> at <paramref name="price"/>.</summary>
    public static VnBuyQuote QuoteBuy(int shares, decimal price, VnEquityCostSpec? costs = null)
    {
        costs ??= DefaultCosts;
        costs.Validate();
        if (shares < 0) throw new ArgumentException("shares cannot be negative.", nameof(shares));
        if (price <= 0 && shares > 0) throw new ArgumentException("price must be positive.", nameof(price));
        var notional = shares * price;
        var fee = notional * costs.BrokerFeeFraction;
        return new VnBuyQuote(shares, price, notional, fee, notional + fee);
    }

    /// <summary>Net proceeds of selling <paramref name="shares"/> at <paramref name="price"/> after fee + sell tax.</summary>
    public static VnSellQuote QuoteSell(int shares, decimal price, VnEquityCostSpec? costs = null)
    {
        costs ??= DefaultCosts;
        costs.Validate();
        if (shares < 0) throw new ArgumentException("shares cannot be negative.", nameof(shares));
        if (price <= 0 && shares > 0) throw new ArgumentException("price must be positive.", nameof(price));
        var gross = shares * price;
        var fee = gross * costs.BrokerFeeFraction;
        var tax = gross * costs.SellTaxFraction;
        return new VnSellQuote(shares, price, gross, fee, tax, gross - fee - tax);
    }

    /// <summary>
    /// Whether a sell at <paramref name="price"/> could fill given the band around
    /// <paramref name="referenceClose"/>. Returns (allowed, reason); reason mirrors
    /// the Python rejection strings (band state) or "no-position" guidance is left
    /// to callers — this method only checks the band.
    /// </summary>
    public static (bool Allowed, string? Reason) CanSellAt(
        decimal price, decimal referenceClose, decimal bandFraction = DefaultPriceBandFraction)
    {
        var band = ClassifyBand(price, referenceClose, bandFraction);
        return band == VnBandStates.WithinBand ? (true, null) : (false, band);
    }
}

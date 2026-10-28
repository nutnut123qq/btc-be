using System.ComponentModel.DataAnnotations;

namespace Backend.Services.Models;

// ---------------------------------------------------------------------------
// MBB-05 position + decision contract.
//
// IMPORTANT: request DTOs in this file intentionally carry NO `Symbol` property.
// The global ProductionSymbolScopeFilter inspects every action argument named
// `symbol`/`symbols` and every DTO with a settable `Symbol` property and would
// reject "MBB" (known but disabled). The MBB position/decision endpoints lock
// the symbol server-side instead.
// ---------------------------------------------------------------------------

public static class MbbDecisionActions
{
    public const string Hold = "hold";
    public const string Wait = "wait";
    public const string BuyMore = "buy-more";
    public const string Reduce = "reduce";
    public const string Sell = "sell";
    public const string ConditionalSell = "conditional-sell";
}

/// <summary>Evidence provenance of a decision. v1 is deterministic rules only —
/// no MBB model exists, so no probability is ever attached.</summary>
public static class MbbEvidenceStatuses
{
    public const string RulesOnly = "rules-only";
}

/// <summary>One lot as returned to clients, with derived settlement state.</summary>
public sealed record MbbPositionLotDto(
    [property: Required] long Id,
    [property: Required] int Quantity,
    [property: Required] decimal CostPerShareVnd,
    [property: Required] string AcquireDate,
    [property: Required] string SettleDate,
    [property: Required] bool Settled,
    [property: Required] string Source);

/// <summary>Where the cash figure of a position comes from (EQX v2 A3).</summary>
public static class EquityCashSources
{
    /// <summary>The shared EquityCashAccounts pool ("default") has been entered.</summary>
    public const string SharedPool = "shared-pool";

    /// <summary>The shared pool has never been entered: buying power is unknown.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Current manually tracked position (lots + cash).
/// EQX v2 A3: cash is ONE shared pool for all equity symbols. <c>AvailableCashVnd</c> is kept for
/// backward compatibility and now mirrors the shared pool (0 when unknown — check <c>CashSource</c>);
/// per-symbol buying power is the declared <c>BuyBudgetVnd</c>.
/// </summary>
public sealed record MbbPositionDto(
    [property: Required] string Symbol,
    [property: Required] int TotalShares,
    [property: Required] int SettledShares,
    [property: Required] int PendingShares,
    decimal? AvgCostPerShareVnd,
    [property: Required] decimal AvailableCashVnd,
    DateTime? UpdatedAtUtc,
    [property: Required] IReadOnlyList<MbbPositionLotDto> Lots,
    // EQX v2 A3 (additive).
    decimal? SharedCashVnd = null,
    decimal? BuyBudgetVnd = null,
    string CashSource = EquityCashSources.Unknown,
    decimal? TotalDeclaredBudgetsVnd = null,
    IReadOnlyList<string>? CashNotes = null);

/// <summary>
/// PUT /api/equities/cash body: the shared brokerage cash pool (VND, settled, spendable).
/// <c>availableCashVnd: null</c> (or omitted) resets the pool to unknown ⇒ decisions report cash-unknown.
/// </summary>
public sealed class UpdateEquityCashRequest
{
    public UpdateEquityCashRequest(decimal? availableCashVnd)
    {
        AvailableCashVnd = availableCashVnd;
    }

    public decimal? AvailableCashVnd { get; init; }
}

/// <summary>Per-symbol declared buy budget drawn from the shared pool.</summary>
public sealed record EquityBuyBudgetDto(
    [property: Required] string Symbol,
    decimal? BuyBudgetVnd);

/// <summary>Shared cash pool state (EQX v2 A3).</summary>
public sealed record EquityCashDto(
    [property: Required] string AccountId,
    decimal? AvailableCashVnd,
    DateTime? UpdatedAtUtc,
    [property: Required] decimal TotalDeclaredBudgetsVnd,
    [property: Required] bool BudgetsExceedCash,
    [property: Required] IReadOnlyList<EquityBuyBudgetDto> Budgets,
    [property: Required] IReadOnlyList<string> Notes);

// PUT bodies are classes, not positional records: MVC model validation throws
// InvalidOperationException ("validation metadata ... will be ignored") for a record whose
// [Required] sits on primary-constructor properties, which turned every PUT into a 500
// before AdminGuard even ran. Same JSON shape and OpenAPI required-ness as before.

/// <summary>
/// Full-replacement PUT body for the position. Idempotent by design.
/// EQX v2 A3: <c>buyBudgetVnd</c> (optional, null = undeclared ⇒ no buy quantity is proposed) is this
/// symbol's budget from the shared pool; Σ budgets must stay ≤ the pool. Legacy <c>availableCashVnd</c>
/// (optional now) is written to the SHARED pool — one pool, never per-symbol — and the response says so.
/// </summary>
public sealed class UpdateMbbPositionRequest
{
    public UpdateMbbPositionRequest(
        IReadOnlyList<UpdateMbbPositionLotItem> lots, decimal? availableCashVnd = null, decimal? buyBudgetVnd = null)
    {
        Lots = lots;
        AvailableCashVnd = availableCashVnd;
        BuyBudgetVnd = buyBudgetVnd;
    }

    [Required] public IReadOnlyList<UpdateMbbPositionLotItem> Lots { get; init; }
    public decimal? AvailableCashVnd { get; init; }
    public decimal? BuyBudgetVnd { get; init; }
}

/// <summary>One lot in a PUT body. AcquireDate is "yyyy-MM-dd" local VN trade date.</summary>
public sealed class UpdateMbbPositionLotItem
{
    public UpdateMbbPositionLotItem(int quantity, decimal costPerShareVnd, string acquireDate)
    {
        Quantity = quantity;
        CostPerShareVnd = costPerShareVnd;
        AcquireDate = acquireDate;
    }

    [Required] public int Quantity { get; init; }
    [Required] public decimal CostPerShareVnd { get; init; }
    [Required] public string AcquireDate { get; init; }
}

/// <summary>
/// Machine-evaluable direction of a level trigger, when it has one.
/// "close-below" fires when a finalized bar on the trigger timeframe closes
/// at/below PriceLevelVnd; "close-above" is the mirror. Null for triggers that
/// are execution notes rather than level conditions.
/// </summary>
public static class MbbTriggerDirections
{
    public const string CloseBelow = "close-below";
    public const string CloseAbove = "close-above";
}

/// <summary>A declared trigger the decision is conditioned on.</summary>
public sealed record MbbDecisionTrigger(
    [property: Required] string Name,
    [property: Required] string Timeframe,
    decimal? PriceLevelVnd,
    [property: Required] string Condition,
    bool? WithinPriceBand,
    string? NonFillRisk,
    string? Direction);

/// <summary>Position snapshot embedded in a decision payload.</summary>
public sealed record MbbPositionSummary(
    [property: Required] int TotalShares,
    [property: Required] int SettledShares,
    [property: Required] int PendingShares,
    [property: Required] int SellableSharesNow,
    decimal? AvgCostPerShareVnd,
    decimal? LastPriceVnd,
    decimal? UnrealizedPnlVnd,
    double? UnrealizedPnlPct,
    decimal? NetProceedsIfSellableNowVnd,
    // EQX §6 (additive): false when a corporate action went ex after the earliest lot, so the
    // VCI-adjusted price is not comparable with the real (unconverted) cost basis; then every
    // cost-vs-price figure is null and no cost-derived trigger is emitted. Note explains why.
    bool CostBasisComparable = true,
    string? CostBasisNote = null,
    // EQX v2 A3 (additive): how each corporate action since the earliest lot was handled. Shares and
    // average cost above are the CONVERTED figures when every action converted.
    IReadOnlyList<MbbCorporateActionApplication>? CorporateActions = null);

/// <summary>Handling status of one corporate action for the position (EQX v2 A3).</summary>
public static class MbbCorporateActionStatuses
{
    /// <summary>Verified stock-dividend / bonus-shares / split with ShareRatio: lots converted.</summary>
    public const string Converted = "converted";

    /// <summary>Cash dividend: no share/cost change (the cash received is not included in P&amp;L).</summary>
    public const string NoShareChange = "no-share-change";

    /// <summary>Rights issue / other / unverified / ratio missing: P&amp;L, proceeds and cost-based levels withheld.</summary>
    public const string Unconverted = "unconverted";
}

public sealed record MbbCorporateActionApplication(
    [property: Required] string ExDate,
    [property: Required] string ActionType,
    [property: Required] string VerificationStatus,
    decimal? ShareRatio,
    [property: Required] string Status,
    [property: Required] int AffectedLots,
    string? Reason);

/// <summary>Cash inputs of a decision (EQX v2 A3).</summary>
public sealed record MbbCashContext(
    decimal? SharedCashVnd,
    decimal? BuyBudgetVnd,
    [property: Required] decimal TotalDeclaredBudgetsVnd,
    [property: Required] string CashSource,
    decimal? SpendableVnd);

/// <summary>
/// Buy capacity under HOSE lot/fee rules, populated on buy-more. Since MBBSIZE-1 (reason
/// buy-sized-at-next-session-ceiling) shares and EstimatedCostVnd are priced at the next session's
/// ceiling, so EstimatedCostVnd is an upper bound, not an estimate at the last close.
/// </summary>
public sealed record MbbBuyMoreCapacity(
    [property: Required] int MaxBuyableShares,
    [property: Required] int MaxBuyableLots,
    [property: Required] decimal EstimatedCostVnd);

/// <summary>Market context snapshot (1d primary, 1h timing reference).</summary>
public sealed record MbbMarketContext(
    [property: Required] string Trend,
    [property: Required] string RsiZone,
    double? Rsi14,
    double? Atr14,
    decimal? Sma50,
    decimal? Ema50,
    decimal? LastDailyCloseVnd,
    decimal? PreviousDailyCloseVnd,
    decimal? LatestHourlyCloseVnd,
    decimal? CeilingPriceVnd,
    decimal? FloorPriceVnd);

/// <summary>Per-timeframe freshness: latest stored bar vs latest expected finalized open.</summary>
public sealed record MbbTimeframeFreshness(
    [property: Required] string Timeframe,
    long? LatestOpenTimeMs,
    long? LatestExpectedFinalizedOpenMs,
    [property: Required] bool IsStale,
    // EQX v2 review #1 (additive): e.g. "calendar-coverage-expired" — today is after the HOSE calendar
    // coverage, so the expected bar is unknown and the timeframe is reported stale (fail closed).
    string? Reason = null);

/// <summary>
/// MBB position decision. `action` is position-relative (sell never shorts;
/// buy-more never exceeds cash). `evidenceStatus` is "rules-only" in v1 —
/// no MBB model exists, so no probability is attached, honestly.
/// </summary>
public sealed record MbbDecisionDto(
    [property: Required] string Symbol,
    [property: Required] string Action,
    [property: Required] IReadOnlyList<string> Reasons,
    [property: Required] IReadOnlyList<MbbDecisionTrigger> Triggers,
    string? Invalidation,
    [property: Required] MbbPositionSummary Position,
    MbbBuyMoreCapacity? BuyMore,
    [property: Required] MbbMarketContext Market,
    [property: Required] string EvidenceStatus,
    [property: Required] string PriceBasis,
    [property: Required] IReadOnlyList<MbbTimeframeFreshness> DataFreshness,
    [property: Required] DateTime GeneratedAtUtc,
    MbbCashContext? Cash = null);

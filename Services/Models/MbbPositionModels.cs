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

/// <summary>Current manually tracked MBB position (lots + cash).</summary>
public sealed record MbbPositionDto(
    [property: Required] string Symbol,
    [property: Required] int TotalShares,
    [property: Required] int SettledShares,
    [property: Required] int PendingShares,
    decimal? AvgCostPerShareVnd,
    [property: Required] decimal AvailableCashVnd,
    DateTime? UpdatedAtUtc,
    [property: Required] IReadOnlyList<MbbPositionLotDto> Lots);

/// <summary>Full-replacement PUT body for the MBB position. Idempotent by design.</summary>
public sealed record UpdateMbbPositionRequest(
    [property: Required] IReadOnlyList<UpdateMbbPositionLotItem> Lots,
    [property: Required] decimal AvailableCashVnd);

/// <summary>One lot in a PUT body. AcquireDate is "yyyy-MM-dd" local VN trade date.</summary>
public sealed record UpdateMbbPositionLotItem(
    [property: Required] int Quantity,
    [property: Required] decimal CostPerShareVnd,
    [property: Required] string AcquireDate);

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
    decimal? NetProceedsIfSellableNowVnd);

/// <summary>Buy capacity under HOSE lot/fee rules, populated on buy-more.</summary>
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
    [property: Required] bool IsStale);

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
    [property: Required] DateTime GeneratedAtUtc);

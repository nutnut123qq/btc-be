namespace Backend.Services.Models;

public static class GapLedgerStatuses
{
    public const string Reconciled = "Reconciled";
    public const string LiveFallback = "LiveFallback";
}

public record DataAuditResponse(
    string Symbol,
    DateTime GeneratedAtUtc,
    IReadOnlyList<TimeframeAudit> Timeframes,
    NewsAudit News,
    RulesAlertsAudit RulesAlerts,
    DerivativesAudit? Derivatives = null);

public record TimeframeAudit(
    string Timeframe,
    long TotalKlines,
    long? MinOpenTimeMs,
    long? MaxOpenTimeMs,
    long? ExpectedBars,
    long MissingBars,
    long GapRangeCount,
    double DataCoveragePct,
    long LargestGapMs,
    long PendingGapCount,
    long UnavailableGapCount,
    long? LatestCandleAgeSeconds,
    string GapLedgerStatus,
    long? CandlePatterns,
    long? TechnicalIndicators,
    long? WindowVectors,
    long? MlFeatureStores,
    long? PriceTargets,
    long? WindowClassificationDatasets,
    IReadOnlyList<CandleGap> TopGaps,
    bool Active = true,
    KlineQualityAudit? Quality = null,
    IReadOnlyList<DerivedTableAudit>? DerivedTables = null);

public record KlineQualityAudit(
    long FinalizedRows,
    long FormingRows,
    long InvalidOhlcvRows,
    long DuplicateOpenTimeRows,
    long? LatestFinalizedCloseTimeMs,
    long? LatestFinalizedAgeSeconds,
    bool IsStale);

public record DerivedTableAudit(
    string Table,
    long Rows,
    long? LatestSourceTimeMs,
    long? LatestAgeSeconds,
    bool ExpectedOnePerFinalizedBar,
    long? MissingRows);

public record DerivativesAudit(
    FuturesMetricQuality FuturesMetrics,
    IReadOnlyList<MarketMetricQuality> MarketMetrics,
    string AvailabilityCaveat);

public record FuturesMetricQuality(
    long Rows,
    long DuplicateOpenTimeRows,
    long? LatestOpenTimeMs,
    long? LatestAgeSeconds,
    long MissingOpenInterest,
    long MissingLongShortRatio,
    long MissingTakerRatio,
    long MissingFundingRate,
    long MissingMarkPrice,
    DerivativeLineageQuality? Lineage = null);

public record MarketMetricQuality(
    string Timeframe,
    long Rows,
    long DuplicateOpenTimeRows,
    long? LatestOpenTimeMs,
    long? LatestAgeSeconds,
    long MissingFundingRate,
    long MissingOpenInterest,
    long MissingLongShortRatio,
    long MissingLiquidations,
    DerivativeLineageQuality? Lineage = null);

public record DerivativeLineageQuality(
    long CompleteRows,
    long MissingSourceEventTime,
    long MissingReceivedAt,
    long MissingAvailableAt,
    long MissingSource,
    long MissingMarketType,
    long ReconstructedRows,
    long AsOfEligibleRows);

public record CandleGap(
    long? Id,
    long StartOpenTimeMs,
    long EndOpenTimeMs,
    long MissingBars,
    string? Status,
    int AttemptCount,
    DateTime? NextRetryAtUtc,
    string? Reason);

public record NewsAudit(
    long Articles,
    long Chunks,
    DateTimeOffset? MinDate,
    DateTimeOffset? MaxDate);

public record RulesAlertsAudit(
    long Rules,
    long Signals,
    long Alerts);

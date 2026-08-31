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
    RulesAlertsAudit RulesAlerts);

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
    IReadOnlyList<CandleGap> TopGaps);

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

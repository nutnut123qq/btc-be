namespace Backend.Services.Models;

public record DataAuditResponse(
    string Symbol,
    DateTime GeneratedAtUtc,
    IReadOnlyDictionary<string, TimeframeAudit> Timeframes,
    NewsAudit News,
    RulesAlertsAudit RulesAlerts);

public record TimeframeAudit(
    long KlinesCount,
    long? MinOpenTimeMs,
    long? MaxOpenTimeMs,
    long? ExpectedCount,
    long GapCount,
    long CandlePatternsCount,
    long TechnicalIndicatorsCount,
    long WindowVectorsCount,
    long MlFeatureStoresCount,
    long PriceTargetsCount,
    long WindowClassificationDatasetsCount,
    IReadOnlyList<CandleGap> Gaps);

public record CandleGap(
    long StartMs,
    long EndMs,
    long MissingCount);

public record NewsAudit(
    long Articles,
    long Chunks,
    DateTimeOffset? MinDate,
    DateTimeOffset? MaxDate);

public record RulesAlertsAudit(
    long Rules,
    long Signals,
    long Alerts);

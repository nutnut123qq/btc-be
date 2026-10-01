using System.ComponentModel.DataAnnotations;

namespace Backend.Services.Models;

public static class KlineDataQualityTaxonomy
{
    public const string Version = "btc-kline-data-quality-v1";
    public static class IssueTypes
    {
        public const string MissingInterval = "missing_interval";
        public const string InvalidDuration = "invalid_duration";
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            MissingInterval, InvalidDuration
        };
    }
    public static class ResolutionStates
    {
        public const string Open = "open";
        public const string RetryScheduled = "retry_scheduled";
        public const string SourceUnavailable = "source_unavailable";
        public const string Repaired = "repaired";
    }
    public static class SourceClassifications
    {
        public const string NotChecked = "not_checked";
        public const string BinanceSpotVerified = "binance_spot_verified";
        public const string BinanceSpotEmpty = "binance_spot_empty";
        public const string BinanceSpotRejected = "binance_spot_rejected";
        public const string BinanceSpotError = "binance_spot_error";
    }
    public static readonly IReadOnlyList<string> AffectedDownstreamArtifacts =
    [
        "CandlePatterns", "CandleVolumeStats", "TechnicalIndicators", "WindowVectors",
        "MlFeatureStores", "PriceTargets", "WindowClassificationDatasets", "PatternSequences",
        "SmartMoneyStructures (legacy isolated cache)", "CausalSmartMoneyEvents", "TechnicalEvidenceRecords"
    ];
}

public sealed record KlineIssueEvidenceDto(
    [property: Required] string DetectionMethod,
    [property: Required] string SourceClassification,
    [property: Required] string AuthoritativeRepairSource,
    int SourceAttemptCount,
    DateTime? LastSourceAttemptAtUtc,
    DateTime? NextSourceRetryAtUtc,
    string? Detail);

public sealed record KlineDataIssueDto(
    [property: Required] string IssueKey,
    [property: Required] string Symbol,
    [property: Required] string Timeframe,
    [property: Required] string IssueType,
    [property: Required] string CauseCode,
    [property: Required] string ResolutionState,
    long StartOpenTimeMs,
    long EndOpenTimeMs,
    long AffectedBars,
    long ExpectedDurationMs,
    long? ActualDurationMs,
    DateTime? FirstDetectedAtUtc,
    DateTime? UpdatedAtUtc,
    bool Repairable,
    [property: Required] KlineIssueEvidenceDto Evidence,
    [property: Required] IReadOnlyList<string> AffectedDownstreamArtifacts);

public sealed record KlineRepairAuditDto(
    long Id,
    [property: Required] string PlanSha256,
    [property: Required] string SourceEvidenceSha256,
    [property: Required] string IssueType,
    long StartOpenTimeMs,
    long EndOpenTimeMs,
    int RequestedBars,
    int VerifiedSourceBars,
    int InsertedBars,
    int ReplacedBars,
    int NoopBars,
    int UnresolvedBars,
    [property: Required] string SourceClassification,
    DateTime SourceCheckedAtUtc,
    DateTime AppliedAtUtc);

public sealed record KlineDataIssuesResponse(
    [property: Required] string TaxonomyVersion,
    [property: Required] string Symbol,
    [property: Required] string Timeframe,
    DateTime GeneratedAtUtc,
    long AuditEndOpenTimeMs,
    long TotalKnownIssues,
    bool Truncated,
    [property: Required] IReadOnlyList<KlineDataIssueDto> Issues,
    [property: Required] IReadOnlyList<KlineRepairAuditDto> RecentRepairs,
    [property: Required] IReadOnlyList<string> Limitations);

public sealed class KlineDataRepairRequest
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    [Required] public string IssueType { get; set; } = "";
    public long StartOpenTimeMs { get; set; }
    public long EndOpenTimeMs { get; set; }
    public bool DryRun { get; set; } = true;
    public string? ExpectedPlanSha256 { get; set; }
}

public sealed record KlineDataRepairResponse(
    [property: Required] string TaxonomyVersion,
    [property: Required] string Symbol,
    [property: Required] string Timeframe,
    [property: Required] string IssueType,
    bool DryRun,
    bool Applied,
    bool AlreadyApplied,
    long StartOpenTimeMs,
    long EndOpenTimeMs,
    int RequestedBars,
    int SourceRows,
    int VerifiedSourceBars,
    int InsertedBars,
    int ReplacedBars,
    int NoopBars,
    [property: Required] IReadOnlyList<long> UnresolvedOpenTimeMs,
    [property: Required] string SourceClassification,
    [property: Required] string SourceEndpoint,
    DateTime SourceCheckedAtUtc,
    [property: Required] string SourceEvidenceSha256,
    [property: Required] string PlanSha256,
    long? RepairAuditId,
    bool DerivedRebuildRequired,
    [property: Required] IReadOnlyList<string> AffectedDownstreamArtifacts,
    [property: Required] IReadOnlyList<string> Limitations);

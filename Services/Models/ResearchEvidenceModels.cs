using Backend.Data;
using System.ComponentModel.DataAnnotations;

namespace Backend.Services.Models;

public static class ResearchEvidenceStatuses
{
    public const string Supported = "supported";
    public const string Inconclusive = "inconclusive";
    public const string Unavailable = "unavailable";
    public const string IntegrityLimited = "integrity-limited";
}

public sealed record EvidenceIntegritySummaryDto(
    int ScannedArtifactCount,
    int PublishedArtifactCount,
    int RejectedArtifactCount);

public sealed record EvidenceIntegrityDto(
    bool Verified,
    bool ManifestHashVerified,
    bool ReportHashVerified,
    bool ReportHashEmbedded,
    [property: Required] string VerificationMode);

public sealed record ResearchEvidenceCatalogItemDto(
    [property: Required] string Id,
    [property: Required] string Kind,
    [property: Required] string Title,
    [property: Required] string Status,
    [property: Required] string EvidenceTier,
    [property: Required] string Symbol,
    [property: Required] string Timeframe,
    DateTime? CreatedAtUtc,
    [property: Required] string ManifestSha256,
    [property: Required] string ReportSha256,
    [property: Required] string Summary,
    [property: Required] IReadOnlyList<string> Limitations,
    [property: Required] EvidenceIntegrityDto Integrity);

public sealed record ResearchEvidenceCatalogResponse(
    [property: Required] string ContractVersion,
    [property: Required] string Symbol,
    [property: Required] DateTime GeneratedAtUtc,
    [property: Required] IReadOnlyList<ResearchEvidenceCatalogItemDto> Items,
    [property: Required] EvidenceIntegritySummaryDto Integrity)
{
    public static ResearchEvidenceCatalogResponse Create(
        DateTime generatedAtUtc,
        IReadOnlyList<ResearchEvidenceCatalogItemDto> items,
        EvidenceIntegritySummaryDto integrity) =>
        new(ResearchVersions.ResearchEvidenceCatalog, "BTCUSDT", generatedAtUtc, items, integrity);
}

public sealed record ResearchEvidenceDatasetDto(
    [property: Required] string Source,
    long? RowCount,
    long? FirstDecisionTimeMs,
    long? LastDecisionTimeMs,
    string? DatasetSha256);

public sealed record ResearchEvidenceProtocolDto(
    string? EvaluatorVersion,
    string? DecisionTime,
    string? OutcomePriceBasis,
    bool? ChronologicalOos,
    string? MultipleTesting);

public sealed record ResearchEvidenceBaselineDto(
    [property: Required] string Id,
    [property: Required] string Description);

public sealed record ResearchEvidenceMetricDto(
    [property: Required] string Name,
    [property: Required] string Label,
    double? Value,
    [property: Required] string Unit,
    string? Baseline,
    string? Interpretation = null);

public sealed record ResearchEvidenceFindingDto(
    [property: Required] string Id,
    [property: Required] string Label,
    [property: Required] string Status,
    [property: Required] string MetricName,
    double? Value,
    double? Lower,
    double? Upper,
    long? SampleSize);

public sealed record ResearchEvidenceUncertaintyDto(
    [property: Required] string Name,
    double? Lower,
    double? Upper,
    double? ConfidenceLevel,
    bool Familywise);

public sealed record ResearchEvidenceCoverageDto(
    long? EvaluatedRows,
    long? EligibleRows,
    double? Ratio,
    int? FoldCount);

public sealed record ResearchEvidenceProvenanceDto(
    string? ContractVersion,
    string? Experiment,
    string? EvaluatorSha256,
    string? ResearchContractSha256,
    string? GitCommit,
    bool? GitDirty);

public sealed record ResearchEvidenceArtifactDto(
    [property: Required] string Role,
    [property: Required] string Sha256,
    long Bytes,
    long? RowCount);

public sealed record ResearchEvidenceDetailDto(
    [property: Required] string Id,
    [property: Required] string Kind,
    [property: Required] string Title,
    [property: Required] string Status,
    [property: Required] string EvidenceTier,
    [property: Required] string Symbol,
    [property: Required] string Timeframe,
    DateTime? CreatedAtUtc,
    [property: Required] string ManifestSha256,
    [property: Required] string ReportSha256,
    [property: Required] string Summary,
    [property: Required] string Hypothesis,
    [property: Required] ResearchEvidenceDatasetDto Dataset,
    [property: Required] ResearchEvidenceProtocolDto Protocol,
    [property: Required] IReadOnlyList<ResearchEvidenceBaselineDto> Baselines,
    [property: Required] IReadOnlyList<ResearchEvidenceMetricDto> Metrics,
    [property: Required] IReadOnlyList<ResearchEvidenceFindingDto> Findings,
    [property: Required] IReadOnlyList<ResearchEvidenceUncertaintyDto> Uncertainty,
    [property: Required] ResearchEvidenceCoverageDto Coverage,
    [property: Required] string Conclusion,
    [property: Required] IReadOnlyList<string> Limitations,
    [property: Required] ResearchEvidenceProvenanceDto Provenance,
    [property: Required] IReadOnlyList<ResearchEvidenceArtifactDto> Artifacts,
    [property: Required] EvidenceIntegrityDto Integrity);

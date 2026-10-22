using System.Text.Json;

namespace Backend.Services.Models;

// EQX v3 — document pipeline runs, fundamentals coverage grid, financial facts, and the additive
// snapshot sections (spec-v3-document-pipeline.md §4). All public DTOs are market-only and carry no
// secrets or absolute paths (run logs are scrubbed before they are stored).

public static class EquityDocumentPipelineContracts
{
    public const string PipelineRunsVersion = "equity-pipeline-runs-v1";
    public const string CoverageVersion = "equity-fundamentals-coverage-v1";
    public const string FactsVersion = "equity-financial-facts-v1";

    public const string PipelineBusy = "PIPELINE_BUSY";
    public const string PipelineUnavailable = "PIPELINE_UNAVAILABLE";
    public const string InvalidPipelineRequest = "INVALID_PIPELINE_REQUEST";
    public const string RunNotFound = "PIPELINE_RUN_NOT_FOUND";
    public const string RunNotRunning = "PIPELINE_RUN_NOT_RUNNING";
    public const string RunNotOwned = "PIPELINE_RUN_NOT_OWNED";
    public const string InvalidFactsQuery = "INVALID_FACTS_QUERY";
}

/// <summary>Coverage stages, lowest → highest (spec v3 §4).</summary>
public static class EquityCoverageStages
{
    public const string NotAttempted = "not-attempted";
    public const string NotListed = "not-listed";
    public const string Discovered = "discovered";
    public const string Downloaded = "downloaded";
    public const string Extracted = "extracted";
    /// <summary>At least one validated fact in the cell, but not every expected metric of the group.</summary>
    public const string Validated = "validated";
    /// <summary>Every expected metric of the group (metric catalog) has a validated fact persisted.</summary>
    public const string Persisted = "persisted";

    public static readonly string[] Ordered = [NotAttempted, NotListed, Discovered, Downloaded, Extracted, Validated, Persisted];
}

// --- pipeline runs ----------------------------------------------------------------------

public sealed record EquityPipelineRunDto(
    Guid RunId,
    string Symbol,
    string Mode,
    string Trigger,
    string Status,
    DateTime StartedAtUtc,
    DateTime HeartbeatAtUtc,
    DateTime? FinishedAtUtc,
    JsonElement? Counts,
    JsonElement? Checkpoint,
    string? Error,
    string? LogTail,
    bool SupervisedByThisProcess,
    // --extract-scope the run was launched with (CheckpointJson.launch.extractScope): consolidated | all.
    string? ExtractScope = null);

public sealed record EquityPipelineRunListResponse(
    string ContractVersion, string Symbol, IReadOnlyList<EquityPipelineRunDto> Items);

// --- coverage --------------------------------------------------------------------------

public sealed record EquityCoverageCatalogInfo(
    string Status, string? Reason, string? Version, string? Sha256, int MetricCount, IReadOnlyList<string> CoreMetrics, bool CoreMetricsFromCatalog);

public sealed record EquityCoverageCellCounts(
    int Documents,
    int Extractions,
    int Facts,
    int Validated,
    int Extracted,
    int ReviewNeeded,
    int Quarantined,
    int Derived,
    int ExpectedMetrics,
    int ValidatedMetrics);

/// <summary>
/// One (period × metric group × consolidation) cell. Stage = highest stage reached; Reason explains the
/// blocker; AttemptId/DocumentId point at the evidence (latest attempt / best document for the cell).
/// MissingMetrics: expected catalog metrics without a validated fact (only when the cell has documents/facts).
/// </summary>
public sealed record EquityCoverageCell(
    string Period,
    string PeriodKind,
    string MetricGroup,
    string Consolidation,
    string Stage,
    string? Reason,
    long? AttemptId,
    string? AttemptOutcome,
    Guid? DocumentId,
    Guid? ExtractionId,
    EquityCoverageCellCounts Counts,
    IReadOnlyList<string> MissingMetrics,
    // Extractor version of the current extraction behind the cell (null when nothing was extracted).
    string? ExtractorVersion = null,
    // own-report | comparative-only (every counted fact is a comparative column of a later report) | null (no facts).
    string? Source = null,
    // comparative-only: the later documents whose comparative columns supply the cell.
    IReadOnlyList<Guid>? SourceDocumentIds = null);

public sealed record EquityCoverageSummary(
    int Cells,
    IReadOnlyDictionary<string, int> ByStage,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ByPeriodKind,
    int Documents,
    int Extractions,
    int Attempts,
    int Facts,
    int ValidatedFacts,
    int QuarantinedFacts,
    int ReviewNeededFacts,
    // Cells whose facts come ONLY from comparative columns of later reports (no own-period report behind them).
    int ComparativeOnlyCells = 0,
    IReadOnlyDictionary<string, int>? ComparativeOnlyByStage = null);

public sealed record EquityFundamentalsCoverageResponse(
    string ContractVersion,
    string Symbol,
    DateTime AsOfUtc,
    string FirstPeriod,
    string LastPeriod,
    EquityCoverageCatalogInfo Catalog,
    EquityCoverageSummary Summary,
    IReadOnlyList<EquityCoverageCell> Cells,
    IReadOnlyList<string> Rules);

// --- financial facts -------------------------------------------------------------------

public sealed record EquityFactDocumentRef(
    Guid DocumentId,
    string Url,
    string Sha256,
    string Title,
    string DocType,
    string? ReportingPeriod,
    int Revision,
    string Provider,
    DateTime AvailableAtUtc,
    string AvailabilityBasis);

public sealed record EquityFactExtractionRef(
    Guid ExtractionId, string ExtractorVersion, string Method, string Status, int PageCount, int PagesOcr, DateTime FinishedAtUtc);

public sealed record EquityFinancialFactDto(
    string FactKey,
    string MetricCode,
    string MetricDefinitionVersion,
    string MetricGroup,
    string Statement,
    string PeriodLabel,
    string PeriodType,
    string? PeriodStart,
    string PeriodEnd,
    int FiscalYear,
    string Consolidation,
    decimal Value,
    string Unit,
    decimal Scale,
    string? RawLabel,
    string? RawValueText,
    string? RawUnitText,
    string ColumnRole,
    int? Page,
    string? Location,
    string Status,
    JsonElement? Validation,
    bool IsDerived,
    JsonElement? Derivation,
    DateTime AvailableAtUtc,
    EquityFactDocumentRef? Document,
    EquityFactExtractionRef? Extraction,
    // true when this is the fact the snapshot series uses for its (metric, periodLabel, periodType, consolidation) at asOf.
    bool Selected = false,
    /// latest-available-validated | superseded-by-later-report | superseded-by-later-available-validated | superseded-extraction | not-validated
    string? Selection = null,
    // Value × Scale, exact, in Unit (value is as printed, e.g. in "triệu đồng" with scale 1000000).
    decimal? NormalizedValue = null);

public sealed record EquityFinancialFactsResponse(
    string ContractVersion,
    string Symbol,
    DateTime AsOfUtc,
    string StatusFilter,
    IReadOnlyList<string> Metrics,
    string? PeriodType,
    string? From,
    string? To,
    int Count,
    bool Truncated,
    IReadOnlyList<EquityFinancialFactDto> Items,
    IReadOnlyList<string> Rules);

// --- snapshot additions (equity-research-snapshot-v1, additive) ------------------------

/// <summary>One validated fact point in the snapshot series (compact).</summary>
public sealed record EquityFundamentalsSeriesPoint(
    string Period,
    string PeriodType,
    string PeriodEnd,
    string Consolidation,
    decimal Value,
    string Unit,
    string FactKey,
    Guid DocumentId,
    int? Page,
    bool IsDerived,
    DateTime? AvailableAtUtc = null,
    // value is as printed; normalizedValue = value × scale, in Unit (e.g. VND). Use normalizedValue for magnitudes.
    decimal? Scale = null,
    decimal? NormalizedValue = null,
    // current | cumulative-current | comparative | cumulative-comparative | derived (comparative = printed in a later report).
    string? ColumnRole = null,
    // latest-available-validated | superseded-by-later-report (value/factKey come from the superseding fact).
    string? Selection = null,
    // superseded-by-later-report: the slot's own (original) fact, the superseding fact key(s), the reason, and the
    // period type of the superseding fact (e.g. annual filling a 12-month ytd slot).
    string? SupersededFactKey = null,
    IReadOnlyList<string>? SupersededBy = null,
    string? SupersessionReason = null,
    string? SourcePeriodType = null);

public sealed record EquityFundamentalsSeriesMetric(
    string MetricCode, string MetricGroup, IReadOnlyList<EquityFundamentalsSeriesPoint> Points);

/// <summary>
/// Validated facts visible at asOf (fact.AvailableAtUtc ≤ asOf AND its document available at asOf). One point per
/// (metric, period, periodType, consolidation): the validated fact with the LATEST AvailableAtUtc (spec v3 §6).
/// Status is the CURRENT validation status (a later quarantine is not reconstructible as-of).
/// </summary>
public sealed record EquityFundamentalsSeriesSection(
    string Status,
    string? Reason,
    int FactCount,
    int DuplicatesDropped,
    IReadOnlyList<EquityFundamentalsSeriesMetric> Metrics,
    IReadOnlyList<string> Rules);

public sealed record EquityFundamentalsCoverageGap(
    string Period, string PeriodKind, string MetricGroup, string Consolidation, string Stage, string? Reason,
    long? AttemptId, Guid? DocumentId, IReadOnlyList<string> MissingMetrics, string? Source = null);

public sealed record EquityFundamentalsCoverageSection(
    string FirstPeriod,
    string LastPeriod,
    EquityCoverageCatalogInfo Catalog,
    EquityCoverageSummary Summary,
    int GapCount,
    bool GapsTruncated,
    IReadOnlyList<EquityFundamentalsCoverageGap> Gaps,
    IReadOnlyList<string> Rules);

/// <summary>Current extraction of a document visible at asOf (latest successful known now; not gated by FinishedAtUtc) + its fact counts.</summary>
public sealed record EquityDocumentExtractionStatusDto(
    string Status, string? Method, string? ExtractorVersion, int? PageCount, int? PagesOcr, int Facts, int ValidatedFacts);

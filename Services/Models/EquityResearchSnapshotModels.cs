using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Services.Models;

// EQX v2 §B / §C1 / §C2 — research readiness, PIT snapshot, source documents and the session
// research journal. Public snapshot/readiness/source-document DTOs are market-only: they never
// carry position, lots or cash (portfolio-action readiness answers "requires-admin").

public static class EquityResearchContracts
{
    public const string SnapshotVersion = "equity-research-snapshot-v1";
    public const string ReadinessVersion = "equity-research-readiness-v1";
    // v2 (2026-10-21): discovery attempts count only when attemptedAtUtc <= asOf (a later poll no longer rewrites a
    // past snapshot/hash); latestExpectedOpenTimeMsAtAsOf is null when asOf is beyond calendar coverage and
    // readiness freshness compares against it. Same data ⇒ different id than v1; notes keep the v1 id they were verified with.
    public const string SnapshotMethodVersion = "equity-research-snapshot-method-v2";
    public const string NoteBodyVersion = "equity-research-note-v1";
    public const string NotesVersion = "equity-research-notes-v1";
    public const string SourceDocumentsVersion = "equity-source-documents-v1";

    /// <summary>Canonical series line: openTimeMs,closeTimeMs,open,high,low,close,volume (normalized decimals).</summary>
    public const string SeriesFingerprintMethod = "sha256-lf-joined-csv-normalized-decimal-v1";
}

public static class EquityReadinessStatuses
{
    public const string Ready = "ready";
    public const string Partial = "partial";
    public const string NotReady = "not-ready";
    public const string RequiresAdmin = "requires-admin";
}

public static class EquityReadinessCheckStatuses
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

// --- snapshot -------------------------------------------------------------------------

public sealed record EquityResearchSnapshot(
    string ContractVersion,
    string SnapshotId,
    string SnapshotSha256,
    string Symbol,
    DateTime RequestedAsOf,
    long RequestedAsOfMs,
    long EffectiveCutoffMs,
    DateTime GeneratedAt,
    string MethodVersion,
    bool AsOfIsLatest,
    EquitySnapshotBarDto? SignalBar,
    long? LatestExpectedOpenTimeMsAtAsOf,
    string PriceBasis,
    string Provider,
    EquitySnapshotCalendar Calendar,
    EquitySnapshotPit Pit,
    IReadOnlyDictionary<string, EquitySnapshotDatasetDto> Dataset,
    IReadOnlyList<EquitySnapshotCoverageDto> Coverage,
    EquitySnapshotConditions Conditions,
    IReadOnlyList<EquitySnapshotSeriesDto> Series,
    EquityFundamentalsSection Fundamentals,
    EquityCorporateActionsSection CorporateActions,
    EquityEventsSection Events,
    EquityRelativeStrengthSection RelativeStrength,
    IReadOnlyList<string> ContextLimitations,
    EquityBaseRatesResponse BaseRates,
    IReadOnlyList<EquitySourceDocumentRefDto> SourceDocuments,
    IReadOnlyList<EquitySnapshotUnavailable> Unavailable,
    IReadOnlyList<string> HowToGoDeeper,
    // EQX v3 §4 (additive): validated financial facts visible at asOf + document-pipeline coverage.
    EquityFundamentalsSeriesSection? FundamentalsSeries = null,
    EquityFundamentalsCoverageSection? FundamentalsCoverage = null);

public sealed record EquitySnapshotBarDto(
    long OpenTimeMs, long CloseTimeMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>
/// Which HOSE calendar the backend used: MarketCalendarDays (synced from the checked-in file) read
/// inside the snapshot transaction, compared date-by-date with the in-memory VietnamMarketCalendar
/// that drives coverage/gaps. Sha256 = SHA-256 of the sorted ISO dates joined by LF — the same value
/// ai/vietnam_market_calendar.MarketCalendar.provenance() writes into base-rates/replay artifacts.
/// Status: ok | mismatch | coverage-expired (cutoff or the session after the signal bar lies after
/// CoverageTo, so session status is unknown) | unavailable. BarsBeforeCoverage counts stored bars <= cutoff
/// that predate CoverageFrom (missing expected opens there may be unlisted holidays).
/// </summary>
public sealed record EquitySnapshotCalendar(
    string Status,
    string? Reason,
    string Venue,
    string Source,
    IReadOnlyList<string> Versions,
    int Count,
    int VerifiedCount,
    int UnverifiedLegacyCount,
    string? FirstDate,
    string? LastDate,
    string? Sha256,
    bool? MatchesInMemoryCalendar,
    string UsedFor,
    string? CoverageFrom = null,
    string? CoverageTo = null,
    string? NextSessionCheckDate = null,
    IReadOnlyDictionary<string, long>? BarsBeforeCoverage = null);

/// <summary>
/// Point-in-time honesty block. Prices are latest-adjusted (VCI back-adjusts the whole history at
/// every ex-date) ⇒ reconstruction is at most "partial".
/// </summary>
public sealed record EquitySnapshotPit(
    string Reconstruction,
    string PriceBasis,
    IReadOnlyDictionary<string, long> BarsUpdatedAfterAsOf,
    IReadOnlyList<string> Limitations);

/// <summary>Fingerprint of all stored bars with CloseTimeMs ≤ effectiveCutoffMs (see SeriesFingerprintMethod).</summary>
public sealed record EquitySnapshotDatasetDto(
    long BarCount,
    long? FirstOpenTimeMs,
    long? LastOpenTimeMs,
    string? MaxUpdatedAtUtc,
    string Sha256,
    string FingerprintMethod);

public sealed record EquitySnapshotCoverageDto(
    string Timeframe,
    long BarCount,
    long ExpectedBars,
    long MissingBars,
    long RecentExpectedBars,
    long RecentMissingBars,
    long UnexpectedSessionBars,
    long? LatestExpectedOpenTimeMs,
    long? LastStoredOpenTimeMs,
    bool UpToDate,
    IReadOnlyList<CandleGapRange> TopGaps);

public sealed record EquitySnapshotConditions(string Status, string? Reason, JsonObject? Payload);

public sealed record EquitySnapshotSeriesDto(
    string Timeframe,
    string Endpoint,
    long EndTimeMs,
    long BarCount,
    string Fingerprint,
    string FingerprintMethod,
    int PageLimit);

public sealed record EquitySnapshotUnavailable(string Section, string Reason);

public sealed record EquitySourceDocumentRefDto(
    Guid DocumentId,
    string DocType,
    string Title,
    string? ReportingPeriod,
    string Consolidation,
    string AuditStatus,
    string ContentSha256,
    string SourceUrl,
    string Provider,
    DateTime? PublishedAtUtc,
    DateTime FirstSeenAtUtc,
    int Revision,
    Guid? SupersedesDocumentId,
    bool HasExtraction,
    DateTime AvailableAtUtc,
    string AvailabilityBasis,
    int ExtractedMetricCount = 0,
    bool ExtractionRecorded = false,
    // EQX v3 (additive): latest extraction visible at asOf + financial-fact counts of this document.
    EquityDocumentExtractionStatusDto? Extraction = null);

// --- readiness ------------------------------------------------------------------------

public sealed record EquityResearchReadiness(
    string ContractVersion,
    string Symbol,
    DateTime RequestedAsOf,
    long EffectiveCutoffMs,
    bool AsOfIsLatest,
    string SnapshotId,
    string SnapshotSha256,
    IReadOnlyDictionary<string, EquityReadinessPurpose> Purposes);

public sealed record EquityReadinessPurpose(string Status, IReadOnlyList<string> Reasons, IReadOnlyList<EquityReadinessCheck> Checks);

public sealed record EquityReadinessCheck(string Name, string Status, string? Detail);

// --- source documents -----------------------------------------------------------------

public sealed record EquitySourceDocumentListResponse(
    string ContractVersion, string Symbol, IReadOnlyList<EquitySourceDocumentDto> Items);

public sealed record EquitySourceDocumentDto(
    Guid DocumentId,
    string Symbol,
    string Entity,
    string DocType,
    string Title,
    string SourceUrl,
    string Provider,
    string ContentSha256,
    string StoragePath,
    long ByteSize,
    string MimeType,
    string? ReportingPeriod,
    string Consolidation,
    string AuditStatus,
    DateTime? PublishedAtUtc,
    DateTime FirstSeenAtUtc,
    DateTime FetchedAtUtc,
    int Revision,
    Guid? SupersedesDocumentId,
    JsonElement? Extraction,
    DateTime CreatedAtUtc,
    DateTime AvailableAtUtc,
    string AvailabilityBasis,
    int ExtractedMetricCount = 0,
    bool HasExtraction = false);

/// <summary>Admin registration of an already-downloaded document (file must exist under EquityDocuments:RootPath).</summary>
public sealed class EquitySourceDocumentRegistration
{
    public string Entity { get; set; } = "";
    public string DocType { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string? ReportingPeriod { get; set; }
    public string? Consolidation { get; set; }
    public string? AuditStatus { get; set; }
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>
    /// Issuer-stated publication DATE (yyyy-MM-dd, date-only). Stored conservatively as 00:00 VN of the
    /// NEXT day. Mutually exclusive with PublishedAtUtc. On an already-registered document it may fill a
    /// NULL publication time once (write-once); a set value is never changed.
    /// </summary>
    public string? PublishedDate { get; set; }
    public DateTime? FirstSeenAtUtc { get; set; }
    public DateTime? FetchedAtUtc { get; set; }
    public Guid? SupersedesDocumentId { get; set; }
    public JsonElement? Extraction { get; set; }
}

public sealed record EquitySourceDocumentRegistrationResult(bool Created, EquitySourceDocumentDto Item, bool PublishedAtFilled = false);

// --- research notes -------------------------------------------------------------------

/// <summary>POST research-notes body (spec v2 §B). The body block is equity-research-note-v1.</summary>
public sealed class EquityResearchNoteRequest
{
    public string Mode { get; set; } = "";
    public string RequestedAsOf { get; set; } = "";
    public long? SignalBarOpenTimeMs { get; set; }
    public int? HorizonSessions { get; set; }
    public string SnapshotId { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public string SkillVersion { get; set; } = "";
    public string MethodVersion { get; set; } = "";
    public string Direction { get; set; } = "";
    public string ActionView { get; set; } = "";
    public string ActionBasis { get; set; } = "";
    public JsonElement? Body { get; set; }
}

public sealed record EquityResearchNoteDto(
    string NoteId,
    string Symbol,
    string Mode,
    DateTime RequestedAsOfUtc,
    long SignalBarOpenTimeMs,
    long SignalBarCloseTimeMs,
    int HorizonSessions,
    string SnapshotId,
    string SnapshotSha256,
    bool SnapshotVerified,
    string SkillVersion,
    string MethodVersion,
    string Direction,
    string ActionView,
    string ActionBasis,
    JsonElement? Body,
    long AvailableTimeMs,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> ForecastIds);

public sealed record EquityResearchNoteListResponse(
    string ContractVersion, string Symbol, IReadOnlyList<EquityResearchNoteDto> Items);

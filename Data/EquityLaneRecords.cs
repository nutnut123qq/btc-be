using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

// Equity-lane persistence (EQX, docs/plans/equity-expansion/spec.md §3/§7/§8).
// Every row carries provenance (source URL / provider / availableAt) so context and
// journal readers never present an unsourced or back-dated number as fact.

/// <summary>Corporate action (dividend, bonus shares, rights, split) for an equity.</summary>
public class EquityCorporateAction
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";
    public DateOnly ExDate { get; set; }
    public DateOnly? RecordDate { get; set; }
    public DateOnly? AnnouncedDate { get; set; }

    /// <summary>cash-dividend | stock-dividend | bonus-shares | rights-issue | split | other.</summary>
    public string ActionType { get; set; } = "";

    /// <summary>Ratio as published (e.g. "100:15"); free text, never parsed into a formula implicitly.</summary>
    public string? RatioText { get; set; }

    [Column(TypeName = "numeric")]
    public decimal? CashPerShareVnd { get; set; }

    /// <summary>Price adjustment factor only when the source provides/verifies one; null otherwise.</summary>
    [Column(TypeName = "numeric")]
    public decimal? AdjustmentFactor { get; set; }

    /// <summary>
    /// New shares received per 1 old share (stock-dividend/bonus-shares 0.15 = 100:15; split 1.0 = 1:2).
    /// Position conversion only uses it when VerificationStatus = verified (EQX v2 A3).
    /// </summary>
    [Column(TypeName = "numeric")]
    public decimal? ShareRatio { get; set; }

    public string SourceUrl { get; set; } = "";

    /// <summary>provider:VCI | provider:KBS | admin-manual.</summary>
    public string Provenance { get; set; } = "";

    /// <summary>unverified | verified | rejected.</summary>
    public string VerificationStatus { get; set; } = "unverified";

    /// <summary>When the information became available to this system (point-in-time guard).</summary>
    public DateTime AvailableAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Scheduled/occurred corporate event (earnings release, AGM, additional listing).</summary>
public class EquityEvent
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";

    /// <summary>earnings | agm | additional-listing | other.</summary>
    public string EventType { get; set; } = "";
    public DateOnly EventDate { get; set; }
    public string Title { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Provenance { get; set; } = "";
    public DateTime AvailableAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One quarterly fundamental metric for a bank equity, with publication provenance.</summary>
public class EquityFundamental
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";

    /// <summary>Reporting period, "YYYYQn".</summary>
    public string Period { get; set; } = "";
    public DateOnly PeriodEndDate { get; set; }

    /// <summary>BVPS | PB | ROE | NIM | NPL | COVERAGE | LLR | CAR | LDR.</summary>
    public string Metric { get; set; } = "";

    /// <summary>Null = unavailable (then <see cref="UnavailableReason"/> is required). Never inferred.</summary>
    [Column(TypeName = "numeric")]
    public decimal? Value { get; set; }

    /// <summary>VND | ratio | percent.</summary>
    public string Unit { get; set; } = "";
    public string? UnavailableReason { get; set; }

    /// <summary>Report publication date (null when the source does not publish one).</summary>
    public DateOnly? PublishedDate { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public DateTime AsOfUtc { get; set; }
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Provenance { get; set; } = "";

    /// <summary>Issuer source document the value was extracted from (EQX v2 C1); null for provider feeds.</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>Where in the document (page/table/row) the value sits, enough to re-check it.</summary>
    public string? SourceLocation { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Immutable forward forecast (quant kNN or LLM judge) for one barrier config at one
/// finalized daily signal bar. Forecast fields are written once at insert; the outcome
/// block is write-once and stays NULL until the horizon matures on stored bars.
/// </summary>
public class EquityForecastRecord
{
    public Guid Id { get; set; }

    /// <summary>{source}/{SYMBOL}/{configId}/{signalBarOpenTimeMs}[/{llmRunId}].</summary>
    public string ForecastId { get; set; } = "";

    /// <summary>quant-knn | llm-judge (legacy, disabled) | session-research.</summary>
    public string Source { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "1d";
    public string ConfigId { get; set; } = "";
    public double TargetPct { get; set; }
    public double StopPct { get; set; }
    public int HorizonSessions { get; set; }

    public long SignalBarOpenTimeMs { get; set; }
    public long SignalBarCloseTimeMs { get; set; }

    /// <summary>Epoch ms when the forecast was made; >= SignalBarCloseTimeMs.</summary>
    public long AvailableTimeMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public double? ProbTargetFirst { get; set; }
    public double? ProbStopFirst { get; set; }
    public bool Abstained { get; set; }
    public string? AbstainReason { get; set; }

    /// <summary>True only for quant forecasts whose artifact passed the OOS gate. LLM: always false.</summary>
    public bool Validated { get; set; }
    public string MethodVersion { get; set; } = "";
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public string InputSnapshotSha256 { get; set; } = "";
    public Guid? LlmRunId { get; set; }

    /// <summary>Session research note that produced this forecast (Source = session-research).</summary>
    public Guid? ResearchNoteId { get; set; }

    public long? EntryBarOpenTimeMs { get; set; }
    [Column(TypeName = "numeric")]
    public decimal? EntryPrice { get; set; }

    /// <summary>target-first | stop-first | timeout.</summary>
    public string? OutcomeLabel { get; set; }
    public double? OutcomeReturnNet { get; set; }
    public long? ResolvedBarOpenTimeMs { get; set; }
    public DateTime? OutcomeResolvedAtUtc { get; set; }
}

/// <summary>One LLM research+judge run over a dossier snapshot (audit trail, incl. failures).</summary>
public class EquityLlmRun
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = "";
    public long SignalBarOpenTimeMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>succeeded | failed | unavailable.</summary>
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string? ResearchModel { get; set; }
    public string? JudgeModel { get; set; }
    public string PromptVersion { get; set; } = "";
    public string SchemaVersion { get; set; } = "";
    public string InputSnapshotSha256 { get; set; } = "";
    public string InputSnapshotJson { get; set; } = "";
    public string? OutputJson { get; set; }
    public string? UsageJson { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>
/// One HOSE non-trading day (EQX v2 A1). Synced from the checked-in canonical calendar file so the
/// backend and the Python jobs read the same set; verification says whether an official source backs it.
/// </summary>
public class MarketCalendarDay
{
    public long Id { get; set; }
    public string Venue { get; set; } = "HOSE";
    public DateOnly Date { get; set; }

    /// <summary>market-holiday | market-closure.</summary>
    public string Kind { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? SourceUrl { get; set; }
    public string? SourceTitle { get; set; }

    /// <summary>verified | unverified-legacy.</summary>
    public string Verification { get; set; } = "";
    public string CalendarVersion { get; set; } = "";

    /// <summary>
    /// Coverage window of the calendar version this row came from (EQX v2 review #1): dates outside
    /// [CoverageFromDate, CoverageToDate] are NOT known to be trading days — readers must flag
    /// `calendar-coverage-expired` / fail closed instead of assuming every weekday trades.
    /// </summary>
    public DateOnly CoverageFromDate { get; set; }
    public DateOnly CoverageToDate { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Shared brokerage cash pool (EQX v2 A3): one pool for all equity symbols, so the same cash can never
/// back buy-more capacity on MBB and BID at the same time. Null cash = not entered yet (unknown).
/// </summary>
public class EquityCashAccount
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "default";

    [Column(TypeName = "numeric")]
    public decimal? AvailableCashVnd { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Original issuer document (financial statement, investor deck, disclosure) kept outside PG (EQX v2 C1).</summary>
public class EquitySourceDocument
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Entity { get; set; } = "";

    /// <summary>financial-statement | investor-presentation | disclosure | annual-report | other.</summary>
    public string DocType { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ContentSha256 { get; set; } = "";

    /// <summary>Path relative to EquityDocuments:RootPath.</summary>
    public string StoragePath { get; set; } = "";
    public long ByteSize { get; set; }
    public string MimeType { get; set; } = "";

    /// <summary>"YYYYQn", "YYYY" or null.</summary>
    public string? ReportingPeriod { get; set; }

    /// <summary>consolidated | separate | unknown.</summary>
    public string Consolidation { get; set; } = "unknown";

    /// <summary>audited | reviewed | unaudited | unknown.</summary>
    public string AuditStatus { get; set; } = "unknown";
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    public int Revision { get; set; }
    public Guid? SupersedesDocumentId { get; set; }

    /// <summary>Extraction provenance (tool/version, pages read, what could not be extracted).</summary>
    public string? ExtractionJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Research note written by an interactive research session (skill investment-research), EQX v2 B.
/// Immutable after insert. Forward notes are bound to the latest finalized daily bar at write time;
/// retrospective notes are stored but never scored as forecasts.
/// </summary>
public class EquityResearchNote
{
    public Guid Id { get; set; }
    public string NoteId { get; set; } = "";
    public string Symbol { get; set; } = "";

    /// <summary>forward | retrospective.</summary>
    public string Mode { get; set; } = "";
    public DateTime RequestedAsOfUtc { get; set; }
    public long SignalBarOpenTimeMs { get; set; }
    public long SignalBarCloseTimeMs { get; set; }
    public int HorizonSessions { get; set; }
    public string SnapshotId { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public string SkillVersion { get; set; } = "";
    public string MethodVersion { get; set; } = "";

    /// <summary>up | down | sideways | no-view.</summary>
    public string Direction { get; set; } = "";

    /// <summary>buy-more | hold | reduce | sell | wait | none.</summary>
    public string ActionView { get; set; } = "";

    /// <summary>position-aware | market-only | insufficient-input.</summary>
    public string ActionBasis { get; set; } = "";

    /// <summary>equity-research-note-v1 body (thesis, evidence, counter-evidence, scenarios, limitations...).</summary>
    public string BodyJson { get; set; } = "";

    /// <summary>Epoch ms when the note was actually written.</summary>
    public long AvailableTimeMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

// ---------------------------------------------------------------------------------------------
// EQX v3 — issuer document → financial facts pipeline (spec-v3-document-pipeline.md §2).
// ---------------------------------------------------------------------------------------------

/// <summary>Every URL/listing where a stored document (by content hash) was seen. Same bytes on several URLs ⇒ one document.</summary>
public class EquitySourceDocumentLocation
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public string SourceUrl { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ListingTitle { get; set; }
    public DateOnly? ListingDate { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? LastFetchedAtUtc { get; set; }
    public string? HttpEtag { get; set; }
    public string? HttpLastModified { get; set; }
}

/// <summary>One discovery/download attempt; coverage gaps point at the attempt that explains them.</summary>
public class EquitySourceAttempt
{
    public long Id { get; set; }
    public Guid? RunId { get; set; }
    public string Symbol { get; set; } = "";
    public string Source { get; set; } = "";
    public string? TargetPeriod { get; set; }
    public string? DocType { get; set; }
    public string? Consolidation { get; set; }
    public string? Url { get; set; }
    public DateTime AttemptedAtUtc { get; set; }

    /// <summary>listed | not-listed | downloaded | unchanged | changed | http-error | rate-limited | timeout | parse-error | budget-exhausted | too-large.</summary>
    public string Outcome { get; set; } = "";
    public int? HttpStatus { get; set; }
    public string? Detail { get; set; }
}

/// <summary>One extraction pass of a document by an extractor version (text layer and/or local OCR).</summary>
public class EquityDocumentExtraction
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string ExtractorVersion { get; set; } = "";

    /// <summary>text-layer | ocr | mixed.</summary>
    public string Method { get; set; } = "";

    /// <summary>succeeded | partial | failed.</summary>
    public string Status { get; set; } = "";
    public int PageCount { get; set; }
    public int PagesOcr { get; set; }

    /// <summary>Intermediate text/table artifacts, relative to EquityDocuments:RootPath.</summary>
    public string? ArtifactPath { get; set; }
    public string? QualityJson { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
}

/// <summary>
/// One normalized financial fact traced to a document page/location (or derived from other facts with a stored
/// formula). Core columns are immutable (trigger); only Status/ValidationJson may change. Missing values are
/// never stored as rows (no zero-fill).
/// </summary>
public class EquityFinancialFact
{
    public Guid Id { get; set; }
    public string FactKey { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string MetricCode { get; set; } = "";
    public string MetricDefinitionVersion { get; set; } = "";
    public string MetricGroup { get; set; } = "";
    public string Statement { get; set; } = "";
    public string PeriodLabel { get; set; } = "";
    public string PeriodType { get; set; } = "";
    public DateOnly? PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public int FiscalYear { get; set; }
    public string Consolidation { get; set; } = "";

    [Column(TypeName = "numeric")]
    public decimal Value { get; set; }
    public string Unit { get; set; } = "";

    [Column(TypeName = "numeric")]
    public decimal Scale { get; set; } = 1m;
    public string? RawLabel { get; set; }
    public string? RawValueText { get; set; }
    public string? RawUnitText { get; set; }
    public string ColumnRole { get; set; } = "";
    public int? PageNumber { get; set; }
    public string? Location { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? ExtractionId { get; set; }

    /// <summary>extracted | validated | review-needed | quarantined.</summary>
    public string Status { get; set; } = "";
    public string? ValidationJson { get; set; }
    public bool IsDerived { get; set; }
    public string? DerivationJson { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One pipeline run (backfill/incremental) for one symbol; also the operational status surface.</summary>
public class EquityPipelineRun
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime HeartbeatAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? CountsJson { get; set; }
    public string? CheckpointJson { get; set; }
    public string? Error { get; set; }
    public string? LogTail { get; set; }
}

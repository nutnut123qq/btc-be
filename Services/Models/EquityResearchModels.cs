namespace Backend.Services.Models;

// EQX wave 2 (spec §5, §6, §8, §11). All quant ratios are FRACTIONS (0.05 = 5%);
// fundamentals keep the published unit (percent / ratio / VND) — see §11.

// --- §5 base rates ------------------------------------------------------------------

public static class EquityBaseRatesStatuses
{
    public const string Ok = "ok";
    public const string Abstained = "abstained";
    public const string Unavailable = "unavailable";
}

public sealed record EquityBaseRatesResponse(
    string ContractVersion,
    string Symbol,
    string Timeframe,
    string Status,
    IReadOnlyList<string> Reasons,
    EquityBaseRatesArtifactInfo? Artifact,
    IReadOnlyList<EquityBaseRatesConfigDto> Configs)
{
    public const string Version = "equity-base-rates-v1";
}

public sealed record EquityBaseRatesArtifactInfo(
    string GeneratedAtUtc,
    long DataAsOfOpenTimeMs,
    string PriceBasis,
    string MethodVersion,
    string ProtocolVersion,
    string DatasetSha256);

public sealed record EquityBaseRatesConfigDto(
    string ConfigId,
    double TargetPct,
    double StopPct,
    int HorizonSessions,
    bool Abstained,
    IReadOnlyList<string> AbstainReasons,
    EquityBaseRatesDecisionUse? DecisionUse,
    EquityBaseRatesResearch? Research,
    EquityBaseRatesValidation? Validation);

/// <summary>Only non-null when the gate passed, the artifact is fresh and the fingerprint matches.</summary>
public sealed record EquityBaseRatesDecisionUse(
    double ProbTargetFirst,
    double ProbStopFirst,
    double ProbTimeout,
    double? EvNetFraction);

public sealed record EquityBaseRatesResearch(
    string Label,
    long SignalBarOpenTimeMs,
    double ProbTargetFirst,
    double ProbStopFirst,
    double ProbTimeout,
    double? EvNetFraction,
    int? K,
    int NEligible,
    EquityBaseRatesPrior Prior);

public static class EquityResearchLabels
{
    public const string Validated = "validated";
    public const string Unvalidated = "unvalidated";
}

public sealed record EquityBaseRatesPrior(double? ProbTargetFirst, double? ProbStopFirst, double? ProbTimeout);

public sealed record EquityBaseRatesValidation(
    bool GatePassed,
    IReadOnlyList<string> GateReasons,
    int? NOos,
    int? NEffective,
    double? BrierKnn,
    double? BrierPrior,
    double? BrierDiffMean,
    IReadOnlyList<double>? BrierDiffCi90,
    double? Ece,
    IReadOnlyList<EquityCalibrationBin> CalibrationBins,
    double? Coverage,
    double? AbstentionRate,
    double? EvNetOosMean,
    double? EvNetOosWhenPositive);

public sealed record EquityCalibrationBin(double Lower, double Upper, int N, double? MeanPredicted, double? ObservedRate);

// --- §6 context ---------------------------------------------------------------------

public static class EquityContextStatuses
{
    public const string Ok = "ok";
    public const string Empty = "empty";
    public const string Unavailable = "unavailable";

    /// <summary>EQX v2 A4: benchmark data exists but does not reach the instrument's latest session.</summary>
    public const string Stale = "stale";
}

public sealed record EquityContextResponse(
    string ContractVersion,
    string Symbol,
    DateTime GeneratedAtUtc,
    EquityRelativeStrengthSection RelativeStrength,
    EquityCorporateActionsSection CorporateActions,
    EquityEventsSection Events,
    EquityFundamentalsSection Fundamentals,
    IReadOnlyList<string> Limitations)
{
    public const string Version = "equity-context-v1";
}

public sealed record EquityRelativeStrengthSection(
    string Status,
    string? Reason,
    IReadOnlyList<int> Windows,
    IReadOnlyList<EquityRsBenchmarkDto> Benchmarks);

public sealed record EquityRsBenchmarkDto(
    string Symbol,
    string Kind,
    string Status,
    string? Reason,
    int AlignedSessions,
    long? LastAlignedOpenTimeMs,
    IReadOnlyDictionary<string, EquityRsWindowDto?> Returns,
    // EQX v2 A4 (additive): staleness evidence. Status "stale" keeps the returns of the last aligned
    // window for reference, but they do not describe the instrument's latest session.
    long? InstrumentLatestOpenTimeMs = null,
    long? BenchmarkLatestOpenTimeMs = null,
    int? LagSessions = null);

/// <summary>Close-to-close fractions over the aligned window; excess = instrument − benchmark.</summary>
public sealed record EquityRsWindowDto(double Instrument, double Benchmark, double Excess);

public sealed record EquityCorporateActionsSection(string Status, string? Reason, IReadOnlyList<EquityCorporateActionDto> Items);
public sealed record EquityEventsSection(string Status, string? Reason, IReadOnlyList<EquityEventDto> Items);
public sealed record EquityFundamentalsSection(string Status, string? Reason, IReadOnlyList<EquityFundamentalDto> Items);

public sealed record EquityCorporateActionDto(
    string ExDate,
    string ActionType,
    string? RatioText,
    decimal? CashPerShareVnd,
    decimal? AdjustmentFactor,
    string? RecordDate,
    string? AnnouncedDate,
    string SourceUrl,
    string Provenance,
    string VerificationStatus,
    DateTime AvailableAtUtc,
    // EQX v2 A3 (additive): new shares per 1 old share; positions convert only when verified.
    decimal? ShareRatio = null,
    // EQX v2 PIT (additive): AvailableAtUtc above is the EFFECTIVE availability; see EquityAvailabilityBases.
    string AvailabilityBasis = EquityAvailabilityBases.ProviderPublishDate,
    string? AvailabilityReason = null,
    DateTime? PublishedAvailableAtUtc = null);

/// <summary>
/// How an event / corporate action's effective availability was derived (EQX v2 PIT). VCI sometimes reuses an
/// old disclosure date years before the item itself; such a date would let a past snapshot "know" the future.
/// </summary>
public static class EquityAvailabilityBases
{
    /// <summary>The stored publish-based AvailableAtUtc is plausible (≤ 90 days before the item's own date).</summary>
    public const string ProviderPublishDate = "provider-publish-date";

    /// <summary>Publish date implausible ⇒ the row's first-seen time (CreatedAtUtc) is used instead.</summary>
    public const string FirstSeen = "first-seen";

    public const string ImplausibleReason = "provider-publish-date-implausible";

    /// <summary>A publish date earlier than this before the item's own date is treated as implausible.</summary>
    public const int MaxLeadDays = 90;
}

public sealed record EquityEventDto(
    string EventType,
    string EventDate,
    string Title,
    string SourceUrl,
    string Provenance,
    DateTime AvailableAtUtc,
    // EQX v2 PIT (additive): AvailableAtUtc above is the EFFECTIVE availability; see EquityAvailabilityBases.
    string AvailabilityBasis = EquityAvailabilityBases.ProviderPublishDate,
    string? AvailabilityReason = null,
    DateTime? PublishedAvailableAtUtc = null);

public sealed record EquityFundamentalDto(
    string? Period,
    string? PeriodEndDate,
    string Metric,
    decimal? Value,
    string Unit,
    string? UnavailableReason,
    string? PublishedDate,
    DateTime? AvailableAtUtc,
    DateTime? AsOfUtc,
    string? Source,
    string? SourceUrl,
    string? Note,
    // EQX v2 C1 (additive): original issuer document behind an issuer-report row, and where in it.
    Guid? DocumentId = null,
    string? SourceLocation = null);

/// <summary>Result of one collector run (context/sync).</summary>
public sealed record EquityContextSyncReport(
    string Symbol,
    DateTime GeneratedAtUtc,
    string Status,
    int ProviderRequests,
    int CorporateActionsUpserted,
    int EventsUpserted,
    int FundamentalsUpserted,
    int SkippedRows,
    IReadOnlyList<string> Errors);

// --- admin import bodies (spec §6) -------------------------------------------------
// Classes (not positional records) so MVC model validation works (see MbbPositionModels).
// No `Symbol` property: the symbol comes from the route.

public sealed class EquityCorporateActionImport
{
    public string ExDate { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string? RatioText { get; set; }
    public decimal? CashPerShareVnd { get; set; }
    public decimal? AdjustmentFactor { get; set; }

    /// <summary>
    /// EQX v2 A3: EXTRA shares received per 1 old share — the lot becomes shares × (1 + shareRatio)
    /// (stock-dividend/bonus-shares 100:15 ⇒ 0.15; split 1:2 ⇒ 1.0, NOT 2.0). Must be in (0, 10].
    /// Lots bought before the ex-date are converted only when verificationStatus = verified and the type is
    /// stock-dividend, bonus-shares or split. Not allowed for cash-dividend. Reverse splits are not representable.
    /// </summary>
    public decimal? ShareRatio { get; set; }
    public string? RecordDate { get; set; }
    public string? AnnouncedDate { get; set; }
    public string SourceUrl { get; set; } = "";
    public string? VerificationStatus { get; set; }
    public DateTime? AvailableAtUtc { get; set; }
}

public sealed class EquityEventImport
{
    public string EventType { get; set; } = "";
    public string EventDate { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public DateTime? AvailableAtUtc { get; set; }
}

public sealed class EquityFundamentalImport
{
    public string Period { get; set; } = "";
    public string? PeriodEndDate { get; set; }
    public string Metric { get; set; } = "";
    public decimal? Value { get; set; }
    public string Unit { get; set; } = "";
    public string? UnavailableReason { get; set; }
    public string? PublishedDate { get; set; }
    public DateTime? AvailableAtUtc { get; set; }
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";

    /// <summary>EQX v2 C1: registered EquitySourceDocuments row of the same symbol (else 400). Required (with sourceLocation) when source = "issuer-report".</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>Page / table / line in the document (at most 500 characters).</summary>
    public string? SourceLocation { get; set; }
}

public sealed record EquityImportReport(string Symbol, string Kind, int Inserted, int Updated);

// --- §8 forecast journal -------------------------------------------------------------

public sealed record EquityForecastJournalResponse(
    string ContractVersion,
    string Symbol,
    IReadOnlyList<EquityForecastItemDto> Items,
    IReadOnlyList<EquityForecastScoringDto> Scoring)
{
    public const string Version = "equity-forecast-journal-v1";
}

public sealed record EquityForecastItemDto(
    string ForecastId,
    string Source,
    string ConfigId,
    double TargetPct,
    double StopPct,
    int HorizonSessions,
    long SignalBarOpenTimeMs,
    long AvailableTimeMs,
    DateTime CreatedAtUtc,
    double? ProbTargetFirst,
    double? ProbStopFirst,
    bool Abstained,
    string? AbstainReason,
    bool Validated,
    string MethodVersion,
    string? ModelId,
    string? PromptVersion,
    Guid? LlmRunId,
    EquityForecastOutcomeDto? Outcome);

public sealed record EquityForecastOutcomeDto(
    long EntryBarOpenTimeMs,
    decimal EntryPrice,
    string Label,
    double ReturnNet,
    long ResolvedBarOpenTimeMs,
    DateTime ResolvedAtUtc);

/// <summary>
/// Paired scoring per config. brierDiff = brierLlm − brierQuant (negative = LLM better on the
/// same paired sample). nEffective counts non-overlapping signal bars (≥ H sessions apart).
/// </summary>
public sealed record EquityForecastScoringDto(
    string ConfigId,
    string Status,
    int PairedN,
    int NEffective,
    double? BrierQuant,
    double? BrierLlm,
    double? BrierDiff,
    double? CoverageQuant,
    double? CoverageLlm,
    // EQX v2 (additive): the same paired rules per challenger source (llm-judge, session-research).
    // The top-level fields above remain quant vs llm-judge.
    IReadOnlyList<EquityForecastPairScoreDto>? Pairs = null);

/// <summary>Quant vs one challenger source, scored with the same look-ahead/earliest-per-bar rules.</summary>
public sealed record EquityForecastPairScoreDto(
    string Source,
    string Status,
    int PairedN,
    int NEffective,
    double? BrierQuant,
    double? BrierChallenger,
    double? BrierDiff,
    double? CoverageQuant,
    double? CoverageChallenger);

public sealed record EquityForecastRecordReport(
    string Symbol,
    string Status,
    long? SignalBarOpenTimeMs,
    int Recorded,
    int AlreadyRecorded,
    int Abstained,
    string? Detail);

// --- §7 dossier + LLM runs -----------------------------------------------------------------

/// <summary>
/// Market-only research dossier (equity-dossier-v1). Deliberately carries NO position, lots,
/// cost basis or cash: it is public and it is the LLM's input. Each section degrades on its own
/// (unavailable instance) instead of failing the dossier.
/// </summary>
public sealed record EquityDossierResponse(
    string ContractVersion,
    string Symbol,
    DateTime AsOfUtc,
    long? SignalBarOpenTimeMs,
    System.Text.Json.Nodes.JsonObject Conditions,
    EquityBaseRatesResponse BaseRates,
    EquityContextResponse Context,
    EquityLlmRunDto? LatestLlmRun,
    IReadOnlyList<EquityForecastScoringDto> JournalSummary)
{
    public const string Version = "equity-dossier-v1";
}

public static class EquityLlmRunStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";
}

/// <summary>One LLM run (list item; POST returns the same shape). Output is equity-llm-judge-v1 or null.</summary>
public sealed record EquityLlmRunDto(
    Guid RunId,
    string Symbol,
    long SignalBarOpenTimeMs,
    DateTime CreatedAtUtc,
    string Status,
    string? Reason,
    string? ResearchModel,
    string? JudgeModel,
    string PromptVersion,
    string SchemaVersion,
    System.Text.Json.JsonElement? Output,
    long DurationMs);

public sealed record EquityLlmRunListResponse(IReadOnlyList<EquityLlmRunDto> Items);

/// <summary>Error envelope that also carries the persisted run (503 LLM_UNAVAILABLE / 502 upstream failure).</summary>
public sealed class EquityLlmRunErrorEnvelope : ApiErrorEnvelope
{
    public EquityLlmRunDto? Item { get; set; }
}

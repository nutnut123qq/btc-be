namespace Backend.Services.Models;

// EQTECH-1 lane S: direction scoring of session research notes without probabilities
// (rule direction-score-v1). Returns and thresholds are FRACTIONS (0.05 = +5%), like
// EquityBarrierConfigs.TargetPct/StopPct. Hit rates are descriptive counts, not probabilities.

/// <summary>Item statuses, horizon statuses, pending reasons and realized classes of direction-score-v1.</summary>
public static class EquityDirectionScoreValues
{
    // Realized / called classes.
    public const string Up = "up";
    public const string Down = "down";
    public const string Sideways = "sideways";

    // Item statuses.
    public const string StatusResolved = "resolved";
    public const string StatusPending = "pending";
    public const string StatusNoView = "no-view";
    public const string StatusSuperseded = "superseded-not-scored";
    public const string StatusRetrospective = "retrospective-not-scored";
    public const string StatusOutOfWindow = "out-of-window-not-scored";
    public const string StatusWindowUndetermined = "window-undetermined-not-scored";
    public const string StatusNoDeclaredThresholds = "no-declared-thresholds";

    // Pending reasons (why the realized class is not available yet).
    public const string PendingImmature = "immature";
    public const string PendingTargetBarMissing = "target-bar-missing";
    public const string PendingCalendarCoverageExpired = "calendar-coverage-expired";
    public const string PendingSignalBarMissing = "signal-bar-missing";

    // Horizon statuses.
    public const string HorizonNoData = "no-data";
    public const string HorizonInsufficientSample = "insufficient-sample";
    public const string HorizonOk = "ok";
}

/// <summary>GET /api/equities/{symbol}/research-notes/direction-scores (equity-direction-scores-v1).</summary>
public sealed record EquityDirectionScoresResponse(
    string ContractVersion,
    string Symbol,
    string RuleVersion,
    string Rule,
    DateTime GeneratedAtUtc,
    string? CalendarCoverageTo,
    int TotalNotes,
    int RetrospectiveNotes,
    int NoDeclaredThresholdNotes,
    IReadOnlyList<EquityDirectionHorizonScoreDto> Horizons,
    IReadOnlyList<EquityDirectionScoreItemDto> Items)
{
    public const string Version = "equity-direction-scores-v1";
}

/// <summary>
/// One declared horizon (one barrier config). forwardNotes = outOfWindow + windowUndetermined + eligible;
/// eligible = scored + superseded; scored = noView + directional (one per signal bar); directional = resolved + pending.
/// coverage = directional / scored.
/// </summary>
public sealed record EquityDirectionHorizonScoreDto(
    int HorizonSessions,
    string ConfigId,
    double UpThresholdPct,
    double DownThresholdPct,
    string Status,
    int ForwardNotes,
    int OutOfWindow,
    int WindowUndetermined,
    int Eligible,
    int Superseded,
    int Scored,
    int NoView,
    int Directional,
    int Resolved,
    int Pending,
    EquityDirectionPendingCounts PendingReasons,
    int Hits,
    double? HitRate,
    double? Coverage,
    int NEffective,
    int MinEffectiveForScoring,
    EquityDirectionPriorBaselineDto BaselinePrior,
    EquityDirectionConstantBaselineDto BestConstantInHindsight);

/// <summary>Directional notes not resolvable yet, per reason (sums to pending).</summary>
public sealed record EquityDirectionPendingCounts(
    int Immature,
    int TargetBarMissing,
    int CalendarCoverageExpired,
    int SignalBarMissing);

/// <summary>Point-in-time prior baseline on the same resolved directional set (the call is per note).</summary>
public sealed record EquityDirectionPriorBaselineDto(int Hits, double? HitRate);

/// <summary>The single class with the most hits on the resolved set (hindsight; label null when nothing is resolved).</summary>
public sealed record EquityDirectionConstantBaselineDto(string? Label, int Hits, double? HitRate);

/// <summary>
/// One research note. signalClose/target*/returnPct/realized/pendingReason are filled only for the note that
/// holds the scored slot of its (signal bar, horizon) (status resolved, pending or no-view): pendingReason says
/// why realized is not available yet; a no-view note may show realized but is never right or wrong.
/// baselineCall and hit only for resolved directional notes.
/// </summary>
public sealed record EquityDirectionScoreItemDto(
    string NoteId,
    string Mode,
    long SignalBarOpenTimeMs,
    int HorizonSessions,
    string Direction,
    long AvailableTimeMs,
    string Status,
    string? PendingReason,
    decimal? SignalClose,
    long? TargetBarOpenTimeMs,
    decimal? TargetClose,
    double? ReturnPct,
    string? Realized,
    string? BaselineCall,
    bool? Hit);

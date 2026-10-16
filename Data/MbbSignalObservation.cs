namespace Backend.Data;

/// <summary>
/// Forward observation journal for the MBB decision lane (MBB-07): one row per
/// finalized signal bar, written at observation time with the live decision
/// snapshot. Mirrors the PaperObservations honesty contract:
///
/// - Rows can only be created for the *latest expected finalized open* — a
///   recorder cannot backdate decisions onto historical bars.
/// - Decision/action fields are written once at insert; the full decision DTO
///   is preserved in <see cref="DecisionPayloadJson"/> for provenance.
/// - Fill is the FIRST stored bar opening at/after the observation moment
///   (AvailableTimeMs), resolved only after that bar exists and only for
///   action-bearing decisions (buy-more/reduce/sell/conditional-sell).
///   conditional-sell additionally requires a declared close-below trigger to
///   fire on a finalized bar first; an unfired condition is not a fill.
///   wait/hold never get fills — they are non-transactions.
/// - Outcome is the raw instrument price return over the next bar (close-to-
///   fill), action-direction agnostic; interpretation happens at analysis time.
/// - Fill/outcome are write-once and never fabricated: an unresolved field is
///   NULL, not an inferred value.
/// </summary>
public sealed class MbbSignalObservation
{
    public Guid Id { get; set; }

    /// <summary>Deterministic unique key: mbb-observe/{timeframe}/{signalBarOpenTimeMs}.</summary>
    public string DecisionId { get; set; } = string.Empty;
    public string RecorderVersion { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;

    public long SignalBarOpenTimeMs { get; set; }
    public long SignalBarCloseTimeMs { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }

    /// <summary>Epoch ms of the observation moment; must be >= SignalBarCloseTimeMs.</summary>
    public long AvailableTimeMs { get; set; }

    /// <summary>MbbDecisionActions value recorded at observation time.</summary>
    public string Decision { get; set; } = string.Empty;
    public string? RulesVersion { get; set; }
    public string? EvidenceStatus { get; set; }
    public string PriceBasis { get; set; } = string.Empty;
    public decimal? MarketCloseVnd { get; set; }

    /// <summary>Full MbbDecisionDto snapshot (jsonb) for provenance.</summary>
    public string DecisionPayloadJson { get; set; } = string.Empty;

    public decimal? FillPrice { get; set; }
    public DateTimeOffset? FillObservedAtUtc { get; set; }
    public string? FillSource { get; set; }
    public long? FillBarOpenTimeMs { get; set; }

    public double? OutcomeReturn { get; set; }
    public DateTimeOffset? OutcomeObservedAtUtc { get; set; }
    public string? OutcomeHorizon { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

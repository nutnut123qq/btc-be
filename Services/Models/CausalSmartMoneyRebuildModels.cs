namespace Backend.Services.Models;

public sealed class CausalSmartMoneyRebuildRequest
{
    public string Symbol { get; init; } = ProductionSymbolPolicy.Symbol;
    public string Timeframe { get; init; } = "4h";
    public bool DryRun { get; init; } = true;
    /// <summary>Dry-run only: estimate the first historical batch without requiring rebuild tables.</summary>
    public bool PreviewFromBeginning { get; init; }
    public int MaxCandles { get; init; } = 1_000;
}

public sealed class CausalSmartMoneyGapBoundaryDto
{
    public long? PreviousOpenTimeMs { get; init; }
    public long? NextOpenTimeMs { get; init; }
    public long? InvalidOpenTimeMs { get; init; }
    public long MissingBars { get; init; }
    public string BoundaryType { get; init; } = "missing_candles";
    public string LedgerStatus { get; init; } = "untracked";
    public long? GapStateId { get; init; }
}

public sealed class CausalSmartMoneyRebuildResult
{
    public string Symbol { get; init; } = ProductionSymbolPolicy.Symbol;
    public string Timeframe { get; init; } = "4h";
    public string CalculationVersion { get; init; } = "";
    public bool DryRun { get; init; }
    public long? PreviousCheckpointOpenTimeMs { get; init; }
    public long? CoverageStartOpenTimeMs { get; init; }
    public long? BatchStartOpenTimeMs { get; init; }
    public long? LastProcessedOpenTimeMs { get; init; }
    public int CandidateCandles { get; init; }
    public int ValidCandidateCandles { get; init; }
    public int InvalidDurationCandles { get; init; }
    public int ContextCandles { get; init; }
    public int ContiguousSegments { get; init; }
    public int EstimatedEvents { get; init; }
    public long EstimatedEvidenceBytes { get; init; }
    public int InsertedEvents { get; init; }
    public int UpdatedEvents { get; init; }
    public int ExistingEvents { get; init; }
    public string Status { get; init; } = "dry_run";
    public IReadOnlyList<CausalSmartMoneyGapBoundaryDto> GapBoundaries { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed class CausalSmartMoneyCoverageResponse
{
    public string Symbol { get; init; } = ProductionSymbolPolicy.Symbol;
    public string Timeframe { get; init; } = "4h";
    public string CalculationVersion { get; init; } = "";
    public long? LastProcessedOpenTimeMs { get; init; }
    public long? CoverageStartOpenTimeMs { get; init; }
    public long? LatestSegmentStartOpenTimeMs { get; init; }
    public long ProcessedCandleCount { get; init; }
    public long MaterializedEventCount { get; init; }
    public string CheckpointStatus { get; init; } = "not_started";
    public long PersistedEventCount { get; init; }
    public IReadOnlyDictionary<string, long> EventsByType { get; init; } = new Dictionary<string, long>();
    public long InvalidDurationRows { get; init; }
    public long HistoricalPendingGapRanges { get; init; }
    public long UnavailableGapRanges { get; init; }
    public long TrailingNotYetFinalizedGapRanges { get; init; }
    public string LegacyStorageStatus { get; init; } = "isolated_not_read_or_overwritten";
}

public sealed class CausalSmartMoneyRebuildLimitException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

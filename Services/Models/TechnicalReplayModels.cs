namespace Backend.Services.Models;

/// <summary>
/// Point-in-time technical evidence reconstructed exclusively from finalized source candles.
/// </summary>
public sealed class TechnicalReplayResponse
{
    public string ModuleContractVersion { get; init; } = "";
    public string ModuleContractSha256 { get; init; } = "";
    public string Symbol { get; init; } = ProductionSymbolPolicy.Symbol;
    public string Timeframe { get; init; } = "4h";
    public long RequestedAsOfTimeMs { get; init; }
    public long? EffectiveAsOfTimeMs { get; init; }
    public long? LastFinalizedCandleCloseTimeMs { get; init; }
    public int RequestedLookbackBars { get; init; }
    public long? ReplayWindowStartTimeMs { get; init; }
    public long? ContiguousSegmentStartTimeMs { get; init; }
    /// <summary>Number of bounded candles returned for chart replay.</summary>
    public int SourceCandleCount { get; init; }
    /// <summary>Number of gap-free candles evaluated to reconstruct causal SMC state.</summary>
    public int AnalysisCandleCount { get; init; }
    public string CalculationVersion { get; init; } = "";
    public TechnicalReplayProvenanceDto Provenance { get; init; } = new();
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public IReadOnlyList<TechnicalReplayCandleDto> Candles { get; init; } = [];
    public IReadOnlyList<TechnicalEventEvidenceDto> Events { get; init; } = [];
    public TechnicalReplayLayersDto Layers { get; init; } = new();
    public IReadOnlyList<TechnicalLayerCoverageDto> Coverage { get; init; } = [];
    public TechnicalReplayAdministrationDto Administration { get; init; } = new();
}

public sealed class TechnicalReplayProvenanceDto
{
    public string Source { get; init; } = "stored-finalized-klines";
    public string EvaluationMode { get; init; } = "point-in-time-reconstruction";
    public string AvailabilityRule { get; init; } = "candle.closeTimeMs <= requestedAsOfTimeMs";
    public string ContextRule { get; init; } = "evaluate the latest gap-free, valid-duration stored history segment, then return events whose origin is inside the requested lookback window";
    public bool PersistedByReplay { get; init; }
}

public sealed class TechnicalReplayContextLimitException(int maxBars)
    : Exception($"Technical replay needs more than the maximum {maxBars} finalized source candles.")
{
    public int MaxBars { get; } = maxBars;
}

public sealed class TechnicalEventEvidenceDto
{
    public string EventId { get; init; } = "";
    public string EventType { get; init; } = "";
    public string Description { get; init; } = "";
    public long OriginTimeMs { get; init; }
    public long AvailableTimeMs { get; init; }
    public long? ReferenceTimeMs { get; init; }
    public double Price { get; init; }
    public double? HighPrice { get; init; }
    public double? LowPrice { get; init; }
    public string CalculationVersion { get; init; } = "";
    public string StateAtAsOf { get; init; } = "confirmed";
    public long? MitigatedAtMs { get; init; }
    public long? InvalidatedAtMs { get; init; }
    public string? MitigationRule { get; init; }
    public string? InvalidationRule { get; init; }
    public IReadOnlyList<TechnicalSourceCandleDto> SourceCandles { get; init; } = [];
    public IReadOnlyList<string> DetectionConditions { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed class TechnicalSourceCandleDto
{
    public string Role { get; init; } = "";
    public long OpenTimeMs { get; init; }
    public long CloseTimeMs { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }
}

public sealed class TechnicalReplayCandleDto
{
    public long OpenTimeMs { get; init; }
    public long CloseTimeMs { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal Volume { get; init; }
}

public static class TechnicalLayerAvailability
{
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
}

public sealed class TechnicalLayerLineageDto
{
    public string ModuleContractVersion { get; init; } = "";
    public string ModuleContractSha256 { get; init; } = "";
    public string Producer { get; init; } = "";
    public string CalculationVersion { get; init; } = "";
    public string Source { get; init; } = "stored-finalized-klines";
    public string EvaluationMode { get; init; } = "point-in-time-reconstruction";
    public long RequestedAsOfTimeMs { get; init; }
    public long? EffectiveAsOfTimeMs { get; init; }
    public long? AvailableTimeMs { get; init; }
    public long? SourceStartTimeMs { get; init; }
    public long? SourceEndTimeMs { get; init; }
    public int SourceCandleCount { get; init; }
    public int RequiredWarmupBars { get; init; }
    public bool IsCausal { get; init; } = true;
    public bool IsPersisted { get; init; }
}

public sealed class TechnicalLayerEnvelopeDto<TPayload>
{
    public string LayerKey { get; init; } = "";
    public string Availability { get; init; } = TechnicalLayerAvailability.Unavailable;
    public TechnicalLayerLineageDto Lineage { get; init; } = new();
    public string? UnavailableReason { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public TPayload? Payload { get; init; }
}

public sealed class TechnicalReplayLayersDto
{
    public TechnicalLayerEnvelopeDto<TechnicalIndicatorReplayDto> Indicators { get; init; } = new();
    public TechnicalLayerEnvelopeDto<CandlePatternReplayDto> CandlePatterns { get; init; } = new();
    public TechnicalLayerEnvelopeDto<VolumeAnomalyReplayDto> VolumeAnomaly { get; init; } = new();
    public TechnicalLayerEnvelopeDto<MarketRegimeReplayDto> MarketRegime { get; init; } = new();
    public TechnicalLayerEnvelopeDto<FibonacciReplayDto> Fibonacci { get; init; } = new();
    public TechnicalLayerEnvelopeDto<VolumeProfileReplayDto> VolumeProfile { get; init; } = new();
    public TechnicalLayerEnvelopeDto<ConfluenceReplayDto> Confluence { get; init; } = new();
}

public sealed class TechnicalIndicatorReplayDto
{
    public long OpenTimeMs { get; init; }
    public long AvailableTimeMs { get; init; }
    public double? Rsi14 { get; init; }
    public decimal? Ema12 { get; init; }
    public decimal? Ema26 { get; init; }
    public decimal? Sma50 { get; init; }
    public IReadOnlyList<TechnicalIndicatorEventDto> Events { get; init; } = [];
}

public sealed record TechnicalIndicatorEventDto(string EventType, int Direction, double Value);

public sealed class CandlePatternReplayDto
{
    public IReadOnlyList<CandlePatternEventDto> Events { get; init; } = [];
}

public sealed class CandlePatternEventDto
{
    public string PatternType { get; init; } = "";
    public string PatternCategory { get; init; } = "";
    public string TrendDirection { get; init; } = "";
    public long OriginTimeMs { get; init; }
    public long AvailableTimeMs { get; init; }
    public IReadOnlyList<long> SourceOpenTimeMs { get; init; } = [];
}

public sealed class VolumeAnomalyReplayDto
{
    public long OpenTimeMs { get; init; }
    public long AvailableTimeMs { get; init; }
    public decimal Volume { get; init; }
    public decimal VolumeSma20 { get; init; }
    public double VolumeAnomalyRatio { get; init; }
    public double VolumeVsPrevious { get; init; }
    public double VolumeVsMax10 { get; init; }
    public string VolumeTrend { get; init; } = "normal";
    public IReadOnlyList<string> TriggeredEvents { get; init; } = [];
}

public sealed class MarketRegimeReplayDto
{
    public long OpenTimeMs { get; init; }
    public long AvailableTimeMs { get; init; }
    public string RegimeType { get; init; } = "";
    public string Trend { get; init; } = "sideways";
    public string Volatility { get; init; } = "insufficient_history";
    public string? EventType { get; init; }
    public int UpChanges { get; init; }
    public int DownChanges { get; init; }
    public double CurrentTrueRangePct { get; init; }
    public double PriorMedianTrueRangePct { get; init; }
    public double RangeRatio { get; init; }
}

public sealed class FibonacciReplayDto
{
    public string EventType { get; init; } = "";
    public long AvailableTimeMs { get; init; }
    public string Direction { get; init; } = "";
    public long AnchorStartTimeMs { get; init; }
    public long AnchorEndTimeMs { get; init; }
    public double AnchorLow { get; init; }
    public double AnchorHigh { get; init; }
    public IReadOnlyList<FibonacciLevelDto> Levels { get; init; } = [];
}

public sealed record FibonacciLevelDto(double Ratio, double Price);

public sealed class VolumeProfileReplayDto
{
    public string Method { get; init; } = "full-bar-volume-at-typical-price";
    public int BinCount { get; init; }
    public double ValueAreaFraction { get; init; }
    public long WindowStartMs { get; init; }
    public long WindowEndMs { get; init; }
    public double InputVolume { get; init; }
    public double PocPrice { get; init; }
    public double VahPrice { get; init; }
    public double ValPrice { get; init; }
    public IReadOnlyList<VolumeProfileBinDto> Bins { get; init; } = [];
    public IReadOnlyList<string> Events { get; set; } = [];
}

public sealed class ConfluenceReplayDto
{
    public long AvailableTimeMs { get; init; }
    public string? EventType { get; init; }
    public IReadOnlyList<string> TriggeredEvents { get; init; } = [];
    public double Score { get; init; }
    public string ScoreKind { get; init; } = "descriptive_regime_index";
    public bool IsProbability { get; init; }
    public string OverallDirection { get; init; } = "Neutral";
    public bool HasConflict { get; init; }
    public int AlignedDirectionalModules { get; init; }
    public IReadOnlyList<ConfluenceModuleVoteDto> ModuleVotes { get; init; } = [];
}

public sealed record ConfluenceModuleVoteDto(
    string LayerKey,
    int Vote,
    string Reason,
    long AvailableTimeMs);

public sealed class TechnicalLayerCoverageDto
{
    public string LayerKey { get; init; } = "";
    public string Availability { get; init; } = TechnicalLayerAvailability.Unavailable;
    public int SourceBars { get; init; }
    public int RequiredWarmupBars { get; init; }
    public long? LatestAvailableTimeMs { get; init; }
    public bool HasGapBoundary { get; init; }
    public string CheckpointStatus { get; init; } = "not_started";
    public string StorageStatus { get; init; } = "on_demand_state";
    public bool IsEventEnvelopeMaterializedAtAsOf { get; init; }
}

public sealed class TechnicalReplayAdministrationDto
{
    public bool HasGapBoundary { get; init; }
    public int ContextLimitBars { get; init; }
    public string LegacySmartMoneyStatus { get; init; } = "isolated";
    public bool RebuildRequired { get; init; }
    public string? RebuildReason { get; init; }
}

public sealed class TechnicalEvidenceRebuildRequest
{
    public string Symbol { get; init; } = ProductionSymbolPolicy.Symbol;
    public string Timeframe { get; init; } = "4h";
    public bool DryRun { get; init; } = true;
    public int MaxCandles { get; init; } = 25;
}

public sealed class TechnicalEvidenceRebuildResult
{
    public string Symbol { get; init; } = ProductionSymbolPolicy.Symbol;
    public string Timeframe { get; init; } = "4h";
    public bool DryRun { get; init; }
    public string ModuleContractVersion { get; init; } = "";
    public string ModuleContractSha256 { get; init; } = "";
    public long? PreviousCheckpointCloseTimeMs { get; init; }
    public long? CoverageStartCloseTimeMs { get; init; }
    public long? BatchStartCloseTimeMs { get; init; }
    public bool HistoricalBackfill { get; init; }
    public long? LastProcessedCloseTimeMs { get; init; }
    public int CandidateCandles { get; init; }
    public int EstimatedSparseRecords { get; init; }
    public long EstimatedEnvelopeBytes { get; init; }
    public int InsertedRecords { get; init; }
    public int ExistingRecords { get; init; }
    public string Status { get; init; } = "dry_run";
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed class TechnicalEvidenceCoverageResponse
{
    public string Symbol { get; init; } = ProductionSymbolPolicy.Symbol;
    public string Timeframe { get; init; } = "4h";
    public string ModuleContractVersion { get; init; } = "";
    public string ModuleContractSha256 { get; init; } = "";
    public long? LastProcessedCloseTimeMs { get; init; }
    public long? CoverageStartCloseTimeMs { get; init; }
    public bool HistoricalBackfill { get; init; }
    public string CheckpointStatus { get; init; } = "not_started";
    public long SparseRecordCount { get; init; }
    public IReadOnlyDictionary<string, long> RecordsByLayer { get; init; } = new Dictionary<string, long>();
    public string StoragePolicy { get; init; } = "sparse-events-only";
}

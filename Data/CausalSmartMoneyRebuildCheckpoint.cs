namespace Backend.Data;

public sealed class CausalSmartMoneyRebuildCheckpoint
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public string CalculationVersion { get; set; } = "smc-causal-v2";
    public long? LastProcessedOpenTimeMs { get; set; }
    public long? CoverageStartOpenTimeMs { get; set; }
    public long? LatestSegmentStartOpenTimeMs { get; set; }
    public long ProcessedCandleCount { get; set; }
    public long MaterializedEventCount { get; set; }
    public string Status { get; set; } = "not_started";
    public string? LastError { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

namespace Backend.Data;

/// <summary>
/// Canonical, point-in-time Smart Money Concept event reconstructed from finalized Klines.
/// This table is intentionally separate from the legacy SmartMoneyStructures cache.
/// </summary>
public sealed class CausalSmartMoneyEvent
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public long OriginTimeMs { get; set; }
    public long AvailableTimeMs { get; set; }
    public long? ReferenceTimeMs { get; set; }
    public double Price { get; set; }
    public double? HighPrice { get; set; }
    public double? LowPrice { get; set; }
    /// <summary>Latest observed lifecycle state. Historical consumers must gate MitigatedAtMs by their cutoff.</summary>
    public string State { get; set; } = "confirmed";
    public long? MitigatedAtMs { get; set; }
    public long? MitigationSourceOpenTimeMs { get; set; }
    public string CalculationVersion { get; set; } = "smc-causal-v2";
    public long SegmentStartOpenTimeMs { get; set; }
    public long EvaluatedThroughCloseTimeMs { get; set; }
    public int AnalysisCandleCount { get; set; }
    public int DecisionSourceCandleCount { get; set; }
    public string DecisionSourceOpenTimeMsJson { get; set; } = "[]";
    public string DecisionEvidenceJson { get; set; } = "{}";
    public string DecisionEvidenceSha256 { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

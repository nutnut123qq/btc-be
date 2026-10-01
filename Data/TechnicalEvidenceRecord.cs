namespace Backend.Data;

/// <summary>
/// Materialized, versioned technical replay layer. Legacy feature tables remain isolated;
/// this table is populated only from the causal replay reconstruction path.
/// </summary>
public sealed class TechnicalEvidenceRecord
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public string LayerKey { get; set; } = "";
    public long AsOfTimeMs { get; set; }
    public string ModuleContractVersion { get; set; } = "";
    public string ModuleContractSha256 { get; set; } = "";
    public string CalculationVersion { get; set; } = "";
    public string Availability { get; set; } = "unavailable";
    public long? AvailableTimeMs { get; set; }
    public long? SourceStartTimeMs { get; set; }
    public long? SourceEndTimeMs { get; set; }
    public int SourceCandleCount { get; set; }
    public string EnvelopeJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TechnicalEvidenceRebuildCheckpoint
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public string ModuleContractVersion { get; set; } = "";
    public string ModuleContractSha256 { get; set; } = "";
    public long? LastProcessedCloseTimeMs { get; set; }
    public long? CoverageStartCloseTimeMs { get; set; }
    public bool HistoricalBackfill { get; set; }
    public string Status { get; set; } = "pending";
    public long MaterializedRecordCount { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

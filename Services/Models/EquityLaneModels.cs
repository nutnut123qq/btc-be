namespace Backend.Services.Models;

/// <summary>GET /api/equities envelope (EQX spec §2).</summary>
public sealed record EquityInstrumentListResponse(
    string ContractVersion,
    DateTime GeneratedAtUtc,
    bool WorkerActive,
    IReadOnlyList<EquityInstrumentDto> Items)
{
    public const string Version = "equity-instruments-v1";
}

/// <summary>Equity-lane capability flags (crypto-only flags are omitted on this lane).</summary>
public sealed record EquityCapabilitiesDto(bool Ingestion, bool TechnicalAnalysis, bool Decision);

public sealed record EquityInstrumentDto(
    string Symbol,
    string Label,
    string Venue,
    string AssetClass,
    string Currency,
    string PriceBasis,
    string DataProvider,
    string? CrossCheckDataProvider,
    IReadOnlyList<string> ActiveTimeframes,
    string DefaultTimeframe,
    EquityCapabilitiesDto Capabilities,
    EquityIngestionStatusDto Ingestion);

/// <summary>
/// Per-symbol ingestion status. <see cref="Status"/> is data-plane only
/// (up-to-date | pending-provider | failed | gave-up | disabled); worker liveness lives in
/// the worker heartbeat, never here. Timeframes carry the DB-derived freshness facts.
/// </summary>
public sealed record EquityIngestionStatusDto(
    string Status,
    long? SessionOpenTimeMs,
    int Attempts,
    int MaxAttempts,
    DateTime? LastAttemptAtUtc,
    DateTime? LastCompletedAtUtc,
    string? Detail,
    IReadOnlyList<EquityTimeframeFreshnessDto> Timeframes);

public sealed record EquityTimeframeFreshnessDto(
    string Timeframe,
    long BarCount,
    long? LatestStoredOpenTimeMs,
    long? LatestExpectedFinalizedOpenTimeMs,
    bool UpToDate);

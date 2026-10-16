namespace Backend.Services.Models;

/// <summary>Outcome of one technical module inside an MBB recompute run.</summary>
public sealed record MbbRecomputeModuleResult(
    string Module,
    string Status,
    int RowsWritten,
    string? Detail = null);

public static class MbbRecomputeModuleStatuses
{
    /// <summary>Module ran and wrote its artifact rows (count may legitimately be 0).</summary>
    public const string Recomputed = "recomputed";

    /// <summary>Module was not run — input precondition unmet; see Detail.</summary>
    public const string Skipped = "skipped";

    /// <summary>Module threw; artifact rows for this run were left deleted.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Result of <c>MbbTechnicalRecomputeService.RecomputeAsync</c>: a full, idempotent
/// rebuild of the technical artifact tables for one instrument/timeframe from
/// <c>InstrumentCandles</c>. Never reads Binance <c>Klines</c> and never writes a
/// different symbol's rows.
/// </summary>
public sealed record MbbTechnicalRecomputeReport(
    string Symbol,
    string Timeframe,
    int CandleCount,
    string Status,
    DateTime GeneratedAtUtc,
    IReadOnlyList<MbbRecomputeModuleResult> Modules);

public static class MbbRecomputeStatuses
{
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string NoData = "no_data";
}

namespace Backend.Options;

/// <summary>
/// Scheduler options for <see cref="Backend.Services.EquityIngestionWorker"/> (EQX spec §4).
/// Disabled by default and not enabled in any appsettings: the equity lane runs only when an
/// operator turns it on explicitly. Provider calls additionally require
/// <see cref="MbbIngestionOptions.Enabled"/> (the VCI/KBS lane master switch); with that switch
/// off the worker reports every symbol as "disabled" instead of failing.
/// Bound to the "EquityIngestion" configuration section.
/// </summary>
public sealed class EquityIngestionOptions
{
    public const string SectionName = "EquityIngestion";

    /// <summary>Master switch for the hosted worker. false = the worker exits at startup.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Symbols to process, intersected with catalog instruments that carry Ingestion. Empty (the
    /// default) = every Ingestion instrument (MBB, BID, VCB, CTG, VNINDEX). The default must stay
    /// empty: the configuration binder APPENDS array items to a pre-filled default, so a non-empty
    /// default could never be narrowed from appsettings.
    /// </summary>
    public string[] Symbols { get; set; } = [];

    /// <summary>Local (Asia/Ho_Chi_Minh) time after which today's session is processed, "HH:mm".</summary>
    public string RunAfterLocal { get; set; } = "15:15";

    /// <summary>Tick cadence while enabled.</summary>
    public int PollMinutes { get; set; } = 15;

    /// <summary>Per-symbol attempts per session before the session is marked gave-up.</summary>
    public int MaxAttemptsPerSession { get; set; } = 6;

    /// <summary>Wave-2 seam flag: rebuild quant base-rates after observe (no-op until implemented).</summary>
    public bool BaseRatesRebuild { get; set; } = true;

    /// <summary>Wave-2 seam flag: record quant forecasts after base-rates (no-op until implemented).</summary>
    public bool RecordForecasts { get; set; } = true;

    /// <summary>
    /// KBS finance-info (bank fundamentals) call in <see cref="Backend.Services.EquityContextCollector.SyncAsync"/>.
    /// Off by default (VNONLY-1: the lane is technical-only); VCI events and corporate actions, which feed price
    /// adjustment, are always synced. Stored fundamentals stay readable. Host opt-in:
    /// EquityIngestion__KbsFundamentalsEnabled=true.
    /// </summary>
    public bool KbsFundamentalsEnabled { get; set; } = false;
}

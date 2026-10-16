namespace Backend.Options;

/// <summary>
/// Options for the MBB (HOSE equity) ingestion lane. Disabled by default: MBB is a known
/// but not-yet-enabled instrument (InstrumentCatalog), so this lane is wired for manual /
/// explicitly-enabled invocation only and must never join the production BTC scheduler
/// silently. Bound to the "MbbIngestion" configuration section.
/// </summary>
public sealed class MbbIngestionOptions
{
    public const string SectionName = "MbbIngestion";

    /// <summary>Master switch. false = nothing runs, ever, unless explicitly enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Instrument symbol. Locked to a known catalog instrument at sync time.</summary>
    public string Symbol { get; set; } = "MBB";

    /// <summary>MBB v1 production timeframes: exactly 1h and 1d (no synthetic 4h).</summary>
    public string[] Timeframes { get; set; } = ["1d", "1h"];

    public string VciBaseUrl { get; set; } = "https://trading.vietcap.com.vn";
    public string KbsBaseUrl { get; set; } = "https://kbbuddywts.kbsec.com.vn";

    /// <summary>Bars requested in the incremental "latest" fetch per timeframe per cycle.</summary>
    public int LatestCountBack { get; set; } = 400;

    /// <summary>Bars requested per historical/gap-fill page (VCI countBack; max ~5000).</summary>
    public int BatchCountBack { get; set; } = 2000;

    /// <summary>Total provider requests allowed per sync cycle, all timeframes combined.</summary>
    public int MaxRequestsPerCycle { get; set; } = 10;

    /// <summary>Missing-open ranges processed per timeframe per cycle (bounded resume).</summary>
    public int MaxGapRangesPerCycle { get; set; } = 20;

    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int RequestDelayMs { get; set; } = 500;

    /// <summary>Worker cadence when Enabled. Only used if the worker is explicitly registered.</summary>
    public int CycleMinutes { get; set; } = 20;

    /// <summary>Grace after a session close before a bar counts as finalized/expected.</summary>
    public int PublishGraceMinutes { get; set; } = 10;

    /// <summary>Daily history start (local trading date). VCI daily coverage starts 2011-11-01.</summary>
    public string DailyBackfillStartLocal { get; set; } = "2011-11-01";

    /// <summary>Hourly history start (local trading date). VCI hourly coverage starts 2023-09-11.</summary>
    public string HourlyBackfillStartLocal { get; set; } = "2023-09-11";

    /// <summary>
    /// Gap-fill floor for incremental sync (local date). Older gaps are still reported by
    /// data quality, but are not retried every sync cycle: configured holiday coverage is
    /// best-effort from 2020, so pre-2020 expected opens can include permanent non-trading
    /// days that would otherwise starve fresh/latest fetches.
    /// </summary>
    public string GapFillStartLocal { get; set; } = "2020-01-01";

    /// <summary>
    /// Explicit non-trading dates ("yyyy-MM-dd", local Vietnam calendar): public holidays
    /// and known suspension days. These are NON-gaps by construction — the expected-open
    /// set excludes them. Best-effort defaults cover the verified hourly window
    /// (2023-09 -> 2026); an unlisted holiday/suspension surfaces as a pending gap on
    /// trading weekdays, which is the honest failure mode (visible, fixable here) rather
    /// than a silently hidden one.
    /// </summary>
    public string[] NonTradingDates { get; set; } =
    [
        // 2020
        "2020-01-01", "2020-01-23", "2020-01-24", "2020-01-27", "2020-01-28", "2020-01-29",
        "2020-04-02", "2020-04-30", "2020-05-01", "2020-09-02",
        // 2021
        "2021-01-01", "2021-02-10", "2021-02-11", "2021-02-12", "2021-02-15", "2021-02-16",
        "2021-04-21", "2021-04-30", "2021-05-03", "2021-09-02", "2021-09-03", "2021-12-31",
        // 2022
        "2022-01-31", "2022-02-01", "2022-02-02", "2022-02-03", "2022-02-04",
        "2022-04-11", "2022-05-02", "2022-05-03", "2022-09-01", "2022-09-02",
        // 2023
        "2023-01-02", "2023-01-20", "2023-01-23", "2023-01-24", "2023-01-25", "2023-01-26",
        "2023-05-01", "2023-05-02", "2023-05-03", "2023-09-01", "2023-09-04",
        // 2024
        "2024-01-01", "2024-02-08", "2024-02-09", "2024-02-12", "2024-02-13", "2024-02-14",
        "2024-04-18", "2024-04-29", "2024-04-30", "2024-05-01", "2024-09-02", "2024-09-03",
        // 2025
        "2025-01-01", "2025-01-27", "2025-01-28", "2025-01-29", "2025-01-30", "2025-01-31",
        "2025-02-03", "2025-04-07", "2025-04-30", "2025-05-01", "2025-05-02",
        "2025-09-01", "2025-09-02",
        // 2026 (verified horizon through the MBB-00 probe date 2026-10-15)
        "2026-01-01", "2026-02-16", "2026-02-17", "2026-02-18", "2026-02-19", "2026-02-20",
        "2026-04-27", "2026-04-30", "2026-05-01", "2026-09-02",
    ];
}

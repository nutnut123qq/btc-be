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
    /// Per-symbol daily history start (EQX W0 probe, VCI first daily bar). Symbols absent here
    /// (MBB) use <see cref="DailyBackfillStartLocal"/>. VNINDEX starts 2010-01-01 on purpose:
    /// VCI serves ~29 malformed OHLC rows around 2005 that must never be ingested.
    /// </summary>
    public Dictionary<string, string> DailyBackfillStartBySymbol { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BID"] = "2014-01-24",
        ["VCB"] = "2009-06-30",
        ["CTG"] = "2009-07-16",
        ["VNINDEX"] = "2010-01-01",
    };

    /// <summary>
    /// Page cap for the full-history refetch triggered when an incremental sync sees a stored
    /// finalized bar change price (VCI/KBS back-adjust the WHOLE history at every ex-date).
    /// The refetch only writes once every page is in hand, so the DB never mixes two
    /// adjustment factors; hitting the cap writes nothing and reports an error instead.
    /// </summary>
    public int RevisionRefetchMaxPages { get; set; } = 12;

    /// <summary>
    /// Gap-fill floor for incremental sync (local date). Older gaps are still reported by
    /// data quality, but are not retried every sync cycle: configured holiday coverage is
    /// best-effort from 2020, so pre-2020 expected opens can include permanent non-trading
    /// days that would otherwise starve fresh/latest fetches.
    /// </summary>
    public string GapFillStartLocal { get; set; } = "2020-01-01";

    /// <summary>
    /// Copy of these options scoped to one catalog symbol (EQX: one ingestion lane instance per
    /// symbol). Timeframes are still intersected with the instrument's ActiveTimeframes by the
    /// services; the daily start comes from <see cref="DailyBackfillStartBySymbol"/> when set.
    /// For MBB the copy is value-identical to the original (alias parity).
    /// </summary>
    public MbbIngestionOptions ForSymbol(string symbol)
    {
        var copy = (MbbIngestionOptions)MemberwiseClone();
        copy.Symbol = symbol;
        copy.Timeframes = (string[])Timeframes.Clone();
        copy.NonTradingDates = (string[])NonTradingDates.Clone();
        copy.DailyBackfillStartBySymbol = new Dictionary<string, string>(DailyBackfillStartBySymbol, StringComparer.OrdinalIgnoreCase);
        if (DailyBackfillStartBySymbol.TryGetValue(symbol, out var start) && !string.IsNullOrWhiteSpace(start))
            copy.DailyBackfillStartLocal = start;
        return copy;
    }

    /// <summary>
    /// OVERRIDE ONLY (EQX v2 A1): extra non-trading dates ("yyyy-MM-dd", local Vietnam calendar) unioned on top
    /// of the canonical calendar file <c>Data/Calendars/vn-hose-non-trading-days.json</c>. Empty by default;
    /// every override date is reported by data quality / GET /api/market-calendar/vn-hose as unverified and is
    /// synced to MarketCalendarDays so the Python jobs see the same set. Never add a date only because bars are missing.
    /// </summary>
    public string[] NonTradingDates { get; set; } = [];
}

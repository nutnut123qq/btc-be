using System.Net;

namespace Backend.Services.Models;

/// <summary>MBB lane timeframes — locked to the MBB-00/MBB-01 contract (1h, 1d; no 4h).</summary>
public static class EquityCandleTimeframes
{
    public const string Hour = "1h";
    public const string Day = "1d";
}

/// <summary>
/// Provider-normalized OHLCV bar. Open time is Unix ms UTC; the provider does not supply
/// a close time, so the session-aware close is assigned at write time by the calendar.
/// </summary>
public sealed record ProviderOhlcvBar(
    long OpenTimeMs,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>Transport/HTTP failure from a market-data provider. Never produces fake bars.</summary>
public sealed class MarketDataProviderException : Exception
{
    public MarketDataProviderException(string provider, string message, HttpStatusCode? statusCode = null,
        bool isTransient = false, Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        StatusCode = statusCode;
        IsTransient = isTransient;
    }

    public string Provider { get; }
    public HttpStatusCode? StatusCode { get; }
    public bool IsTransient { get; }
}

/// <summary>
/// Provider payload failed strict shape/semantic validation. Payload problems are never
/// retried — a malformed contract needs investigation, not another request.
/// </summary>
public sealed class MarketDataPayloadException : Exception
{
    public MarketDataPayloadException(string provider, string message)
        : base($"{provider}: {message}")
    {
        Provider = provider;
    }

    public string Provider { get; }
}

/// <summary>One contiguous run of missing expected session opens.</summary>
public sealed record CandleGapRange(long StartOpenTimeMs, long EndOpenTimeMs, long MissingBars);

/// <summary>Per-timeframe outcome of one ingestion sync.</summary>
public sealed record MbbTimeframeSync(
    string Timeframe,
    int FetchedBars,
    int InsertedBars,
    int UpdatedBars,
    int SkippedFormingBars,
    int RejectedInvalidBars,
    long MissingExpectedBars,
    int GapRangesProcessed,
    int GapRangesRemaining,
    int FetchedRequests,
    string? Error = null,
    int RevisionRefetchRequests = 0,
    string? RevisionRefetchStatus = null);

/// <summary>
/// Outcome of the full-history refetch an incremental sync runs when a stored finalized bar
/// changed price (provider re-adjusted the whole history). Null = no revision detected.
/// </summary>
public static class RevisionRefetchStatuses
{
    /// <summary>Whole history refetched and rewritten in one save (single adjustment basis).</summary>
    public const string Completed = "completed";

    /// <summary>Page cap hit before covering history: nothing written, retried next cycle.</summary>
    public const string Incomplete = "incomplete";
}

public sealed record MbbSyncReport(
    string Symbol,
    DateTime GeneratedAtUtc,
    string Status,
    IReadOnlyList<MbbTimeframeSync> Timeframes);

public static class MbbSyncStatuses
{
    public const string Succeeded = "Succeeded";
    public const string Partial = "Partial";
    public const string Failed = "Failed";
    public const string Disabled = "Disabled";
}

/// <summary>Per-timeframe coverage/freshness block of the MBB data-quality report.</summary>
public sealed record MbbTimeframeCoverage(
    string Timeframe,
    long TotalBars,
    DateTimeOffset? FirstOpenUtc,
    DateTimeOffset? LastOpenUtc,
    long ExpectedBars,
    long MissingBars,
    double CoveragePct,
    long GapRangeCount,
    IReadOnlyList<CandleGapRange> TopGaps,
    long UnexpectedSessionBars,
    IReadOnlyList<long> UnexpectedSessionOpenTimeMs,
    long InvalidOhlcvRows,
    DateTimeOffset? LatestExpectedFinalizedOpenUtc,
    long? LatestBarAgeSeconds,
    bool IsStale,
    // EQX v2 A1 (additive): every missing expected bar classified against the other equity-lane
    // instruments + VNINDEX on the same local date.
    long MarketWideMissingBars = 0,
    long SymbolMissingBars = 0,
    long NoPeerDataMissingBars = 0,
    IReadOnlyList<MbbClassifiedGapRange>? TopClassifiedGaps = null,
    long UnverifiedNonTradingDaysInRange = 0,
    // EQX v2 review #1 (additive): missing expected bars on dates the calendar does not cover are not real
    // gaps (holidays there are unknown); CalendarFlags carries below-calendar-coverage / calendar-coverage-expired.
    long BelowCalendarCoverageMissingBars = 0,
    long CalendarCoverageExpiredMissingBars = 0,
    IReadOnlyList<string>? CalendarFlags = null,
    string? FreshnessReason = null);

/// <summary>Gap classes for a missing expected open (EQX v2 A1).</summary>
public static class MbbGapClassifications
{
    /// <summary>Every other scope instrument + VNINDEX also lacks a bar that date: suspected unverified closure (not auto-added to the calendar).</summary>
    public const string MarketWideMissing = "market-wide-missing";

    /// <summary>Another scope instrument has a bar that date: provider gap or symbol suspension (no reason is inferred).</summary>
    public const string SymbolMissing = "symbol-missing";

    /// <summary>No other scope instrument has stored history covering that date, so it cannot be classified.</summary>
    public const string NoPeerData = "no-peer-data";

    /// <summary>Date before the calendar coverage: holidays unknown, so this is NOT counted as a real gap.</summary>
    public const string BelowCalendarCoverage = "below-calendar-coverage";

    /// <summary>Date after the calendar coverage: holidays unknown (fail closed, not a real gap either).</summary>
    public const string CalendarCoverageExpired = "calendar-coverage-expired";
}

public sealed record MbbClassifiedGapRange(
    long StartOpenTimeMs,
    long EndOpenTimeMs,
    long MissingBars,
    string Classification);

/// <summary>Calendar provenance block of the coverage report (EQX v2 A1).</summary>
public sealed record MbbCalendarInfo(
    string Source,
    string? CalendarVersion,
    string? CoverageFromDate,
    string? CoverageToDate,
    int VerifiedDays,
    int UnverifiedLegacyDays,
    IReadOnlyList<string> ConfigOverrideDates,
    IReadOnlyList<string> PeerSymbols);

public sealed record MbbCoverageReport(
    string Symbol,
    string Provider,
    string CrossCheckProvider,
    string PriceBasis,
    string Venue,
    string SourceTimezone,
    DateTime GeneratedAtUtc,
    IReadOnlyList<MbbTimeframeCoverage> Timeframes,
    IReadOnlyList<string> Limitations,
    MbbCalendarInfo? Calendar = null);

public sealed record MbbCrossCheckMismatch(
    long OpenTimeMs,
    decimal PrimaryClose,
    decimal CrossCheckClose,
    decimal MaxAbsPriceDiff);

public sealed record MbbCrossCheckReport(
    string Symbol,
    string PrimaryProvider,
    string CrossCheckProvider,
    string Timeframe,
    int ComparedBars,
    int OnlyInPrimary,
    int OnlyInCrossCheck,
    int PriceMismatchCount,
    IReadOnlyList<MbbCrossCheckMismatch> TopMismatches);

/// <summary>
/// OHLCV bar served on the MBB lane, shaped like <see cref="KlineDto"/> so shared
/// chart code can consume it unchanged. The Binance-only fields are nullable and
/// always null here: equity providers do not publish quote volume, trade counts
/// or taker-buy splits, and zeroes would be read as real measurements (the same
/// reason MBB does not write into the <see cref="Backend.Data.Kline"/> table).
/// </summary>
/// <summary>
/// Slim projection of one MbbSignalObservation row for GET /api/mbb/observations.
/// The full decision DTO stays in the table (provenance) — not re-served per row.
/// </summary>
public sealed class MbbObservationDto
{
    public string DecisionId { get; set; } = string.Empty;
    public string RecorderVersion { get; set; } = string.Empty;

    /// <summary>Instrument symbol (additive, EQX): MBB on /api/mbb, any Decision equity on /api/equities.</summary>
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public long SignalBarOpenTimeMs { get; set; }
    public long SignalBarCloseTimeMs { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string? EvidenceStatus { get; set; }
    public string? PriceBasis { get; set; }
    public decimal? MarketCloseVnd { get; set; }
    public decimal? FillPrice { get; set; }
    public DateTimeOffset? FillObservedAtUtc { get; set; }
    public string? FillSource { get; set; }
    public long? FillBarOpenTimeMs { get; set; }
    public double? OutcomeReturn { get; set; }
    public DateTimeOffset? OutcomeObservedAtUtc { get; set; }
    public string? OutcomeHorizon { get; set; }
}

public sealed class MbbCandleDto
{
    public long OpenTimeMs { get; set; }
    public string TimeIso { get; set; } = string.Empty;
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public long CloseTimeMs { get; set; }
    public decimal? QuoteVolume { get; set; }
    public int? TradeCount { get; set; }
    public decimal? TakerBuyVolume { get; set; }
    public decimal? TakerBuyQuoteVolume { get; set; }
}

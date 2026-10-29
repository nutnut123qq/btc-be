using System.Text.Json.Nodes;

namespace Backend.Services.Models;

// EQTECH-1 lane T: GET /api/equities/{symbol}/technical (equity-technical-v1). One technical-only payload an AI
// in chat reads to analyse MBB/BID from candles: no fundamentals, events, corporate actions, documents or news.
// Every number is computed or stored by the backend; the payload never asks the reader for a probability.

public static class EquityTechnicalContracts
{
    public const string Version = "equity-technical-v1";

    // The scoring block's ruleVersion and rule text come from EquityDirectionScoringService.RuleVersion / .Rule
    // (the scorer behind GET research-notes/direction-scores is the single source of truth).

    public const string ProbabilityPolicyOmit = "omit";

    public const int DefaultBars = 100;
    public const int MinBars = 1;
    public const int MaxBars = 500;
}

public sealed record EquityTechnicalResponse(
    string ContractVersion,
    string Symbol,
    DateTime AsOfUtc,
    DateTime GeneratedAtUtc,
    string PriceBasis,
    string DataProvider,
    int BarsRequested,
    int BarsApplied,
    IReadOnlyList<EquityTechnicalTimeframeDto> Timeframes,
    EquityBaseRatesResponse? BaseRates,
    string? BaseRatesUnavailableReason,
    EquityRelativeStrengthSection RelativeStrength,
    EquityTechnicalCalendarDto Calendar,
    EquityTechnicalNoteBindingDto? NoteBinding,
    string? NoteBindingUnavailableReason,
    EquityTechnicalScoringDto Scoring,
    IReadOnlyList<string> Limitations);

public sealed record EquityTechnicalTimeframeDto(
    string Timeframe,
    EquityTechnicalFreshnessDto Freshness,
    IReadOnlyList<EquityTechnicalCandleDto> Candles,
    EquityTechnicalIndicatorsDto? Indicators,
    string? IndicatorsUnavailableReason,
    IReadOnlyList<EquityTechnicalPatternDto> CandlePatterns,
    EquityTechnicalConditionsDto Conditions,
    EquityTechnicalDataQualityDto DataQuality);

/// <summary>
/// latestStoredOpenTimeMs = newest stored FINALIZED bar; expectedLatestOpenTimeMs = newest bar the HOSE calendar says
/// must already be finalized. isStale comes from the existing rules: the data-quality report's freshness (publish +
/// cycle grace, fails closed past the calendar coverage) when that report has the timeframe ("data-quality"), else the
/// instrument-list rule latest &lt; expected ("instrument-list").
/// </summary>
public sealed record EquityTechnicalFreshnessDto(
    long? LatestStoredOpenTimeMs,
    long? ExpectedLatestOpenTimeMs,
    bool IsStale,
    string StaleRule,
    string? Reason);

public sealed record EquityTechnicalCandleDto(
    long OpenTimeMs, long CloseTimeMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>Latest stored TechnicalIndicators row (every stored value) + the formula each value was computed with.</summary>
public sealed record EquityTechnicalIndicatorsDto(
    long OpenTimeMs,
    bool IsLatestCandle,
    double? Rsi14,
    decimal? Ema12,
    decimal? Ema26,
    decimal? Ema50,
    decimal? Ema200,
    decimal? Sma50,
    decimal? Sma200,
    double? Macd,
    double? MacdSignal,
    double? MacdHistogram,
    double? MacdNorm,
    double? MacdSignalNorm,
    double? MacdHistogramNorm,
    decimal? BollingerUpper,
    decimal? BollingerMiddle,
    decimal? BollingerLower,
    double? Atr14,
    double? Obv,
    double? ObvEma50,
    decimal? Vwap,
    decimal? RollingVwap24,
    IReadOnlyDictionary<string, string> Methods);

/// <summary>
/// A stored CandlePatterns row whose open time lies inside the returned candle window. openTimeMs is the FIRST bar of
/// the pattern; spanBars is 1/2/3 (Single/Double/Triple); lastBarOpenTimeMs is the pattern's last bar when it is
/// inside the window.
/// </summary>
public sealed record EquityTechnicalPatternDto(
    long OpenTimeMs,
    long? LastBarOpenTimeMs,
    string PatternType,
    string PatternCategory,
    int? SpanBars,
    string TrendDirection);

/// <summary>
/// The AI current-conditions payload for this timeframe (same service + cache as GET conditions). status ok|unavailable;
/// a failure never fails the whole technical payload. alignedWithLatestCandle = payload.latestClosedBar.openTimeMs
/// equals the newest returned candle.
/// </summary>
public sealed record EquityTechnicalConditionsDto(
    string Status,
    string? Reason,
    JsonObject? Payload,
    bool? AlignedWithLatestCandle);

/// <summary>
/// Compact data-quality summary. The counts come from MbbDataQualityService (expected finalized bars from the configured
/// backfill start to today); inCoverage is computed on the same HOSE calendar from the first stored bar (clipped to the
/// calendar coverage) and adds the per-local-open breakdown (intraday) and the most recent missing bars.
/// </summary>
public sealed record EquityTechnicalDataQualityDto(
    string Status,
    string? Reason,
    long? ExpectedBars,
    long? MissingBars,
    double? CoveragePct,
    long? GapRangeCount,
    long? SymbolMissingBars,
    long? MarketWideMissingBars,
    long? NoPeerDataMissingBars,
    long? BelowCalendarCoverageMissingBars,
    long? UnexpectedSessionBars,
    long? InvalidOhlcvRows,
    IReadOnlyList<string> CalendarFlags,
    EquityTechnicalInCoverageGapsDto? InCoverage,
    IReadOnlyList<EquityTechnicalCaveatDto> Caveats);

public sealed record EquityTechnicalInCoverageGapsDto(
    long FromOpenTimeMs,
    long ToOpenTimeMs,
    long ExpectedBars,
    long MissingBars,
    IReadOnlyDictionary<string, long>? MissingByLocalOpen,
    IReadOnlyList<long> LatestMissingOpenTimeMs,
    long MissingInCandleWindow);

public sealed record EquityTechnicalCaveatDto(string Code, string Text);

/// <summary>
/// Forward-note window, mirroring POST research-notes: signal bar = latest expected finalized 1d bar; the window closes
/// at the next session's 09:00 open (EquityForecastJournalService.TryNextSessionOpenUtcMs).
/// </summary>
public sealed record EquityTechnicalCalendarDto(
    string Source,
    string? CalendarVersion,
    string? CoverageFrom,
    string? CoverageTo,
    long? SignalBarOpenTimeMs,
    bool SignalBarStored,
    long? NextSessionOpenUtcMs,
    bool ForwardNoteOpen,
    string? Reason);

/// <summary>
/// What POST research-notes needs to bind a note to the snapshot the backend rebuilds: send requestedAsOf back verbatim
/// (epoch ms), with snapshotId, snapshotSha256 and signalBarOpenTimeMs.
/// </summary>
public sealed record EquityTechnicalNoteBindingDto(
    string SnapshotId,
    string SnapshotSha256,
    string RequestedAsOf,
    long SignalBarOpenTimeMs);

public sealed record EquityTechnicalScoringDto(
    string RuleVersion,
    string Rule,
    IReadOnlyList<EquityTechnicalScoringHorizonDto> Horizons,
    string ProbabilityPolicy,
    string ProbabilityInstruction,
    string ScoresEndpoint);

/// <summary>
/// Signed fractions taken from EquityBarrierConfigs.All, as in direction-scores: up if R >= UpThresholdPct (0.05 = +5 %),
/// down if R <= DownThresholdPct (-0.04 = -4 %).
/// </summary>
public sealed record EquityTechnicalScoringHorizonDto(
    string ConfigId,
    int HorizonSessions,
    double UpThresholdPct,
    double DownThresholdPct);

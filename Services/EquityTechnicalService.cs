using System.Globalization;
using System.Text.Json.Nodes;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// GET /api/equities/{symbol}/technical (equity-technical-v1, EQTECH-1 lane T): one technical-only payload for an AI
/// in chat. Per catalog ActiveTimeframe: freshness, the last N stored finalized bars, the latest stored
/// TechnicalIndicators row with the formula of every value, CandlePatterns inside the candle window, the AI
/// current-conditions payload (failure ⇒ that entry unavailable, never a failed response) and a compact data-quality
/// summary. Plus base rates exactly as GET base-rates, relative strength, the forward-note calendar window, the binding
/// a research note needs (EquityResearchSnapshotService at the same asOf) and the direction-scoring rule.
/// Never reads fundamentals, events, corporate actions, documents or news into the response; never fetches providers.
/// Market-only: no position, lots or cash.
/// </summary>
public sealed class EquityTechnicalService
{
    /// <summary>Unfinalized rows (the forming session) skipped at the head of the newest-first read.</summary>
    private const int FormingBarsMargin = 8;
    private const int LatestMissingShown = 5;

    public const string ConditionsSourceUnavailable = "conditions-source-unavailable";
    public const string ConditionsPayloadInvalid = "conditions-payload-invalid";
    public const string ConditionsSectionError = "conditions-section-error";

    /// <summary>
    /// How each stored indicator value is computed, read from TechnicalIndicatorIndexer (FillSegment and the Compute*
    /// helpers) and MbbTechnicalRecomputeService.BuildSessionAdjacency; keys are the payload's indicator field names.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> IndicatorMethods =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["segmentation"] = "Every indicator restarts with a null warm-up after a missing expected bar: two stored bars are adjacent only when they are consecutive expected opens of the HOSE calendar (lunch, overnight, weekend and holiday breaks stay adjacent). MbbTechnicalRecomputeService.BuildSessionAdjacency + TechnicalIndicatorIndexer.CalculateIndicators.",
            ["rsi14"] = "Wilder RSI(14) on closes: the first value averages the first 14 gains and losses of the segment, then avg = (prev * 13 + current) / 14; RSI = 100 - 100 / (1 + avgGain / avgLoss), 100 when avgLoss = 0 (50 when both are 0). Not the conditions-payload RSI14, which is a simple-average (Cutler) RSI over the last 14 changes (ai/technical_event_modules.py _rsi).",
            ["ema12"] = "EMA(12) of closes, alpha = 2 / 13, seeded with the simple mean of the segment's first 12 closes (null before).",
            ["ema26"] = "EMA(26) of closes, alpha = 2 / 27, seeded with the simple mean of the segment's first 26 closes (null before).",
            ["ema50"] = "EMA(50) of closes, alpha = 2 / 51, seeded with the simple mean of the segment's first 50 closes (null before).",
            ["ema200"] = "EMA(200) of closes, alpha = 2 / 201, seeded with the simple mean of the segment's first 200 closes (null before).",
            ["sma50"] = "Simple mean of the last 50 closes.",
            ["sma200"] = "Simple mean of the last 200 closes.",
            ["macd"] = "ema12 - ema26.",
            ["macdSignal"] = "EMA(9) of macd, alpha = 2 / 10, seeded with the simple mean of the first 9 macd values.",
            ["macdHistogram"] = "macd - macdSignal.",
            ["macdNorm"] = "macd / atr14 (null when atr14 is null or 0).",
            ["macdSignalNorm"] = "macdSignal / atr14 (null when atr14 is null or 0).",
            ["macdHistogramNorm"] = "macdHistogram / atr14 (null when atr14 is null or 0).",
            ["bollingerUpper"] = "bollingerMiddle + 2 x population standard deviation of the last 20 closes.",
            ["bollingerMiddle"] = "Simple mean of the last 20 closes.",
            ["bollingerLower"] = "bollingerMiddle - 2 x population standard deviation of the last 20 closes.",
            ["atr14"] = "Wilder ATR(14): true range = max(high - low, |high - prevClose|, |low - prevClose|) (the segment's first bar uses its own open as prevClose); the first value is the simple mean of 14 true ranges, then (prev * 13 + TR) / 14.",
            ["obv"] = "On-balance volume accumulated from 0 at the segment start: + volume when close > previous close, - volume when close < previous close.",
            ["obvEma50"] = "EMA(50) of obv, alpha = 2 / 51, seeded with the simple mean of the first 50 obv values.",
            ["vwap"] = "Sum(typical price x volume) / sum(volume), typical price = (high + low + close) / 3, reset at every UTC day (openTimeMs / 86,400,000): one HOSE session for 1h bars, the bar's own typical price for 1d bars.",
            ["rollingVwap24"] = "Volume-weighted typical price over the last 24 bars (no reset).",
        };

    private readonly AppDbContext _db;
    private readonly VietnamMarketCalendar _calendar;
    private readonly MbbDataQualityService _quality;
    private readonly EquityBaseRatesService _baseRates;
    private readonly EquityContextService _context;
    private readonly EquityResearchSnapshotService _snapshots;
    private readonly CurrentConditionsService _conditions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EquityTechnicalService> _logger;

    public EquityTechnicalService(
        AppDbContext db,
        VietnamMarketCalendar calendar,
        MbbDataQualityService quality,
        EquityBaseRatesService baseRates,
        EquityContextService context,
        EquityResearchSnapshotService snapshots,
        CurrentConditionsService conditions,
        TimeProvider? timeProvider = null,
        ILogger<EquityTechnicalService>? logger = null)
    {
        _db = db;
        _calendar = calendar;
        _quality = quality;
        _baseRates = baseRates;
        _context = context;
        _snapshots = snapshots;
        _conditions = conditions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityTechnicalService>.Instance;
    }

    public static int ClampBars(int requested) =>
        Math.Clamp(requested, EquityTechnicalContracts.MinBars, EquityTechnicalContracts.MaxBars);

    /// <summary>Payload for a TechnicalAnalysis-capable instrument already resolved through <see cref="EquityScope"/>.</summary>
    public async Task<EquityTechnicalResponse> BuildAsync(
        InstrumentDescriptor instrument, int barsRequested, CancellationToken ct = default)
    {
        var bars = ClampBars(barsRequested);
        var sym = instrument.Symbol;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var asOf = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
        var timeframes = instrument.ActiveTimeframes.ToArray();

        // AI calls (no DB) start first so they overlap the DB reads below; each one is failure-isolated.
        var conditionTasks = timeframes.ToDictionary(tf => tf, tf => ConditionsAsync(sym, tf, ct), StringComparer.Ordinal);

        MbbCoverageReport? report = null;
        string? reportError = null;
        try
        {
            report = await _quality.ForSymbol(sym).BuildReportAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Data-quality report failed for {Symbol}", sym);
            reportError = "data-quality-report-failed";
        }

        var entries = new List<EquityTechnicalTimeframeDto>(timeframes.Length);
        foreach (var tf in timeframes)
            entries.Add(await TimeframeAsync(sym, tf, bars, nowMs, report, reportError, conditionTasks[tf], ct));

        var hasDecision = EquityScope.Has(instrument, EquityCapability.Decision);
        var baseRates = hasDecision ? await _baseRates.GetAsync(sym, ct) : null;
        var relativeStrength = await _context.RelativeStrengthAsync(sym, nowMs, ct);

        // Note binding: the same snapshot POST research-notes rebuilds for requestedAsOf. Conditions above already
        // filled the shared cache, so the snapshot's 1d conditions read is normally a cache hit.
        EquityResearchSnapshot? snapshot = null;
        string? bindingReason = null;
        if (!hasDecision)
        {
            bindingReason = "no-decision-capability";
        }
        else
        {
            try
            {
                snapshot = await _snapshots.BuildAsync(sym, asOf, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Research snapshot for the note binding failed for {Symbol}", sym);
                bindingReason = "snapshot-build-failed";
            }
        }
        EquityTechnicalNoteBindingDto? binding = null;
        if (snapshot is not null)
        {
            if (snapshot.SignalBar is null)
                bindingReason = EquityResearchSnapshotService.ReasonNoSignalBar;
            else
                binding = new EquityTechnicalNoteBindingDto(snapshot.SnapshotId, snapshot.SnapshotSha256,
                    nowMs.ToString(CultureInfo.InvariantCulture), snapshot.SignalBar.OpenTimeMs);
        }

        var calendar = await CalendarAsync(sym, nowMs, binding, snapshot, ct);
        var scoring = Scoring(sym);
        var limitations = Limitations(instrument, baseRates, report);

        return new EquityTechnicalResponse(
            EquityTechnicalContracts.Version, sym, asOf.UtcDateTime, _timeProvider.GetUtcNow().UtcDateTime,
            instrument.PriceBasis, instrument.DataProvider, barsRequested, bars, entries,
            baseRates, hasDecision ? null : "no-decision-capability",
            relativeStrength, calendar, binding, binding is null ? bindingReason : null, scoring, limitations);
    }

    private async Task<EquityTechnicalConditionsDto> ConditionsAsync(string sym, string tf, CancellationToken ct)
    {
        try
        {
            var result = await _conditions.GetAsync(sym, tf, ct);
            if (result.Outcome == CurrentConditionsOutcome.Ok && result.Payload is not null)
                return new EquityTechnicalConditionsDto("ok", null, (JsonObject)result.Payload.DeepClone(), null);
            return new EquityTechnicalConditionsDto("unavailable",
                result.Outcome == CurrentConditionsOutcome.SourceUnavailable ? ConditionsSourceUnavailable : ConditionsPayloadInvalid,
                null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Conditions failed for {Symbol} {Timeframe}", sym, tf);
            return new EquityTechnicalConditionsDto("unavailable", ConditionsSectionError, null, null);
        }
    }

    private async Task<EquityTechnicalTimeframeDto> TimeframeAsync(
        string sym, string tf, int bars, long nowMs, MbbCoverageReport? report, string? reportError,
        Task<EquityTechnicalConditionsDto> conditionsTask, CancellationToken ct)
    {
        var recent = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == sym && c.Timeframe == tf && c.OpenTimeMs <= nowMs)
            .OrderByDescending(c => c.OpenTimeMs)
            .Take(bars + FormingBarsMargin)
            .ToListAsync(ct);
        var candles = recent
            .Where(c => _calendar.IsFinalized(tf, c.OpenTimeMs, nowMs))
            .Take(bars)
            .OrderBy(c => c.OpenTimeMs)
            .Select(c => new EquityTechnicalCandleDto(c.OpenTimeMs, c.CloseTimeMs, c.Open, c.High, c.Low, c.Close, c.Volume))
            .ToArray();
        long? latest = candles.Length == 0 ? null : candles[^1].OpenTimeMs;

        var expected = _calendar.LastExpectedFinalizedOpenUtcMs(tf, nowMs);
        var block = report?.Timeframes.FirstOrDefault(b => string.Equals(b.Timeframe, tf, StringComparison.Ordinal));
        var freshness = block is not null
            ? new EquityTechnicalFreshnessDto(latest, expected, block.IsStale, "data-quality", block.FreshnessReason)
            : new EquityTechnicalFreshnessDto(latest, expected,
                !(latest is not null && expected is not null && latest >= expected), "instrument-list", null);

        EquityTechnicalIndicatorsDto? indicators = null;
        string? indicatorsReason = null;
        if (latest is null)
        {
            indicatorsReason = "no-finalized-candles";
        }
        else
        {
            var row = await _db.TechnicalIndicators.AsNoTracking()
                .Where(x => x.Symbol == sym && x.Timeframe == tf && x.OpenTimeMs <= latest.Value)
                .OrderByDescending(x => x.OpenTimeMs)
                .FirstOrDefaultAsync(ct);
            if (row is null) indicatorsReason = "no-indicator-row";
            else indicators = ToIndicators(row, latest.Value);
        }

        var patterns = await PatternsAsync(sym, tf, candles, ct);

        var conditions = await conditionsTask;
        if (conditions is { Status: "ok", Payload: { } payload })
        {
            long? barOpen = payload["latestClosedBar"] is JsonObject bar && bar["openTimeMs"] is JsonValue v
                && v.TryGetValue<long>(out var open) ? open : null;
            conditions = conditions with { AlignedWithLatestCandle = latest is not null && barOpen == latest };
        }

        var inCoverage = await InCoverageAsync(sym, tf, nowMs, candles, ct);
        var caveats = Caveats(sym, tf, freshness, block, inCoverage, indicators, indicatorsReason, candles.Length);
        var dataQuality = block is null
            ? new EquityTechnicalDataQualityDto("unavailable", reportError ?? "timeframe-not-in-data-quality-report",
                null, null, null, null, null, null, null, null, null, null, [], inCoverage, caveats)
            : new EquityTechnicalDataQualityDto("ok", null, block.ExpectedBars, block.MissingBars, block.CoveragePct,
                block.GapRangeCount, block.SymbolMissingBars, block.MarketWideMissingBars, block.NoPeerDataMissingBars,
                block.BelowCalendarCoverageMissingBars, block.UnexpectedSessionBars, block.InvalidOhlcvRows,
                block.CalendarFlags ?? [], inCoverage, caveats);

        return new EquityTechnicalTimeframeDto(tf, freshness, candles, indicators, indicatorsReason, patterns,
            conditions, dataQuality);
    }

    private static EquityTechnicalIndicatorsDto ToIndicators(TechnicalIndicator r, long latestOpen) => new(
        r.OpenTimeMs, r.OpenTimeMs == latestOpen,
        r.Rsi14, r.Ema12, r.Ema26, r.Ema50, r.Ema200, r.Sma50, r.Sma200,
        r.Macd, r.MacdSignal, r.MacdHistogram, r.MacdNorm, r.MacdSignalNorm, r.MacdHistogramNorm,
        r.BollingerUpper, r.BollingerMiddle, r.BollingerLower, r.Atr14, r.Obv, r.ObvEma50, r.Vwap, r.RollingVwap24,
        IndicatorMethods);

    private async Task<IReadOnlyList<EquityTechnicalPatternDto>> PatternsAsync(
        string sym, string tf, IReadOnlyList<EquityTechnicalCandleDto> candles, CancellationToken ct)
    {
        if (candles.Count == 0) return [];
        var first = candles[0].OpenTimeMs;
        var last = candles[^1].OpenTimeMs;
        var rows = await _db.CandlePatterns.AsNoTracking()
            .Where(p => p.Symbol == sym && p.Timeframe == tf && p.OpenTimeMs >= first && p.OpenTimeMs <= last)
            .ToListAsync(ct);
        var index = new Dictionary<long, int>(candles.Count);
        for (var i = 0; i < candles.Count; i++) index[candles[i].OpenTimeMs] = i;
        return rows
            .OrderBy(p => p.OpenTimeMs)
            .ThenBy(p => p.PatternType, StringComparer.Ordinal)
            .Select(p =>
            {
                int? span = p.PatternCategory switch { "Single" => 1, "Double" => 2, "Triple" => 3, _ => null };
                long? lastBar = span is { } s && index.TryGetValue(p.OpenTimeMs, out var at) && at + s - 1 < candles.Count
                    ? candles[at + s - 1].OpenTimeMs
                    : null;
                return new EquityTechnicalPatternDto(p.OpenTimeMs, lastBar, p.PatternType, p.PatternCategory, span,
                    p.TrendDirection);
            })
            .ToArray();
    }

    /// <summary>
    /// Missing expected bars from the first stored bar (clipped to the HOSE calendar coverage) to now, on the same
    /// calendar as the data-quality report; nothing is inferred about why a bar is missing.
    /// </summary>
    private async Task<EquityTechnicalInCoverageGapsDto?> InCoverageAsync(
        string sym, string tf, long nowMs, IReadOnlyList<EquityTechnicalCandleDto> candles, CancellationToken ct)
    {
        var firstOpen = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == sym && c.Timeframe == tf)
            .Select(c => (long?)c.OpenTimeMs)
            .MinAsync(ct);
        if (firstOpen is null) return null;
        var from = LocalDate(firstOpen.Value);
        var to = LocalDate(nowMs);
        if (_calendar.CoverageFrom is { } coverageFrom && from < coverageFrom) from = coverageFrom;
        if (_calendar.CoverageTo is { } coverageTo && to > coverageTo) to = coverageTo;
        if (from > to) return null;
        var expected = _calendar.EnumerateExpectedOpensUtcMs(tf, from, to)
            .Where(o => o >= firstOpen.Value && _calendar.IsFinalized(tf, o, nowMs))
            .ToArray();
        if (expected.Length == 0) return null;
        var lo = expected[0];
        var hi = expected[^1];
        var stored = (await _db.InstrumentCandles.AsNoTracking()
                .Where(c => c.Symbol == sym && c.Timeframe == tf && c.OpenTimeMs >= lo && c.OpenTimeMs <= hi)
                .Select(c => c.OpenTimeMs)
                .ToListAsync(ct))
            .ToHashSet();
        var missing = expected.Where(o => !stored.Contains(o)).ToArray();
        SortedDictionary<string, long>? byOpen = null;
        if (tf != EquityCandleTimeframes.Day)
        {
            byOpen = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var o in missing)
            {
                var key = VietnamMarketCalendar.ToLocal(o).ToString("HH:mm", CultureInfo.InvariantCulture);
                byOpen[key] = byOpen.GetValueOrDefault(key) + 1;
            }
        }
        var latestMissing = missing.Reverse().Take(LatestMissingShown).ToArray();
        var inWindow = candles.Count == 0
            ? 0
            : missing.LongCount(o => o >= candles[0].OpenTimeMs && o <= candles[^1].OpenTimeMs);
        return new EquityTechnicalInCoverageGapsDto(lo, hi, expected.Length, missing.Length, byOpen, latestMissing, inWindow);
    }

    private static List<EquityTechnicalCaveatDto> Caveats(
        string sym, string tf, EquityTechnicalFreshnessDto freshness, MbbTimeframeCoverage? block,
        EquityTechnicalInCoverageGapsDto? inCoverage, EquityTechnicalIndicatorsDto? indicators, string? indicatorsReason,
        int candleCount)
    {
        var list = new List<EquityTechnicalCaveatDto>();
        if (candleCount == 0)
            list.Add(new("no-finalized-candles", $"No finalized {tf} bars are stored for {sym}."));
        if (freshness.IsStale)
            list.Add(new("stale",
                $"The newest stored finalized {tf} bar ({Local(freshness.LatestStoredOpenTimeMs)}) is behind the newest bar the HOSE calendar expects ({Local(freshness.ExpectedLatestOpenTimeMs)}); rule {freshness.StaleRule}{(freshness.Reason is null ? "" : $", {freshness.Reason}")}."));
        if (inCoverage is { MissingBars: > 0 } gaps)
            list.Add(new("missing-expected-bars",
                $"{gaps.MissingBars} of {gaps.ExpectedBars} expected finalized {tf} bars between {Local(gaps.FromOpenTimeMs)} and {Local(gaps.ToOpenTimeMs)} are not stored; missing bars are never synthesized."));
        if (tf == EquityCandleTimeframes.Hour && inCoverage?.MissingByLocalOpen is { } byOpen
            && byOpen.TryGetValue("09:00", out var sessionOpen) && sessionOpen > 0)
            list.Add(new("session-open-1h-bars-missing",
                $"{sessionOpen} expected 09:00 (session-open) 1h bars are missing from the provider history; on those sessions the 09:00 bar is absent (the data-quality limitations state what is measured about where its trades went) and the 1h indicators restart after each missing expected bar."));
        if (inCoverage is { MissingInCandleWindow: > 0 } window)
            list.Add(new("missing-bars-in-candle-window",
                $"{window.MissingInCandleWindow} expected {tf} bars are missing inside the returned candle window; neighbouring candles there are not consecutive sessions."));
        if (indicatorsReason is not null)
            list.Add(new("indicators-unavailable",
                $"No stored TechnicalIndicators row for {sym} {tf} at or before the newest candle ({indicatorsReason})."));
        else if (indicators is { IsLatestCandle: false })
            list.Add(new("indicators-not-latest",
                $"The newest stored TechnicalIndicators row ({Local(indicators.OpenTimeMs)}) is older than the newest candle; the technical recompute has not covered the latest bar."));
        if (block is { UnexpectedSessionBars: > 0 })
            list.Add(new("unexpected-session-bars",
                $"{block.UnexpectedSessionBars} stored {tf} bars open outside the modeled HOSE session (provider anomaly); they are kept as stored."));
        if (block is { InvalidOhlcvRows: > 0 })
            list.Add(new("invalid-ohlcv-rows", $"{block.InvalidOhlcvRows} stored {tf} rows have inconsistent OHLCV values."));
        if (block is { MarketWideMissingBars: > 0 })
            list.Add(new("market-wide-missing",
                $"{block.MarketWideMissingBars} missing {tf} bars fall on dates when no other scope instrument traded either (suspected closures the calendar does not list)."));
        foreach (var flag in block?.CalendarFlags ?? [])
        {
            list.Add(new(flag, flag switch
            {
                VietnamMarketCalendar.BelowCoverageReason =>
                    "Part of the stored history predates the HOSE calendar coverage: holidays there are unknown, so missing bars before it are not real gaps.",
                VietnamMarketCalendar.CoverageExpiredReason =>
                    "Today is beyond the HOSE calendar coverage: expected bars and freshness cannot be evaluated (fail closed).",
                _ => flag,
            }));
        }
        return list;
    }

    private async Task<EquityTechnicalCalendarDto> CalendarAsync(
        string sym, long nowMs, EquityTechnicalNoteBindingDto? binding, EquityResearchSnapshot? snapshot,
        CancellationToken ct)
    {
        // Same rules, same order as POST research-notes (mode forward).
        var signal = _calendar.LastExpectedFinalizedOpenUtcMs(EquityCandleTimeframes.Day, nowMs);
        var stored = signal is not null && await _db.InstrumentCandles.AsNoTracking()
            .AnyAsync(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day && c.OpenTimeMs == signal.Value, ct);
        var windowClose = signal is null ? null : EquityForecastJournalService.TryNextSessionOpenUtcMs(_calendar, signal.Value);
        string? reason =
            signal is null ? "no-expected-signal-bar"
            : !stored ? "signal-bar-not-stored"
            : windowClose is null ? VietnamMarketCalendar.CoverageExpiredReason
            : nowMs >= windowClose.Value ? "forecast-window-closed"
            : binding is null ? "note-binding-unavailable"
            : binding.SignalBarOpenTimeMs != signal.Value ? "snapshot-signal-bar-differs"
            : snapshot?.Calendar.Status == "coverage-expired" ? VietnamMarketCalendar.CoverageExpiredReason
            : null;
        var source = _calendar.Source;
        return new EquityTechnicalCalendarDto(
            source is null ? "explicit" : "canonical-file",
            source?.CalendarVersion,
            _calendar.CoverageFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _calendar.CoverageTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            signal, stored, windowClose, reason is null, reason);
    }

    private static EquityTechnicalScoringDto Scoring(string sym) => new(
        EquityDirectionScoringService.RuleVersion,
        EquityDirectionScoringService.Rule,
        EquityBarrierConfigs.All
            .Select(c => new EquityTechnicalScoringHorizonDto(c.ConfigId, c.HorizonSessions, c.TargetPct, -c.StopPct))
            .ToArray(),
        EquityTechnicalContracts.ProbabilityPolicyOmit,
        "Do not send a probability: set body.probability to null and record direction (up, down, sideways or no-view) with horizonSessions equal to one of horizons[].horizonSessions; the backend scores the note.",
        $"/api/equities/{sym}/research-notes/direction-scores");

    private List<string> Limitations(InstrumentDescriptor instrument, EquityBaseRatesResponse? baseRates, MbbCoverageReport? report)
    {
        var sym = instrument.Symbol;
        var list = new List<string>();
        if (baseRates is null)
            list.Add($"No base rates for {sym}: the symbol has no decision capability.");
        else if (baseRates.Status == EquityBaseRatesStatuses.Unavailable)
            list.Add($"Base rates are unavailable ({string.Join("; ", baseRates.Reasons)}): there are no base-rate numbers for {sym}.");
        else if (baseRates.Status != EquityBaseRatesStatuses.Ok)
            list.Add($"Base rates status '{baseRates.Status}' ({string.Join("; ", baseRates.Reasons)}): every base-rate number here is research-only and unvalidated, including per-config research numbers with abstained = false; never present them as decision-grade.");
        else
            list.Add("Base rates passed the out-of-sample gate: only configs[].decisionUse is validated; research numbers stay unvalidated.");
        list.Add($"Prices are the {instrument.DataProvider} {instrument.PriceBasis} series: the provider back-adjusts the whole history at every ex-date, so compare across ex-dates with returns, not absolute levels.");
        list.Add("Two RSI14 implementations: indicators.rsi14 is Wilder's RSI (TechnicalIndicatorIndexer); the conditions payload RSI14 is a simple-average (Cutler) RSI over the last 14 changes (ai/technical_event_modules.py). Name which one you quote.");
        list.Add("The conditions modules and their rule set (indicator events, candle-pattern events, smart-money zones) come from the BTC research lane and run unchanged on HOSE bars; statistical evidence is attached only when a verified per-symbol bundle exists (conditions.payload.evidence.available).");
        list.Add("Stored indicators restart with a null warm-up after every missing expected session bar; candle patterns are rule-based shapes (CandlePatternRecognizer) on the stored bar sequence, can straddle a missing bar, and carry a 6-bar close-count trend label, with no forward-return evidence attached here.");
        list.Add($"Only finalized stored bars are returned (the forming bar of an open session is excluded) and nothing is fetched from providers; older history: GET /api/equities/{sym}/candles?timeframe=<tf>&endTimeMs=<first openTimeMs - 1>.");
        list.Add("relativeStrength compares close-to-close returns over 20 and 60 aligned daily sessions with VNINDEX and the bank peers (latest ~400 stored daily bars).");
        if (!instrument.ActiveTimeframes.Contains(EquityCandleTimeframes.Hour, StringComparer.Ordinal))
            list.Add($"{sym} has no 1h timeframe in the instrument catalog: no intraday analysis is available for it.");
        if (_calendar.CoverageTo is { } coverageTo)
            list.Add($"The HOSE calendar covers sessions up to {coverageTo:yyyy-MM-dd}; forward notes and direction scoring need the sessions they depend on inside that coverage.");
        if (report is not null)
            list.AddRange(report.Limitations.Select(l => $"data-quality: {l}"));
        return list;
    }

    private static DateOnly LocalDate(long utcMs) => DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(utcMs).DateTime);

    private static string Local(long? utcMs) => utcMs is { } ms
        ? VietnamMarketCalendar.ToLocal(ms).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " local"
        : "none";
}

using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Barrier labelling identical to ai/equity_base_rates.py <c>label_barriers</c> + <c>Costs.net_return</c>
/// (spec §5): entry = open of the first bar; target/stop from the entry price; per bar in order:
/// open ≤ stop ⇒ stop-first at open (gap); open ≥ target ⇒ target-first at target; high ≥ target ∧
/// low ≤ stop ⇒ stop-first at stop (double touch); low ≤ stop ⇒ stop-first at stop; high ≥ target ⇒
/// target-first at target; last horizon bar ⇒ timeout at close. Fewer than H bars without a touch ⇒
/// pending (null). Arithmetic in double, like the Python reference.
/// </summary>
public static class EquityBarrierLabeler
{
    public const string TargetFirst = "target-first";
    public const string StopFirst = "stop-first";
    public const string Timeout = "timeout";

    public sealed record Bar(long OpenTimeMs, double Open, double High, double Low, double Close);

    public sealed record Outcome(string Label, int ResolveIndex, double ExitPrice, double NetReturn, bool GapExit, bool DoubleTouch);

    /// <summary>Net long return: exit*(1-sellFee-sellTax)/(entry*(1+buyFee)) - 1.</summary>
    public static double NetReturn(double entry, double exit, double buyFee, double sellFee, double sellTax) =>
        exit * (1.0 - sellFee - sellTax) / (entry * (1.0 + buyFee)) - 1.0;

    /// <summary>
    /// Label a forecast whose entry bar is <paramref name="bars"/>[0]. Returns null while the
    /// window is immature (no touch and fewer than H bars available).
    /// </summary>
    public static Outcome? Label(IReadOnlyList<Bar> bars, double targetPct, double stopPct, int horizon,
        double buyFee, double sellFee, double sellTax)
    {
        if (bars.Count == 0 || horizon < 1) return null;
        var entry = bars[0].Open;
        var target = entry * (1.0 + targetPct);
        var stop = entry * (1.0 - stopPct);
        var last = horizon - 1;
        for (var j = 0; j <= Math.Min(last, bars.Count - 1); j++)
        {
            var (o, h, lo) = (bars[j].Open, bars[j].High, bars[j].Low);
            string? label = null;
            double exit = 0;
            bool gap = false, dbl = false;
            if (o <= stop) { label = StopFirst; exit = o; gap = true; }
            else if (o >= target) { label = TargetFirst; exit = target; gap = true; }
            else if (h >= target && lo <= stop) { label = StopFirst; exit = stop; dbl = true; }
            else if (lo <= stop) { label = StopFirst; exit = stop; }
            else if (h >= target) { label = TargetFirst; exit = target; }
            else if (j == last) { label = Timeout; exit = bars[j].Close; }
            if (label is not null)
                return new Outcome(label, j, exit, NetReturn(entry, exit, buyFee, sellFee, sellTax), gap, dbl);
        }
        return null;
    }
}

/// <summary>
/// Forward forecast journal (spec §8/§11): quant-knn rows for the LATEST finalized 1d bar only,
/// write-once outcome resolution on stored finalized bars, and GET forecast-journal with paired
/// Brier scoring quant vs llm-judge. Rows are immutable once inserted; only the NULL outcome block
/// is ever filled, exactly once.
/// </summary>
public sealed class EquityForecastJournalService
{
    public const string SourceQuant = "quant-knn";
    public const string SourceLlm = "llm-judge";
    public const string SourceSession = "session-research";
    public const int MinEffectiveForScoring = 30;

    private readonly AppDbContext _db;
    private readonly EquityBaseRatesService _baseRates;
    private readonly VietnamMarketCalendar _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;
    private readonly VnEquityCostSpec _costs;

    public EquityForecastJournalService(
        AppDbContext db,
        EquityBaseRatesService baseRates,
        VietnamMarketCalendar calendar,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null,
        VnEquityCostSpec? costs = null)
    {
        _db = db;
        _baseRates = baseRates;
        _calendar = calendar;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _costs = costs ?? VietnamExecutionRules.DefaultCosts;
    }

    private string RequireDecision(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        return instrument.Symbol;
    }

    /// <summary>
    /// Forecast window close for a signal bar: the 09:00 local open of the next trading session.
    /// A forecast made at/after it could see that session's prices/news, so it is never scored
    /// (and LLM runs are refused once it has passed).
    /// </summary>
    /// <exception cref="CalendarCoverageExpiredException">the next session falls after the calendar coverage.</exception>
    public static long NextSessionOpenUtcMs(VietnamMarketCalendar calendar, long signalBarOpenTimeMs)
    {
        var signalDate = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(signalBarOpenTimeMs).DateTime);
        calendar.EnsureCovered(signalDate, "Signal bar date");
        var next = calendar.AddTradingDays(signalDate, 1);
        calendar.EnsureCovered(next, "Next session date");
        return VietnamMarketCalendar.ToUtcMs(next, VietnamMarketCalendar.MorningOpenLocal);
    }

    /// <summary>Window close, or null when it is beyond the calendar coverage (such rows are never scored).</summary>
    public static long? TryNextSessionOpenUtcMs(VietnamMarketCalendar calendar, long signalBarOpenTimeMs)
    {
        try { return NextSessionOpenUtcMs(calendar, signalBarOpenTimeMs); }
        catch (CalendarCoverageExpiredException) { return null; }
    }

    public static string ForecastIdFor(string source, string symbol, string configId, long signalOpenMs, Guid? runId = null) =>
        runId is null ? $"{source}/{symbol}/{configId}/{signalOpenMs}" : $"{source}/{symbol}/{configId}/{signalOpenMs}/{runId}";

    /// <summary>
    /// Records one quant row per config for the latest expected finalized 1d bar (must be stored).
    /// Fresh artifact ⇒ research probabilities (Validated = gatePassed); stale/missing/abstained ⇒
    /// Abstained with the reason. Idempotent by ForecastId.
    /// </summary>
    public async Task<EquityForecastRecordReport> RecordQuantAsync(string symbol, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var now = _timeProvider.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var todayLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(nowMs).DateTime);
        if (_calendar.IsBeyondCoverage(todayLocal))
            return new EquityForecastRecordReport(sym, VietnamMarketCalendar.CoverageExpiredReason, null, 0, 0, 0,
                $"today {todayLocal:yyyy-MM-dd} is after the HOSE calendar coverage ({_calendar.CoverageTo:yyyy-MM-dd}); nothing is recorded until the calendar is extended");
        var signalOpen = _calendar.LastExpectedFinalizedOpenUtcMs(EquityCandleTimeframes.Day, nowMs);
        if (signalOpen is null)
            return new EquityForecastRecordReport(sym, "no-expected-open", null, 0, 0, 0, null);
        if (TryNextSessionOpenUtcMs(_calendar, signalOpen.Value) is null)
            return new EquityForecastRecordReport(sym, VietnamMarketCalendar.CoverageExpiredReason, signalOpen, 0, 0, 0,
                "the session after the signal bar is beyond the HOSE calendar coverage: its forecast window cannot be determined");
        var signalBar = await _db.InstrumentCandles.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day
                                      && c.OpenTimeMs == signalOpen.Value, ct);
        if (signalBar is null)
            return new EquityForecastRecordReport(sym, "finalized-bar-not-ingested", signalOpen, 0, 0, 0,
                "the latest expected finalized 1d bar is not stored yet; never recorded on stale data");

        var evaluation = await _baseRates.EvaluateAsync(sym, ct);
        int recorded = 0, already = 0, abstained = 0;
        foreach (var cfg in evaluation.Configs)
        {
            var id = ForecastIdFor(SourceQuant, sym, cfg.ConfigId, signalBar.OpenTimeMs);
            if (await _db.EquityForecastRecords.AsNoTracking().AnyAsync(f => f.ForecastId == id, ct))
            {
                already++;
                continue;
            }
            string? reason = cfg.AbstainReason;
            if (reason is null && cfg.CurrentSignalBarOpenTimeMs != signalBar.OpenTimeMs)
                reason = EquityBaseRatesService.ReasonStaleAsOf;
            var isAbstained = reason is not null || cfg.ProbTargetFirst is null || cfg.ProbStopFirst is null;
            if (isAbstained) reason ??= "current-abstained";
            var row = new EquityForecastRecord
            {
                Id = Guid.NewGuid(),
                ForecastId = id,
                Source = SourceQuant,
                Symbol = sym,
                Timeframe = EquityCandleTimeframes.Day,
                ConfigId = cfg.ConfigId,
                TargetPct = cfg.TargetPct,
                StopPct = cfg.StopPct,
                HorizonSessions = cfg.HorizonSessions,
                SignalBarOpenTimeMs = signalBar.OpenTimeMs,
                SignalBarCloseTimeMs = signalBar.CloseTimeMs,
                AvailableTimeMs = Math.Max(nowMs, signalBar.CloseTimeMs),
                CreatedAtUtc = now.UtcDateTime,
                ProbTargetFirst = isAbstained ? null : cfg.ProbTargetFirst,
                ProbStopFirst = isAbstained ? null : cfg.ProbStopFirst,
                Abstained = isAbstained,
                AbstainReason = isAbstained ? Truncate(reason!, 500) : null,
                Validated = !isAbstained && evaluation.Fresh && cfg.GatePassed,
                MethodVersion = evaluation.MethodVersion,
                SchemaVersion = evaluation.SchemaVersion,
                InputSnapshotSha256 = evaluation.InputSha256,
            };
            _db.EquityForecastRecords.Add(row);
            try
            {
                await _db.SaveChangesAsync(ct);
                recorded++;
                if (isAbstained) abstained++;
            }
            catch (DbUpdateException)
            {
                _db.Entry(row).State = EntityState.Detached;
                if (!await _db.EquityForecastRecords.AsNoTracking().AnyAsync(f => f.ForecastId == id, ct)) throw;
                already++;
            }
        }
        var status = recorded > 0 ? "recorded" : "already-recorded";
        return new EquityForecastRecordReport(sym, status, signalBar.OpenTimeMs, recorded, already, abstained,
            evaluation.Response.Status == EquityBaseRatesStatuses.Ok ? null : string.Join(";", evaluation.Response.Reasons));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>
    /// Write-once outcome resolution for unresolved rows of one symbol: entry = first stored
    /// finalized expected-session 1d bar with open ≥ AvailableTimeMs, barrier rules of
    /// <see cref="EquityBarrierLabeler"/>; immature windows stay NULL.
    /// </summary>
    public async Task<int> ResolveOutcomesAsync(string symbol, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var now = _timeProvider.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var pending = await _db.EquityForecastRecords
            .Where(f => f.Symbol == sym && f.OutcomeLabel == null)
            .OrderBy(f => f.SignalBarOpenTimeMs)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var minAvailable = pending.Min(f => f.AvailableTimeMs);
        var bars = (await _db.InstrumentCandles.AsNoTracking()
                .Where(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day && c.OpenTimeMs >= minAvailable)
                .OrderBy(c => c.OpenTimeMs)
                .ToListAsync(ct))
            // Same bar set as the Python labeller: finalized, expected sessions only.
            .Where(c => _calendar.IsFinalized(EquityCandleTimeframes.Day, c.OpenTimeMs, nowMs)
                        && _calendar.ClassifyDailyOpenUtcMs(c.OpenTimeMs) == SessionOpenKind.Expected)
            .ToList();

        var buyFee = (double)_costs.BrokerFeeFraction;
        var sellFee = (double)_costs.BrokerFeeFraction;
        var tax = (double)_costs.SellTaxFraction;
        var resolved = 0;
        foreach (var f in pending)
        {
            var start = bars.FindIndex(b => b.OpenTimeMs >= f.AvailableTimeMs && b.OpenTimeMs > f.SignalBarOpenTimeMs);
            if (start < 0) continue;
            var window = bars.Skip(start).Take(f.HorizonSessions)
                .Select(b => new EquityBarrierLabeler.Bar(b.OpenTimeMs, (double)b.Open, (double)b.High, (double)b.Low, (double)b.Close))
                .ToArray();
            var outcome = EquityBarrierLabeler.Label(window, f.TargetPct, f.StopPct, f.HorizonSessions, buyFee, sellFee, tax);
            if (outcome is null) continue;
            f.EntryBarOpenTimeMs = window[0].OpenTimeMs;
            f.EntryPrice = bars[start].Open;
            f.OutcomeLabel = outcome.Label;
            f.OutcomeReturnNet = outcome.NetReturn;
            f.ResolvedBarOpenTimeMs = window[outcome.ResolveIndex].OpenTimeMs;
            f.OutcomeResolvedAtUtc = now.UtcDateTime;
            resolved++;
        }
        if (resolved > 0) await _db.SaveChangesAsync(ct);
        return resolved;
    }

    /// <summary>GET forecast-journal: newest items first (≤ take) + paired scoring per config.</summary>
    public async Task<EquityForecastJournalResponse> GetJournalAsync(
        string symbol, string? configId, int take, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var query = _db.EquityForecastRecords.AsNoTracking().Where(f => f.Symbol == sym);
        if (!string.IsNullOrWhiteSpace(configId))
            query = query.Where(f => f.ConfigId == configId);
        var all = await query.ToListAsync(ct);

        var items = all
            .OrderByDescending(f => f.SignalBarOpenTimeMs).ThenBy(f => f.ConfigId).ThenBy(f => f.Source)
            .ThenByDescending(f => f.CreatedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(ToDto)
            .ToArray();

        var sessionIndex = (await _db.InstrumentCandles.AsNoTracking()
                .Where(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day)
                .OrderBy(c => c.OpenTimeMs).Select(c => c.OpenTimeMs).ToListAsync(ct))
            .Select((open, i) => (open, i)).ToDictionary(x => x.open, x => x.i);

        var configIds = all.Select(f => f.ConfigId).Distinct(StringComparer.Ordinal)
            .Union(string.IsNullOrWhiteSpace(configId)
                ? EquityBarrierConfigs.All.Select(c => c.ConfigId)
                : [configId!], StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
        var scoring = configIds.Select(id => Score(id, all.Where(f => f.ConfigId == id).ToArray(), sessionIndex,
            signal => TryNextSessionOpenUtcMs(_calendar, signal) ?? long.MinValue)).ToArray();
        return new EquityForecastJournalResponse(EquityForecastJournalResponse.Version, sym, items, scoring);
    }

    /// <summary>
    /// Paired scoring. Look-ahead guards: only forecasts made before the next session's open
    /// (<paramref name="windowCloseMs"/>) are scored, and only the EARLIEST challenger forecast per signal bar —
    /// later re-runs are listed but never scored (no cherry-picking a run that "looks right").
    /// Top-level fields stay quant vs llm-judge (FE compatibility); <c>Pairs</c> (EQX v2) repeats the same
    /// rules per challenger source: llm-judge and session-research.
    /// </summary>
    internal static EquityForecastScoringDto Score(
        string configId, IReadOnlyList<EquityForecastRecord> rows, IReadOnlyDictionary<long, int> sessionIndex,
        Func<long, long> windowCloseMs)
    {
        var llm = ScorePair(SourceLlm, rows, sessionIndex, windowCloseMs);
        var session = ScorePair(SourceSession, rows, sessionIndex, windowCloseMs);
        return new EquityForecastScoringDto(configId, llm.Status, llm.PairedN, llm.NEffective,
            llm.BrierQuant, llm.BrierChallenger, llm.BrierDiff, llm.CoverageQuant, llm.CoverageChallenger,
            [llm, session]);
    }

    internal static EquityForecastPairScoreDto ScorePair(
        string challengerSource, IReadOnlyList<EquityForecastRecord> rows, IReadOnlyDictionary<long, int> sessionIndex,
        Func<long, long> windowCloseMs)
    {
        var quant = rows.Where(r => r.Source == SourceQuant).ToArray();
        var challenger = rows.Where(r => r.Source == challengerSource).ToArray();
        static bool HasProb(EquityForecastRecord r) => !r.Abstained && r.ProbTargetFirst is not null;
        bool InWindow(EquityForecastRecord r) => r.AvailableTimeMs < windowCloseMs(r.SignalBarOpenTimeMs);
        double? Coverage(EquityForecastRecord[] set) => set.Length == 0 ? null : (double)set.Count(HasProb) / set.Length;

        // Earliest challenger forecast per signal bar (rows are already one symbol + one config), and only
        // if it was made inside the window.
        var byBar = challenger
            .GroupBy(r => r.SignalBarOpenTimeMs)
            .Select(g => g.OrderBy(r => r.AvailableTimeMs).ThenBy(r => r.CreatedAtUtc).First())
            .Where(r => HasProb(r) && InWindow(r))
            .ToDictionary(r => r.SignalBarOpenTimeMs);
        var pairs = quant.Where(q => HasProb(q) && InWindow(q) && q.OutcomeLabel is not null
                                     && byBar.ContainsKey(q.SignalBarOpenTimeMs))
            .OrderBy(q => q.SignalBarOpenTimeMs)
            .Select(q => (q, c: byBar[q.SignalBarOpenTimeMs]))
            .ToArray();
        var horizon = rows.Count > 0 ? rows.Max(r => r.HorizonSessions) : 1;
        if (pairs.Length == 0)
            return new EquityForecastPairScoreDto(challengerSource, "no-data", 0, 0, null, null, null,
                Coverage(quant), Coverage(challenger));

        double bq = 0, bc = 0;
        foreach (var (q, c) in pairs)
        {
            var y = q.OutcomeLabel == EquityBarrierLabeler.TargetFirst ? 1.0 : 0.0;
            bq += Math.Pow(q.ProbTargetFirst!.Value - y, 2);
            bc += Math.Pow(c.ProbTargetFirst!.Value - y, 2);
        }
        bq /= pairs.Length;
        bc /= pairs.Length;
        var nEff = NonOverlappingCount(pairs.Select(p => sessionIndex.TryGetValue(p.q.SignalBarOpenTimeMs, out var i) ? i : -1)
            .Where(i => i >= 0), horizon);
        var status = nEff < MinEffectiveForScoring ? "insufficient-sample" : "ok";
        return new EquityForecastPairScoreDto(challengerSource, status, pairs.Length, nEff, bq, bc, bc - bq,
            Coverage(quant), Coverage(challenger));
    }

    /// <summary>Greedy count of signal indices ≥ horizon sessions apart (Python non_overlapping_count).</summary>
    internal static int NonOverlappingCount(IEnumerable<int> signalIndices, int horizon)
    {
        int count = 0, last = int.MinValue;
        var first = true;
        foreach (var i in signalIndices.OrderBy(x => x))
        {
            if (first || i - last >= horizon)
            {
                count++;
                last = i;
                first = false;
            }
        }
        return count;
    }

    private static EquityForecastItemDto ToDto(EquityForecastRecord f) => new(
        f.ForecastId, f.Source, f.ConfigId, f.TargetPct, f.StopPct, f.HorizonSessions,
        f.SignalBarOpenTimeMs, f.AvailableTimeMs, f.CreatedAtUtc, f.ProbTargetFirst, f.ProbStopFirst,
        f.Abstained, f.AbstainReason, f.Validated, f.MethodVersion, f.ModelId, f.PromptVersion, f.LlmRunId,
        f.OutcomeLabel is null || f.EntryBarOpenTimeMs is null || f.EntryPrice is null || f.OutcomeReturnNet is null
            || f.ResolvedBarOpenTimeMs is null || f.OutcomeResolvedAtUtc is null
            ? null
            : new EquityForecastOutcomeDto(f.EntryBarOpenTimeMs.Value, f.EntryPrice.Value, f.OutcomeLabel,
                f.OutcomeReturnNet.Value, f.ResolvedBarOpenTimeMs.Value, f.OutcomeResolvedAtUtc.Value));
}

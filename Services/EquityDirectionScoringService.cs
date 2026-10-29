using System.Globalization;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using static Backend.Services.Models.EquityDirectionScoreValues;

namespace Backend.Services;

/// <summary>
/// Read-only direction scoring of session research notes without probabilities (EQTECH-1 lane S,
/// rule <see cref="RuleVersion"/>): the outcome-check convention the investment-research skill already
/// declares (ai/skills/investment-research/SKILL.md step 10), computed by the backend.
/// <list type="bullet">
/// <item>R = close(target) / close(signal) − 1 on the stored adjusted 1d closes at scoring time; the target is
///   the session exactly H trading days after the signal bar on the HOSE calendar, never another stored bar;</item>
/// <item>thresholds from <see cref="EquityBarrierConfigs"/> (single source): up if R ≥ +TargetPct, down if
///   R ≤ −StopPct, else sideways; other horizons are not scored;</item>
/// <item>forward notes recorded before the next session's 09:00 open are eligible; the earliest eligible note
///   per (signal bar, H) holds the scored slot (same look-ahead rule as the forecast journal's ScorePair);</item>
/// <item>baselines on the same resolved directional set: a point-in-time prior over stored rows and the best
///   constant class in hindsight.</item>
/// </list>
/// Never writes to the database (AsNoTracking reads only).
/// </summary>
public sealed class EquityDirectionScoringService
{
    public const string RuleVersion = "direction-score-v1";
    public const string ContractVersion = EquityDirectionScoresResponse.Version;
    public const int DefaultTake = 200;
    public const int MaxTake = 500;

    private const string Forward = "forward";

    /// <summary>One barrier config per horizon; a duplicated horizon fails at type initialization (fail closed).</summary>
    private static readonly IReadOnlyDictionary<int, EquityBarrierConfigs.Config> ConfigByHorizon =
        EquityBarrierConfigs.All.ToDictionary(c => c.HorizonSessions);

    /// <summary>Human-readable rule served with every response (and declared by the technical payload).</summary>
    public static string Rule { get; } = BuildRule();

    private readonly AppDbContext _db;
    private readonly VietnamMarketCalendar _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;

    public EquityDirectionScoringService(
        AppDbContext db,
        VietnamMarketCalendar calendar,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null)
    {
        _db = db;
        _calendar = calendar;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    private sealed record Bar(long OpenTimeMs, decimal Close);

    private sealed class Evaluation(EquityResearchNote note)
    {
        public EquityResearchNote Note { get; } = note;
        public EquityBarrierConfigs.Config? Config { get; set; }
        public string Status { get; set; } = "";
        public string? PendingReason { get; set; }
        public int SignalIndex { get; set; } = -1;
        public decimal? SignalClose { get; set; }
        public long? TargetOpenTimeMs { get; set; }
        public decimal? TargetClose { get; set; }
        public decimal? Return { get; set; }
        public string? Realized { get; set; }
        public string? BaselineCall { get; set; }
        public bool? Hit { get; set; }
    }

    private string RequireDecision(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        return instrument.Symbol;
    }

    public async Task<EquityDirectionScoresResponse> GetAsync(string symbol, int take = DefaultTake, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var now = _timeProvider.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();

        var notes = await _db.EquityResearchNotes.AsNoTracking().Where(n => n.Symbol == sym).ToListAsync(ct);
        var stored = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day)
            .OrderBy(c => c.OpenTimeMs)
            .Select(c => new { c.OpenTimeMs, c.Close })
            .ToListAsync(ct);

        // Same bar set as the forecast journal's outcome resolver: finalized, expected sessions only.
        // Stored-row offsets (baseline prior, nEffective) are indices into this list.
        var bars = new List<Bar>(stored.Count);
        var indexByOpen = new Dictionary<long, int>(stored.Count);
        foreach (var c in stored)
        {
            if (!_calendar.IsFinalized(EquityCandleTimeframes.Day, c.OpenTimeMs, nowMs)
                || _calendar.ClassifyDailyOpenUtcMs(c.OpenTimeMs) != SessionOpenKind.Expected
                || !indexByOpen.TryAdd(c.OpenTimeMs, bars.Count))
                continue;
            bars.Add(new Bar(c.OpenTimeMs, c.Close));
        }

        var evaluations = notes.Select(n => new Evaluation(n)).ToList();
        var eligible = new List<Evaluation>();
        foreach (var e in evaluations)
        {
            var n = e.Note;
            if (n.Mode != Forward) { e.Status = StatusRetrospective; continue; }
            if (!ConfigByHorizon.TryGetValue(n.HorizonSessions, out var config)) { e.Status = StatusNoDeclaredThresholds; continue; }
            e.Config = config;
            var windowClose = EquityForecastJournalService.TryNextSessionOpenUtcMs(_calendar, n.SignalBarOpenTimeMs);
            if (windowClose is null) { e.Status = StatusWindowUndetermined; continue; }
            if (n.AvailableTimeMs >= windowClose.Value) { e.Status = StatusOutOfWindow; continue; }
            eligible.Add(e);
        }

        // Earliest eligible note per (signal bar, H) holds the slot; later ones are listed, never scored.
        var slots = new List<Evaluation>();
        foreach (var group in eligible.GroupBy(e => (e.Note.SignalBarOpenTimeMs, e.Note.HorizonSessions)))
        {
            var ordered = group.OrderBy(e => e.Note.AvailableTimeMs).ThenBy(e => e.Note.CreatedAtUtc)
                .ThenBy(e => e.Note.NoteId, StringComparer.Ordinal).ToList();
            slots.Add(ordered[0]);
            foreach (var later in ordered.Skip(1)) later.Status = StatusSuperseded;
        }

        foreach (var e in slots) EvaluateSlot(e, bars, indexByOpen, nowMs);

        var horizons = new List<EquityDirectionHorizonScoreDto>();
        foreach (var config in EquityBarrierConfigs.All.OrderBy(c => c.HorizonSessions))
            horizons.Add(Summarize(config, evaluations, bars));

        var items = evaluations
            .OrderByDescending(e => e.Note.SignalBarOpenTimeMs)
            .ThenBy(e => e.Note.HorizonSessions)
            .ThenBy(e => e.Note.AvailableTimeMs)
            .ThenBy(e => e.Note.CreatedAtUtc)
            .ThenBy(e => e.Note.NoteId, StringComparer.Ordinal)
            .Take(Math.Clamp(take, 1, MaxTake))
            .Select(ToDto)
            .ToArray();

        return new EquityDirectionScoresResponse(
            ContractVersion, sym, RuleVersion, Rule, now.UtcDateTime,
            _calendar.CoverageTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            notes.Count,
            evaluations.Count(e => e.Status == StatusRetrospective),
            evaluations.Count(e => e.Status == StatusNoDeclaredThresholds),
            horizons, items);
    }

    private void EvaluateSlot(Evaluation e, List<Bar> bars, Dictionary<long, int> indexByOpen, long nowMs)
    {
        var n = e.Note;
        var config = e.Config!;
        if (indexByOpen.TryGetValue(n.SignalBarOpenTimeMs, out var s) && bars[s].Close > 0)
        {
            e.SignalIndex = s;
            e.SignalClose = bars[s].Close;
        }

        var signalDate = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(n.SignalBarOpenTimeMs).DateTime);
        var targetDate = TargetDate(signalDate, n.HorizonSessions);
        string? reason = null;
        if (_calendar.IsBeyondCoverage(targetDate))
        {
            reason = PendingCalendarCoverageExpired; // holidays after coverage are unknown: no target date
        }
        else
        {
            var targetOpen = VietnamMarketCalendar.DailyOpenUtcMs(targetDate);
            e.TargetOpenTimeMs = targetOpen;
            if (!_calendar.IsFinalized(EquityCandleTimeframes.Day, targetOpen, nowMs))
                reason = PendingImmature;
            else if (e.SignalClose is null)
                reason = PendingSignalBarMissing;
            else if (!indexByOpen.TryGetValue(targetOpen, out var t) || bars[t].Close <= 0)
                reason = PendingTargetBarMissing; // never shifted to another stored bar
            else
            {
                e.TargetClose = bars[t].Close;
                e.Return = bars[t].Close / e.SignalClose.Value - 1m;
                e.Realized = Classify(e.Return.Value, config);
            }
        }
        e.PendingReason = reason;

        if (n.Direction is not (Up or Down or Sideways))
        {
            e.Status = StatusNoView;
            return;
        }
        if (reason is not null)
        {
            e.Status = StatusPending;
            return;
        }
        e.Status = StatusResolved;
        e.Hit = e.Realized == n.Direction;
    }

    /// <summary>
    /// The session exactly <paramref name="sessions"/> trading days after <paramref name="signalDate"/>, one
    /// trading day at a time: VietnamMarketCalendar.AddTradingDays walks at most 45 calendar days in total, so a
    /// single AddTradingDays(date, 60) call would throw. Past the coverage every weekday counts, and the result
    /// then lies after the coverage (the caller reports calendar-coverage-expired).
    /// </summary>
    private DateOnly TargetDate(DateOnly signalDate, int sessions)
    {
        var date = signalDate;
        for (var i = 0; i < sessions; i++) date = _calendar.AddTradingDays(date, 1);
        return date;
    }

    private static EquityDirectionHorizonScoreDto Summarize(
        EquityBarrierConfigs.Config config, List<Evaluation> evaluations, List<Bar> bars)
    {
        var h = config.HorizonSessions;
        var forward = evaluations.Where(e => e.Note.Mode == Forward && e.Note.HorizonSessions == h).ToList();
        int Count(string status) => forward.Count(e => e.Status == status);
        var noView = Count(StatusNoView);
        var superseded = Count(StatusSuperseded);
        var resolved = forward.Where(e => e.Status == StatusResolved).OrderBy(e => e.SignalIndex).ToList();
        var pending = forward.Where(e => e.Status == StatusPending).ToList();
        var directional = resolved.Count + pending.Count;
        var scored = noView + directional;
        int PendingCount(string reason) => pending.Count(e => e.PendingReason == reason);

        // Point-in-time prior: classes of every stored row t (R_t over H stored rows), prefix counts per class.
        var n = bars.Count;
        var up = new int[n + 1];
        var down = new int[n + 1];
        var side = new int[n + 1];
        for (var t = 0; t < n; t++)
        {
            string? cls = t + h < n && bars[t].Close > 0 && bars[t + h].Close > 0
                ? Classify(bars[t + h].Close / bars[t].Close - 1m, config)
                : null;
            up[t + 1] = up[t] + (cls == Up ? 1 : 0);
            down[t + 1] = down[t] + (cls == Down ? 1 : 0);
            side[t + 1] = side[t] + (cls == Sideways ? 1 : 0);
        }
        var priorHits = 0;
        foreach (var e in resolved)
        {
            var k = Math.Clamp(e.SignalIndex - h + 1, 0, n); // rows t in [0, s − H]
            e.BaselineCall = MostFrequent(up[k], down[k], side[k]);
            if (e.BaselineCall == e.Realized) priorHits++;
        }

        var hits = resolved.Count(e => e.Hit == true);
        string? bestLabel = null;
        var bestHits = 0;
        if (resolved.Count > 0)
        {
            bestLabel = MostFrequent(
                resolved.Count(e => e.Realized == Up), resolved.Count(e => e.Realized == Down),
                resolved.Count(e => e.Realized == Sideways));
            bestHits = resolved.Count(e => e.Realized == bestLabel);
        }

        var nEffective = EquityForecastJournalService.NonOverlappingCount(resolved.Select(e => e.SignalIndex), h);
        var status = resolved.Count == 0 ? HorizonNoData
            : nEffective < EquityForecastJournalService.MinEffectiveForScoring ? HorizonInsufficientSample
            : HorizonOk;
        double? Rate(int k) => resolved.Count == 0 ? null : (double)k / resolved.Count;

        return new EquityDirectionHorizonScoreDto(
            h, config.ConfigId, config.TargetPct, -config.StopPct, status,
            ForwardNotes: forward.Count,
            OutOfWindow: Count(StatusOutOfWindow),
            WindowUndetermined: Count(StatusWindowUndetermined),
            Eligible: scored + superseded,
            Superseded: superseded,
            Scored: scored,
            NoView: noView,
            Directional: directional,
            Resolved: resolved.Count,
            Pending: pending.Count,
            PendingReasons: new EquityDirectionPendingCounts(
                PendingCount(PendingImmature), PendingCount(PendingTargetBarMissing),
                PendingCount(PendingCalendarCoverageExpired), PendingCount(PendingSignalBarMissing)),
            Hits: hits,
            HitRate: Rate(hits),
            Coverage: scored == 0 ? null : (double)directional / scored,
            NEffective: nEffective,
            MinEffectiveForScoring: EquityForecastJournalService.MinEffectiveForScoring,
            BaselinePrior: new EquityDirectionPriorBaselineDto(priorHits, Rate(priorHits)),
            BestConstantInHindsight: new EquityDirectionConstantBaselineDto(bestLabel, bestHits, Rate(bestHits)));
    }

    /// <summary>up if R ≥ +TargetPct, down if R ≤ −StopPct, else sideways (decimal, so the bounds are exact).</summary>
    internal static string Classify(decimal r, EquityBarrierConfigs.Config config) =>
        r >= (decimal)config.TargetPct ? Up
        : r <= -(decimal)config.StopPct ? Down
        : Sideways;

    /// <summary>Most frequent class; ties broken sideways &gt; up &gt; down (all zero ⇒ sideways).</summary>
    internal static string MostFrequent(int up, int down, int sideways)
    {
        var max = Math.Max(sideways, Math.Max(up, down));
        return sideways == max ? Sideways : up == max ? Up : Down;
    }

    private static EquityDirectionScoreItemDto ToDto(Evaluation e) => new(
        e.Note.NoteId, e.Note.Mode, e.Note.SignalBarOpenTimeMs, e.Note.HorizonSessions, e.Note.Direction,
        e.Note.AvailableTimeMs, e.Status, e.PendingReason, e.SignalClose, e.TargetOpenTimeMs, e.TargetClose,
        e.Return is { } r ? (double)r : null, e.Realized, e.BaselineCall, e.Hit);

    private static string BuildRule()
    {
        static string Pct(double fraction) => (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        var thresholds = string.Join("; ", EquityBarrierConfigs.All.OrderBy(c => c.HorizonSessions).Select(c =>
            $"H={c.HorizonSessions} ({c.ConfigId}): up if R >= +{Pct(c.TargetPct)}, down if R <= -{Pct(c.StopPct)}"));
        return $"{RuleVersion}: computed by the backend from the note's direction only (no probability). " +
               "R = close(target bar) / close(signal bar) - 1 on the stored adjusted 1d closes at scoring time; " +
               "the target bar is the session exactly H trading days after the signal bar on the HOSE calendar and is never " +
               $"replaced by another stored bar. Thresholds from the barrier configs: {thresholds}; otherwise sideways. " +
               "A horizon without a barrier config is not scored (no-declared-thresholds). " +
               "Only forward notes recorded before the 09:00 open of the session after their signal bar are eligible " +
               "(else out-of-window-not-scored; window-undetermined-not-scored when that session is after the calendar coverage). " +
               "Only the earliest eligible note per (signal bar, H), ordered by availableTimeMs, createdAtUtc, noteId, holds the " +
               "scored slot; later ones are superseded-not-scored whatever they say. Retrospective notes are never scored. " +
               "A no-view note holds its slot and counts toward coverage only, never as right or wrong. " +
               "A directional note stays pending while calendar-coverage-expired (target date after the calendar coverage), " +
               "immature (target bar not finalized), signal-bar-missing or target-bar-missing (finalized but not stored); " +
               "a stored close <= 0 counts as missing. hit = realized class equals the note's direction; hitRate = hits / resolved; " +
               "coverage = directional / scored. Baselines on exactly the same resolved directional set: baselinePrior is " +
               "point-in-time - for a note whose signal bar is stored row s of the finalized expected-session 1d bars, every " +
               "stored row t with t + H <= s is classified on R_t = close[t+H] / close[t] - 1 (stored-row offsets, same thresholds) " +
               "and the most frequent class is called, ties broken sideways > up > down (no history: sideways); " +
               "bestConstantInHindsight is the single class with the most hits on the resolved set (ties sideways > up > down). " +
               "nEffective = greedy count of resolved signal rows at least H stored rows apart; status no-data when nothing is " +
               $"resolved, insufficient-sample when nEffective < {EquityForecastJournalService.MinEffectiveForScoring}, else ok. " +
               "returnPct and thresholds are fractions (0.05 = +5%); hit rates are descriptive counts, not probabilities.";
    }
}

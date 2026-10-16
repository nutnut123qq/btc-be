using System.Text.Json;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Forward signal recorder for the MBB decision lane (MBB-07). Manual/admin
/// invocation only — there is no hosted worker, matching the MBB ingestion
/// contract. Two honesty properties are enforced by construction, not policy:
///
/// 1. NO BACKFILL: <see cref="RecordAsync"/> only ever writes for the single
///    latest expected finalized open (from <see cref="VietnamMarketCalendar"/>),
///    and requires that bar to already be stored. Older bars can never get
///    rows, so past decisions cannot be retro-fabricated.
/// 2. NO FABRICATED FILLS: fill = the first stored bar opening at/after
///    <see cref="MbbSignalObservation.AvailableTimeMs"/> (a price can only be
///    taken once the decision itself exists — recording late moves the fill
///    forward, it never reaches back). conditional-sell fills only after a
///    declared close-below trigger actually fires on a finalized bar, then at
///    the next session open. Outcome = raw close-to-fill return over the fill
///    bar. Until the matching bar/trigger exists, fields stay NULL.
///
/// Idempotent by key: DecisionId = mbb-observe/{timeframe}/{signalBarOpenTimeMs}
/// plus a unique (Symbol, Timeframe, SignalBarOpenTimeMs) index — repeat polls
/// of the same bar are no-ops.
/// </summary>
public sealed class MbbSignalObservationService
{
    public const string RecorderVersion = "mbb-observe-v1";
    public const string OutcomeHorizonNextBarClose = "next-bar-close";
    public const string FillSourceNextBarOpen = "next-bar-open-vci-adjusted";
    public const string FillSourcePostTriggerNextOpen = "post-trigger-close-next-open";

    private static readonly HashSet<string> ActionableDecisions = new(StringComparer.Ordinal)
    {
        MbbDecisionActions.BuyMore, MbbDecisionActions.Reduce,
        MbbDecisionActions.Sell, MbbDecisionActions.ConditionalSell,
    };

    private readonly AppDbContext _db;
    private readonly MbbDecisionService _decision;
    private readonly VietnamMarketCalendar _calendar;
    private readonly TimeProvider _timeProvider;

    public MbbSignalObservationService(
        AppDbContext db,
        MbbDecisionService decision,
        VietnamMarketCalendar calendar,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _decision = decision;
        _calendar = calendar;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public sealed record ObserveReport(
        string Timeframe,
        string Status,
        long? SignalBarOpenTimeMs,
        string? Decision,
        string? DecisionId,
        int FillsResolved,
        int OutcomesResolved,
        string? Detail);

    /// <summary>
    /// Records one observation if the latest expected finalized bar is stored
    /// and not yet observed, then resolves pending fills/outcomes. Returns an
    /// honest status for the no-op cases (nothing to observe / already
    /// recorded / finalized bar not yet ingested).
    /// </summary>
    public async Task<ObserveReport> RecordAsync(string timeframe, CancellationToken cancellationToken = default)
    {
        var canonical = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (canonical is not (EquityCandleTimeframes.Hour or EquityCandleTimeframes.Day))
            throw new ArgumentException(
                $"MBB observations support '1h' and '1d' only; got '{timeframe}'.", nameof(timeframe));

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var nowUtc = _timeProvider.GetUtcNow();

        var record = await TryRecordAsync(canonical, nowMs, nowUtc, cancellationToken);
        // Fill/outcome resolution runs on EVERY poll — also when there is nothing
        // new to record — so pending rows resolve once later bars are ingested.
        var resolved = await ResolvePendingAsync(cancellationToken);
        return record with { FillsResolved = resolved.Fills, OutcomesResolved = resolved.Outcomes };
    }

    private async Task<ObserveReport> TryRecordAsync(
        string canonical, long nowMs, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var signalOpenMs = _calendar.LastExpectedFinalizedOpenUtcMs(canonical, nowMs);
        if (signalOpenMs is null)
            return new ObserveReport(canonical, "no-expected-open", null, null, null, 0, 0,
                "no finalized expected open exists yet for this timeframe");

        var already = await _db.MbbSignalObservations.AsNoTracking()
            .AnyAsync(o => o.Symbol == MbbDecisionService.SupportedSymbol
                           && o.Timeframe == canonical
                           && o.SignalBarOpenTimeMs == signalOpenMs.Value,
                cancellationToken);
        if (already)
        {
            return new ObserveReport(canonical, "already-recorded", signalOpenMs, null, null, 0, 0,
                "signal bar already observed");
        }

        var signalBar = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == MbbDecisionService.SupportedSymbol
                        && c.Timeframe == canonical
                        && c.OpenTimeMs == signalOpenMs.Value)
            .FirstOrDefaultAsync(cancellationToken);
        if (signalBar is null)
        {
            return new ObserveReport(canonical, "finalized-bar-not-ingested", signalOpenMs, null, null, 0, 0,
                "the latest expected finalized bar is not stored yet — sync first; never observed on stale data");
        }
        if (nowMs < signalBar.CloseTimeMs)
        {
            return new ObserveReport(canonical, "bar-not-finalized", signalOpenMs, null, null, 0, 0,
                "signal bar close is still in the future");
        }

        var decision = await _decision.DecideAsync(MbbDecisionService.SupportedSymbol, cancellationToken);
        var row = new MbbSignalObservation
        {
            Id = Guid.NewGuid(),
            DecisionId = $"mbb-observe/{canonical}/{signalOpenMs.Value}",
            RecorderVersion = RecorderVersion,
            Symbol = MbbDecisionService.SupportedSymbol,
            Timeframe = canonical,
            SignalBarOpenTimeMs = signalBar.OpenTimeMs,
            SignalBarCloseTimeMs = signalBar.CloseTimeMs,
            ObservedAtUtc = nowUtc,
            AvailableTimeMs = nowMs,
            Decision = decision.Action,
            RulesVersion = "declared-rules-v1",
            EvidenceStatus = decision.EvidenceStatus,
            PriceBasis = decision.PriceBasis,
            MarketCloseVnd = decision.Market.LastDailyCloseVnd,
            DecisionPayloadJson = JsonSerializer.Serialize(decision),
            CreatedAtUtc = nowUtc,
        };
        _db.MbbSignalObservations.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var alreadyInserted = await _db.MbbSignalObservations.AsNoTracking()
                .AnyAsync(o => o.DecisionId == row.DecisionId, cancellationToken);
            if (!alreadyInserted) throw;
            // Concurrent poll inserted the same deterministic key — idempotent no-op.
            _db.Entry(row).State = EntityState.Detached;
            return new ObserveReport(canonical, "already-recorded", signalOpenMs, decision.Action,
                row.DecisionId, 0, 0, "concurrent insert; already observed");
        }

        return new ObserveReport(canonical, "recorded", signalOpenMs, decision.Action, row.DecisionId, 0, 0, null);
    }

    /// <summary>
    /// Resolves fills/outcomes for unresolved actionable observations from
    /// already-stored later bars. Write-once: only NULL fields are updated.
    /// </summary>
    private async Task<(int Fills, int Outcomes)> ResolvePendingAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var fills = 0;
        var outcomes = 0;

        var unfilled = await _db.MbbSignalObservations
            .Where(o => o.Symbol == MbbDecisionService.SupportedSymbol
                        && o.FillPrice == null
                        && (o.Decision == MbbDecisionActions.BuyMore
                            || o.Decision == MbbDecisionActions.Reduce
                            || o.Decision == MbbDecisionActions.Sell
                            || o.Decision == MbbDecisionActions.ConditionalSell))
            .OrderBy(o => o.SignalBarOpenTimeMs)
            .ToListAsync(cancellationToken);

        var nowMs = nowUtc.ToUnixTimeMilliseconds();
        foreach (var obs in unfilled)
        {
            if (obs.Decision == MbbDecisionActions.ConditionalSell)
            {
                if (await TryResolveConditionalSellAsync(obs, nowUtc, nowMs, cancellationToken))
                    fills++;
                continue;
            }

            // buy-more/reduce/sell execute at the first stored open at/after the
            // observation moment — never at an open that already existed before
            // the decision was recorded (late recording pushes the fill forward).
            // The fill bar itself must be finalized (CloseTimeMs <= now) — a
            // still-forming bar can be amended, so its open is not a real fill.
            var nextBar = await _db.InstrumentCandles.AsNoTracking()
                .Where(c => c.Symbol == obs.Symbol
                            && c.Timeframe == obs.Timeframe
                            && c.OpenTimeMs >= obs.AvailableTimeMs
                            && c.CloseTimeMs <= nowMs)
                .OrderBy(c => c.OpenTimeMs)
                .FirstOrDefaultAsync(cancellationToken);
            if (nextBar is null) continue; // nothing stored yet — stay unresolved, never fabricate

            obs.FillPrice = nextBar.Open;
            obs.FillBarOpenTimeMs = nextBar.OpenTimeMs;
            obs.FillObservedAtUtc = nowUtc;
            obs.FillSource = FillSourceNextBarOpen;
            fills++;
        }

        // Persist fills before the outcome query: outcome resolution keys off
        // FillPrice != null, which must be visible to the next read.
        if (fills > 0)
            await _db.SaveChangesAsync(cancellationToken);

        var unresolvedOutcomes = await _db.MbbSignalObservations
            .Where(o => o.Symbol == MbbDecisionService.SupportedSymbol
                        && o.FillPrice != null
                        && o.FillBarOpenTimeMs != null
                        && o.OutcomeReturn == null)
            .ToListAsync(cancellationToken);

        foreach (var obs in unresolvedOutcomes)
        {
            var fillBar = await _db.InstrumentCandles.AsNoTracking()
                .Where(c => c.Symbol == obs.Symbol
                            && c.Timeframe == obs.Timeframe
                            && c.OpenTimeMs == obs.FillBarOpenTimeMs)
                .FirstOrDefaultAsync(cancellationToken);
            if (fillBar is null || fillBar.CloseTimeMs > nowUtc.ToUnixTimeMilliseconds())
                continue; // fill bar not finalized yet

            obs.OutcomeReturn = (double)((fillBar.Close - obs.FillPrice!.Value) / obs.FillPrice.Value);
            obs.OutcomeObservedAtUtc = nowUtc;
            obs.OutcomeHorizon = OutcomeHorizonNextBarClose;
            outcomes++;
        }

        if (outcomes > 0)
            await _db.SaveChangesAsync(cancellationToken);
        return (fills, outcomes);
    }

    /// <summary>
    /// conditional-sell is NOT a next-open order: it is a conditional order that
    /// only exists once a declared trigger fires. Semantics: evaluate the
    /// decision's close-below triggers on their own timeframes; the first
    /// finalized bar (closed at/after the observation moment) whose close
    /// breaches the highest level activates the order, and execution then takes
    /// the next stored open of the OBSERVATION timeframe at/after that trigger
    /// close. If no trigger ever fires, the row honestly stays unfilled.
    /// </summary>
    private async Task<bool> TryResolveConditionalSellAsync(
        MbbSignalObservation obs, DateTimeOffset nowUtc, long nowMs, CancellationToken cancellationToken)
    {
        List<MbbDecisionTrigger> triggers;
        try
        {
            triggers = JsonSerializer.Deserialize<MbbDecisionDto>(obs.DecisionPayloadJson)
                ?.Triggers.Where(t => t.Direction == MbbTriggerDirections.CloseBelow
                                      && t.PriceLevelVnd is not null
                                      && (t.Timeframe == EquityCandleTimeframes.Day
                                          || t.Timeframe == EquityCandleTimeframes.Hour))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return false; // payload unreadable → stay unresolved, never guess
        }
        if (triggers.Count == 0) return false;

        // Earliest breached trigger across all trigger timeframes wins. Trigger
        // bars must be FINALIZED at/after the observation moment — a close that
        // completed before we recorded could not have been acted on.
        InstrumentCandle? triggerBar = null;
        foreach (var group in triggers.GroupBy(t => t.Timeframe))
        {
            var level = group.Max(t => t.PriceLevelVnd!.Value);
            var hit = await _db.InstrumentCandles.AsNoTracking()
                .Where(c => c.Symbol == obs.Symbol
                            && c.Timeframe == group.Key
                            && c.CloseTimeMs >= obs.AvailableTimeMs
                            && c.CloseTimeMs <= nowMs
                            && c.Close <= level)
                .OrderBy(c => c.CloseTimeMs)
                .FirstOrDefaultAsync(cancellationToken);
            if (hit is not null && (triggerBar is null || hit.CloseTimeMs < triggerBar.CloseTimeMs))
                triggerBar = hit;
        }
        if (triggerBar is null) return false; // condition never fired → not a fill

        var fillBar = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == obs.Symbol
                        && c.Timeframe == obs.Timeframe
                        && c.OpenTimeMs >= triggerBar.CloseTimeMs
                        && c.CloseTimeMs <= nowMs)
            .OrderBy(c => c.OpenTimeMs)
            .FirstOrDefaultAsync(cancellationToken);
        if (fillBar is null) return false; // triggered but next open not stored yet

        obs.FillPrice = fillBar.Open;
        obs.FillBarOpenTimeMs = fillBar.OpenTimeMs;
        obs.FillObservedAtUtc = nowUtc;
        obs.FillSource = FillSourcePostTriggerNextOpen;
        return true;
    }
}

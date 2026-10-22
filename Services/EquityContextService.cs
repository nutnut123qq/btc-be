using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// GET /api/equities/{symbol}/context (equity-context-v1, spec §6/§11). Read-only, point-in-time:
/// rows whose AvailableAtUtc is still in the future are not shown. Relative strength is computed
/// from stored finalized 1d InstrumentCandles on the INTERSECTION of session dates with each
/// benchmark (VNINDEX + bank peers), windows 20/60; a missing/short benchmark is reported
/// unavailable with its reason rather than approximated.
/// </summary>
public sealed class EquityContextService
{
    public static readonly IReadOnlyList<int> Windows = [20, 60];
    private const int LoadBars = 400;
    private const int MaxItems = 60;
    private const int FundamentalPeriods = 8;

    private readonly AppDbContext _db;
    private readonly VietnamMarketCalendar _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;

    public EquityContextService(
        AppDbContext db, VietnamMarketCalendar calendar, TimeProvider? timeProvider = null, InstrumentCatalog? catalog = null)
    {
        _db = db;
        _calendar = calendar;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    /// <summary>Benchmarks: the index plus peers (VCB, CTG and the other decision bank).</summary>
    internal IReadOnlyList<(string Symbol, string Kind)> BenchmarksFor(string symbol) =>
        _catalog.Instruments
            .Where(i => i.Symbol != symbol && EquityScope.Has(i, EquityCapability.Ingestion)
                        && i.ActiveTimeframes.Contains(EquityCandleTimeframes.Day, StringComparer.Ordinal))
            .OrderBy(i => i.AssetClass == InstrumentAssetClasses.Index ? 0 : 1)
            .Select(i => (i.Symbol, i.AssetClass == InstrumentAssetClasses.Index ? "index" : "peer"))
            .ToArray();

    public async Task<EquityContextResponse> GetAsync(string symbol, CancellationToken ct = default)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        var sym = instrument.Symbol;
        var now = _timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        var rs = await RelativeStrengthAsync(sym, now.ToUnixTimeMilliseconds(), ct);

        // PIT: filter on the EFFECTIVE availability (stored rows are never modified). The DB prefilter on the
        // stored value is safe because effective >= stored.
        var actions = (await _db.EquityCorporateActions.AsNoTracking()
                .Where(x => x.Symbol == sym && x.AvailableAtUtc <= nowUtc).ToListAsync(ct))
            .Select(a => (Row: a, Avail: EffectiveAvailability(a.AvailableAtUtc, a.CreatedAtUtc, ItemDate(a))))
            .Where(x => x.Avail.At <= nowUtc)
            .OrderByDescending(x => x.Row.ExDate).Take(MaxItems).ToList();
        var events = (await _db.EquityEvents.AsNoTracking()
                .Where(x => x.Symbol == sym && x.AvailableAtUtc <= nowUtc).ToListAsync(ct))
            .Select(e => (Row: e, Avail: EffectiveAvailability(e.AvailableAtUtc, e.CreatedAtUtc, e.EventDate)))
            .Where(x => x.Avail.At <= nowUtc)
            .OrderByDescending(x => x.Row.EventDate).Take(MaxItems).ToList();
        var fundamentals = await _db.EquityFundamentals.AsNoTracking()
            .Where(x => x.Symbol == sym && x.AvailableAtUtc <= nowUtc)
            .ToListAsync(ct);
        var anySynced = actions.Count + events.Count + fundamentals.Count > 0
            || await _db.EquityCorporateActions.AnyAsync(x => x.Symbol == sym, ct)
            || await _db.EquityFundamentals.AnyAsync(x => x.Symbol == sym, ct);

        var periods = fundamentals.Select(f => f.Period).Distinct().OrderByDescending(p => p, StringComparer.Ordinal)
            .Take(FundamentalPeriods).ToHashSet(StringComparer.Ordinal);
        var fundamentalItems = fundamentals
            .Where(f => periods.Contains(f.Period))
            .OrderByDescending(f => f.Period, StringComparer.Ordinal).ThenBy(f => f.Metric, StringComparer.Ordinal)
            .ThenBy(f => f.Source, StringComparer.Ordinal) // same metric from KBS and issuer-report = separate rows
            .Select(f => new EquityFundamentalDto(f.Period, f.PeriodEndDate.ToString("yyyy-MM-dd"), f.Metric, f.Value, f.Unit,
                f.UnavailableReason, f.PublishedDate?.ToString("yyyy-MM-dd"), f.AvailableAtUtc, f.AsOfUtc, f.Source,
                f.SourceUrl, EquityContextRules.NoteFor(f.Metric, f.Source), f.DocumentId, f.SourceLocation))
            .ToList();
        var latestPeriod = periods.OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
        foreach (var metric in EquityContextRules.UnavailableMetrics)
        {
            if (fundamentals.Any(f => f.Metric == metric)) continue;
            fundamentalItems.Add(new EquityFundamentalDto(latestPeriod,
                latestPeriod is null ? null : EquityContextRules.QuarterEnd(latestPeriod).ToString("yyyy-MM-dd"),
                metric, null, "percent", EquityContextRules.NoPitSourceReason, null, null, null, null, null, null));
        }

        var notSynced = anySynced ? null : "context-not-synced";
        return new EquityContextResponse(
            EquityContextResponse.Version, sym, nowUtc, rs,
            new EquityCorporateActionsSection(Section(actions.Count, notSynced), actions.Count == 0 ? notSynced ?? "no-corporate-actions" : null,
                actions.Select(x => new EquityCorporateActionDto(x.Row.ExDate.ToString("yyyy-MM-dd"), x.Row.ActionType, x.Row.RatioText,
                    x.Row.CashPerShareVnd, x.Row.AdjustmentFactor, x.Row.RecordDate?.ToString("yyyy-MM-dd"),
                    x.Row.AnnouncedDate?.ToString("yyyy-MM-dd"), x.Row.SourceUrl, x.Row.Provenance, x.Row.VerificationStatus,
                    x.Avail.At, x.Row.ShareRatio, x.Avail.Basis, x.Avail.Reason, x.Row.AvailableAtUtc)).ToArray()),
            new EquityEventsSection(Section(events.Count, notSynced), events.Count == 0 ? notSynced ?? "no-events" : null,
                events.Select(x => new EquityEventDto(x.Row.EventType, x.Row.EventDate.ToString("yyyy-MM-dd"), x.Row.Title,
                    x.Row.SourceUrl, x.Row.Provenance, x.Avail.At, x.Avail.Basis, x.Avail.Reason, x.Row.AvailableAtUtc)).ToArray()),
            new EquityFundamentalsSection(Section(fundamentals.Count, notSynced),
                fundamentals.Count == 0 ? notSynced ?? "no-fundamentals" : null, fundamentalItems),
            Limitations());
    }

    /// <summary>The item's own date: ex-date, or the record date when that is earlier.</summary>
    internal static DateOnly ItemDate(EquityCorporateAction a) =>
        a.RecordDate is { } rd && rd < a.ExDate ? rd : a.ExDate;

    /// <summary>
    /// EQX v2 PIT: a publish-based availability more than <see cref="EquityAvailabilityBases.MaxLeadDays"/> days
    /// before the item's own date (00:00 VN) is implausible (VCI reuses old disclosure dates) ⇒ use the row's
    /// first-seen time instead (never earlier than the stored value).
    /// </summary>
    internal static (DateTime At, string Basis, string? Reason) EffectiveAvailability(
        DateTime storedAvailableAtUtc, DateTime createdAtUtc, DateOnly itemDate)
    {
        var floorUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            VietnamMarketCalendar.ToUtcMs(itemDate.AddDays(-EquityAvailabilityBases.MaxLeadDays), TimeOnly.MinValue)).UtcDateTime;
        var stored = DateTime.SpecifyKind(storedAvailableAtUtc, DateTimeKind.Utc);
        if (stored >= floorUtc)
            return (stored, EquityAvailabilityBases.ProviderPublishDate, null);
        var firstSeen = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        return (firstSeen > stored ? firstSeen : stored, EquityAvailabilityBases.FirstSeen, EquityAvailabilityBases.ImplausibleReason);
    }

    private static string Section(int count, string? notSynced) =>
        count > 0 ? EquityContextStatuses.Ok : notSynced is null ? EquityContextStatuses.Empty : EquityContextStatuses.Unavailable;

    private static string[] Limitations() =>
    [
        "Prices are VCI back-adjusted (latest adjustment basis, not point-in-time); RS returns are close-to-close fractions on the intersected session dates.",
        "Corporate actions/events come from VCI IQ events; publication dates are date-only, so availableAtUtc = 00:00 VN of the next day. Provider rows are unverified.",
        "Insider-dealing and other VCI event codes are not collected.",
        "Fundamentals come from KBS finance-info CSTC only (one source per field); values keep the published unit (percent / ratio / VND). Only the latest quarter is taken per sync (KBS Value↔period mapping beyond the latest is unverified).",
        "NPL, COVERAGE and CAR have no verified point-in-time source and are reported unavailable unless imported by an admin with a source URL.",
        "Rows whose availableAtUtc is in the future are hidden.",
        "Events/corporate actions whose provider publish date is more than 90 days before the item's own date (VCI reuses old disclosure dates) use the row's first-seen time instead: availabilityBasis first-seen, reason provider-publish-date-implausible; publishedAvailableAtUtc keeps the stored value.",
    ];

    internal async Task<EquityRelativeStrengthSection> RelativeStrengthAsync(string symbol, long nowMs, CancellationToken ct)
    {
        var instrumentBars = await LoadClosesAsync(symbol, nowMs, ct);
        var benchmarks = new List<EquityRsBenchmarkDto>();
        if (instrumentBars.Count == 0)
        {
            var none = Windows.ToDictionary(w => w.ToString(), _ => (EquityRsWindowDto?)null);
            return new EquityRelativeStrengthSection(EquityContextStatuses.Unavailable, "instrument-not-ingested", Windows,
                BenchmarksFor(symbol).Select(b => new EquityRsBenchmarkDto(b.Symbol, b.Kind, EquityContextStatuses.Unavailable,
                    "instrument-not-ingested", 0, null, none)).ToArray());
        }

        foreach (var (bench, kind) in BenchmarksFor(symbol))
        {
            var benchBars = await LoadClosesAsync(bench, nowMs, ct);
            benchmarks.Add(Compare(bench, kind, instrumentBars, benchBars, _calendar));
        }
        var ok = benchmarks.Any(b => b.Status == EquityContextStatuses.Ok);
        var stale = !ok && benchmarks.Any(b => b.Status == EquityContextStatuses.Stale);
        return new EquityRelativeStrengthSection(
            ok ? EquityContextStatuses.Ok : stale ? EquityContextStatuses.Stale : EquityContextStatuses.Unavailable,
            ok ? null : stale ? "benchmarks-stale" : "no-benchmark-available", Windows, benchmarks);
    }

    public const string BenchmarkStaleReason = "benchmark-stale";
    public const string AlignedWindowBehindReason = "aligned-window-ends-before-instrument-latest";

    /// <summary>
    /// Trading sessions the benchmark is behind the instrument: calendar sessions in
    /// (benchmark latest date, instrument latest date] when a calendar is given, else the
    /// instrument's own bars after the benchmark's latest bar.
    /// </summary>
    internal static int LagSessions(long instrumentLatest, long benchmarkLatest,
        IReadOnlyDictionary<long, decimal> instrument, VietnamMarketCalendar? calendar)
    {
        if (benchmarkLatest >= instrumentLatest) return 0;
        if (calendar is null) return instrument.Keys.Count(k => k > benchmarkLatest);
        var from = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(benchmarkLatest).DateTime).AddDays(1);
        var to = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(instrumentLatest).DateTime);
        return to < from ? 0 : calendar.EnumerateTradingDates(from, to).Count;
    }

    internal static EquityRsBenchmarkDto Compare(
        string bench, string kind, IReadOnlyDictionary<long, decimal> instrument, IReadOnlyDictionary<long, decimal> benchmark,
        VietnamMarketCalendar? calendar = null)
    {
        var empty = Windows.ToDictionary(w => w.ToString(), _ => (EquityRsWindowDto?)null);
        if (benchmark.Count == 0)
            return new EquityRsBenchmarkDto(bench, kind, EquityContextStatuses.Unavailable, "benchmark-not-ingested", 0, null, empty);
        var aligned = instrument.Keys.Where(benchmark.ContainsKey).OrderBy(k => k).ToArray();
        var minNeeded = Windows.Min() + 1;
        if (aligned.Length < minNeeded)
            return new EquityRsBenchmarkDto(bench, kind, EquityContextStatuses.Unavailable,
                $"insufficient-aligned-sessions ({aligned.Length} < {minNeeded})", aligned.Length,
                aligned.Length == 0 ? null : aligned[^1], empty);
        var returns = new Dictionary<string, EquityRsWindowDto?>();
        var last = aligned[^1];
        foreach (var w in Windows)
        {
            if (aligned.Length < w + 1) { returns[w.ToString()] = null; continue; }
            var start = aligned[^(w + 1)];
            var ri = (double)(instrument[last] / instrument[start]) - 1.0;
            var rb = (double)(benchmark[last] / benchmark[start]) - 1.0;
            returns[w.ToString()] = new EquityRsWindowDto(Math.Round(ri, 6), Math.Round(rb, 6), Math.Round(ri - rb, 6));
        }
        // EQX v2 A4: a benchmark that stopped ingesting silently moves the intersection back; never call that "ok".
        var instrumentLatest = instrument.Keys.Max();
        var benchmarkLatest = benchmark.Keys.Max();
        var lag = LagSessions(instrumentLatest, benchmarkLatest, instrument, calendar);
        var (status, reason) = lag > 1
            ? (EquityContextStatuses.Stale, BenchmarkStaleReason)
            : last < instrumentLatest
                ? (EquityContextStatuses.Stale, AlignedWindowBehindReason)
                : (EquityContextStatuses.Ok, (string?)null);
        return new EquityRsBenchmarkDto(bench, kind, status, reason, aligned.Length, last, returns,
            instrumentLatest, benchmarkLatest, lag);
    }

    /// <summary>Last ~400 finalized expected-session daily closes keyed by open time.</summary>
    private async Task<IReadOnlyDictionary<long, decimal>> LoadClosesAsync(string symbol, long nowMs, CancellationToken ct)
    {
        var rows = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == EquityCandleTimeframes.Day)
            .OrderByDescending(c => c.OpenTimeMs)
            .Take(LoadBars)
            .Select(c => new { c.OpenTimeMs, c.Close })
            .ToListAsync(ct);
        return rows
            .Where(r => r.Close > 0 && _calendar.IsFinalized(EquityCandleTimeframes.Day, r.OpenTimeMs, nowMs))
            .ToDictionary(r => r.OpenTimeMs, r => r.Close);
    }
}

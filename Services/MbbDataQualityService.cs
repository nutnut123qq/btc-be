using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// MBB coverage / freshness / anomaly reporting over <see cref="InstrumentCandle"/>.
/// Internal service surface (no public endpoint in this lane): MBB-03+ can project these
/// reports into API/UI. All expectations come from <see cref="VietnamMarketCalendar"/>,
/// so weekends/holidays/lunch are non-gaps and the known 2024-06-20 12:00 VCI bar
/// surfaces as an anomaly count rather than corrupting the expected set.
/// </summary>
public sealed class MbbDataQualityService
{
    /// <summary>KBS vs VCI close-price diff (VND) counted as a mismatch. Per MBB-00
    /// evidence 1,463 of 1,692 overlapping daily rows differ by more than 1 VND because
    /// the bases differ (adjusted vs integer-rounded); the threshold only splits noise.</summary>
    internal const decimal CrossCheckMismatchThresholdVnd = 1m;

    private readonly AppDbContext _db;
    private readonly IKbsOhlcvClient? _kbs;
    private readonly VietnamMarketCalendar _calendar;
    private readonly MbbIngestionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentDescriptor _instrument;
    private readonly InstrumentCatalog _catalog;

    public MbbDataQualityService(
        AppDbContext db,
        MbbIngestionOptions? options = null,
        VietnamMarketCalendar? calendar = null,
        IKbsOhlcvClient? kbs = null,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null)
    {
        _db = db;
        _options = options ?? new MbbIngestionOptions();
        _calendar = calendar ?? VietnamMarketCalendar.CreateDefault(
            _options.NonTradingDates,
            TimeSpan.FromMinutes(_options.PublishGraceMinutes));
        _kbs = kbs;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _instrument = _catalog.Get(_options.Symbol)
            ?? throw new ArgumentException(
                $"Symbol '{_options.Symbol}' is not a known instrument in the catalog.", nameof(options));
    }

    public InstrumentDescriptor Instrument => _instrument;

    /// <summary>
    /// Same report bound to another Ingestion-capable catalog symbol (EQX). MBB yields this
    /// same instance, so the /api/mbb alias report is unchanged.
    /// </summary>
    public MbbDataQualityService ForSymbol(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Ingestion))
            throw new ArgumentException(
                $"Symbol '{instrument.Symbol}' does not support the equity ingestion lane.", nameof(symbol));
        if (string.Equals(instrument.Symbol, _instrument.Symbol, StringComparison.Ordinal))
            return this;
        return new MbbDataQualityService(_db, _options.ForSymbol(instrument.Symbol), _calendar, _kbs,
            _timeProvider, _catalog);
    }

    private IReadOnlyList<string> EffectiveTimeframes() =>
        _options.Timeframes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Where(t => _instrument.ActiveTimeframes.Contains(t, StringComparer.Ordinal))
            .ToArray();

    private DateOnly BackfillStartLocal(string timeframe)
    {
        var raw = timeframe == EquityCandleTimeframes.Day
            ? _options.DailyBackfillStartLocal
            : _options.HourlyBackfillStartLocal;
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", out var date)
            ? date
            : throw new InvalidOperationException(
                $"MbbIngestion backfill start '{raw}' is not a yyyy-MM-dd local date.");
    }

    /// <summary>Full coverage/freshness report across the instrument's active timeframes.</summary>
    public async Task<MbbCoverageReport> BuildReportAsync(CancellationToken cancellationToken = default)
    {
        var nowUtcMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var todayLocal = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(nowUtcMs).DateTime);
        var blocks = new List<MbbTimeframeCoverage>();
        var peers = await LoadPeerDailyPresenceAsync(cancellationToken);

        foreach (var tf in EffectiveTimeframes())
        {
            var rows = await _db.InstrumentCandles
                .Where(c => c.Symbol == _instrument.Symbol && c.Timeframe == tf)
                .OrderBy(c => c.OpenTimeMs)
                .ToListAsync(cancellationToken);

            var expected = _calendar
                .EnumerateExpectedOpensUtcMs(tf, BackfillStartLocal(tf), todayLocal)
                .Where(o => _calendar.IsFinalized(tf, o, nowUtcMs))
                .ToArray();
            var storedSet = rows.Select(r => r.OpenTimeMs).ToHashSet();
            var missing = expected.Where(o => !storedSet.Contains(o)).ToHashSet();
            var ranges = VietnamMarketCalendar.GroupMissingRanges(expected, missing)
                .OrderByDescending(r => r.MissingBars)
                .ToArray();
            var classified = ClassifyMissingRanges(expected, missing, peers, _calendar);
            var flags = new List<string>();
            if (_calendar.IsBelowCoverage(BackfillStartLocal(tf))) flags.Add(VietnamMarketCalendar.BelowCoverageReason);
            var coverageExpired = _calendar.IsBeyondCoverage(todayLocal);
            if (coverageExpired) flags.Add(VietnamMarketCalendar.CoverageExpiredReason);
            long CountClass(string c) => classified.Where(r => r.Classification == c).Sum(r => r.MissingBars);
            var unverifiedDays = CountUnverifiedNonTradingDays(BackfillStartLocal(tf), todayLocal);

            var unexpected = new List<long>();
            var invalid = 0L;
            foreach (var row in rows)
            {
                var kind = tf == EquityCandleTimeframes.Day
                    ? _calendar.ClassifyDailyOpenUtcMs(row.OpenTimeMs)
                    : _calendar.ClassifyHourlyOpenUtcMs(row.OpenTimeMs);
                if (kind != SessionOpenKind.Expected) unexpected.Add(row.OpenTimeMs);
                if (row.Open <= 0 || row.High <= 0 || row.Low <= 0 || row.Close <= 0
                    || row.Volume < 0 || row.High < row.Low
                    || row.High < row.Open || row.High < row.Close
                    || row.Low > row.Open || row.Low > row.Close)
                    invalid++;
            }

            var lastExpected = _calendar.LastExpectedFinalizedOpenUtcMs(tf, nowUtcMs);
            var lastStored = rows.Count == 0 ? (long?)null : rows[^1].OpenTimeMs;
            var ageSec = lastStored.HasValue ? (nowUtcMs - lastStored.Value) / 1000 : (long?)null;
            var staleAfterMs = lastExpected.HasValue
                ? _calendar.SessionCloseUtcMs(tf, lastExpected.Value)
                    + (long)TimeSpan.FromMinutes(
                        Math.Max(1, _options.PublishGraceMinutes)
                        + Math.Max(1, _options.CycleMinutes)).TotalMilliseconds
                : (long?)null;
            // Past the calendar coverage the expected bar is unknown: report stale (fail closed) with the reason.
            var isStale = coverageExpired || (lastExpected.HasValue
                && nowUtcMs > staleAfterMs!.Value
                && (!lastStored.HasValue || lastStored.Value < lastExpected.Value));

            blocks.Add(new MbbTimeframeCoverage(
                tf,
                rows.Count,
                rows.Count == 0 ? null : VietnamMarketCalendar.ToLocal(rows[0].OpenTimeMs).ToUniversalTime(),
                rows.Count == 0 ? null : VietnamMarketCalendar.ToLocal(rows[^1].OpenTimeMs).ToUniversalTime(),
                expected.Length,
                missing.Count,
                expected.Length == 0 ? 100.0 : Math.Round(100.0 * (expected.Length - missing.Count) / expected.Length, 3),
                ranges.Length,
                ranges.Take(10).ToArray(),
                unexpected.Count,
                unexpected.Take(10).ToArray(),
                invalid,
                lastExpected.HasValue
                    ? VietnamMarketCalendar.ToLocal(lastExpected.Value).ToUniversalTime() : null,
                ageSec,
                isStale,
                CountClass(MbbGapClassifications.MarketWideMissing),
                CountClass(MbbGapClassifications.SymbolMissing),
                CountClass(MbbGapClassifications.NoPeerData),
                classified.OrderByDescending(r => r.MissingBars).ThenBy(r => r.StartOpenTimeMs).Take(10).ToArray(),
                unverifiedDays,
                CountClass(MbbGapClassifications.BelowCalendarCoverage),
                CountClass(MbbGapClassifications.CalendarCoverageExpired),
                flags,
                coverageExpired ? VietnamMarketCalendar.CoverageExpiredReason : null));
        }

        return new MbbCoverageReport(
            _instrument.Symbol,
            _instrument.DataProvider,
            _instrument.CrossCheckDataProvider ?? "",
            _instrument.PriceBasis,
            _instrument.Venue,
            _instrument.Timezone,
            _timeProvider.GetUtcNow().UtcDateTime,
            blocks,
            BuildLimitations(_instrument),
            BuildCalendarInfo(peers.Symbols));
    }

    private MbbCalendarInfo BuildCalendarInfo(IReadOnlyList<string> peerSymbols)
    {
        var source = _calendar.Source;
        return source is null
            ? new MbbCalendarInfo("explicit", null, null, null, 0, 0, [], peerSymbols)
            : new MbbCalendarInfo(
                "canonical-file",
                source.CalendarVersion,
                source.CoverageFrom.ToString("yyyy-MM-dd"),
                source.CoverageTo.ToString("yyyy-MM-dd"),
                source.VerifiedCount,
                source.UnverifiedLegacyCount,
                source.ConfigOverrideDates.Select(d => d.ToString("yyyy-MM-dd")).ToArray(),
                peerSymbols);
    }

    private long CountUnverifiedNonTradingDays(DateOnly from, DateOnly to)
    {
        var source = _calendar.Source;
        if (source is null) return 0;
        return source.Days.Where(d => d.Verification != MarketCalendarVerification.Verified)
            .Select(d => d.Date)
            .Concat(source.ConfigOverrideDates)
            .Count(d => d >= from && d <= to);
    }

    /// <summary>Daily-bar presence of the OTHER equity-lane instruments (incl. VNINDEX), by local date.</summary>
    internal sealed record PeerDailyPresence(
        IReadOnlyList<string> Symbols,
        IReadOnlySet<DateOnly> TradedDates,
        IReadOnlyList<(DateOnly First, DateOnly Last)> Coverage);

    private async Task<PeerDailyPresence> LoadPeerDailyPresenceAsync(CancellationToken ct)
    {
        var symbols = _catalog.Instruments
            .Where(i => EquityScope.Has(i, EquityCapability.Ingestion)
                && !string.Equals(i.Symbol, _instrument.Symbol, StringComparison.Ordinal))
            .Select(i => i.Symbol)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        var rows = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => symbols.Contains(c.Symbol) && c.Timeframe == EquityCandleTimeframes.Day)
            .Select(c => new { c.Symbol, c.OpenTimeMs })
            .ToListAsync(ct);
        var traded = new HashSet<DateOnly>();
        var coverage = new List<(DateOnly, DateOnly)>();
        foreach (var group in rows.GroupBy(r => r.Symbol))
        {
            var dates = group.Select(r => DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(r.OpenTimeMs).DateTime)).ToArray();
            traded.UnionWith(dates);
            coverage.Add((dates.Min(), dates.Max()));
        }
        return new PeerDailyPresence(symbols, traded, coverage);
    }

    internal static string ClassifyMissingDate(DateOnly date, PeerDailyPresence peers, VietnamMarketCalendar? calendar = null)
    {
        if (calendar is not null && calendar.IsBelowCoverage(date)) return MbbGapClassifications.BelowCalendarCoverage;
        if (calendar is not null && calendar.IsBeyondCoverage(date)) return MbbGapClassifications.CalendarCoverageExpired;
        if (!peers.Coverage.Any(c => c.First <= date && c.Last >= date))
            return MbbGapClassifications.NoPeerData;
        return peers.TradedDates.Contains(date)
            ? MbbGapClassifications.SymbolMissing
            : MbbGapClassifications.MarketWideMissing;
    }

    /// <summary>
    /// Contiguous missing ranges (on the expected-open sequence), split wherever the classification changes.
    /// A market-wide-missing date is a suspected closure that the calendar does not know; it is reported,
    /// never added to the calendar automatically.
    /// </summary>
    internal static IReadOnlyList<MbbClassifiedGapRange> ClassifyMissingRanges(
        IReadOnlyList<long> expected, IReadOnlySet<long> missing, PeerDailyPresence peers,
        VietnamMarketCalendar? calendar = null)
    {
        var result = new List<MbbClassifiedGapRange>();
        long start = 0, end = 0, count = 0;
        string? current = null;
        var byDate = new Dictionary<DateOnly, string>();
        foreach (var open in expected)
        {
            if (!missing.Contains(open))
            {
                if (count > 0) { result.Add(new MbbClassifiedGapRange(start, end, count, current!)); count = 0; }
                continue;
            }
            var date = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(open).DateTime);
            if (!byDate.TryGetValue(date, out var cls))
                byDate[date] = cls = ClassifyMissingDate(date, peers, calendar);
            if (count > 0 && cls != current)
            {
                result.Add(new MbbClassifiedGapRange(start, end, count, current!));
                count = 0;
            }
            if (count == 0) { start = open; current = cls; }
            end = open;
            count++;
        }
        if (count > 0) result.Add(new MbbClassifiedGapRange(start, end, count, current!));
        return result;
    }

    private static string[] BuildLimitations(InstrumentDescriptor instrument) =>
        (string.Equals(instrument.Symbol, "MBB", StringComparison.Ordinal)
            ? MbbLimitations()
            : EquityLimitations(instrument))
        .Concat(HourlyLimitations(instrument))
        .ToArray();

    /// <summary>Prefix of the first hourly caveat; stable so readers can match it.</summary>
    internal const string HourlyOpeningBarLimitationPrefix = "1h opening bar";

    /// <summary>
    /// Caveats for every instrument that declares 1h (EQTECH-1 H; measurements and raw data in
    /// evidence/eqtech-1-20261029/H). Reported, never repaired: the missing 09:00 bars are not
    /// synthesised and no second source is mixed into the canonical VCI series.
    /// </summary>
    private static IEnumerable<string> HourlyLimitations(InstrumentDescriptor instrument)
    {
        if (!instrument.ActiveTimeframes.Contains(EquityCandleTimeframes.Hour, StringComparer.Ordinal))
            yield break;
        yield return HourlyOpeningBarLimitationPrefix + " (VCI 1h starts 2023-09-11): MBB has no 09:00 bar on any session "
            + "from 2023-09-11 to 2025-01-17 and one on every session from 2025-01-20 (measured 2026-10-29 on stored bars). "
            + "Missing 09:00 bars stay counted as missing expected bars and are never synthesised.";
        yield return "On those MBB sessions the 10:00 bar's volume also carries the 09:00 hour while its open/high/low start at "
            + "10:00 (342 sessions: sum of 1h volume = 99.9% of the 1d volume, as on complete sessions; the 10:00 bar holds a "
            + "median 37% of the day vs 18% when a 09:00 bar exists; the 1d open equals the first 1h open on 60 of 342). "
            + "Opening-hour price action and 1h volume readings before 2025-01-20 are not comparable with later sessions.";
        if (string.Equals(instrument.Symbol, "BID", StringComparison.Ordinal))
            yield return "BID: the 2026-10-16 provider probe found 342 of 773 sessions since 2023-09-11 without a 09:00 bar "
                + "(the same count as MBB) and the same stray 12:00 bar on 2024-06-20, which is stored and counted under "
                + "UnexpectedSessionBars. Which BID sessions lack 09:00, and the 10:00 volume fold, were not measured on BID: "
                + "read this report's 1h block.";
        yield return "No second source fills the opening bar: KBS data_60P is a cross-check only (integer-rounded, different "
            + "adjustment basis) and is never written into the store.";
    }

    /// <summary>Per-symbol caveats for the EQX instruments (W0 provider probe, 2026-10-16).</summary>
    private static string[] EquityLimitations(InstrumentDescriptor instrument)
    {
        var list = new List<string>();
        if (instrument.AssetClass == InstrumentAssetClasses.Index)
        {
            list.Add($"{instrument.Symbol} is an index quoted in {instrument.Currency}; it is never traded and carries no lot/fee/tax/band.");
            list.Add("No cross-check provider: VCI and KBS index volume definitions differ (0/30 recent bars equal), so volume is never cross-checked.");
            list.Add("VCI serves malformed OHLC rows around 2005; history is ingested from 2010-01-01 and invalid OHLC rows are rejected at write time.");
        }
        else
        {
            list.Add("VCI and KBS both back-adjust the WHOLE history at every ex-date (cash and stock dividends): stored levels reflect the latest adjustment basis, not as-of-time prices.");
            list.Add("A price change on an already-stored finalized bar triggers a full-history refetch so the stored series never mixes two adjustment factors.");
            list.Add("KBS daily history covers roughly the last ten years and is integer-rounded on a slightly different factor; cross-check diffs on old bars are expected.");
        }
        list.Add("Non-trading days come from the canonical HOSE calendar file (2020-2026; see GET /api/market-calendar/vn-hose for the official source per day). Older holidays surface as gaps in the expected-open count (honest, not hidden).");
        list.Add("Missing expected bars are classified: market-wide-missing (every other scope instrument + VNINDEX also lacks the date — suspected closure, never auto-added to the calendar) vs symbol-missing (others traded — provider gap or suspension, no reason inferred).");
        list.Add("Only finalized bars count as expected; a still-forming latest bar is never a gap.");
        return list.ToArray();
    }

    private static string[] MbbLimitations() =>
    [
        "KBS history begins 2020-01-02 — cross-check overlap is limited to that window.",
        "KBS closes are integer-rounded VND on a different adjustment basis; >1 VND diffs are expected on most rows.",
        "Non-trading days come from the canonical HOSE calendar file; an unlisted holiday/suspension on a weekday surfaces as a pending gap classified market-wide-missing or symbol-missing (honest, fixable) rather than being hidden.",
        "The VCI 1h bar at 2024-06-20 12:00 local is a known provider anomaly; it is stored but counted under UnexpectedSessionBars.",
        "Only finalized bars count as expected; a still-forming latest bar is never a gap.",
    ];

    /// <summary>
    /// Diagnostic cross-check of stored VCI closes against KBS closes over a local date
    /// window. Read-only: KBS data is never written into InstrumentCandles.
    /// </summary>
    public async Task<MbbCrossCheckReport> CrossCheckAsync(
        string timeframe,
        DateOnly fromLocal,
        DateOnly toLocal,
        CancellationToken cancellationToken = default)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_instrument.ActiveTimeframes.Contains(timeframe, StringComparer.Ordinal))
            throw new ArgumentException(
                $"timeframe '{timeframe}' is inactive for instrument '{_instrument.Symbol}'.",
                nameof(timeframe));
        if (string.IsNullOrWhiteSpace(_instrument.CrossCheckDataProvider))
            throw new InvalidOperationException(
                $"Symbol '{_instrument.Symbol}' declares no cross-check provider; cross-check is skipped by design.");
        if (_kbs is null)
            throw new InvalidOperationException("KBS cross-check client is not configured.");

        var kbsBars = timeframe == EquityCandleTimeframes.Day
            ? await _kbs.GetDailyBarsAsync(_instrument.Symbol, fromLocal, toLocal, cancellationToken)
            : await _kbs.GetHourlyBarsAsync(_instrument.Symbol, fromLocal, toLocal, cancellationToken);
        var kbsByOpen = kbsBars.ToDictionary(b => b.OpenTimeMs, b => b.Close);

        var fromMs = VietnamMarketCalendar.ToUtcMs(fromLocal, TimeOnly.MinValue);
        var toMs = VietnamMarketCalendar.ToUtcMs(toLocal.AddDays(1), TimeOnly.MinValue) - 1;
        var stored = await _db.InstrumentCandles
            .Where(c => c.Symbol == _instrument.Symbol && c.Timeframe == timeframe
                && c.OpenTimeMs >= fromMs && c.OpenTimeMs <= toMs)
            .ToListAsync(cancellationToken);
        var storedByOpen = stored.ToDictionary(c => c.OpenTimeMs, c => c.Close);

        var compared = 0;
        var mismatches = new List<MbbCrossCheckMismatch>();
        var onlyPrimary = 0;
        foreach (var (open, vciClose) in storedByOpen.OrderBy(kv => kv.Key))
        {
            if (!kbsByOpen.TryGetValue(open, out var kbsClose)) { onlyPrimary++; continue; }
            compared++;
            var diff = Math.Abs(vciClose - kbsClose);
            if (diff > CrossCheckMismatchThresholdVnd)
                mismatches.Add(new MbbCrossCheckMismatch(open, vciClose, kbsClose, diff));
        }
        var onlyKbs = kbsByOpen.Keys.Count(o => !storedByOpen.ContainsKey(o));

        return new MbbCrossCheckReport(
            _instrument.Symbol,
            _instrument.DataProvider,
            _instrument.CrossCheckDataProvider ?? "",
            timeframe,
            compared,
            onlyPrimary,
            onlyKbs,
            mismatches.Count,
            mismatches.OrderByDescending(m => m.MaxAbsPriceDiff).Take(20).ToArray());
    }
}

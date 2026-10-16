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
        _calendar = calendar ?? new VietnamMarketCalendar(
            VietnamMarketCalendar.ParseNonTradingDates(_options.NonTradingDates),
            TimeSpan.FromMinutes(_options.PublishGraceMinutes));
        _kbs = kbs;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _instrument = (catalog ?? InstrumentCatalog.Default).Get(_options.Symbol)
            ?? throw new ArgumentException(
                $"Symbol '{_options.Symbol}' is not a known instrument in the catalog.", nameof(options));
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
            var isStale = lastExpected.HasValue
                && nowUtcMs > staleAfterMs!.Value
                && (!lastStored.HasValue || lastStored.Value < lastExpected.Value);

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
                isStale));
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
            BuildLimitations());
    }

    private static string[] BuildLimitations() =>
    [
        "KBS history begins 2020-01-02 — cross-check overlap is limited to that window.",
        "KBS closes are integer-rounded VND on a different adjustment basis; >1 VND diffs are expected on most rows.",
        "NonTradingDates config is best-effort: an unlisted holiday/suspension on a weekday surfaces as a pending gap (honest, fixable) rather than being hidden.",
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

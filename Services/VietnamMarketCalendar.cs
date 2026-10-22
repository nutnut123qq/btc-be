using Backend.Services.Models;

namespace Backend.Services;

/// <summary>How a stored/observed hourly bar open relates to the HOSE session model.</summary>
public enum SessionOpenKind
{
    /// <summary>Expected 1h open on a trading day (09/10/11/13/14 local).</summary>
    Expected,
    /// <summary>12:00 local open — inside the lunch break; normally never produced.
    /// Presence is a provider anomaly (real: one VCI bar on 2024-06-20).</summary>
    LunchBreak,
    /// <summary>Local open time outside every session slot on a trading day.</summary>
    OffSession,
    /// <summary>Date is a weekend or configured non-trading date.</summary>
    NonTradingDay,
}

/// <summary>
/// Vietnam (HOSE) market calendar + session model for equity ingestion and gap auditing.
///
/// Key facts (MBB-00 probe evidence):
/// - VN timezone has no DST; a fixed UTC+7 offset is exact for all of MBB's history.
/// - HOSE sessions: morning continuous 09:00-11:30, afternoon 13:00-14:45 (incl. ATC).
/// - VCI/KBS 1h bars open at 09/10/11/13/14 local. The 11:00 bar closes 11:30 and the
///   14:00 bar closes 14:45 — durations are session-aware, never assumed +60m.
/// - VCI 1d bar open is stamped 07:00 local (= 00:00 UTC of the trading date).
/// - Missing 12:00 bars are NOT gaps (lunch break); a PRESENT 12:00 bar is an anomaly
///   to surface, not a normal bar to assume.
/// - Weekends and configured non-trading dates (holidays/suspensions) are non-gaps by
///   construction: they are excluded from the expected-open set. There is no implicit
///   crypto-style continuous cadence.
/// </summary>
public sealed class VietnamMarketCalendar
{
    public static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static readonly TimeOnly MorningOpenLocal = new(9, 0);
    public static readonly TimeOnly MorningCloseLocal = new(11, 30);
    public static readonly TimeOnly AfternoonOpenLocal = new(13, 0);
    public static readonly TimeOnly AfternoonCloseLocal = new(14, 45);

    /// <summary>VCI stamps daily bars at 07:00 local (00:00 UTC of the trading date).</summary>
    public static readonly TimeOnly DailyOpenLocal = new(7, 0);

    /// <summary>Expected 1h bar opens within a trading day, local time.</summary>
    public static readonly IReadOnlyList<TimeOnly> HourlyOpensLocal =
        [new TimeOnly(9, 0), new(10, 0), new(11, 0), new(13, 0), new(14, 0)];

    private readonly IReadOnlySet<DateOnly> _nonTradingDates;
    private readonly IReadOnlySet<DateOnly> _unverifiedDates;
    private readonly TimeSpan _publishGrace;

    /// <summary>
    /// Explicit calendar (tests / fixtures): exactly <paramref name="nonTradingDates"/> plus weekends.
    /// Production uses <see cref="FromSource"/> over the canonical file (EQX v2 A1).
    /// </summary>
    public VietnamMarketCalendar(IEnumerable<DateOnly>? nonTradingDates = null, TimeSpan? publishGrace = null)
    {
        _nonTradingDates = nonTradingDates is null
            ? new HashSet<DateOnly>()
            : new HashSet<DateOnly>(nonTradingDates);
        _unverifiedDates = new HashSet<DateOnly>();
        _publishGrace = publishGrace ?? TimeSpan.FromMinutes(10);
    }

    private VietnamMarketCalendar(MarketCalendarSource source, TimeSpan? publishGrace)
    {
        Source = source;
        _nonTradingDates = new HashSet<DateOnly>(source.AllNonTradingDates());
        _unverifiedDates = new HashSet<DateOnly>(source.Days
            .Where(d => d.Verification != MarketCalendarVerification.Verified)
            .Select(d => d.Date)
            .Concat(source.ConfigOverrideDates));
        _publishGrace = publishGrace ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>Calendar over the canonical HOSE file (∪ config override dates, which stay reported).</summary>
    public static VietnamMarketCalendar FromSource(MarketCalendarSource source, TimeSpan? publishGrace = null) =>
        new(source, publishGrace);

    /// <summary>
    /// Canonical calendar file ∪ <paramref name="configuredOverrides"/> ("yyyy-MM-dd"). Used by DI and by
    /// services constructed without an explicit calendar. Throws when the canonical file is missing/invalid.
    /// </summary>
    public static VietnamMarketCalendar CreateDefault(IEnumerable<string>? configuredOverrides = null, TimeSpan? publishGrace = null) =>
        new(MarketCalendarSource.Default.WithOverrides(ParseNonTradingDates(configuredOverrides)), publishGrace);

    /// <summary>Canonical source behind this calendar; null for an explicit (test) calendar.</summary>
    public MarketCalendarSource? Source { get; }

    /// <summary>Reason/flag: a needed date is after the canonical file's coverage.toDate (EQX v2 review #1).</summary>
    public const string CoverageExpiredReason = "calendar-coverage-expired";

    /// <summary>Flag: a date is before coverage.fromDate — holidays there are unknown, gaps are not real gaps.</summary>
    public const string BelowCoverageReason = "below-calendar-coverage";

    /// <summary>Coverage of the canonical file; null bounds for an explicit (test) calendar = unbounded.</summary>
    public DateOnly? CoverageFrom => Source?.CoverageFrom;
    public DateOnly? CoverageTo => Source?.CoverageTo;

    /// <summary>
    /// After coverage.toDate the calendar does NOT know the holidays: a weekday there is not known to trade,
    /// so callers must fail closed / flag <see cref="CoverageExpiredReason"/> instead of assuming it does.
    /// </summary>
    public bool IsBeyondCoverage(DateOnly date) => CoverageTo is { } to && date > to;

    public bool IsBelowCoverage(DateOnly date) => CoverageFrom is { } from && date < from;

    /// <summary>How [from, to] relates to the calendar coverage.</summary>
    public CalendarCoverageStatus Coverage(DateOnly from, DateOnly to) => new(
        CoverageFrom, CoverageTo,
        BelowCoverage: IsBelowCoverage(from),
        BeyondCoverage: IsBeyondCoverage(to));

    /// <summary>Throws <see cref="CalendarCoverageExpiredException"/> when <paramref name="date"/> is beyond coverage.</summary>
    public void EnsureCovered(DateOnly date, string what)
    {
        if (IsBeyondCoverage(date))
            throw new CalendarCoverageExpiredException(
                $"{what} {date:yyyy-MM-dd} is after the HOSE calendar coverage ({CoverageTo:yyyy-MM-dd}); add the new HOSE notice to {MarketCalendarSource.DefaultRelativePath}.");
    }

    /// <summary>
    /// Non-trading date that is NOT backed by an official source (unverified-legacy or config override).
    /// Still treated as closed (no fake gap), but reported by data quality.
    /// </summary>
    public bool IsUnverifiedNonTradingDate(DateOnly date) => _unverifiedDates.Contains(date);

    public static long ToUtcMs(DateOnly localDate, TimeOnly localTime) =>
        new DateTimeOffset(localDate.ToDateTime(localTime), VietnamOffset).ToUnixTimeMilliseconds();

    public static DateTimeOffset ToLocal(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToOffset(VietnamOffset);

    public bool IsNonTradingDate(DateOnly date) => _nonTradingDates.Contains(date);

    /// <summary>Trading day = weekday that is not a configured non-trading date.</summary>
    public bool IsTradingDay(DateOnly date) =>
        date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
        && !_nonTradingDates.Contains(date);

    /// <summary>Enumerate trading dates in [from, to] inclusive.</summary>
    public IReadOnlyList<DateOnly> EnumerateTradingDates(DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (IsTradingDay(d)) dates.Add(d);
        }
        return dates;
    }

    /// <summary>Expected 1d bar open (UTC ms) for a trading date.</summary>
    public static long DailyOpenUtcMs(DateOnly tradingDate) => ToUtcMs(tradingDate, DailyOpenLocal);

    /// <summary>Expected 1h bar opens (UTC ms) for a trading date: 09/10/11/13/14 local.</summary>
    public static IReadOnlyList<long> HourlyOpensUtcMs(DateOnly tradingDate) =>
        HourlyOpensLocal.Select(t => ToUtcMs(tradingDate, t)).ToArray();

    /// <summary>
    /// Expected bar opens (UTC ms, ascending) for a timeframe across [from, to] local dates.
    /// Only trading days contribute; weekends and configured non-trading dates produce no
    /// expectations, so they can never become gaps.
    /// </summary>
    public IReadOnlyList<long> EnumerateExpectedOpensUtcMs(string timeframe, DateOnly fromLocal, DateOnly toLocal)
    {
        var opens = new List<long>();
        foreach (var date in EnumerateTradingDates(fromLocal, toLocal))
        {
            switch (timeframe)
            {
                case EquityCandleTimeframes.Day:
                    opens.Add(DailyOpenUtcMs(date));
                    break;
                case EquityCandleTimeframes.Hour:
                    opens.AddRange(HourlyOpensUtcMs(date));
                    break;
                default:
                    throw new ArgumentException($"Unsupported equity timeframe '{timeframe}'.", nameof(timeframe));
            }
        }
        return opens;
    }

    /// <summary>
    /// Session-aware modeled bar close (UTC ms). For 1h the close is the next session
    /// boundary: 09->10:00, 10->11:00, 11->11:30, 13->14:00, 14->14:45 local. For 1d the
    /// close is the same-day session end 14:45 local. For an off-model open time the
    /// caller's open time is echoed back (anomalous bars keep minimal duration).
    /// </summary>
    public long SessionCloseUtcMs(string timeframe, long openUtcMs)
    {
        var local = ToLocal(openUtcMs);
        var date = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);

        if (timeframe == EquityCandleTimeframes.Day)
            return ToUtcMs(date, AfternoonCloseLocal);

        if (timeframe == EquityCandleTimeframes.Hour)
        {
            if (time == new TimeOnly(9, 0)) return ToUtcMs(date, new TimeOnly(10, 0));
            if (time == new TimeOnly(10, 0)) return ToUtcMs(date, new TimeOnly(11, 0));
            if (time == new TimeOnly(11, 0)) return ToUtcMs(date, MorningCloseLocal);
            if (time == new TimeOnly(13, 0)) return ToUtcMs(date, new TimeOnly(14, 0));
            if (time == new TimeOnly(14, 0)) return ToUtcMs(date, AfternoonCloseLocal);
            return openUtcMs; // off-model open (e.g. 12:00 anomaly): zero-duration, flagged elsewhere
        }

        throw new ArgumentException($"Unsupported equity timeframe '{timeframe}'.", nameof(timeframe));
    }

    /// <summary>Bar is finalized once its modeled session close + publish grace has passed.</summary>
    public bool IsFinalized(string timeframe, long openUtcMs, long nowUtcMs) =>
        openUtcMs <= nowUtcMs
        && SessionCloseUtcMs(timeframe, openUtcMs) + (long)_publishGrace.TotalMilliseconds <= nowUtcMs;

    /// <summary>
    /// Latest expected bar open that is already finalized as of <paramref name="nowUtcMs"/>.
    /// Walks back over recent days (covers weekends/holiday runs) — this is the freshness
    /// anchor: the newest bar a correct pipeline must already contain.
    /// </summary>
    public long? LastExpectedFinalizedOpenUtcMs(string timeframe, long nowUtcMs, int searchBackDays = 20)
    {
        var todayLocal = DateOnly.FromDateTime(ToLocal(nowUtcMs).DateTime);
        for (var d = todayLocal; d >= todayLocal.AddDays(-searchBackDays); d = d.AddDays(-1))
        {
            if (!IsTradingDay(d)) continue;
            IReadOnlyList<long> opens = timeframe == EquityCandleTimeframes.Day
                ? [DailyOpenUtcMs(d)]
                : HourlyOpensUtcMs(d);
            for (var i = opens.Count - 1; i >= 0; i--)
            {
                if (IsFinalized(timeframe, opens[i], nowUtcMs))
                    return opens[i];
            }
        }
        return null;
    }

    /// <summary>Bounded day-walk for T+N arithmetic, mirroring vn_execution_model.py's
    /// _SETTLE_LOOKAHEAD_DAYS so a pathological non-trading list cannot spin forever.</summary>
    private const int TradingDayWalkLookaheadDays = 45;

    /// <summary>
    /// Return the date <paramref name="tradingDays"/> TRADING days after
    /// <paramref name="date"/> (T+N settlement arithmetic; T+0 = same date).
    /// Weekends and configured non-trading dates are skipped. Throws if no trading
    /// day is found within the bounded lookahead — mirrors settlement_date() in
    /// ai/vn_execution_model.py.
    /// </summary>
    public DateOnly AddTradingDays(DateOnly date, int tradingDays)
    {
        if (tradingDays < 0)
            throw new ArgumentOutOfRangeException(nameof(tradingDays), "tradingDays cannot be negative.");
        var probe = date;
        var counted = 0;
        for (var i = 0; i < TradingDayWalkLookaheadDays; i++)
        {
            if (counted == tradingDays) return probe;
            probe = probe.AddDays(1);
            if (IsTradingDay(probe)) counted++;
        }
        throw new InvalidOperationException(
            $"No settlement date within {TradingDayWalkLookaheadDays} days of {date}.");
    }

    /// <summary>
    /// Classify an observed hourly bar open against the session model. Absence of a 12:00
    /// bar is never a gap (lunch is not a session slot); a present 12:00 bar returns
    /// <see cref="SessionOpenKind.LunchBreak"/> so callers surface it as an anomaly
    /// instead of normalizing it away.
    /// </summary>
    public SessionOpenKind ClassifyHourlyOpenUtcMs(long openUtcMs)
    {
        var local = ToLocal(openUtcMs);
        var date = DateOnly.FromDateTime(local.DateTime);
        if (!IsTradingDay(date)) return SessionOpenKind.NonTradingDay;
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (HourlyOpensLocal.Contains(time)) return SessionOpenKind.Expected;
        if (time == new TimeOnly(12, 0)) return SessionOpenKind.LunchBreak;
        return SessionOpenKind.OffSession;
    }

    /// <summary>Classify an observed daily bar open (trading date + 07:00 local expected).</summary>
    public SessionOpenKind ClassifyDailyOpenUtcMs(long openUtcMs)
    {
        var local = ToLocal(openUtcMs);
        var date = DateOnly.FromDateTime(local.DateTime);
        if (!IsTradingDay(date)) return SessionOpenKind.NonTradingDay;
        return TimeOnly.FromDateTime(local.DateTime) == DailyOpenLocal
            ? SessionOpenKind.Expected
            : SessionOpenKind.OffSession;
    }

    /// <summary>
    /// Group a sorted set of missing expected opens into contiguous ranges. Contiguity is
    /// defined on the expected-open sequence: consecutive expected opens that are all
    /// missing form one range even across trading-day boundaries.
    /// </summary>
    public static IReadOnlyList<CandleGapRange> GroupMissingRanges(
        IReadOnlyList<long> expectedOpensUtcMs,
        IReadOnlySet<long> missingSet)
    {
        var ranges = new List<CandleGapRange>();
        long rangeStart = 0, rangeEnd = 0, count = 0;
        foreach (var open in expectedOpensUtcMs)
        {
            if (!missingSet.Contains(open))
            {
                if (count > 0) { ranges.Add(new CandleGapRange(rangeStart, rangeEnd, count)); count = 0; }
                continue;
            }
            if (count == 0) rangeStart = open;
            rangeEnd = open;
            count++;
        }
        if (count > 0) ranges.Add(new CandleGapRange(rangeStart, rangeEnd, count));
        return ranges;
    }

    /// <summary>Parse configured "yyyy-MM-dd" non-trading dates; invalid entries throw.</summary>
    public static IReadOnlySet<DateOnly> ParseNonTradingDates(IEnumerable<string>? configured)
    {
        var set = new HashSet<DateOnly>();
        foreach (var raw in configured ?? Enumerable.Empty<string>())
        {
            if (!DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", out var date))
                throw new ArgumentException($"Invalid non-trading date '{raw}'. Expected yyyy-MM-dd.");
            set.Add(date);
        }
        return set;
    }
}

/// <summary>Coverage relation of a date range (EQX v2 review #1).</summary>
public sealed record CalendarCoverageStatus(DateOnly? CoverageFrom, DateOnly? CoverageTo, bool BelowCoverage, bool BeyondCoverage)
{
    public bool FullyCovered => !BelowCoverage && !BeyondCoverage;
}

/// <summary>A computation needed a calendar date after the canonical coverage (fail closed).</summary>
public sealed class CalendarCoverageExpiredException : InvalidOperationException
{
    public CalendarCoverageExpiredException(string message) : base(message) { }
    public string Code => "CALENDAR_COVERAGE_EXPIRED";
}

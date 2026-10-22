using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

public sealed record MarketCalendarDayDto(
    string Date,
    string Kind,
    string Reason,
    string? SourceUrl,
    string? SourceTitle,
    string Verification);

public sealed record MarketCalendarSyncDto(
    string Status,
    int Inserted,
    int Updated,
    int Deleted,
    int Unchanged,
    DateTime AtUtc,
    string? Error);

public sealed record MarketCalendarResponse(
    string ContractVersion,
    string Venue,
    string CalendarVersion,
    string CoverageFromDate,
    string CoverageToDate,
    int VerifiedDays,
    int UnverifiedLegacyDays,
    IReadOnlyList<MarketCalendarDayDto> Days,
    IReadOnlyList<string> ConfigOverrideDates,
    MarketCalendarSyncDto? DatabaseSync,
    IReadOnlyList<string> Limitations,
    DateTime GeneratedAtUtc);

/// <summary>
/// Public HOSE non-trading-day calendar (EQX v2 A1): the canonical checked-in file with the
/// official source per day, plus any config override dates (reported, never hidden).
/// Weekends are implicit and not listed.
/// </summary>
[ApiController]
[Route("api/market-calendar")]
public sealed class MarketCalendarController : ControllerBase
{
    public const string ContractVersion = "vn-hose-calendar-v1";

    private readonly VietnamMarketCalendar _calendar;
    private readonly MarketCalendarSyncStatusStore _sync;
    private readonly TimeProvider _timeProvider;

    public MarketCalendarController(
        VietnamMarketCalendar calendar, MarketCalendarSyncStatusStore sync, TimeProvider? timeProvider = null)
    {
        _calendar = calendar;
        _sync = sync;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [HttpGet("vn-hose")]
    [ProducesResponseType(typeof(MarketCalendarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<MarketCalendarResponse> GetVnHose()
    {
        var source = _calendar.Source;
        if (source is null)
            return EquityLaneHttp.Error(this, StatusCodes.Status503ServiceUnavailable, "CALENDAR_UNAVAILABLE",
                "The HOSE calendar is not loaded from its canonical file.");

        var last = _sync.Last;
        return Ok(new MarketCalendarResponse(
            ContractVersion,
            MarketCalendarSource.Venue,
            source.CalendarVersion,
            source.CoverageFrom.ToString("yyyy-MM-dd"),
            source.CoverageTo.ToString("yyyy-MM-dd"),
            source.VerifiedCount,
            source.UnverifiedLegacyCount,
            source.Days.Select(d => new MarketCalendarDayDto(
                d.Date.ToString("yyyy-MM-dd"), d.Kind, d.Reason, d.SourceUrl, d.SourceTitle, d.Verification)).ToArray(),
            source.ConfigOverrideDates.Select(d => d.ToString("yyyy-MM-dd")).ToArray(),
            last is null ? null : new MarketCalendarSyncDto(
                last.Status, last.Inserted, last.Updated, last.Deleted, last.Unchanged, last.AtUtc, last.Error),
            [
                "Weekends are non-trading by construction and are not listed; Saturday make-up working days are never trading days on HOSE.",
                "verified = an official HOSE notice was read (sourceUrl); unverified-legacy = carried over without an official document, still treated as closed.",
                "configOverrideDates come from MbbIngestion:NonTradingDates and are not in the canonical file.",
                $"Coverage {source.CoverageFrom:yyyy-MM-dd}..{source.CoverageTo:yyyy-MM-dd}: holidays outside it are unknown. After coverageToDate the backend fails closed (calendar-coverage-expired: decision waits, observations/forecasts/forward notes are refused); before coverageFromDate data quality flags below-calendar-coverage.",
            ],
            _timeProvider.GetUtcNow().UtcDateTime));
    }
}

using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>Outcome of one file → MarketCalendarDays sync.</summary>
public sealed record MarketCalendarSyncResult(
    string Status,
    int Inserted,
    int Updated,
    int Deleted,
    int Unchanged,
    string CalendarVersion,
    DateTime AtUtc,
    string? Error = null);

/// <summary>
/// Idempotent mirror of the canonical HOSE calendar (file ∪ config overrides) into
/// <c>MarketCalendarDays</c> (EQX v2 A1), so batch Python jobs read exactly the set the backend uses.
/// The file is canonical: DB rows for dates no longer in the source are deleted, changed rows are
/// updated, identical rows are left untouched (re-running writes nothing).
/// </summary>
public static class MarketCalendarSync
{
    public const string ConfigOverrideReason = "config-override (MbbIngestion:NonTradingDates) — not in the canonical calendar file";

    public static async Task<MarketCalendarSyncResult> SyncAsync(
        AppDbContext db, MarketCalendarSource source, TimeProvider? timeProvider = null, CancellationToken ct = default)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var desired = source.Days
            .Select(d => new MarketCalendarDay
            {
                Venue = MarketCalendarSource.Venue,
                Date = d.Date,
                Kind = d.Kind,
                Reason = d.Reason,
                SourceUrl = d.SourceUrl,
                SourceTitle = d.SourceTitle,
                Verification = d.Verification,
                CalendarVersion = source.CalendarVersion,
                CoverageFromDate = source.CoverageFrom,
                CoverageToDate = source.CoverageTo,
            })
            .Concat(source.ConfigOverrideDates.Select(d => new MarketCalendarDay
            {
                Venue = MarketCalendarSource.Venue,
                Date = d,
                Kind = MarketCalendarKinds.MarketClosure,
                Reason = ConfigOverrideReason,
                Verification = MarketCalendarVerification.UnverifiedLegacy,
                CalendarVersion = source.CalendarVersion,
                // Overrides may sit outside the file's coverage; widen so the row's own CHECK holds.
                CoverageFromDate = d < source.CoverageFrom ? d : source.CoverageFrom,
                CoverageToDate = d > source.CoverageTo ? d : source.CoverageTo,
            }))
            .ToDictionary(d => d.Date);

        var existing = await db.MarketCalendarDays
            .Where(d => d.Venue == MarketCalendarSource.Venue)
            .ToListAsync(ct);

        int inserted = 0, updated = 0, deleted = 0, unchanged = 0;
        foreach (var row in existing)
        {
            if (!desired.TryGetValue(row.Date, out var want))
            {
                db.MarketCalendarDays.Remove(row);
                deleted++;
                continue;
            }
            desired.Remove(row.Date);
            if (row.Kind == want.Kind && row.Reason == want.Reason && row.SourceUrl == want.SourceUrl
                && row.SourceTitle == want.SourceTitle && row.Verification == want.Verification
                && row.CalendarVersion == want.CalendarVersion
                && row.CoverageFromDate == want.CoverageFromDate && row.CoverageToDate == want.CoverageToDate)
            {
                unchanged++;
                continue;
            }
            row.Kind = want.Kind;
            row.Reason = want.Reason;
            row.SourceUrl = want.SourceUrl;
            row.SourceTitle = want.SourceTitle;
            row.Verification = want.Verification;
            row.CalendarVersion = want.CalendarVersion;
            row.CoverageFromDate = want.CoverageFromDate;
            row.CoverageToDate = want.CoverageToDate;
            row.UpdatedAtUtc = now;
            updated++;
        }
        foreach (var add in desired.Values.OrderBy(d => d.Date))
        {
            add.UpdatedAtUtc = now;
            db.MarketCalendarDays.Add(add);
            inserted++;
        }
        if (inserted + updated + deleted > 0)
            await db.SaveChangesAsync(ct);

        return new MarketCalendarSyncResult("synced", inserted, updated, deleted, unchanged, source.CalendarVersion, now);
    }
}

/// <summary>Holds the last startup sync outcome for the public calendar endpoint.</summary>
public sealed class MarketCalendarSyncStatusStore
{
    private MarketCalendarSyncResult? _last;
    public MarketCalendarSyncResult? Last => Volatile.Read(ref _last);
    public void Set(MarketCalendarSyncResult result) => Volatile.Write(ref _last, result);
}

/// <summary>
/// Startup sync (MarketCalendar:SyncOnStartup, default true). Registered independently of
/// BackgroundWorkers:Enabled because it is reference data, not a scheduler. Never throws out of
/// StartAsync: a failure (e.g. table not migrated yet, pitfall #13) is logged and reported, the API keeps serving.
/// </summary>
public sealed class MarketCalendarSyncHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly VietnamMarketCalendar _calendar;
    private readonly MarketCalendarSyncStatusStore _status;
    private readonly ILogger<MarketCalendarSyncHostedService> _logger;

    public MarketCalendarSyncHostedService(
        IServiceScopeFactory scopes,
        VietnamMarketCalendar calendar,
        MarketCalendarSyncStatusStore status,
        ILogger<MarketCalendarSyncHostedService> logger)
    {
        _scopes = scopes;
        _calendar = calendar;
        _status = status;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_calendar.Source is null)
        {
            _status.Set(new MarketCalendarSyncResult("skipped", 0, 0, 0, 0, "", DateTime.UtcNow, "calendar has no canonical source"));
            return;
        }
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var result = await MarketCalendarSync.SyncAsync(db, _calendar.Source, ct: cancellationToken);
            _status.Set(result);
            _logger.LogInformation(
                "Market calendar {Version} synced: +{Inserted} ~{Updated} -{Deleted} ={Unchanged}",
                result.CalendarVersion, result.Inserted, result.Updated, result.Deleted, result.Unchanged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status.Set(new MarketCalendarSyncResult("failed", 0, 0, 0, 0, _calendar.Source.CalendarVersion,
                DateTime.UtcNow, ex.GetType().Name));
            _logger.LogError(ex, "Market calendar startup sync failed; the API keeps serving the file-based calendar");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

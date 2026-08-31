using Backend.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AlertsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AlertsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>List alerts for the default (or specified) user, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<object>> GetAlerts(
        [FromQuery] string userId = "default",
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int take = 50,
        [FromQuery] bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 200) take = 50;

        var q = _db.AppAlerts.AsNoTracking()
            .Where(a => a.UserId == userId);
        if (!includeArchived)
            q = q.Where(a => a.ArchivedAtUtc == null);

        if (unreadOnly)
            q = q.Where(a => !a.IsRead);

        var items = await q
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(a => new
            {
                a.Id,
                a.UserId,
                a.Type,
                a.Title,
                a.Message,
                a.PriceSnapshot,
                a.CreatedAt,
                a.IsRead,
                a.SourceKey,
                a.ArchivedAtUtc
            })
            .ToListAsync(cancellationToken);

        var unread = await _db.AppAlerts.AsNoTracking()
            .CountAsync(a => a.UserId == userId && !a.IsRead && a.ArchivedAtUtc == null, cancellationToken);

        return Ok(new { userId, unreadCount = unread, items });
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<object>> GetUnreadCount(
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        var count = await _db.AppAlerts.AsNoTracking()
            .CountAsync(a => a.UserId == userId && !a.IsRead && a.ArchivedAtUtc == null, cancellationToken);

        return Ok(new { userId, unreadCount = count });
    }

    [HttpPost("{id:guid}/read")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken = default)
    {
        var alert = await _db.AppAlerts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (alert == null)
            return NotFound();

        alert.IsRead = true;
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("read-all")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> MarkAllRead(
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        await _db.AppAlerts
            .Where(a => a.UserId == userId && !a.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRead, true), cancellationToken);

        return NoContent();
    }

    /// <summary>Archive one alert if it belongs to the given user.</summary>
    [HttpDelete("{id:guid}")]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> DeleteAlert(
        Guid id,
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        var alert = await _db.AppAlerts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (alert == null || alert.UserId != userId)
            return NotFound();

        alert.ArchivedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Archive all alerts for the user.</summary>
    [HttpDelete]
    [Backend.Filters.AdminGuard]
    public async Task<IActionResult> DeleteAllAlerts(
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        await _db.AppAlerts
            .Where(a => a.UserId == userId && a.ArchivedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ArchivedAtUtc, DateTime.UtcNow), cancellationToken);

        return NoContent();
    }

    [HttpPost("deduplicate")]
    [Backend.Filters.AdminGuard]
    public async Task<ActionResult<object>> Deduplicate(
        [FromQuery] bool apply = false,
        CancellationToken cancellationToken = default)
    {
        var alerts = await _db.AppAlerts
            .Where(a => a.ArchivedAtUtc == null)
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        var groups = alerts
            .Where(a => a.SourceKey is not null)
            .GroupBy(a => $"source:{a.UserId}:{a.SourceKey}")
            .Where(g => g.Count() > 1)
            .Select(g => new
            {
                key = g.Key,
                keepId = g.First().Id,
                duplicateIds = g.Skip(1).Select(a => a.Id).ToArray()
            })
            .ToList();

        var duplicateIds = groups.SelectMany(g => g.duplicateIds).ToHashSet();
        if (apply && duplicateIds.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var alert in alerts.Where(a => duplicateIds.Contains(a.Id)))
                alert.ArchivedAtUtc = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new
        {
            dryRun = !apply,
            duplicateGroupCount = groups.Count,
            duplicateAlertCount = duplicateIds.Count,
            groups
        });
    }
}

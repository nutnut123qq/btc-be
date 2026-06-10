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
        CancellationToken cancellationToken = default)
    {
        if (take is < 1 or > 200) take = 50;

        var q = _db.AppAlerts.AsNoTracking()
            .Where(a => a.UserId == userId);

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
                a.IsRead
            })
            .ToListAsync(cancellationToken);

        var unread = await _db.AppAlerts.AsNoTracking()
            .CountAsync(a => a.UserId == userId && !a.IsRead, cancellationToken);

        return Ok(new { userId, unreadCount = unread, items });
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<object>> GetUnreadCount(
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        var count = await _db.AppAlerts.AsNoTracking()
            .CountAsync(a => a.UserId == userId && !a.IsRead, cancellationToken);

        return Ok(new { userId, unreadCount = count });
    }

    [HttpPost("{id:guid}/read")]
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
    public async Task<IActionResult> MarkAllRead(
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        await _db.AppAlerts
            .Where(a => a.UserId == userId && !a.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRead, true), cancellationToken);

        return NoContent();
    }

    /// <summary>Delete one alert if it belongs to the given user.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAlert(
        Guid id,
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        var alert = await _db.AppAlerts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (alert == null || alert.UserId != userId)
            return NotFound();

        _db.AppAlerts.Remove(alert);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Delete all alerts for the user.</summary>
    [HttpDelete]
    public async Task<IActionResult> DeleteAllAlerts(
        [FromQuery] string userId = "default",
        CancellationToken cancellationToken = default)
    {
        await _db.AppAlerts
            .Where(a => a.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        return NoContent();
    }
}

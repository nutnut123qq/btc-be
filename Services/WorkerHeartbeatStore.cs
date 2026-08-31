using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

internal static class WorkerHeartbeatStore
{
    public static async Task MarkStartedAsync(AppDbContext db, string workerName, DateTime startedAtUtc, CancellationToken ct)
    {
        var row = await db.WorkerHeartbeats.FindAsync([workerName], ct);
        if (row is null)
        {
            row = new WorkerHeartbeat { WorkerName = workerName };
            db.WorkerHeartbeats.Add(row);
        }
        row.Status = "Running";
        row.LastStartedAtUtc = startedAtUtc;
        row.UpdatedAtUtc = startedAtUtc;
        await db.SaveChangesAsync(ct);
    }

    public static async Task MarkSucceededAsync(AppDbContext db, string workerName, DateTime startedAtUtc, DateTime completedAt, CancellationToken ct)
    {
        var row = await db.WorkerHeartbeats.SingleAsync(x => x.WorkerName == workerName, ct);
        row.Status = "Succeeded";
        row.LastSucceededAtUtc = completedAt;
        row.LastDurationMs = Math.Max(0, (long)(completedAt - startedAtUtc).TotalMilliseconds);
        row.LastError = null;
        row.UpdatedAtUtc = completedAt;
        await db.SaveChangesAsync(ct);
    }

    public static async Task MarkFailedAsync(AppDbContext db, string workerName, DateTime startedAtUtc, DateTime completedAt, Exception exception, CancellationToken ct)
    {
        var row = await db.WorkerHeartbeats.SingleOrDefaultAsync(x => x.WorkerName == workerName, ct)
            ?? new WorkerHeartbeat { WorkerName = workerName, LastStartedAtUtc = startedAtUtc };
        if (db.Entry(row).State == EntityState.Detached)
            db.WorkerHeartbeats.Add(row);
        row.Status = "Failed";
        row.LastFailedAtUtc = completedAt;
        row.LastDurationMs = Math.Max(0, (long)(completedAt - startedAtUtc).TotalMilliseconds);
        row.LastError = $"{exception.GetType().Name}: worker cycle failed; see backend logs.";
        row.UpdatedAtUtc = completedAt;
        await db.SaveChangesAsync(ct);
    }
}

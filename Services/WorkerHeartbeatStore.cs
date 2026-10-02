using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>Outcome of a single worker cycle; persisted as WorkerHeartbeat.Status.</summary>
internal enum WorkerCycleOutcome
{
    /// <summary>Cycle ran and everything it attempted succeeded.</summary>
    Succeeded,
    /// <summary>Cycle ran; some items succeeded but others failed.</summary>
    Partial,
    /// <summary>Cycle ran but everything it attempted failed (provider down / all items failed).</summary>
    Failed,
    /// <summary>Cycle ran and there was nothing to do.</summary>
    Idle,
    /// <summary>Worker cannot do its job because required configuration is missing (e.g. no API key).</summary>
    Disabled
}

/// <summary>Per-cycle result a worker reports to the heartbeat store.</summary>
internal sealed record WorkerCycleReport(
    WorkerCycleOutcome Outcome,
    int Attempted = 0,
    int Succeeded = 0,
    int Failed = 0,
    int Skipped = 0,
    int Remaining = 0,
    string? Detail = null)
{
    public static WorkerCycleReport Disabled(string detail, int remaining = 0) =>
        new(WorkerCycleOutcome.Disabled, Skipped: remaining, Remaining: remaining, Detail: detail);

    public static WorkerCycleReport Idle(int skipped = 0, int remaining = 0, string? detail = null) =>
        new(WorkerCycleOutcome.Idle, Skipped: skipped, Remaining: remaining, Detail: detail);
}

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

    /// <summary>
    /// Persists a completed cycle with its outcome (Succeeded/Partial/Failed/Idle/Disabled) and
    /// per-cycle counters. Non-Failed outcomes refresh LastSucceededAtUtc so a disabled or idle
    /// worker still proves liveness; Failed refreshes LastFailedAtUtc and LastError instead.
    /// </summary>
    public static async Task MarkCompletedAsync(AppDbContext db, string workerName, DateTime startedAtUtc,
        DateTime completedAt, WorkerCycleReport report, CancellationToken ct)
    {
        var row = await db.WorkerHeartbeats.SingleOrDefaultAsync(x => x.WorkerName == workerName, ct)
            ?? new WorkerHeartbeat { WorkerName = workerName, LastStartedAtUtc = startedAtUtc };
        if (db.Entry(row).State == EntityState.Detached)
            db.WorkerHeartbeats.Add(row);

        row.Status = report.Outcome.ToString();
        row.LastDurationMs = Math.Max(0, (long)(completedAt - startedAtUtc).TotalMilliseconds);
        row.LastCycleAttempted = report.Attempted;
        row.LastCycleSucceeded = report.Succeeded;
        row.LastCycleFailed = report.Failed;
        row.LastCycleSkipped = report.Skipped;
        row.LastCycleRemaining = report.Remaining;
        var detail = report.Detail;
        row.LastCycleDetail = string.IsNullOrWhiteSpace(detail) ? null : detail[..Math.Min(detail.Length, 1000)];

        if (report.Outcome == WorkerCycleOutcome.Failed)
        {
            row.LastFailedAtUtc = completedAt;
            row.LastError = row.LastCycleDetail ?? "Cycle failed.";
        }
        else
        {
            row.LastSucceededAtUtc = completedAt;
            row.LastError = null;
        }
        row.UpdatedAtUtc = completedAt;
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
        var root = exception.GetBaseException();
        var message = ReferenceEquals(root, exception)
            ? exception.Message
            : $"{exception.Message} | {root.GetType().Name}: {root.Message}";
        var detail = string.Join(" ", message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var error = string.IsNullOrWhiteSpace(detail)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {detail}";
        row.LastError = error[..Math.Min(error.Length, 1000)];
        // A crashed cycle produced no reliable counters; clear them rather than show stale numbers.
        row.LastCycleAttempted = null;
        row.LastCycleSucceeded = null;
        row.LastCycleFailed = null;
        row.LastCycleSkipped = null;
        row.LastCycleRemaining = null;
        row.LastCycleDetail = null;
        row.UpdatedAtUtc = completedAt;
        await db.SaveChangesAsync(ct);
    }
}

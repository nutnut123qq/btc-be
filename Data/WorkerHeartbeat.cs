namespace Backend.Data;

public class WorkerHeartbeat
{
    public string WorkerName { get; set; } = string.Empty;
    public string Status { get; set; } = "Running";
    public DateTime? LastStartedAtUtc { get; set; }
    public DateTime? LastSucceededAtUtc { get; set; }
    public DateTime? LastFailedAtUtc { get; set; }
    public long? LastDurationMs { get; set; }
    public string? LastError { get; set; }

    /// <summary>Items attempted in the last completed cycle (null when the worker does not report counters).</summary>
    public int? LastCycleAttempted { get; set; }
    public int? LastCycleSucceeded { get; set; }
    public int? LastCycleFailed { get; set; }
    /// <summary>Items skipped in the last cycle without being attempted (e.g. poison-pilled).</summary>
    public int? LastCycleSkipped { get; set; }
    /// <summary>Items still pending after the last cycle.</summary>
    public int? LastCycleRemaining { get; set; }
    /// <summary>Human/machine-readable detail for the last cycle (failure breakdown, disable reason, ...).</summary>
    public string? LastCycleDetail { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

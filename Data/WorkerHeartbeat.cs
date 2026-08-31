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
    public DateTime UpdatedAtUtc { get; set; }
}

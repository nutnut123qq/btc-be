namespace Backend.Data;

public static class KlineGapStatuses
{
    public const string Pending = "Pending";
    public const string Unavailable = "Unavailable";
    public const string Filled = "Filled";
}

public class KlineGapState
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long StartOpenTimeMs { get; set; }
    public long EndOpenTimeMs { get; set; }
    public long MissingBars { get; set; }
    /// <summary>Actual empty Binance responses; transport failures do not prove unavailability.</summary>
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }
    public string Status { get; set; } = KlineGapStatuses.Pending;
    public string? Reason { get; set; }
    public DateTime FirstDetectedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

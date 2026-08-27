namespace Backend.Data;

public class ConfluenceSnapshot
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public long TimeMs { get; set; }
    public double ConfluenceScore { get; set; } // 0 - 100
    public string OverallDirection { get; set; } = "Neutral"; // StrongBullish, Bullish, Neutral, Bearish, StrongBearish
    public string TimeframeAlignmentsJson { get; set; } = "[]";
    public bool HasConflict { get; set; }
    public string? ConflictDetails { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

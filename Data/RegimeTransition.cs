namespace Backend.Data;

public class RegimeTransition
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public string FromRegime { get; set; } = "";
    public string ToRegime { get; set; } = "";
    public long TransitionTimeMs { get; set; }
    public int DurationBars { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

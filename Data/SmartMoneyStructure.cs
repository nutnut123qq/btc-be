namespace Backend.Data;

public class SmartMoneyStructure
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long TimeMs { get; set; }
    public string EventType { get; set; } = ""; // BOS_BULL, BOS_BEAR, CHOCH_BULL, CHOCH_BEAR, FVG_BULL, FVG_BEAR, SWING_HIGH, SWING_LOW
    public double Price { get; set; }
    public double? HighPrice { get; set; } // for FVG upper bound
    public double? LowPrice { get; set; }  // for FVG lower bound
    public bool IsMitigated { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

namespace Backend.Data;

public class SmartMoneyStructure
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    /// <summary>Visual/geometric origin of the event.</summary>
    public long TimeMs { get; set; }
    public long OriginTimeMs { get; set; }
    /// <summary>Earliest close time at which the event could have been known.</summary>
    public long AvailableTimeMs { get; set; }
    /// <summary>Origin of the structural level broken by BOS/CHoCH.</summary>
    public long? ReferenceTimeMs { get; set; }
    public string EventType { get; set; } = ""; // BOS_BULL, BOS_BEAR, CHOCH_BULL, CHOCH_BEAR, FVG_BULL, FVG_BEAR, SWING_HIGH, SWING_LOW
    public double Price { get; set; }
    public double? HighPrice { get; set; } // for FVG upper bound
    public double? LowPrice { get; set; }  // for FVG lower bound
    public bool IsMitigated { get; set; }
    public long? MitigatedAtMs { get; set; }
    public string CalculationVersion { get; set; } = "smc-causal-v2";
    public string Description { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

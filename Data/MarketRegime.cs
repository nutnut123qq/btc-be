namespace Backend.Data;

public class MarketRegime
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long OpenTimeMs { get; set; }
    public string RegimeType { get; set; } = "RangeBound"; // TrendingUp, TrendingDown, RangeBound, Breakout, Compression
    public double TrendStrength { get; set; } // 0-100 (from ADX)
    public double VolatilityScore { get; set; } // ATR ratio (ATR_14 / ATR_SMA50)
    public double Adx { get; set; }
    public double PlusDi { get; set; }
    public double MinusDi { get; set; }
    public double AtrRatio { get; set; }
    public double BollingerBandwidth { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

namespace Backend.Data;

public class WindowVector
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "15m";
    public string FeatureType { get; set; } = "all";
    public int WindowSize { get; set; } = 10;
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
    public float[] Vector { get; set; } = [];
    public int VectorDim { get; set; }
    public float VectorNorm { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

namespace Backend.Data;

/// <summary>Thống kê volume phân tích cho từng nến — pre-computed để query nhanh.</summary>
public class CandleVolumeStats
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long OpenTimeMs { get; set; }

    public decimal Volume { get; set; }
    public decimal VolumeSma20 { get; set; }
    public double VolumeAnomalyRatio { get; set; }
    public double VolumeVsPrevious { get; set; }
    public double VolumeVsMax10 { get; set; }
    public string VolumeTrend { get; set; } = "normal";
}

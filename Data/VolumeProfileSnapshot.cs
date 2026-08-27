namespace Backend.Data;

public class VolumeProfileSnapshot
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long WindowStartMs { get; set; }
    public long WindowEndMs { get; set; }
    public double PocPrice { get; set; }
    public double VahPrice { get; set; }
    public double ValPrice { get; set; }
    public string ProfileBinsJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

namespace Backend.Data;

public class ArchetypeOutcome
{
    public long Id { get; set; }
    public long ArchetypeId { get; set; }
    public CandleArchetype Archetype { get; set; } = null!;
    public string Horizon { get; set; } = "4h";
    public int TotalSamples { get; set; }
    public int UpCount { get; set; }
    public int DownCount { get; set; }
    public int SidewaysCount { get; set; }
    public double UpRate { get; set; }
    public double DownRate { get; set; }
    public double SidewaysRate { get; set; }
    public double AvgReturnPct { get; set; }
    public double MedianReturnPct { get; set; }
    public double MaxReturnPct { get; set; }
    public double MinReturnPct { get; set; }
    public double StdDevReturnPct { get; set; }
    public int RecentSamples { get; set; }
    public double RecentUpRate { get; set; }
    public double RecentDownRate { get; set; }
    public double RecentAvgReturnPct { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

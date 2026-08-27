namespace Backend.Data;

public class ArchetypeOccurrence
{
    public long Id { get; set; }
    public long ArchetypeId { get; set; }
    public CandleArchetype Archetype { get; set; } = null!;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int WindowSize { get; set; }
    public long WindowStartMs { get; set; }
    public long WindowEndMs { get; set; }
    public float DistanceToCentroid { get; set; }
    public int Label { get; set; }
    public double? TargetReturn { get; set; }
    public string Horizon { get; set; } = "4h";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

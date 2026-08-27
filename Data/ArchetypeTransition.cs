namespace Backend.Data;

public class ArchetypeTransition
{
    public long Id { get; set; }
    public long FromArchetypeId { get; set; }
    public CandleArchetype FromArchetype { get; set; } = null!;
    public long ToArchetypeId { get; set; }
    public CandleArchetype ToArchetype { get; set; } = null!;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int WindowSize { get; set; }
    public int TransitionCount { get; set; }
    public double TransitionProbability { get; set; }
    public double AvgReturnPct { get; set; }
    public double AvgBarsToTransition { get; set; }
    public long LastSeenMs { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

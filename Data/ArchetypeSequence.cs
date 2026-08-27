namespace Backend.Data;

public class ArchetypeSequence
{
    public long Id { get; set; }
    public long FirstArchetypeId { get; set; }
    public CandleArchetype FirstArchetype { get; set; } = null!;
    public long SecondArchetypeId { get; set; }
    public CandleArchetype SecondArchetype { get; set; } = null!;
    public long ThirdArchetypeId { get; set; }
    public CandleArchetype ThirdArchetype { get; set; } = null!;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int WindowSize { get; set; }
    public int OccurrenceCount { get; set; }
    public double OutcomeUpRate { get; set; }
    public double OutcomeDownRate { get; set; }
    public double OutcomeSidewaysRate { get; set; }
    public double AvgReturnPct { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

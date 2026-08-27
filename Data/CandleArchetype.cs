namespace Backend.Data;

public class CandleArchetype
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int WindowSize { get; set; }
    public int ClusterId { get; set; }
    public string ArchetypeCode { get; set; } = "";
    public float[] CentroidVector { get; set; } = [];
    public int CentroidDim { get; set; }
    public float CentroidNorm { get; set; }
    public int MemberCount { get; set; }
    public float IntraClusterDistance { get; set; }
    public string? RepresentativeOhlcJson { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

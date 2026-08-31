namespace Backend.Services.Models;

public sealed class ConfluenceTimeframeAlignmentDto
{
    public string Timeframe { get; set; } = "";
    public double Weight { get; set; }
    public string Direction { get; set; } = "Neutral";
    public double DirectionalScore { get; set; }
    public string RegimeType { get; set; } = "Unknown";
    public string? ArchetypeCode { get; set; }
}

public sealed class ConfluenceSnapshotDto
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public long TimeMs { get; set; }
    public double ConfluenceScore { get; set; }
    public string OverallDirection { get; set; } = "Neutral";
    public List<ConfluenceTimeframeAlignmentDto> TimeframeAlignments { get; set; } = [];
    public bool HasConflict { get; set; }
    public string? ConflictDetails { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

namespace Backend.Services.Models;

public class ArchetypeDto
{
    public long Id { get; set; }
    public string ArchetypeCode { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public int WindowSize { get; set; }
    public int MemberCount { get; set; }
    public float IntraClusterDistance { get; set; }
    public object? RepresentativeOhlc { get; set; }  // deserialized JSON
    public ArchetypeOutcomeDto? BestOutcome { get; set; }
}

public class ArchetypeOutcomeDto
{
    public string Horizon { get; set; } = "";
    public int TotalSamples { get; set; }
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
}

public class ArchetypeDetailDto
{
    public long Id { get; set; }
    public string ArchetypeCode { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public int WindowSize { get; set; }
    public int MemberCount { get; set; }
    public float IntraClusterDistance { get; set; }
    public object? RepresentativeOhlc { get; set; }
    public List<ArchetypeOutcomeDto> Outcomes { get; set; } = [];
}

public class ArchetypeMatchDto
{
    public int WindowSize { get; set; }
    public ArchetypeDto? Archetype { get; set; }
    public float Similarity { get; set; }
    public string ConfidenceLevel { get; set; } = "";  // High/Medium/Low
    public List<ArchetypeOutcomeDto> Outcomes { get; set; } = [];
}

public class ArchetypeOccurrenceDto
{
    public long WindowStartMs { get; set; }
    public long WindowEndMs { get; set; }
    public float DistanceToCentroid { get; set; }
    public int Label { get; set; }
    public double? TargetReturn { get; set; }
}

public class ArchetypeRankingDto
{
    public int Rank { get; set; }
    public long ArchetypeId { get; set; }
    public string ArchetypeCode { get; set; } = "";
    public int WindowSize { get; set; }
    public string Timeframe { get; set; } = "";
    public int MemberCount { get; set; }
    public double WinRate { get; set; }  // max(upRate, downRate) - the dominant direction rate
    public string DominantDirection { get; set; } = ""; // "Up" or "Down"
    public int TotalSamples { get; set; }
    public double RecentAccuracy { get; set; }
    public double AvgReturnPct { get; set; }
    public string Trend { get; set; } = ""; // "improving", "declining", "stable"
}

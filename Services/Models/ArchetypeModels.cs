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

public sealed class ArchetypeTransitionDto
{
    public long Id { get; set; }
    public long FromArchetypeId { get; set; }
    public string FromArchetypeCode { get; set; } = "";
    public long ToArchetypeId { get; set; }
    public string ToArchetypeCode { get; set; } = "";
    public int TransitionCount { get; set; }
    public double TransitionProbability { get; set; }
    public double AvgReturnPct { get; set; }
    public double AvgBarsToTransition { get; set; }
    public long LastSeenMs { get; set; }
}

public sealed class ArchetypeTransitionsResponse
{
    public long ArchetypeId { get; set; }
    public List<ArchetypeTransitionDto> Transitions { get; set; } = [];
}

public sealed class TransitionMatrixCellDto
{
    public long FromId { get; set; }
    public string FromCode { get; set; } = "";
    public long ToId { get; set; }
    public string ToCode { get; set; } = "";
    public double Probability { get; set; }
    public int Count { get; set; }
}

public sealed class TransitionMatrixDto
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int WindowSize { get; set; }
    public int ArchetypeCount { get; set; }
    public int TotalTransitions { get; set; }
    public List<TransitionMatrixCellDto> Cells { get; set; } = [];
}

public sealed class TransitionPredictionDto
{
    public long? CurrentArchetypeId { get; set; }
    public string? CurrentArchetypeCode { get; set; }
    public double Similarity { get; set; }
    public List<ArchetypeTransitionDto> TopTransitions { get; set; } = [];
    public double EntropyBits { get; set; }
    public string Predictability { get; set; } = "Unavailable";
    public bool Validated { get; set; }
    public string? Reason { get; set; }
}

public sealed class SequencePredictionDto
{
    public string? PreviousArchetypeCode { get; set; }
    public string? CurrentArchetypeCode { get; set; }
    public List<SequencePredictionItemDto> TopSequences { get; set; } = [];
    public bool Validated { get; set; }
    public string? Reason { get; set; }
}

public sealed class SequencePredictionItemDto
{
    public long ThirdArchetypeId { get; set; }
    public string ThirdArchetypeCode { get; set; } = "";
    public int OccurrenceCount { get; set; }
    public double OutcomeUpRate { get; set; }
    public double OutcomeDownRate { get; set; }
    public double OutcomeSidewaysRate { get; set; }
    public double AvgReturnPct { get; set; }
}

public sealed class EntropyRankingResponse
{
    public List<EntropyRankingItemDto> Items { get; set; } = [];
    public bool Validated { get; set; }
    public string Maturity { get; set; } = "Experimental";
    public string Reason { get; set; } = "Transition entropy has not passed out-of-sample promotion gates.";
}

public sealed class EntropyRankingItemDto
{
    public int Rank { get; set; }
    public long ArchetypeId { get; set; }
    public string ArchetypeCode { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public int WindowSize { get; set; }
    public int MemberCount { get; set; }
    public double EntropyBits { get; set; }
    public string Predictability { get; set; } = "Unavailable";
    public string TopTransitionCode { get; set; } = "";
    public double TopTransitionProb { get; set; }
    public bool Validated { get; set; }
    public string Maturity { get; set; } = "Experimental";
    public string Reason { get; set; } = "Transition entropy has not passed out-of-sample promotion gates.";
}

namespace Backend.Services.Models;

public sealed class HistoricalAnalogRequest
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public int WindowSize { get; set; } = 15;
    public int NeighborCount { get; set; } = 50;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 8;
    public int LookbackBars { get; set; } = 20_000;
    public double RoundTripCostPct { get; set; } = 0.30;
    public double AtrMultiplier { get; set; } = 0.25;
    public double MinimumMeanSimilarity { get; set; } = 0.72;
    public long? AsOfTimeMs { get; set; }
}

public sealed class HistoricalAnalogResponse
{
    public string RequestId { get; set; } = "";
    public string ContractVersion { get; set; } = Data.ResearchVersions.HistoricalAnalogApiContract;
    public string Method { get; set; } = "historical-analog-returns-shape-v2";
    public string MethodVersion { get; set; } = "returns_shape_v2_signed";
    public string RankingMethod { get; set; } = "cosine-similarity-desc-point-in-time";
    public string EvaluationMethod { get; set; } = "fixed-horizon-close-to-close-economic-threshold";
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public long IntervalMs { get; set; }
    public int WindowSize { get; set; }
    public int LookbackBars { get; set; }
    public int NeighborCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public int ExclusionBars { get; set; }
    public double RoundTripCostPct { get; set; }
    public double AtrMultiplier { get; set; }
    public double MinimumMeanSimilarity { get; set; }
    public double? MeanSelectedSimilarity { get; set; }
    public long DecisionTimeMs { get; set; }
    public string SignalBarState { get; set; } = "closed";
    public int RawCandidateCount { get; set; }
    public int IndependentCandidateCount { get; set; }
    public int EffectiveSampleCount { get; set; }
    public string CapabilityState { get; set; } = "experimental";
    public HistoricalAnalogFreshnessDto? Freshness { get; set; }
    public string? BaselineName { get; set; }
    public double? BaselineScore { get; set; }
    public double? Lift { get; set; }
    public string? LiftUnit { get; set; }
    public HistoricalAnalogIntervalDto? LiftConfidenceInterval { get; set; }
    public double? Coverage { get; set; }
    public double? AbstentionRate { get; set; }
    public bool Abstained { get; set; }
    public bool QualityGatePassed => !Abstained;
    public string? AbstentionReason { get; set; }
    public HistoricalAnalogEvidenceDto Evidence { get; set; } = new();
    public HistoricalAnalogValidationDto Validation { get; set; } = new();
    public HistoricalAnalogQueryDto? Query { get; set; }
    public List<HistoricalAnalogSummaryDto> Summaries { get; set; } = [];
    public List<HistoricalAnalogItemDto> Items { get; set; } = [];
}

public sealed class HistoricalAnalogValidationDto
{
    public string Status { get; set; } = "exploratory";
    public bool IsOutOfSampleValidated { get; set; }
    public string Reason { get; set; } = "Analog hiện tại chỉ là bằng chứng nghiên cứu mô tả; chưa vượt các ngưỡng kiểm định walk-forward ngoài mẫu. Bối cảnh chỉ dùng để đối chiếu vì trọng số bối cảnh chưa được kiểm định.";
}

public sealed class HistoricalAnalogFreshnessDto
{
    public string Status { get; set; } = "unknown";
    public long? AsOfTimeMs { get; set; }
    public double? AgeSeconds { get; set; }
    public string? Reason { get; set; }
}

public sealed class HistoricalAnalogIntervalDto
{
    public double Lower { get; set; }
    public double Upper { get; set; }
    public double Level { get; set; } = 0.95;
}

public sealed class HistoricalAnalogEvidenceDto
{
    public string Status { get; set; } = "unavailable";
    public string? ArtifactManifestSha256 { get; set; }
    public int? EvaluatedQueries { get; set; }
    public string Reason { get; set; } = "API này chỉ trả kết quả mô tả tại thời điểm truy vấn; chưa nạp artifact walk-forward đã xác minh nên baseline, lift và khoảng tin cậy là null.";
}

public sealed class HistoricalAnalogQueryDto
{
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
    public long AvailableAtTimeMs { get; set; }
    public List<ArchetypeOccurrenceOhlcDto> Ohlc { get; set; } = [];
    public HistoricalAnalogContextDto Context { get; set; } = new();
}

public sealed class HistoricalAnalogContextDto
{
    public Dictionary<string, double> Values { get; set; } = new(StringComparer.Ordinal);
    public int AvailableFeatureCount { get; set; }
}

public sealed class HistoricalAnalogItemDto
{
    public int Rank { get; set; }
    public string WindowId { get; set; } = "";
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
    public long FutureEndTimeMs { get; set; }
    public double ShapeSimilarity { get; set; }
    public double? ContextSimilarity { get; set; }
    public int ContextComparableFeatureCount { get; set; }
    public double Atr14Pct { get; set; }
    public double ThresholdPct { get; set; }
    public List<ArchetypeOccurrenceOhlcDto> Ohlc { get; set; } = [];
    public List<ArchetypeOccurrenceOhlcDto> FutureOhlc { get; set; } = [];
    public List<HistoricalAnalogOutcomeDto> Outcomes { get; set; } = [];
}

public sealed class HistoricalAnalogOutcomeDto
{
    public int BarsAhead { get; set; }
    public long TargetOpenTimeMs { get; set; }
    public decimal TargetClose { get; set; }
    public double ReturnPct { get; set; }
    public double ThresholdPct { get; set; }
    public int Direction { get; set; }
}

public sealed class HistoricalAnalogSummaryDto
{
    public int BarsAhead { get; set; }
    public int TotalSamples { get; set; }
    public int UpCount { get; set; }
    public int DownCount { get; set; }
    public int NeutralCount { get; set; }
    public double UpRate { get; set; }
    public double DownRate { get; set; }
    public double NeutralRate { get; set; }
    public double AvgReturnPct { get; set; }
    public double MedianReturnPct { get; set; }
    public int? DominantDirection { get; set; }
    public string? BaselineName { get; set; }
    public double? BaselineScore { get; set; }
    public double? Lift { get; set; }
    public string? LiftUnit { get; set; }
    public HistoricalAnalogIntervalDto? LiftConfidenceInterval { get; set; }
}

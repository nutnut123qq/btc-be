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
    public double RoundTripCostPct { get; set; } = 0.15;
    public double AtrMultiplier { get; set; } = 0.25;
}

public sealed class HistoricalAnalogResponse
{
    public string RequestId { get; set; } = "";
    public string ContractVersion { get; set; } = Data.ResearchVersions.HistoricalAnalogApiContract;
    public string Method { get; set; } = "historical-analog-returns-shape-v1";
    public string RankingMethod { get; set; } = "shape-similarity-desc-context-audit-only";
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
    public int RawCandidateCount { get; set; }
    public int IndependentCandidateCount { get; set; }
    public int EffectiveSampleCount { get; set; }
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

public sealed class HistoricalAnalogQueryDto
{
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
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
}

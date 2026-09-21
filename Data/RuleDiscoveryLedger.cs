namespace Backend.Data;

/// <summary>Immutable metadata for one bounded discovery search.</summary>
public class RuleDiscoveryRun
{
    public long Id { get; set; }
    public string MethodVersion { get; set; } = "rule-discovery-oos-v2";
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public int FutureBars { get; set; }
    public int CandidateBudget { get; set; }
    public int TrialCount { get; set; }
    public double LabelDeadZonePct { get; set; }
    public double RoundTripCostBps { get; set; }
    public long SelectionStartTimeMs { get; set; }
    public long SelectionEndTimeMs { get; set; }
    public long EvaluationStartTimeMs { get; set; }
    public long EvaluationEndTimeMs { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Every tried candidate, including failed selection and held-out rejection.</summary>
public class RuleDiscoveryTrial
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public int TrialNumber { get; set; }
    public string CandidateKey { get; set; } = string.Empty;
    public string ConditionsJson { get; set; } = "[]";
    public string Status { get; set; } = "rejected";
    public string? RejectedReason { get; set; }
    public int SelectionSampleCount { get; set; }
    public double? SelectionWinRate { get; set; }
    public double? SelectionNetAvgReturnPct { get; set; }
    public int EvaluationSampleCount { get; set; }
    public double? EvaluationWinRate { get; set; }
    public double? EvaluationWinRateCi95Low { get; set; }
    public double? EvaluationWinRateCi95High { get; set; }
    public double? BaselineWinRate { get; set; }
    public double? OosLift { get; set; }
    public double? EvaluationNetAvgReturnPct { get; set; }
    public RuleDiscoveryRun? Run { get; set; }
}

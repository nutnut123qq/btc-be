namespace Backend.Data;

/// <summary>Định nghĩa Rule chuỗi nến — có thể cấu hình động qua JSON.</summary>
public class CandleSequenceRule
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int RequiredBars { get; set; } = 10;
    public bool IsEnabled { get; set; } = true;
    public int CooldownMinutes { get; set; } = 60;
    /// <summary>JSON array các điều kiện (SequenceRuleCondition[]).</summary>
    public string ConditionsJson { get; set; } = "[]";
    public string Action { get; set; } = "ALERT";
    public int Priority { get; set; } = 0;
    public bool IsAutoDiscovered { get; set; } = false;
    public double WinRate { get; set; } = 0;
    public double AvgReturn { get; set; } = 0;
    public int SampleCount { get; set; } = 0;
    /// <summary>descriptive, experimental, validated, forward-observed, or retired.</summary>
    public string CapabilityState { get; set; } = "descriptive";
    public string MethodVersion { get; set; } = "legacy-unversioned";
    public long? DiscoveryRunId { get; set; }
    public long? SelectionStartTimeMs { get; set; }
    public long? SelectionEndTimeMs { get; set; }
    public long? EvaluationStartTimeMs { get; set; }
    public long? EvaluationEndTimeMs { get; set; }
    public int SelectionSampleCount { get; set; }
    public int OosSampleCount { get; set; }
    public double? OosWinRate { get; set; }
    public double? OosWinRateCi95Low { get; set; }
    public double? OosWinRateCi95High { get; set; }
    public double? BaselineWinRate { get; set; }
    public double? OosLift { get; set; }
    public double? OosGrossAvgReturnPct { get; set; }
    public double? OosNetAvgReturnPct { get; set; }
    public double? LabelDeadZonePct { get; set; }
    public double? RoundTripCostBps { get; set; }
    public string? RejectedReason { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>Lịch sử tín hiệu khi CandleSequenceRule trigger.</summary>
public class CandleSequenceSignal
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public long TriggerTimeMs { get; set; }
    public decimal ClosePrice { get; set; }
    public string Message { get; set; } = string.Empty;
    public long AvailableTimeMs { get; set; }
    public string EvidenceKind { get; set; } = "observed-event";
    public string Provenance { get; set; } = "candle-sequence-rule";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

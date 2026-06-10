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
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

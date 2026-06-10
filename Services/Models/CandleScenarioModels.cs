namespace Backend.Services.Models;

public enum CandleScenarioType
{
    None,
    /// <summary>Chuỗi nến range thu hẹp rồi bùng nổ (Squeeze + Breakout).</summary>
    ContractionThenExpansion,
    /// <summary>Nến đóng cửa tiến dần theo 1 hướng (higher/lower closes).</summary>
    ProgressiveCloses,
    /// <summary>Chuỗi nến có bóng dài cùng phía ở vùng giá quan trọng (Rejection cluster).</summary>
    ShadowRejectionCluster,
    /// <summary>Giá tăng/giảm nhưng volume giảm dần (Divergence).</summary>
    VolumePriceDivergence,
    /// <summary>Nến range lớn nhưng đóng cửa gần đầu kia, xuất hiện liên tiếp (Failed drive).</summary>
    IntradayReversalSequence
}

public class CandleScenarioResult
{
    public CandleScenarioType Scenario { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Strength { get; set; } // 0.0 - 1.0
    public string Suggestion { get; set; } = string.Empty;
    public List<string> Details { get; set; } = new();
}

public class CandleSequenceAnalysis
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public int BarsAnalyzed { get; set; }
    public List<CandleScenarioResult> Scenarios { get; set; } = new();
    public string SummaryText { get; set; } = string.Empty;
}

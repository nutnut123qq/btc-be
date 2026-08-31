using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

public class BacktestRun
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public int WindowSize { get; set; }
    public string Horizon { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
    public double FeeBps { get; set; }
    public double SlippageBps { get; set; }
    public int TotalTrades { get; set; }
    public double WinRate { get; set; }
    public double TotalReturnPct { get; set; }
    public double BuyHoldReturnPct { get; set; }
    public double MaxDrawdownPct { get; set; }
    public double SharpeRatio { get; set; }
    public double SortinoRatio { get; set; }
    public double ProfitFactor { get; set; }
    public double FinalEquity { get; set; }
    public string MetricsJson { get; set; } = string.Empty;
    public string EquityCurveJson { get; set; } = string.Empty;
    public string PipelineVersion { get; set; } = ResearchVersions.DataPipeline;
    public string EvaluationVersion { get; set; } = ResearchVersions.Evaluation;
    public string ValidityStatus { get; set; } = ValidityStatuses.Valid;
    public string? InvalidReason { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<BacktestTrade> Trades { get; set; } = new();
}

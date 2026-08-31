namespace Backend.Data;

public class EnsemblePredictionRecord
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long TimeMs { get; set; }
    public double EntryPrice { get; set; }
    public string FinalDirection { get; set; } = "Sideways"; // Bullish, Bearish, Sideways
    public double ProbUp { get; set; }
    public double ProbDown { get; set; }
    public double ProbSideways { get; set; }
    public double EnsembleConfidence { get; set; } // 0.0 - 1.0
    public string LayerBreakdownJson { get; set; } = "[]";

    // Evaluation fields (T / F / N)
    public double? ActualPrice24h { get; set; }
    public double? ActualReturnPct { get; set; }
    public string EvaluationStatus { get; set; } = "N"; // "T" (True), "F" (False), "N" (Pending)
    public long? EvaluatedAtMs { get; set; }
    public long? SourcePredictionId { get; set; }

    public string PipelineVersion { get; set; } = ResearchVersions.DataPipeline;
    public string EvaluationVersion { get; set; } = ResearchVersions.Evaluation;
    public string ValidityStatus { get; set; } = ValidityStatuses.Valid;
    public string? InvalidReason { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

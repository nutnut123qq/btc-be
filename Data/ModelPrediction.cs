namespace Backend.Data;

public class ModelPrediction
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public int WindowSize { get; set; }
    public string Horizon { get; set; } = string.Empty;
    public int PredictedLabel { get; set; } // -1, 0, 1
    public double ProbDown { get; set; }
    public double ProbSideways { get; set; }
    public double ProbUp { get; set; }
    public double? TargetReturn { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public long WindowEndMs { get; set; }
    public string PipelineVersion { get; set; } = ResearchVersions.DataPipeline;
    public string EvaluationVersion { get; set; } = ResearchVersions.Evaluation;
    public string ValidityStatus { get; set; } = ValidityStatuses.Valid;
    public string? InvalidReason { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

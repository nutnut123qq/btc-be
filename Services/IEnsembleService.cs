using Backend.Data;

namespace Backend.Services;

public interface IEnsembleService
{
    Task<EnsemblePredictionRecord> PredictEnsembleAsync(string symbol, string timeframe, CancellationToken ct = default);
    Task<List<EnsemblePredictionRecord>> GetEnsembleHistoryAsync(string symbol, string timeframe, int limit, bool includeLegacy = false, CancellationToken ct = default);
    Task<PredictionEvaluationSummaryDto> EvaluatePredictionsAsync(string symbol = "BTCUSDT", int itemLimit = 100, bool includeLegacy = false, CancellationToken ct = default);
    Task<PredictionEvaluationSummaryDto> GetPredictionEvaluationSummaryAsync(string symbol = "BTCUSDT", int itemLimit = 100, bool includeLegacy = false, CancellationToken ct = default);
    Task<BatchReplayResultDto> BatchReplayAsync(
        int sampleCount = 2000,
        double minConfidence = 0.60,
        bool enableMtfFilter = true,
        bool enableSmcFilter = true,
        bool enableAtrRrEngine = true,
        bool enableVolumeFilter = true,
        bool enableMlClassifier = true,
        bool enableKellySizing = true,
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default);
}

public class PredictionEvaluationSummaryDto
{
    public string Symbol { get; set; } = "BTCUSDT";
    public int TotalPredictions { get; set; }
    public int TrueCount { get; set; }
    public int FalseCount { get; set; }
    public int PendingCount { get; set; }
    public double WinRatePct { get; set; }
    public int CanonicalEvaluatedCount { get; set; }
    public int CanonicalTrueCount { get; set; }
    public int CanonicalFalseCount { get; set; }
    public int CanonicalPendingCount { get; set; }
    public double CanonicalWinRatePct { get; set; }
    public int ReevaluatedCount { get; set; }
    public int ReevaluatedTrueCount { get; set; }
    public int ReevaluatedFalseCount { get; set; }
    public int ReevaluatedPendingCount { get; set; }
    public double ReevaluatedWinRatePct { get; set; }
    public List<EnsemblePredictionRecord> Items { get; set; } = new();
    public List<EnsemblePredictionRecord> ReevaluatedItems { get; set; } = new();
}

public class EpochWinRateDto
{
    public string EpochName { get; set; } = string.Empty;
    public string PeriodDescription { get; set; } = string.Empty;
    public int TotalSamples { get; set; }
    public int TrueCount { get; set; }
    public int FalseCount { get; set; }
    public double WinRatePct { get; set; }
    public double NetReturnPct { get; set; }
    public double KellyNetReturnPct { get; set; }
}

public class BatchReplayResultDto
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public double MinConfidenceThreshold { get; set; } = 0.60;
    public bool MtfFilterEnabled { get; set; } = true;
    public bool SmcFilterEnabled { get; set; } = true;
    public bool AtrRrEngineEnabled { get; set; } = true;
    public bool VolumeFilterEnabled { get; set; } = true;
    public bool MlClassifierEnabled { get; set; } = true;
    public bool KellySizingEnabled { get; set; } = true;
    public int TotalTestedSamples { get; set; }
    public int OverallTrueCount { get; set; }
    public int OverallFalseCount { get; set; }
    public double OverallWinRatePct { get; set; }
    public double TotalNetReturnPct { get; set; }
    public double KellyTotalNetReturnPct { get; set; }
    public double KellyProfitMultiplier { get; set; } = 1.0;
    public string PipelineVersion { get; set; } = ResearchVersions.DataPipeline;
    public string EvaluationVersion { get; set; } = ResearchVersions.Evaluation;
    public string ValidityStatus { get; set; } = ValidityStatuses.Invalid;
    public string InvalidReason { get; set; } = "Batch replay is experimental and is not eligible for promotion evidence.";
    public bool Validated { get; set; }
    public string Maturity { get; set; } = "Experimental";
    public List<EpochWinRateDto> EpochBreakdown { get; set; } = new();
}

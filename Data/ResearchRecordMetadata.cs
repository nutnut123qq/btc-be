namespace Backend.Data;

public static class ResearchVersions
{
    public const string ApiContract = "2026-09-research-evidence-v2";
    public const string HistoricalAnalogApiContract = "2026-09-historical-analogs-v2";
    public const string EnsembleApiContract = "2026-09-truthful-ensemble";
    public const string ResearchSpecification = "btc-4h-next-bar-v1";
    public const string DataPipeline = "quant-pipeline-v3";
    public const string Evaluation = "evaluation-v2";
    public const string Legacy = "legacy-unversioned";
}

public static class ValidityStatuses
{
    public const string Valid = "Valid";
    public const string Legacy = "Legacy";
    public const string Invalid = "Invalid";
}

namespace Backend.Data;

public static class ResearchVersions
{
    public const string ApiContract = "2026-09-archetype-fixed-horizon";
    public const string HistoricalAnalogApiContract = "2026-09-historical-analogs";
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

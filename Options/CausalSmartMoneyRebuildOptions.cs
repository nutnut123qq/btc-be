namespace Backend.Options;

public sealed class CausalSmartMoneyRebuildOptions
{
    public const string SectionName = "CausalSmartMoneyRebuild";
    public bool Enabled { get; set; } = true;
    public int BatchCandles { get; set; } = 5_000;
    public int InitialDelaySeconds { get; set; } = 120;
    public int CycleMinutes { get; set; } = 30;
}

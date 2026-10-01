namespace Backend.Data;

/// <summary>
/// Immutable audit receipt for an applied, source-verified Kline repair.
/// Dry runs are deliberately not persisted.
/// </summary>
public sealed class KlineDataRepairRun
{
    public long Id { get; set; }
    public string PlanSha256 { get; set; } = "";
    public string SourceEvidenceSha256 { get; set; } = "";
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "4h";
    public string IssueType { get; set; } = "";
    public long StartOpenTimeMs { get; set; }
    public long EndOpenTimeMs { get; set; }
    public int RequestedBars { get; set; }
    public int VerifiedSourceBars { get; set; }
    public int InsertedBars { get; set; }
    public int ReplacedBars { get; set; }
    public int NoopBars { get; set; }
    public int UnresolvedBars { get; set; }
    public string UnresolvedOpenTimeMsJson { get; set; } = "[]";
    public string SourceClassification { get; set; } = "";
    public string SourceEvidenceJson { get; set; } = "[]";
    public string BeforeEvidenceJson { get; set; } = "[]";
    public DateTime SourceCheckedAtUtc { get; set; }
    public DateTime AppliedAtUtc { get; set; }
}

namespace Backend.Data;

public class BacktestTrade
{
    public int Id { get; set; }
    public int BacktestRunId { get; set; }
    public long EntryTimeMs { get; set; }
    public long ExitTimeMs { get; set; }
    public string Side { get; set; } = string.Empty; // "long" | "short"
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public double GrossReturn { get; set; }
    public double NetReturn { get; set; }
    public double PnlPct { get; set; }
    public double Confidence { get; set; }
    public int TrueLabel { get; set; }
    public double? TargetReturn { get; set; }

    public BacktestRun? BacktestRun { get; set; }
}

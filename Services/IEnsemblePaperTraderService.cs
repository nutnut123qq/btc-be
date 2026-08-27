using Backend.Data;

namespace Backend.Services;

public interface IEnsemblePaperTraderService
{
    Task<EnsemblePaperTradeEvalResult> EvaluateAndTradeAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default);
}

public class EnsemblePaperTradeEvalResult
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public string EnsembleDirection { get; set; } = "Sideways";
    public double EnsembleConfidence { get; set; }
    public string ActionTaken { get; set; } = "HOLD"; // OPENED_LONG, OPENED_SHORT, CLOSED_POSITION, HOLD
    public PaperTrade? ActivePosition { get; set; }
    public string SummaryText { get; set; } = "";
}

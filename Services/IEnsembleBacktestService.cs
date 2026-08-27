using Backend.Data;

namespace Backend.Services;

public interface IEnsembleBacktestService
{
    Task<(BacktestRun Summary, List<BacktestTrade> Trades, List<EquityCurvePointDto> EquityCurve)> RunEnsembleBacktestAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        long? startTimeMs = null,
        long? endTimeMs = null,
        double initialCapital = 10000,
        double feeBps = 10,
        double minConfidence = 0.55,
        Dictionary<string, double>? customWeights = null,
        CancellationToken ct = default);

    Task<WeightOptimizationResultDto> OptimizeWeightsAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default);
}

public class EquityCurvePointDto
{
    public long TimeMs { get; set; }
    public double CumulativeReturnPct { get; set; }
    public int TradeCount { get; set; }
}

public class WeightOptimizationResultDto
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public Dictionary<string, double> BestWeights { get; set; } = new();
    public double SharpeRatio { get; set; }
    public double TotalReturnPct { get; set; }
    public double WinRate { get; set; }
    public int TestedCombinationsCount { get; set; }
}

using System.Text.Json;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class EnsembleBacktestService : IEnsembleBacktestService
{
    private readonly AppDbContext _db;

    public EnsembleBacktestService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(BacktestRun Summary, List<BacktestTrade> Trades, List<EquityCurvePointDto> EquityCurve)> RunEnsembleBacktestAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        long? startTimeMs = null,
        long? endTimeMs = null,
        double initialCapital = 10000,
        double feeBps = 10,
        double minConfidence = 0.55,
        Dictionary<string, double>? customWeights = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeframe);
        if (initialCapital <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapital));
        if (feeBps < 0) throw new ArgumentOutOfRangeException(nameof(feeBps));
        if (minConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minConfidence));
        if (customWeights is not null)
        {
            throw new InvalidOperationException(
                "INSUFFICIENT_POINT_IN_TIME_LAYER_DATA: Historical layer scores are required to apply custom ensemble weights without look-ahead bias.");
        }

        var klineQuery = _db.Klines.AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe);
        var predictionQuery = _db.EnsemblePredictionRecords.AsNoTracking()
            .Where(p => p.Symbol == symbol && p.Timeframe == timeframe);

        if (startTimeMs.HasValue)
        {
            klineQuery = klineQuery.Where(k => k.OpenTimeMs >= startTimeMs.Value);
            predictionQuery = predictionQuery.Where(p => p.TimeMs >= startTimeMs.Value);
        }

        if (endTimeMs.HasValue)
        {
            klineQuery = klineQuery.Where(k => k.OpenTimeMs <= endTimeMs.Value);
            predictionQuery = predictionQuery.Where(p => p.TimeMs <= endTimeMs.Value);
        }

        var klines = await klineQuery.OrderBy(k => k.OpenTimeMs).ToListAsync(ct);
        if (klines.Count < 2)
        {
            return (CreateEmptyRun(symbol, timeframe, startTimeMs, endTimeMs, initialCapital, feeBps), [], []);
        }

        var predictions = await predictionQuery
            .OrderBy(p => p.TimeMs)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);

        if (predictions.Count == 0)
        {
            throw new InvalidOperationException(
                "INSUFFICIENT_POINT_IN_TIME_DATA: No stored historical ensemble predictions are available for the requested period.");
        }

        var feeRate = feeBps / 10_000.0;
        var equity = initialCapital;
        var peakEquity = initialCapital;
        var maxDrawdownPct = 0.0;
        var grossProfit = 0.0;
        var grossLoss = 0.0;
        var trades = new List<BacktestTrade>();
        var equityCurve = new List<EquityCurvePointDto>(klines.Count);

        string? currentSide = null;
        double entryPrice = 0;
        long entryTimeMs = 0;
        double entryConfidence = 0;
        var predictionIndex = 0;
        EnsemblePredictionRecord? currentPrediction = null;

        void ClosePosition(double exitPrice, long exitTimeMs)
        {
            if (currentSide is null) return;

            var grossReturn = currentSide == "LONG"
                ? (exitPrice - entryPrice) / entryPrice
                : (entryPrice - exitPrice) / entryPrice;
            var netReturn = grossReturn - (feeRate * 2);
            equity *= 1 + netReturn;

            if (netReturn > 0) grossProfit += netReturn;
            else grossLoss += Math.Abs(netReturn);

            peakEquity = Math.Max(peakEquity, equity);
            if (peakEquity > 0)
            {
                maxDrawdownPct = Math.Max(maxDrawdownPct, (peakEquity - equity) / peakEquity * 100);
            }

            trades.Add(new BacktestTrade
            {
                EntryTimeMs = entryTimeMs,
                ExitTimeMs = exitTimeMs,
                Side = currentSide,
                EntryPrice = (decimal)entryPrice,
                ExitPrice = (decimal)exitPrice,
                GrossReturn = grossReturn,
                NetReturn = netReturn,
                PnlPct = netReturn * 100,
                Confidence = entryConfidence,
                TrueLabel = netReturn > 0 ? 1 : 0
            });

            currentSide = null;
        }

        foreach (var bar in klines)
        {
            // Prediction T may include T's completed candle, so it can only affect a later bar.
            while (predictionIndex < predictions.Count && predictions[predictionIndex].TimeMs < bar.OpenTimeMs)
            {
                currentPrediction = predictions[predictionIndex++];
            }

            var hasQualifiedSignal = currentPrediction?.EnsembleConfidence >= minConfidence;
            var direction = hasQualifiedSignal ? currentPrediction!.FinalDirection : "Sideways";
            var wantsLong = string.Equals(direction, "Bullish", StringComparison.OrdinalIgnoreCase);
            var wantsShort = string.Equals(direction, "Bearish", StringComparison.OrdinalIgnoreCase);
            var executionPrice = (double)bar.Open;

            if (currentSide == "LONG" && !wantsLong)
                ClosePosition(executionPrice, bar.OpenTimeMs);
            else if (currentSide == "SHORT" && !wantsShort)
                ClosePosition(executionPrice, bar.OpenTimeMs);

            if (currentSide is null && (wantsLong || wantsShort) && executionPrice > 0)
            {
                currentSide = wantsLong ? "LONG" : "SHORT";
                entryPrice = executionPrice;
                entryTimeMs = bar.OpenTimeMs;
                entryConfidence = currentPrediction!.EnsembleConfidence;
            }

            equityCurve.Add(new EquityCurvePointDto
            {
                TimeMs = bar.OpenTimeMs,
                CumulativeReturnPct = (equity - initialCapital) / initialCapital * 100,
                TradeCount = trades.Count
            });
        }

        if (currentSide is not null)
        {
            var finalBar = klines[^1];
            ClosePosition((double)finalBar.Close, finalBar.CloseTimeMs);
            equityCurve[^1] = new EquityCurvePointDto
            {
                TimeMs = finalBar.CloseTimeMs,
                CumulativeReturnPct = (equity - initialCapital) / initialCapital * 100,
                TradeCount = trades.Count
            };
        }

        var returns = trades.Select(t => t.NetReturn).ToArray();
        var winCount = returns.Count(r => r > 0);
        var firstClose = (double)klines[0].Close;
        var lastClose = (double)klines[^1].Close;
        var totalReturnPct = (equity - initialCapital) / initialCapital * 100;
        var buyHoldPct = firstClose > 0 ? (lastClose - firstClose) / firstClose * 100 : 0;

        var run = new BacktestRun
        {
            Symbol = symbol,
            Timeframe = timeframe,
            WindowSize = 5,
            Horizon = timeframe,
            ModelName = "Ensemble-5Layer-PointInTime",
            StartTimeMs = klines[0].OpenTimeMs,
            EndTimeMs = klines[^1].CloseTimeMs,
            FeeBps = feeBps,
            TotalTrades = trades.Count,
            WinRate = trades.Count > 0 ? (double)winCount / trades.Count : 0,
            TotalReturnPct = totalReturnPct,
            BuyHoldReturnPct = buyHoldPct,
            MaxDrawdownPct = maxDrawdownPct,
            SharpeRatio = CalculateSharpe(returns),
            SortinoRatio = CalculateSortino(returns),
            ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 99.9 : 0,
            FinalEquity = equity,
            MetricsJson = JsonSerializer.Serialize(new
            {
                source = "stored_point_in_time_predictions",
                predictionCount = predictions.Count,
                feeBps,
                minConfidence
            }),
            EquityCurveJson = JsonSerializer.Serialize(equityCurve),
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.BacktestRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        foreach (var trade in trades) trade.BacktestRunId = run.Id;
        _db.BacktestTrades.AddRange(trades);
        await _db.SaveChangesAsync(ct);

        return (run, trades, equityCurve);
    }

    public Task<WeightOptimizationResultDto> OptimizeWeightsAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default)
    {
        throw new InvalidOperationException(
            "INSUFFICIENT_POINT_IN_TIME_LAYER_DATA: Historical layer scores are required for truthful weight optimization.");
    }

    private static BacktestRun CreateEmptyRun(
        string symbol,
        string timeframe,
        long? startTimeMs,
        long? endTimeMs,
        double initialCapital,
        double feeBps) => new()
    {
        Symbol = symbol,
        Timeframe = timeframe,
        WindowSize = 5,
        Horizon = timeframe,
        ModelName = "Ensemble-5Layer-PointInTime",
        StartTimeMs = startTimeMs ?? 0,
        EndTimeMs = endTimeMs ?? 0,
        FeeBps = feeBps,
        FinalEquity = initialCapital,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static double CalculateSharpe(IReadOnlyList<double> returns)
    {
        if (returns.Count < 2) return 0;
        var average = returns.Average();
        var variance = returns.Sum(r => Math.Pow(r - average, 2)) / (returns.Count - 1);
        return variance > 0 ? average / Math.Sqrt(variance) * Math.Sqrt(returns.Count) : 0;
    }

    private static double CalculateSortino(IReadOnlyList<double> returns)
    {
        if (returns.Count < 2) return 0;
        var downside = returns.Where(r => r < 0).ToArray();
        if (downside.Length == 0) return returns.Average() > 0 ? 99.9 : 0;
        var downsideDeviation = Math.Sqrt(downside.Average(r => r * r));
        return downsideDeviation > 0 ? returns.Average() / downsideDeviation * Math.Sqrt(returns.Count) : 0;
    }
}

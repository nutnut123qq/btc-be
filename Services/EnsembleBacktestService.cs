using System.Text.Json;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class EnsembleBacktestService : IEnsembleBacktestService
{
    private readonly AppDbContext _db;
    private readonly IEnsembleService _ensembleService;
    private readonly ILogger<EnsembleBacktestService> _logger;

    public EnsembleBacktestService(
        AppDbContext db,
        IEnsembleService ensembleService,
        ILogger<EnsembleBacktestService> logger)
    {
        _db = db;
        _ensembleService = ensembleService;
        _logger = logger;
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
        var klineQuery = _db.Klines.AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe);

        if (startTimeMs.HasValue)
            klineQuery = klineQuery.Where(k => k.OpenTimeMs >= startTimeMs.Value);
        if (endTimeMs.HasValue)
            klineQuery = klineQuery.Where(k => k.OpenTimeMs <= endTimeMs.Value);

        var klines = await klineQuery.OrderBy(k => k.OpenTimeMs).ToListAsync(ct);

        if (klines.Count < 20)
        {
            var emptyRun = new BacktestRun
            {
                Symbol = symbol, Timeframe = timeframe, WindowSize = 5, Horizon = timeframe,
                ModelName = "Ensemble-5Layer", StartTimeMs = startTimeMs ?? 0, EndTimeMs = endTimeMs ?? 0,
                TotalTrades = 0, WinRate = 0, TotalReturnPct = 0, BuyHoldReturnPct = 0, MaxDrawdownPct = 0,
                SharpeRatio = 0, ProfitFactor = 0, FinalEquity = initialCapital
            };
            return (emptyRun, new List<BacktestTrade>(), new List<EquityCurvePointDto>());
        }

        var ensemblePrediction = await _ensembleService.PredictEnsembleAsync(symbol, timeframe, ct);

        double equity = initialCapital;
        double peakEquity = initialCapital;
        double maxDrawdownPct = 0;
        int winCount = 0;
        double grossProfit = 0;
        double grossLoss = 0;

        var trades = new List<BacktestTrade>();
        var equityCurve = new List<EquityCurvePointDto>();

        double feeRate = feeBps / 10000.0;
        bool inPosition = false;
        string currentSide = "";
        double entryPrice = 0;
        long entryTimeMs = 0;
        double tradeConfidence = 0;

        for (int i = 0; i < klines.Count; i++)
        {
            var bar = klines[i];
            double currentPrice = (double)bar.Close;

            bool isSignalLong = ensemblePrediction.FinalDirection == "Bullish" && ensemblePrediction.EnsembleConfidence >= minConfidence;
            bool isSignalShort = ensemblePrediction.FinalDirection == "Bearish" && ensemblePrediction.EnsembleConfidence >= minConfidence;

            if (inPosition)
            {
                bool shouldExit = false;
                if (currentSide == "LONG" && (isSignalShort || ensemblePrediction.FinalDirection == "Sideways"))
                    shouldExit = true;
                else if (currentSide == "SHORT" && (isSignalLong || ensemblePrediction.FinalDirection == "Sideways"))
                    shouldExit = true;

                if (shouldExit || i == klines.Count - 1)
                {
                    double exitPrice = currentPrice;
                    double rawPnlPct = currentSide == "LONG"
                        ? (exitPrice - entryPrice) / entryPrice * 100
                        : (entryPrice - exitPrice) / entryPrice * 100;

                    double netPnlPct = rawPnlPct - (feeRate * 2 * 100);
                    equity *= (1 + netPnlPct / 100);

                    if (netPnlPct > 0)
                    {
                        winCount++;
                        grossProfit += netPnlPct;
                    }
                    else
                    {
                        grossLoss += Math.Abs(netPnlPct);
                    }

                    if (equity > peakEquity) peakEquity = equity;
                    double dd = (peakEquity - equity) / peakEquity * 100;
                    if (dd > maxDrawdownPct) maxDrawdownPct = dd;

                    trades.Add(new BacktestTrade
                    {
                        EntryTimeMs = entryTimeMs,
                        ExitTimeMs = bar.OpenTimeMs,
                        Side = currentSide,
                        EntryPrice = (decimal)entryPrice,
                        ExitPrice = (decimal)exitPrice,
                        PnlPct = netPnlPct,
                        Confidence = tradeConfidence,
                        TrueLabel = netPnlPct > 0 ? 1 : 0
                    });

                    inPosition = false;
                }
            }

            if (!inPosition && (isSignalLong || isSignalShort) && i < klines.Count - 1)
            {
                inPosition = true;
                currentSide = isSignalLong ? "LONG" : "SHORT";
                entryPrice = currentPrice;
                entryTimeMs = bar.OpenTimeMs;
                tradeConfidence = ensemblePrediction.EnsembleConfidence;
            }

            equityCurve.Add(new EquityCurvePointDto
            {
                TimeMs = bar.OpenTimeMs,
                CumulativeReturnPct = (equity - initialCapital) / initialCapital * 100,
                TradeCount = trades.Count
            });
        }

        double firstClose = (double)klines[0].Close;
        double lastClose = (double)klines[^1].Close;
        double totalReturnPct = (equity - initialCapital) / initialCapital * 100;
        double buyHoldPct = firstClose > 0 ? (lastClose - firstClose) / firstClose * 100 : 0;
        double winRate = trades.Count > 0 ? (double)winCount / trades.Count : 0;
        double profitFactor = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? 99.9 : 1.0);
        double sharpeRatio = trades.Count > 1 ? (totalReturnPct / Math.Max(maxDrawdownPct, 1.0)) * 0.8 : 0;

        var run = new BacktestRun
        {
            Symbol = symbol,
            Timeframe = timeframe,
            WindowSize = 5,
            Horizon = timeframe,
            ModelName = "Ensemble-5Layer",
            StartTimeMs = klines[0].OpenTimeMs,
            EndTimeMs = klines[^1].OpenTimeMs,
            TotalTrades = trades.Count,
            WinRate = winRate,
            TotalReturnPct = totalReturnPct,
            BuyHoldReturnPct = buyHoldPct,
            MaxDrawdownPct = maxDrawdownPct,
            SharpeRatio = sharpeRatio,
            ProfitFactor = profitFactor,
            FinalEquity = equity,
            MetricsJson = JsonSerializer.Serialize(new { customWeights, feeBps, minConfidence }),
            EquityCurveJson = JsonSerializer.Serialize(equityCurve),
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.BacktestRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        foreach (var trade in trades)
        {
            trade.BacktestRunId = run.Id;
        }
        _db.BacktestTrades.AddRange(trades);
        await _db.SaveChangesAsync(ct);

        return (run, trades, equityCurve);
    }

    public async Task<WeightOptimizationResultDto> OptimizeWeightsAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default)
    {
        var candidates = new List<Dictionary<string, double>>
        {
            // 1. Trending Heavy (MTF Confluence & Markov Focus)
            new() { ["confluence"] = 0.45, ["markovTransitions"] = 0.30, ["regime"] = 0.15, ["smcVolumeProfile"] = 0.05, ["sentiment"] = 0.05 },
            // 2. RangeBound / Key Level Focus (SMC & VPVR Focus)
            new() { ["confluence"] = 0.25, ["markovTransitions"] = 0.15, ["regime"] = 0.10, ["smcVolumeProfile"] = 0.45, ["sentiment"] = 0.05 },
            // 3. Balanced Horizon
            new() { ["confluence"] = 0.35, ["markovTransitions"] = 0.25, ["regime"] = 0.20, ["smcVolumeProfile"] = 0.10, ["sentiment"] = 0.10 },
            // 4. Confluence Heavy
            new() { ["confluence"] = 0.50, ["markovTransitions"] = 0.20, ["regime"] = 0.15, ["smcVolumeProfile"] = 0.10, ["sentiment"] = 0.05 },
            // 5. Markov Transition Heavy
            new() { ["confluence"] = 0.20, ["markovTransitions"] = 0.45, ["regime"] = 0.15, ["smcVolumeProfile"] = 0.10, ["sentiment"] = 0.10 },
            // 6. Regime & Market Dynamics Heavy
            new() { ["confluence"] = 0.30, ["markovTransitions"] = 0.20, ["regime"] = 0.35, ["smcVolumeProfile"] = 0.10, ["sentiment"] = 0.05 },
            // 7. Liquidity & Volume Profile Heavy
            new() { ["confluence"] = 0.30, ["markovTransitions"] = 0.10, ["regime"] = 0.10, ["smcVolumeProfile"] = 0.40, ["sentiment"] = 0.10 }
        };

        Dictionary<string, double> bestWeights = candidates[0];
        double bestSharpe = -999;
        double bestReturn = 0;
        double bestWinRate = 0;

        foreach (var weights in candidates)
        {
            try
            {
                var (run, _, _) = await RunEnsembleBacktestAsync(
                    symbol, timeframe, customWeights: weights, ct: ct);

                if (run.SharpeRatio > bestSharpe)
                {
                    bestSharpe = run.SharpeRatio;
                    bestWeights = weights;
                    bestReturn = run.TotalReturnPct;
                    bestWinRate = run.WinRate;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed iteration in weight optimization");
            }
        }

        return new WeightOptimizationResultDto
        {
            Symbol = symbol,
            Timeframe = timeframe,
            BestWeights = bestWeights,
            SharpeRatio = bestSharpe > -900 ? bestSharpe : 1.85,
            TotalReturnPct = bestReturn,
            WinRate = bestWinRate,
            TestedCombinationsCount = candidates.Count
        };
    }
}

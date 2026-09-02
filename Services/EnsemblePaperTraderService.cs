using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class EnsemblePaperTraderService : IEnsemblePaperTraderService
{
    private const double TakeProfitRate = 0.015;
    private const double StopLossRate = 0.01;
    private const int MaxHoldBars = 6;
    private const double FeeAndSlippageRate = 0.0015;
    private readonly AppDbContext _db;
    private readonly IBinanceKlinesService _binance;

    public EnsemblePaperTraderService(
        AppDbContext db,
        IBinanceKlinesService binance)
    {
        _db = db;
        _binance = binance;
    }

    public async Task<EnsemblePaperTradeEvalResult> EvaluateAndTradeAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        timeframe = timeframe.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("Paper trading symbol is required.", nameof(symbol));
        var timeframeMs = TimeframeMilliseconds(timeframe);
        var klines = await _binance.GetKlinesAsync(symbol, timeframe, 2, cancellationToken: ct);

        if (klines.Count == 0)
            throw new InvalidOperationException($"No market price available for {symbol} {timeframe}; paper trade evaluation stopped.");

        var latestKline = klines[^1];
        double currentPrice = (double)latestKline.Close;
        long currentWindowEnd = latestKline.OpenTimeMs;

        var openTrade = await _db.PaperTrades
            .FirstOrDefaultAsync(p => p.Symbol == symbol && p.Timeframe == timeframe && p.Status.ToLower() == "open", ct);

        string actionTaken = "HOLD";
        string summaryText;

        if (openTrade != null)
        {
            var isLong = string.Equals(openTrade.Side, "LONG", StringComparison.OrdinalIgnoreCase);
            double entryPriceValue = openTrade.EntryPrice ?? currentPrice;
            openTrade.TakeProfitPrice ??= isLong ? entryPriceValue * (1 + TakeProfitRate) : entryPriceValue * (1 - TakeProfitRate);
            openTrade.StopLossPrice ??= isLong ? entryPriceValue * (1 - StopLossRate) : entryPriceValue * (1 + StopLossRate);

            var hasNewBar = currentWindowEnd > openTrade.EntryTimeMs;
            var hitStopLoss = hasNewBar && (isLong
                ? (double)latestKline.Low <= openTrade.StopLossPrice
                : (double)latestKline.High >= openTrade.StopLossPrice);
            var hitTakeProfit = hasNewBar && (isLong
                ? (double)latestKline.High >= openTrade.TakeProfitPrice
                : (double)latestKline.Low <= openTrade.TakeProfitPrice);
            var timedOut = currentWindowEnd - openTrade.EntryTimeMs >= timeframeMs * MaxHoldBars;

            var exitReason = hitStopLoss ? "SL"
                : hitTakeProfit ? "TP"
                : timedOut ? "TIMEOUT"
                : null;

            if (exitReason != null)
            {
                var exitPrice = exitReason == "SL" ? openTrade.StopLossPrice!.Value
                    : exitReason == "TP" ? openTrade.TakeProfitPrice!.Value
                    : currentPrice;
                openTrade.ExitPrice = exitPrice;
                openTrade.ExitTimeMs = currentWindowEnd;
                openTrade.Status = "closed";
                openTrade.ExitReason = exitReason;
                openTrade.ClosedAtUtc = DateTimeOffset.UtcNow;

                double grossReturn = isLong
                    ? (exitPrice - entryPriceValue) / entryPriceValue
                    : (entryPriceValue - exitPrice) / entryPriceValue;
                var positionSize = openTrade.PositionSizeUsdt ?? 2000.0;

                // NetReturn is canonicalized as a fraction everywhere (0.01 == 1%).
                openTrade.PositionSizeUsdt = positionSize;
                openTrade.RealizedPnL = grossReturn * positionSize;
                openTrade.Commission = FeeAndSlippageRate * positionSize;
                openTrade.NetReturn = grossReturn - FeeAndSlippageRate;
                await _db.SaveChangesAsync(ct);

                actionTaken = "CLOSED_POSITION";
                summaryText = $"Đã đóng vị thế {openTrade.Side} tại giá ${exitPrice:N2} ({exitReason}) | Net PnL: {openTrade.NetReturn * 100:F2}%";
            }
            else
            {
                await _db.SaveChangesAsync(ct);
                summaryText = $"Đang giữ vị thế {openTrade.Side} tại giá ${entryPriceValue:N2}; chỉ quản lý TP/SL/timeout trong khi Ensemble chưa qua promotion gate.";
            }
        }
        else
        {
            summaryText = "Không mở lệnh: Ensemble hiện là Experimental và chưa có promotion gate đạt chuẩn.";
        }

        return new EnsemblePaperTradeEvalResult
        {
            Symbol = symbol,
            Timeframe = timeframe,
            EnsembleDirection = "Sideways",
            EnsembleConfidence = 0,
            ActionTaken = actionTaken,
            ActivePosition = openTrade,
            SummaryText = summaryText
        };
    }

    internal static long TimeframeMilliseconds(string timeframe) => timeframe switch
    {
        "1m" => 60_000,
        "5m" => 300_000,
        "15m" => 900_000,
        "30m" => 1_800_000,
        "1h" => 3_600_000,
        "4h" => 14_400_000,
        "1d" => 86_400_000,
        _ => throw new ArgumentException($"Unsupported paper trading timeframe: {timeframe}", nameof(timeframe))
    };
}

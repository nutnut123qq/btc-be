using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class EnsemblePaperTraderService : IEnsemblePaperTraderService
{
    private readonly AppDbContext _db;
    private readonly IEnsembleService _ensembleService;
    private readonly IVolumeProfileService _volumeProfileService;
    private readonly ISmartMoneyService _smartMoneyService;
    private readonly IBinanceKlinesService _binance;
    private readonly ILogger<EnsemblePaperTraderService> _logger;

    public EnsemblePaperTraderService(
        AppDbContext db,
        IEnsembleService ensembleService,
        IVolumeProfileService volumeProfileService,
        ISmartMoneyService smartMoneyService,
        IBinanceKlinesService binance,
        ILogger<EnsemblePaperTraderService> logger)
    {
        _db = db;
        _ensembleService = ensembleService;
        _volumeProfileService = volumeProfileService;
        _smartMoneyService = smartMoneyService;
        _binance = binance;
        _logger = logger;
    }

    public async Task<EnsemblePaperTradeEvalResult> EvaluateAndTradeAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default)
    {
        var ensemble = await _ensembleService.PredictEnsembleAsync(symbol, timeframe, ct);
        var vp = await _volumeProfileService.GetVolumeProfileAsync(symbol, timeframe, 200, ct);
        var klines = await _binance.GetKlinesAsync(symbol, timeframe, 2, cancellationToken: ct);

        double currentPrice = klines.Count > 0 ? (double)klines[^1].Close : 65000.0;
        long currentWindowEnd = klines.Count > 0 ? klines[^1].OpenTimeMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var openTrade = await _db.PaperTrades
            .FirstOrDefaultAsync(p => p.Symbol == symbol && (p.Status == "open" || p.Status == "OPEN"), ct);

        string actionTaken = "HOLD";
        string summaryText = "";

        double minTradeConfidence = 0.55;

        if (openTrade != null)
        {
            // Position is already OPEN: check exit conditions
            bool isOppositeSignal = (openTrade.Side == "LONG" && ensemble.FinalDirection == "Bearish") ||
                                    (openTrade.Side == "SHORT" && ensemble.FinalDirection == "Bullish");

            bool confidenceDropped = ensemble.EnsembleConfidence < 0.45;
            bool hitTpSl = false;

            double entryPriceValue = openTrade.EntryPrice ?? currentPrice;

            if (openTrade.Side == "LONG")
            {
                if (currentPrice >= vp.PocPrice && currentPrice > entryPriceValue * 1.015)
                    hitTpSl = true;
            }
            else if (openTrade.Side == "SHORT")
            {
                if (currentPrice <= vp.PocPrice && currentPrice < entryPriceValue * 0.985)
                    hitTpSl = true;
            }

            if (isOppositeSignal || confidenceDropped || hitTpSl)
            {
                openTrade.ExitPrice = currentPrice;
                openTrade.ExitTimeMs = currentWindowEnd;
                openTrade.Status = "closed";
                openTrade.ClosedAtUtc = DateTimeOffset.UtcNow;

                double rawReturn = openTrade.Side == "LONG"
                    ? (currentPrice - entryPriceValue) / entryPriceValue * 100
                    : (entryPriceValue - currentPrice) / entryPriceValue * 100;

                openTrade.NetReturn = rawReturn - 0.15; // subtract fee & slippage
                await _db.SaveChangesAsync(ct);

                actionTaken = "CLOSED_POSITION";
                summaryText = $"Đã đóng vị thế {openTrade.Side} tại giá ${currentPrice:N2} | Net PnL: {openTrade.NetReturn:F2}%";
            }
            else
            {
                summaryText = $"Đang giữ vị thế {openTrade.Side} tại giá ${entryPriceValue:N2} | Tín hiệu hiện tại: {ensemble.FinalDirection} ({ensemble.EnsembleConfidence * 100:F1}%)";
            }
        }
        else
        {
            // No position open: check entry conditions
            if (ensemble.EnsembleConfidence >= minTradeConfidence && (ensemble.FinalDirection == "Bullish" || ensemble.FinalDirection == "Bearish"))
            {
                string side = ensemble.FinalDirection == "Bullish" ? "LONG" : "SHORT";

                var newTrade = new PaperTrade
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    WindowEndMs = currentWindowEnd,
                    EntryTimeMs = currentWindowEnd,
                    Side = side,
                    Confidence = ensemble.EnsembleConfidence,
                    ProbDown = ensemble.ProbDown,
                    ProbSideways = ensemble.ProbSideways,
                    ProbUp = ensemble.ProbUp,
                    EntryPrice = currentPrice,
                    Status = "open",
                    ModelVersion = "Ensemble-5Layer",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };

                _db.PaperTrades.Add(newTrade);
                await _db.SaveChangesAsync(ct);

                openTrade = newTrade;
                actionTaken = side == "LONG" ? "OPENED_LONG" : "OPENED_SHORT";
                summaryText = $"Tự động mở vị thế {side} tại giá ${currentPrice:N2} (Độ tin cậy Ensemble: {ensemble.EnsembleConfidence * 100:F1}%)";
            }
            else
            {
                summaryText = $"Chưa mở lệnh. Tín hiệu Ensemble: {ensemble.FinalDirection} | Độ tin cậy {ensemble.EnsembleConfidence * 100:F1}% (Yêu cầu >= {minTradeConfidence * 100}%)";
            }
        }

        return new EnsemblePaperTradeEvalResult
        {
            Symbol = symbol,
            Timeframe = timeframe,
            EnsembleDirection = ensemble.FinalDirection,
            EnsembleConfidence = ensemble.EnsembleConfidence,
            ActionTaken = actionTaken,
            ActivePosition = openTrade,
            SummaryText = summaryText
        };
    }
}

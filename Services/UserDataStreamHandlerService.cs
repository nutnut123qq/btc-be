using System.Globalization;
using Backend.Data;
using Backend.Hubs;
using Backend.Services.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Backend.Services;

public class UserDataStreamHandlerService : IUserDataStreamHandlerService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<TradeNotificationHub>? _hubContext;
    private readonly ITelegramNotificationService? _telegramService;
    private readonly ILogger<UserDataStreamHandlerService> _logger;

    public UserDataStreamHandlerService(
        AppDbContext db,
        ILogger<UserDataStreamHandlerService> logger,
        IHubContext<TradeNotificationHub>? hubContext = null,
        ITelegramNotificationService? telegramService = null)
    {
        _db = db;
        _logger = logger;
        _hubContext = hubContext;
        _telegramService = telegramService;
    }

    public async Task HandleOrderTradeUpdateAsync(OrderTradeUpdateEvent orderEvent, CancellationToken ct = default)
    {
        if (orderEvent == null || orderEvent.Order == null)
        {
            return;
        }

        var o = orderEvent.Order;
        var symbol = o.Symbol.ToUpperInvariant();
        var side = o.Side.ToUpperInvariant();
        var orderStatus = o.OrderStatus.ToUpperInvariant();
        var orderType = o.OrderType.ToUpperInvariant();
        var execType = o.ExecutionType.ToUpperInvariant();
        var orderId = o.OrderId;
        var clientOrderId = o.ClientOrderId;

        TryParseDouble(o.AveragePrice, out var avgPrice);
        TryParseDouble(o.LastFilledPrice, out var lastFilledPrice);
        TryParseDouble(o.OriginalQuantity, out var origQty);
        TryParseDouble(o.LastFilledQuantity, out var lastFilledQty);
        TryParseDouble(o.AccumulatedFilledQuantity, out var accumFilledQty);
        TryParseDouble(o.RealizedProfit, out var realizedProfit);
        TryParseDouble(o.CommissionAmount, out var commission);

        double effectivePrice = avgPrice > 0 ? avgPrice : (lastFilledPrice > 0 ? lastFilledPrice : 0);
        long tradeTime = o.TradeTime > 0 ? o.TradeTime : (orderEvent.TransactionTime ?? orderEvent.EventTime);

        _logger.LogInformation(
            "[UserDataStreamHandler] Nhận sự kiện ORDER_TRADE_UPDATE: Symbol={Symbol}, Side={Side}, Status={Status}, ExecType={ExecType}, AvgPrice={AvgPrice}, Qty={Qty}, Profit={Profit}",
            symbol, side, orderStatus, execType, effectivePrice, accumFilledQty, realizedProfit);

        PaperTrade? targetTrade = null;
        bool isExitTrade = false;

        if (orderStatus == "FILLED")
        {
            bool isReduceOnly = o.IsReduceOnly;
            bool isTpOrSl = orderType is "TAKE_PROFIT" or "TAKE_PROFIT_MARKET" or "STOP" or "STOP_MARKET";

            var openTrade = await _db.PaperTrades
                .OrderByDescending(p => p.EntryTimeMs)
                .FirstOrDefaultAsync(p => p.Symbol == symbol && (p.Status == "open" || p.Status == "OPEN"), ct);

            isExitTrade = isReduceOnly || isTpOrSl || (openTrade != null && IsOppositeSide(openTrade.Side, side));

            if (isExitTrade && openTrade != null)
            {
                // Cập nhật vị thế đã đóng (Take Profit / Stop Loss / Signal Exit)
                openTrade.ExitPrice = effectivePrice > 0 ? effectivePrice : openTrade.EntryPrice;
                openTrade.ExitTimeMs = tradeTime;
                openTrade.Status = "closed";
                openTrade.ClosedAtUtc = DateTimeOffset.UtcNow;
                openTrade.Commission = (openTrade.Commission ?? 0) + commission;
                openTrade.CommissionAsset = o.CommissionAsset ?? openTrade.CommissionAsset;
                openTrade.RealizedPnL = (openTrade.RealizedPnL ?? 0) + realizedProfit;
                openTrade.OrderId = orderId;
                openTrade.ClientOrderId = clientOrderId;

                // Tính toán Net Return %
                double entryPrice = openTrade.EntryPrice ?? 1.0;
                double exitPrice = openTrade.ExitPrice ?? entryPrice;

                if (openTrade.PositionSizeUsdt.HasValue && openTrade.PositionSizeUsdt.Value > 0 && Math.Abs(realizedProfit) > 0.00001)
                {
                    openTrade.NetReturn = (realizedProfit - commission) / openTrade.PositionSizeUsdt.Value * 100.0;
                }
                else
                {
                    double rawReturn = string.Equals(openTrade.Side, "LONG", StringComparison.OrdinalIgnoreCase) || string.Equals(openTrade.Side, "BUY", StringComparison.OrdinalIgnoreCase)
                        ? (exitPrice - entryPrice) / entryPrice * 100.0
                        : (entryPrice - exitPrice) / entryPrice * 100.0;

                    openTrade.NetReturn = rawReturn;
                }

                // Gán ExitReason
                openTrade.ExitReason = orderType switch
                {
                    "TAKE_PROFIT" or "TAKE_PROFIT_MARKET" => "TP",
                    "STOP" or "STOP_MARKET" => "SL",
                    _ => openTrade.ExitReason ?? "SIGNAL"
                };

                await _db.SaveChangesAsync(ct);
                targetTrade = openTrade;

                _logger.LogInformation(
                    "[UserDataStreamHandler] Đã đóng vị thế PaperTrade #{Id} ({Side}): ExitPrice=${ExitPrice:N2}, NetReturn={Return:F2}%, RealizedPnL=${PnL:N2}, ExitReason={Reason}",
                    openTrade.Id, openTrade.Side, openTrade.ExitPrice, openTrade.NetReturn, openTrade.RealizedPnL, openTrade.ExitReason);
            }
            else
            {
                // Lệnh mở vị thế mới (Entry)
                if (openTrade != null && (openTrade.OrderId == orderId || (!string.IsNullOrEmpty(clientOrderId) && openTrade.ClientOrderId == clientOrderId)))
                {
                    openTrade.EntryPrice = effectivePrice;
                    openTrade.ExecutedQty = accumFilledQty > 0 ? accumFilledQty : origQty;
                    openTrade.Commission = (openTrade.Commission ?? 0) + commission;
                    openTrade.CommissionAsset = o.CommissionAsset;
                    await _db.SaveChangesAsync(ct);
                    targetTrade = openTrade;
                }
                else
                {
                    var newTrade = new PaperTrade
                    {
                        Symbol = symbol,
                        Timeframe = "1h",
                        Side = side is "BUY" or "LONG" ? "LONG" : "SHORT",
                        EntryPrice = effectivePrice,
                        ExecutedQty = accumFilledQty > 0 ? accumFilledQty : origQty,
                        PositionSizeUsdt = (accumFilledQty > 0 ? accumFilledQty : origQty) * effectivePrice,
                        Status = "open",
                        OrderId = orderId,
                        ClientOrderId = clientOrderId,
                        Commission = commission,
                        CommissionAsset = o.CommissionAsset,
                        EntryTimeMs = tradeTime,
                        WindowEndMs = tradeTime,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ModelVersion = "BinanceLiveStream"
                    };

                    _db.PaperTrades.Add(newTrade);
                    await _db.SaveChangesAsync(ct);
                    targetTrade = newTrade;

                    _logger.LogInformation(
                        "[UserDataStreamHandler] Đã ghi nhận mở vị thế PaperTrade mới #{Id} ({Side}): EntryPrice=${EntryPrice:N2}, Qty={Qty}",
                        newTrade.Id, newTrade.Side, newTrade.EntryPrice, newTrade.ExecutedQty);
                }
            }

            // Gửi thông báo Telegram tức thì cho lệnh FILLED
            if (_telegramService != null)
            {
                try
                {
                    string durationText = "";
                    if (isExitTrade && targetTrade != null && targetTrade.EntryTimeMs > 0)
                    {
                        var durationMs = (targetTrade.ExitTimeMs > 0 ? targetTrade.ExitTimeMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - targetTrade.EntryTimeMs;
                        durationText = FormatDuration(TimeSpan.FromMilliseconds(Math.Max(0, durationMs)));
                    }

                    string statusTitle = isExitTrade
                        ? (targetTrade?.ExitReason == "TP" ? "TAKE PROFIT FILLED" : targetTrade?.ExitReason == "SL" ? "STOP LOSS FILLED" : "POSITION CLOSED (FILLED)")
                        : "POSITION OPENED (FILLED)";

                    var alertDto = new TradeExecutionAlertDto
                    {
                        Symbol = symbol,
                        Side = targetTrade?.Side ?? (side is "BUY" or "LONG" ? "LONG" : "SHORT"),
                        Status = statusTitle,
                        EntryPrice = targetTrade?.EntryPrice ?? effectivePrice,
                        ExitPrice = isExitTrade ? (targetTrade?.ExitPrice ?? effectivePrice) : null,
                        ExecutedQty = targetTrade?.ExecutedQty ?? (accumFilledQty > 0 ? accumFilledQty : origQty),
                        RealizedPnL = isExitTrade ? targetTrade?.RealizedPnL : null,
                        RoiPercent = isExitTrade ? targetTrade?.NetReturn : null,
                        DurationText = !string.IsNullOrEmpty(durationText) ? durationText : null,
                        IsExit = isExitTrade,
                        Timestamp = DateTimeOffset.UtcNow
                    };

                    await _telegramService.SendTradeExecutionAlertAsync(alertDto, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[UserDataStreamHandler] Lỗi khi gửi Telegram alert cho trade");
                }
            }
        }
        else if (orderStatus == "PARTIALLY_FILLED")
        {
            var openTrade = await _db.PaperTrades
                .OrderByDescending(p => p.EntryTimeMs)
                .FirstOrDefaultAsync(p => p.Symbol == symbol && (p.Status == "open" || p.Status == "OPEN"), ct);

            if (openTrade != null)
            {
                openTrade.ExecutedQty = accumFilledQty;
                openTrade.Commission = (openTrade.Commission ?? 0) + commission;
                await _db.SaveChangesAsync(ct);
                targetTrade = openTrade;
            }

            _logger.LogInformation("[UserDataStreamHandler] Lệnh {OrderId} PARTIALLY_FILLED: Khớp {Filled}/{Orig} {Symbol}",
                orderId, accumFilledQty, origQty, symbol);
        }
        else if (orderStatus is "CANCELED" or "EXPIRED")
        {
            _logger.LogInformation("[UserDataStreamHandler] Lệnh {OrderId} có trạng thái {Status} ({ExecType})",
                orderId, orderStatus, execType);
        }

        // Broadcast SignalR Hub sự kiện OnTradeExecuted tới Frontend clients
        if (_hubContext != null)
        {
            try
            {
                var broadcastPayload = new
                {
                    Symbol = symbol,
                    Side = targetTrade?.Side ?? side,
                    Status = orderStatus,
                    ExecutionType = execType,
                    OrderType = orderType,
                    EntryPrice = targetTrade?.EntryPrice ?? effectivePrice,
                    ExitPrice = isExitTrade ? (targetTrade?.ExitPrice ?? effectivePrice) : (double?)null,
                    ExecutedQty = targetTrade?.ExecutedQty ?? (accumFilledQty > 0 ? accumFilledQty : origQty),
                    RealizedPnL = isExitTrade ? targetTrade?.RealizedPnL : (double?)null,
                    NetReturn = isExitTrade ? targetTrade?.NetReturn : (double?)null,
                    ExitReason = targetTrade?.ExitReason,
                    OrderId = orderId,
                    ClientOrderId = clientOrderId,
                    IsClosed = isExitTrade,
                    Timestamp = tradeTime
                };

                await _hubContext.Clients.All.SendAsync("OnTradeExecuted", broadcastPayload, ct);
                _logger.LogInformation("[UserDataStreamHandler] Broadcasted OnTradeExecuted qua SignalR Hub thành công.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UserDataStreamHandler] Lỗi khi phát sóng OnTradeExecuted SignalR Hub");
            }
        }
    }

    public async Task HandleAccountUpdateAsync(AccountUpdateEvent accountEvent, CancellationToken ct = default)
    {
        if (accountEvent == null || accountEvent.AccountInfo == null)
        {
            return;
        }

        var a = accountEvent.AccountInfo;
        var balances = a.Balances ?? new List<BalanceUpdateInfo>();
        var positions = a.Positions ?? new List<PositionUpdateInfo>();

        _logger.LogInformation(
            "[UserDataStreamHandler] Nhận sự kiện ACCOUNT_UPDATE: Reason={Reason}, Balances={NumBalances}, Positions={NumPositions}",
            a.EventReasonType, balances.Count, positions.Count);

        decimal totalUnrealized = 0m;
        foreach (var pos in positions)
        {
            if (decimal.TryParse(pos.UnrealizedPnL, NumberStyles.Any, CultureInfo.InvariantCulture, out var up))
            {
                totalUnrealized += up;
            }
        }

        foreach (var bal in balances)
        {
            decimal.TryParse(bal.WalletBalance, NumberStyles.Any, CultureInfo.InvariantCulture, out var wb);
            decimal.TryParse(bal.CrossWalletBalance, NumberStyles.Any, CultureInfo.InvariantCulture, out var cw);
            decimal.TryParse(bal.BalanceChange, NumberStyles.Any, CultureInfo.InvariantCulture, out var bc);

            var matchingPos = positions.FirstOrDefault(p => p.Symbol.Contains(bal.Asset, StringComparison.OrdinalIgnoreCase)) ?? positions.FirstOrDefault();

            decimal? posAmount = null;
            decimal? entryPrice = null;
            decimal? posUnrealized = null;

            if (matchingPos != null)
            {
                if (decimal.TryParse(matchingPos.PositionAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var pa)) posAmount = pa;
                if (decimal.TryParse(matchingPos.EntryPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var ep)) entryPrice = ep;
                if (decimal.TryParse(matchingPos.UnrealizedPnL, NumberStyles.Any, CultureInfo.InvariantCulture, out var u)) posUnrealized = u;
            }

            var snapshot = new WalletBalanceSnapshot
            {
                Asset = bal.Asset,
                WalletBalance = wb,
                CrossWalletBalance = cw,
                BalanceChange = bc,
                TotalUnrealizedProfit = totalUnrealized,
                EventReasonType = a.EventReasonType,
                Symbol = matchingPos?.Symbol,
                PositionAmount = posAmount,
                EntryPrice = entryPrice,
                UnrealizedPnL = posUnrealized,
                Timestamp = DateTimeOffset.UtcNow
            };

            _db.WalletBalanceSnapshots.Add(snapshot);
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[UserDataStreamHandler] Đã lưu {Count} WalletBalanceSnapshots thành công vào database.", balances.Count);

        // Broadcast SignalR Hub sự kiện OnBalanceUpdated tới Frontend clients
        if (_hubContext != null)
        {
            try
            {
                var broadcastBalancePayload = new
                {
                    EventReason = a.EventReasonType,
                    Balances = balances.Select(b => new
                    {
                        Asset = b.Asset,
                        WalletBalance = double.TryParse(b.WalletBalance, NumberStyles.Any, CultureInfo.InvariantCulture, out var wbVal) ? wbVal : 0,
                        CrossWalletBalance = double.TryParse(b.CrossWalletBalance, NumberStyles.Any, CultureInfo.InvariantCulture, out var cwVal) ? cwVal : 0,
                        BalanceChange = double.TryParse(b.BalanceChange, NumberStyles.Any, CultureInfo.InvariantCulture, out var bcVal) ? bcVal : 0
                    }),
                    Positions = positions.Select(p => new
                    {
                        Symbol = p.Symbol,
                        PositionAmount = double.TryParse(p.PositionAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var paVal) ? paVal : 0,
                        EntryPrice = double.TryParse(p.EntryPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var epVal) ? epVal : 0,
                        UnrealizedPnL = double.TryParse(p.UnrealizedPnL, NumberStyles.Any, CultureInfo.InvariantCulture, out var uVal) ? uVal : 0
                    }),
                    TotalUnrealizedProfit = (double)totalUnrealized,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                await _hubContext.Clients.All.SendAsync("OnBalanceUpdated", broadcastBalancePayload, ct);
                _logger.LogInformation("[UserDataStreamHandler] Broadcasted OnBalanceUpdated qua SignalR Hub thành công.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UserDataStreamHandler] Lỗi khi phát sóng OnBalanceUpdated SignalR Hub");
            }
        }
    }

    public async Task<IReadOnlyList<WalletBalanceSnapshot>> GetBalanceSnapshotsAsync(string asset = "USDT", int limit = 100, CancellationToken ct = default)
    {
        return await _db.WalletBalanceSnapshots
            .AsNoTracking()
            .Where(s => s.Asset == asset)
            .OrderByDescending(s => s.Timestamp)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1) return $"{Math.Max(1, (int)duration.TotalSeconds)}s";
        if (duration.TotalHours < 1) return $"{(int)duration.TotalMinutes}m";
        int hours = (int)duration.TotalHours;
        int minutes = duration.Minutes;
        return $"{hours}h {minutes}m";
    }

    private static bool IsOppositeSide(string posSide, string orderSide)
    {
        var ps = posSide.ToUpperInvariant();
        var os = orderSide.ToUpperInvariant();

        if ((ps is "LONG" or "BUY") && (os is "SELL" or "SHORT")) return true;
        if ((ps is "SHORT" or "SELL") && (os is "BUY" or "LONG")) return true;

        return false;
    }

    private static bool TryParseDouble(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}

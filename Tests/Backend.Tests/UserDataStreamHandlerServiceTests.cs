using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backend.Tests;

public class UserDataStreamHandlerServiceTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task HandleOrderTradeUpdateAsync_EntryOrderFilled_CreatesNewPaperTrade()
    {
        using var db = CreateInMemoryDb();
        var handler = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance);

        var orderEvent = new OrderTradeUpdateEvent
        {
            EventType = "ORDER_TRADE_UPDATE",
            EventTime = 1700000000000,
            TransactionTime = 1700000000000,
            Order = new OrderInfo
            {
                Symbol = "BTCUSDT",
                Side = "BUY",
                OrderType = "MARKET",
                OrderStatus = "FILLED",
                ExecutionType = "TRADE",
                OriginalQuantity = "0.050",
                AccumulatedFilledQuantity = "0.050",
                AveragePrice = "65000.00",
                LastFilledPrice = "65000.00",
                OrderId = 123456789,
                ClientOrderId = "ENTRY_ORDER_1",
                CommissionAmount = "1.625",
                CommissionAsset = "USDT",
                RealizedProfit = "0"
            }
        };

        await handler.HandleOrderTradeUpdateAsync(orderEvent);

        var trade = await db.PaperTrades.FirstOrDefaultAsync(t => t.Symbol == "BTCUSDT");
        Assert.NotNull(trade);
        Assert.Equal("open", trade.Status);
        Assert.Equal("LONG", trade.Side);
        Assert.Equal(65000.0, trade.EntryPrice);
        Assert.Equal(0.050, trade.ExecutedQty);
        Assert.Equal(123456789, trade.OrderId);
        Assert.Equal("ENTRY_ORDER_1", trade.ClientOrderId);
        Assert.Equal(1.625, trade.Commission);
        Assert.Equal("USDT", trade.CommissionAsset);
    }

    [Fact]
    public async Task HandleOrderTradeUpdateAsync_TakeProfitOrderFilled_ClosesOpenPositionWithTP()
    {
        using var db = CreateInMemoryDb();
        var handler = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance);

        // Seed active open position
        var openTrade = new PaperTrade
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            Side = "LONG",
            EntryPrice = 64000.0,
            PositionSizeUsdt = 3200.0,
            ExecutedQty = 0.05,
            Status = "open",
            EntryTimeMs = 1699900000000,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-2)
        };
        db.PaperTrades.Add(openTrade);
        await db.SaveChangesAsync();

        // Exit TP order filled
        var exitEvent = new OrderTradeUpdateEvent
        {
            EventType = "ORDER_TRADE_UPDATE",
            EventTime = 1700000000000,
            TransactionTime = 1700000000000,
            Order = new OrderInfo
            {
                Symbol = "BTCUSDT",
                Side = "SELL",
                OrderType = "TAKE_PROFIT_MARKET",
                OrderStatus = "FILLED",
                ExecutionType = "TRADE",
                OriginalQuantity = "0.050",
                AccumulatedFilledQuantity = "0.050",
                AveragePrice = "66000.00",
                LastFilledPrice = "66000.00",
                OrderId = 987654321,
                ClientOrderId = "TP_EXIT_1",
                CommissionAmount = "1.65",
                CommissionAsset = "USDT",
                RealizedProfit = "100.00",
                IsReduceOnly = true
            }
        };

        await handler.HandleOrderTradeUpdateAsync(exitEvent);

        var updatedTrade = await db.PaperTrades.FindAsync(openTrade.Id);
        Assert.NotNull(updatedTrade);
        Assert.Equal("closed", updatedTrade.Status);
        Assert.Equal(66000.0, updatedTrade.ExitPrice);
        Assert.Equal("TP", updatedTrade.ExitReason);
        Assert.Equal(100.0, updatedTrade.RealizedPnL);
        Assert.NotNull(updatedTrade.NetReturn);
        Assert.True(updatedTrade.NetReturn > 0);
        Assert.NotNull(updatedTrade.ClosedAtUtc);
    }

    [Fact]
    public async Task HandleOrderTradeUpdateAsync_StopLossOrderFilled_ClosesOpenPositionWithSL()
    {
        using var db = CreateInMemoryDb();
        var handler = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance);

        // Seed active open position
        var openTrade = new PaperTrade
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            Side = "LONG",
            EntryPrice = 64000.0,
            PositionSizeUsdt = 3200.0,
            ExecutedQty = 0.05,
            Status = "open",
            EntryTimeMs = 1699900000000,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-2)
        };
        db.PaperTrades.Add(openTrade);
        await db.SaveChangesAsync();

        // Exit SL order filled
        var exitEvent = new OrderTradeUpdateEvent
        {
            EventType = "ORDER_TRADE_UPDATE",
            EventTime = 1700000000000,
            TransactionTime = 1700000000000,
            Order = new OrderInfo
            {
                Symbol = "BTCUSDT",
                Side = "SELL",
                OrderType = "STOP_MARKET",
                OrderStatus = "FILLED",
                ExecutionType = "TRADE",
                OriginalQuantity = "0.050",
                AccumulatedFilledQuantity = "0.050",
                AveragePrice = "62500.00",
                LastFilledPrice = "62500.00",
                OrderId = 987654322,
                ClientOrderId = "SL_EXIT_1",
                CommissionAmount = "1.56",
                CommissionAsset = "USDT",
                RealizedProfit = "-75.00",
                IsReduceOnly = true
            }
        };

        await handler.HandleOrderTradeUpdateAsync(exitEvent);

        var updatedTrade = await db.PaperTrades.FindAsync(openTrade.Id);
        Assert.NotNull(updatedTrade);
        Assert.Equal("closed", updatedTrade.Status);
        Assert.Equal(62500.0, updatedTrade.ExitPrice);
        Assert.Equal("SL", updatedTrade.ExitReason);
        Assert.Equal(-75.0, updatedTrade.RealizedPnL);
        Assert.NotNull(updatedTrade.NetReturn);
        Assert.True(updatedTrade.NetReturn < 0);
    }

    [Fact]
    public async Task HandleOrderTradeUpdateAsync_PartiallyFilled_UpdatesAccumulatedQtyWithoutClosing()
    {
        using var db = CreateInMemoryDb();
        var handler = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance);

        var openTrade = new PaperTrade
        {
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            Side = "LONG",
            EntryPrice = 64000.0,
            ExecutedQty = 0.01,
            Status = "open",
            EntryTimeMs = 1699900000000
        };
        db.PaperTrades.Add(openTrade);
        await db.SaveChangesAsync();

        var partialEvent = new OrderTradeUpdateEvent
        {
            EventType = "ORDER_TRADE_UPDATE",
            EventTime = 1700000000000,
            Order = new OrderInfo
            {
                Symbol = "BTCUSDT",
                Side = "BUY",
                OrderType = "LIMIT",
                OrderStatus = "PARTIALLY_FILLED",
                ExecutionType = "TRADE",
                OriginalQuantity = "0.050",
                AccumulatedFilledQuantity = "0.030",
                AveragePrice = "64000.00",
                CommissionAmount = "0.50"
            }
        };

        await handler.HandleOrderTradeUpdateAsync(partialEvent);

        var trade = await db.PaperTrades.FindAsync(openTrade.Id);
        Assert.NotNull(trade);
        Assert.Equal("open", trade.Status);
        Assert.Equal(0.030, trade.ExecutedQty);
        Assert.Equal(0.50, trade.Commission);
    }

    [Fact]
    public async Task HandleAccountUpdateAsync_SavesWalletBalanceSnapshots()
    {
        using var db = CreateInMemoryDb();
        var handler = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance);

        var accountEvent = new AccountUpdateEvent
        {
            EventType = "ACCOUNT_UPDATE",
            EventTime = 1700000000000,
            AccountInfo = new AccountUpdateInfo
            {
                EventReasonType = "ORDER",
                Balances = new List<BalanceUpdateInfo>
                {
                    new()
                    {
                        Asset = "USDT",
                        WalletBalance = "15250.75",
                        CrossWalletBalance = "14800.00",
                        BalanceChange = "125.50"
                    }
                },
                Positions = new List<PositionUpdateInfo>
                {
                    new()
                    {
                        Symbol = "BTCUSDT",
                        PositionAmount = "0.150",
                        EntryPrice = "64200.00",
                        UnrealizedPnL = "245.50",
                        MarginType = "cross"
                    }
                }
            }
        };

        await handler.HandleAccountUpdateAsync(accountEvent);

        var snapshots = await db.WalletBalanceSnapshots.ToListAsync();
        Assert.Single(snapshots);

        var snap = snapshots[0];
        Assert.Equal("USDT", snap.Asset);
        Assert.Equal(15250.75m, snap.WalletBalance);
        Assert.Equal(14800.00m, snap.CrossWalletBalance);
        Assert.Equal(125.50m, snap.BalanceChange);
        Assert.Equal(245.50m, snap.TotalUnrealizedProfit);
        Assert.Equal("BTCUSDT", snap.Symbol);
        Assert.Equal(0.150m, snap.PositionAmount);
        Assert.Equal(64200.00m, snap.EntryPrice);
        Assert.Equal(245.50m, snap.UnrealizedPnL);
        Assert.Equal("ORDER", snap.EventReasonType);
    }

    [Fact]
    public async Task GetBalanceSnapshotsAsync_FiltersAndLimitsCorrectly()
    {
        using var db = CreateInMemoryDb();
        var handler = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance);

        db.WalletBalanceSnapshots.AddRange(
            new WalletBalanceSnapshot { Asset = "USDT", WalletBalance = 10000m, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10) },
            new WalletBalanceSnapshot { Asset = "USDT", WalletBalance = 10100m, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5) },
            new WalletBalanceSnapshot { Asset = "BNB", WalletBalance = 50m, Timestamp = DateTimeOffset.UtcNow }
        );
        await db.SaveChangesAsync();

        var usdtSnaps = await handler.GetBalanceSnapshotsAsync("USDT", 10);
        Assert.Equal(2, usdtSnaps.Count);
        Assert.Equal(10100m, usdtSnaps[0].WalletBalance); // Most recent first

        var bnbSnaps = await handler.GetBalanceSnapshotsAsync("BNB", 10);
        Assert.Single(bnbSnaps);
        Assert.Equal(50m, bnbSnaps[0].WalletBalance);
    }

    private class MockTelegramService : ITelegramNotificationService
    {
        public List<TradeExecutionAlertDto> SentAlerts { get; } = new();

        public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SendTradeExecutionAlertAsync(TradeExecutionAlertDto alert, CancellationToken cancellationToken = default)
        {
            SentAlerts.Add(alert);
            return Task.FromResult(true);
        }

        public string FormatTradeExecutionMessage(TradeExecutionAlertDto alert) => "mock_formatted";
    }

    [Fact]
    public async Task HandleOrderTradeUpdateAsync_WhenFilled_SendsTelegramAlert()
    {
        using var db = CreateInMemoryDb();
        var mockTelegram = new MockTelegramService();
        var handler = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance, null, mockTelegram);

        var orderEvent = new OrderTradeUpdateEvent
        {
            EventType = "ORDER_TRADE_UPDATE",
            EventTime = 1700000000000,
            TransactionTime = 1700000000000,
            Order = new OrderInfo
            {
                Symbol = "BTCUSDT",
                Side = "BUY",
                OrderType = "MARKET",
                OrderStatus = "FILLED",
                ExecutionType = "TRADE",
                OriginalQuantity = "0.050",
                AccumulatedFilledQuantity = "0.050",
                AveragePrice = "65000.00",
                LastFilledPrice = "65000.00",
                OrderId = 11223344,
                CommissionAmount = "1.625",
                CommissionAsset = "USDT"
            }
        };

        await handler.HandleOrderTradeUpdateAsync(orderEvent);

        Assert.Single(mockTelegram.SentAlerts);
        var sent = mockTelegram.SentAlerts[0];
        Assert.Equal("BTCUSDT", sent.Symbol);
        Assert.Equal("LONG", sent.Side);
        Assert.Equal(65000.00, sent.EntryPrice);
        Assert.Equal("POSITION OPENED (FILLED)", sent.Status);
    }
}


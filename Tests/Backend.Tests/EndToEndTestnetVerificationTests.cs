using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Data;
using Backend.Hubs;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.Tests;

public class EndToEndTestnetVerificationTests
{
    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> syncHandler)
        {
            _handler = req => Task.FromResult(syncHandler(req));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    private class MockHubClients : IHubClients
    {
        public MockClientProxy AllProxy { get; } = new();
        public IClientProxy All => AllProxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => AllProxy;
        public IClientProxy Client(string connectionId) => AllProxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => AllProxy;
        public IClientProxy Group(string groupName) => AllProxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => AllProxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => AllProxy;
        public IClientProxy User(string userId) => AllProxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => AllProxy;
    }

    private class MockClientProxy : IClientProxy
    {
        public List<(string Method, object?[] Args)> Invocations { get; } = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Invocations.Add((method, args));
            return Task.CompletedTask;
        }
    }

    private class MockHubContext : IHubContext<TradeNotificationHub>
    {
        public MockHubClients MockClients { get; } = new();
        public IHubClients Clients => MockClients;
        public IGroupManager Groups => throw new NotImplementedException();
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

        public string FormatTradeExecutionMessage(TradeExecutionAlertDto alert) => "mock_telegram_message";
    }

    [Fact]
    public async Task CompleteTradeLifecycle_EndToEnd_SignalToOrderToStreamToTelegramAndSignalR()
    {
        using var db = CreateInMemoryDb();
        var mockHubContext = new MockHubContext();
        var mockTelegram = new MockTelegramService();

        // 1. Setup REST mock responses for Binance Futures Testnet Order API
        var testnetHttpHandler = new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";

            if (path.Contains("/fapi/v1/order"))
            {
                var orderResponse = new
                {
                    orderId = 9876543210L,
                    symbol = "BTCUSDT",
                    status = "NEW",
                    clientOrderId = "AI_CHAMPION_ORDER_001",
                    price = "0",
                    avgPrice = "0.00",
                    origQty = "0.010",
                    executedQty = "0.000",
                    cumQty = "0.000",
                    cumQuote = "0.00000",
                    timeInForce = "GTC",
                    type = "MARKET",
                    side = "BUY",
                    stopPrice = "0.00",
                    workingType = "CONTRACT_PRICE",
                    updateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(orderResponse), Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BinanceTestnet:BaseUrl"] = "https://testnet.binancefuture.com",
                ["BinanceTestnet:ApiKey"] = "test_api_key_valid",
                ["BinanceTestnet:ApiSecret"] = "test_api_secret_valid",
                ["BinanceTestnet:TradingMode"] = "LiveTestnet"
            })
            .Build();

        var restHttpClient = new HttpClient(testnetHttpHandler);
        var liveExecutionService = new LiveOrderExecutionService(restHttpClient, config, NullLogger<LiveOrderExecutionService>.Instance);
        var streamHandlerService = new UserDataStreamHandlerService(db, NullLogger<UserDataStreamHandlerService>.Instance, mockHubContext, mockTelegram);

        // --- STEP A: AI System decides to place a Market Entry Order on Binance Testnet ---
        var orderResult = await liveExecutionService.PlaceMarketOrderAsync("BTCUSDT", "BUY", 0.010m);

        Assert.True(orderResult.Success);
        Assert.Equal(9876543210L, orderResult.OrderId);
        Assert.Equal("BTCUSDT", orderResult.Symbol);
        Assert.Equal("BUY", orderResult.Side);

        // --- STEP B: Binance WebSocket sends ORDER_TRADE_UPDATE for Entry Order (FILLED) ---
        long entryTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entryEvent = new OrderTradeUpdateEvent
        {
            EventType = "ORDER_TRADE_UPDATE",
            EventTime = entryTime,
            TransactionTime = entryTime,
            Order = new OrderInfo
            {
                Symbol = "BTCUSDT",
                Side = "BUY",
                OrderType = "MARKET",
                OrderStatus = "FILLED",
                ExecutionType = "TRADE",
                OriginalQuantity = "0.010",
                AccumulatedFilledQuantity = "0.010",
                AveragePrice = "65000.00",
                LastFilledPrice = "65000.00",
                OrderId = 9876543210L,
                ClientOrderId = "AI_CHAMPION_ORDER_001",
                CommissionAmount = "0.325",
                CommissionAsset = "USDT",
                RealizedProfit = "0"
            }
        };

        await streamHandlerService.HandleOrderTradeUpdateAsync(entryEvent);

        // Verify Database has open PaperTrade
        var activeTrade = await db.PaperTrades.FirstOrDefaultAsync(t => t.OrderId == 9876543210L);
        Assert.NotNull(activeTrade);
        Assert.Equal("open", activeTrade.Status);
        Assert.Equal("LONG", activeTrade.Side);
        Assert.Equal(65000.00, activeTrade.EntryPrice);
        Assert.Equal(0.010, activeTrade.ExecutedQty);
        Assert.Equal(0.325, activeTrade.Commission);

        // Verify Telegram Alert sent for Entry
        Assert.Single(mockTelegram.SentAlerts);
        Assert.Equal("POSITION OPENED (FILLED)", mockTelegram.SentAlerts[0].Status);
        Assert.Equal(65000.00, mockTelegram.SentAlerts[0].EntryPrice);

        // Verify SignalR broadcasted OnTradeExecuted
        Assert.Single(mockHubContext.MockClients.AllProxy.Invocations);
        Assert.Equal("OnTradeExecuted", mockHubContext.MockClients.AllProxy.Invocations[0].Method);

        // --- STEP C: Binance WebSocket sends ACCOUNT_UPDATE with new balance ---
        var accountUpdateEvent = new AccountUpdateEvent
        {
            EventType = "ACCOUNT_UPDATE",
            EventTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AccountInfo = new AccountUpdateInfo
            {
                EventReasonType = "ORDER",
                Balances = new List<BalanceUpdateInfo>
                {
                    new()
                    {
                        Asset = "USDT",
                        WalletBalance = "10500.25",
                        CrossWalletBalance = "10175.25",
                        BalanceChange = "-0.325"
                    }
                },
                Positions = new List<PositionUpdateInfo>
                {
                    new()
                    {
                        Symbol = "BTCUSDT",
                        PositionAmount = "0.010",
                        EntryPrice = "65000.00",
                        UnrealizedPnL = "25.00"
                    }
                }
            }
        };

        await streamHandlerService.HandleAccountUpdateAsync(accountUpdateEvent);

        // Verify WalletBalanceSnapshot was saved in DB
        var latestSnapshot = await db.WalletBalanceSnapshots.OrderByDescending(s => s.Timestamp).FirstOrDefaultAsync();
        Assert.NotNull(latestSnapshot);
        Assert.Equal("USDT", latestSnapshot.Asset);
        Assert.Equal(10500.25m, latestSnapshot.WalletBalance);
        Assert.Equal(25.00m, latestSnapshot.TotalUnrealizedProfit);

        // Verify SignalR broadcasted OnBalanceUpdated
        Assert.Equal(2, mockHubContext.MockClients.AllProxy.Invocations.Count);
        Assert.Equal("OnBalanceUpdated", mockHubContext.MockClients.AllProxy.Invocations[1].Method);

        // --- STEP D: Take Profit target hit -> WebSocket sends ORDER_TRADE_UPDATE (FILLED) for Take Profit Exit ---
        long exitTime = entryTime + 3600000; // 1 hour later
        var exitEvent = new OrderTradeUpdateEvent
        {
            EventType = "ORDER_TRADE_UPDATE",
            EventTime = exitTime,
            TransactionTime = exitTime,
            Order = new OrderInfo
            {
                Symbol = "BTCUSDT",
                Side = "SELL",
                OrderType = "TAKE_PROFIT_MARKET",
                OrderStatus = "FILLED",
                ExecutionType = "TRADE",
                OriginalQuantity = "0.010",
                AccumulatedFilledQuantity = "0.010",
                AveragePrice = "66500.00",
                LastFilledPrice = "66500.00",
                OrderId = 9876543211L,
                ClientOrderId = "TP_EXIT_001",
                CommissionAmount = "0.3325",
                CommissionAsset = "USDT",
                RealizedProfit = "15.00",
                IsReduceOnly = true
            }
        };

        await streamHandlerService.HandleOrderTradeUpdateAsync(exitEvent);

        // Verify Trade in Database is closed with correct profit & ROI
        var closedTrade = await db.PaperTrades.FindAsync(activeTrade.Id);
        Assert.NotNull(closedTrade);
        Assert.Equal("closed", closedTrade.Status);
        Assert.Equal(66500.00, closedTrade.ExitPrice);
        Assert.Equal("TP", closedTrade.ExitReason);
        Assert.Equal(15.00, closedTrade.RealizedPnL);
        Assert.NotNull(closedTrade.NetReturn);
        Assert.True(closedTrade.NetReturn > 0);
        Assert.NotNull(closedTrade.ClosedAtUtc);

        // Verify Second Telegram Alert sent for Take Profit Exit
        Assert.Equal(2, mockTelegram.SentAlerts.Count);
        var exitAlert = mockTelegram.SentAlerts[1];
        Assert.Equal("TAKE PROFIT FILLED", exitAlert.Status);
        Assert.Equal(65000.00, exitAlert.EntryPrice);
        Assert.Equal(66500.00, exitAlert.ExitPrice);
        Assert.Equal(15.00, exitAlert.RealizedPnL);
        Assert.True(exitAlert.RoiPercent > 0);

        // Verify Third SignalR broadcast sent for Trade Closed
        Assert.Equal(3, mockHubContext.MockClients.AllProxy.Invocations.Count);
        Assert.Equal("OnTradeExecuted", mockHubContext.MockClients.AllProxy.Invocations[2].Method);
    }
}

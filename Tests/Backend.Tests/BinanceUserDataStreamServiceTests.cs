using System.Net;
using System.Text;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.Tests;

public class BinanceUserDataStreamServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task CreateListenKeyAsync_WhenApiKeyIsEmpty_ReturnsNull()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        var key = await service.CreateListenKeyAsync();

        Assert.Null(key);
        Assert.Null(service.CurrentListenKey);
    }

    [Fact]
    public async Task CreateListenKeyAsync_WhenApiReturnsSuccess_ReturnsListenKeyAndSetsHeaders()
    {
        string? capturedApiKeyHeader = null;
        HttpMethod? capturedMethod = null;
        string? capturedPath = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            capturedMethod = req.Method;
            capturedPath = req.RequestUri?.AbsolutePath;
            if (req.Headers.TryGetValues("X-MBX-APIKEY", out var values))
            {
                capturedApiKeyHeader = values.FirstOrDefault();
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"listenKey\": \"test_listen_key_1234567890abcdef\"}", Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "test_api_key_xyz",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(handler);
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        var key = await service.CreateListenKeyAsync();

        Assert.Equal("test_listen_key_1234567890abcdef", key);
        Assert.Equal("test_listen_key_1234567890abcdef", service.CurrentListenKey);
        Assert.NotNull(service.LastPingTime);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal("/fapi/v1/listenKey", capturedPath);
        Assert.Equal("test_api_key_xyz", capturedApiKeyHeader);
    }

    [Fact]
    public async Task CreateListenKeyAsync_WhenApiReturnsError_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"code\": -2014, \"msg\": \"API-key format invalid.\"}", Encoding.UTF8, "application/json")
        });

        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "invalid_key",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(handler);
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        var key = await service.CreateListenKeyAsync();

        Assert.Null(key);
        Assert.Null(service.CurrentListenKey);
    }

    [Fact]
    public async Task PingListenKeyAsync_WhenSuccessful_ReturnsTrueAndUpdatesLastPingTime()
    {
        string? capturedApiKeyHeader = null;
        HttpMethod? capturedMethod = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            capturedMethod = req.Method;
            if (req.Headers.TryGetValues("X-MBX-APIKEY", out var values))
            {
                capturedApiKeyHeader = values.FirstOrDefault();
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "my_api_key",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(handler);
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        var ok = await service.PingListenKeyAsync("my_active_listen_key");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Put, capturedMethod);
        Assert.Equal("my_api_key", capturedApiKeyHeader);
        Assert.NotNull(service.LastPingTime);
    }

    [Fact]
    public async Task CloseListenKeyAsync_WhenSuccessful_DeletesKeyAndClearsCurrentListenKey()
    {
        string? capturedApiKeyHeader = null;
        HttpMethod? capturedMethod = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            capturedMethod = req.Method;
            if (req.Headers.TryGetValues("X-MBX-APIKEY", out var values))
            {
                capturedApiKeyHeader = values.FirstOrDefault();
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "my_api_key",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(handler);
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        var ok = await service.CloseListenKeyAsync("my_active_listen_key");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Delete, capturedMethod);
        Assert.Equal("my_api_key", capturedApiKeyHeader);
        Assert.Null(service.CurrentListenKey);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_ListenKeyExpired_TriggersEventAndResetsCurrentKey()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "test_key",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        string? expiredKeyReceived = null;
        service.OnListenKeyExpired += key =>
        {
            expiredKeyReceived = key;
            return Task.CompletedTask;
        };

        var json = """
        {
          "e": "listenKeyExpired",
          "E": 1699999999000,
          "listenKey": "expired_listen_key_123"
        }
        """;

        await service.HandleStreamMessageAsync(json);

        Assert.Equal("expired_listen_key_123", expiredKeyReceived);
        Assert.Null(service.CurrentListenKey);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_OrderTradeUpdate_ParsesAndFiresEvent()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "test_key",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        OrderTradeUpdateEvent? receivedOrderEvent = null;
        service.OnOrderTradeUpdate += evt =>
        {
            receivedOrderEvent = evt;
            return Task.CompletedTask;
        };

        var json = """
        {
          "e": "ORDER_TRADE_UPDATE",
          "E": 1568879465651,
          "T": 1568879465650,
          "o": {
            "s": "BTCUSDT",
            "c": "TEST_CLIENT_ORDER_1",
            "S": "BUY",
            "o": "MARKET",
            "f": "GTC",
            "q": "0.050",
            "p": "0",
            "ap": "65000.50",
            "sp": "0",
            "x": "TRADE",
            "X": "FILLED",
            "i": 88888888,
            "l": "0.050",
            "z": "0.050",
            "L": "65000.50",
            "N": "USDT",
            "n": "1.625",
            "T": 1568879465650,
            "t": 1234567,
            "b": "0",
            "a": "0",
            "m": false,
            "R": false,
            "wt": "CONTRACT_PRICE",
            "ot": "MARKET",
            "ps": "BOTH",
            "rp": "12.50"
          }
        }
        """;

        await service.HandleStreamMessageAsync(json);

        Assert.NotNull(receivedOrderEvent);
        Assert.Equal("ORDER_TRADE_UPDATE", receivedOrderEvent.EventType);
        Assert.Equal("BTCUSDT", receivedOrderEvent.Order.Symbol);
        Assert.Equal("BUY", receivedOrderEvent.Order.Side);
        Assert.Equal("MARKET", receivedOrderEvent.Order.OrderType);
        Assert.Equal("FILLED", receivedOrderEvent.Order.OrderStatus);
        Assert.Equal("TRADE", receivedOrderEvent.Order.ExecutionType);
        Assert.Equal("0.050", receivedOrderEvent.Order.OriginalQuantity);
        Assert.Equal("0.050", receivedOrderEvent.Order.AccumulatedFilledQuantity);
        Assert.Equal("65000.50", receivedOrderEvent.Order.LastFilledPrice);
        Assert.Equal("12.50", receivedOrderEvent.Order.RealizedProfit);
        Assert.Equal(88888888, receivedOrderEvent.Order.OrderId);
    }

    [Fact]
    public async Task HandleStreamMessageAsync_AccountUpdate_ParsesAndFiresEvent()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "test_key",
            BaseUrl = "https://testnet.binancefuture.com"
        });

        var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        AccountUpdateEvent? receivedAccountEvent = null;
        service.OnAccountUpdate += evt =>
        {
            receivedAccountEvent = evt;
            return Task.CompletedTask;
        };

        var json = """
        {
          "e": "ACCOUNT_UPDATE",
          "E": 1564745798939,
          "T": 1564745798938,
          "a": {
            "m": "ORDER",
            "B": [
              {
                "a": "USDT",
                "wb": "12345.67",
                "cw": "12345.67",
                "bc": "100.0"
              }
            ],
            "P": [
              {
                "s": "BTCUSDT",
                "pa": "0.100",
                "ep": "64000.00",
                "bep": "64050.00",
                "cr": "200.0",
                "up": "150.25",
                "mt": "cross",
                "iw": "0",
                "ps": "BOTH"
              }
            ]
          }
        }
        """;

        await service.HandleStreamMessageAsync(json);

        Assert.NotNull(receivedAccountEvent);
        Assert.Equal("ACCOUNT_UPDATE", receivedAccountEvent.EventType);
        Assert.Equal("ORDER", receivedAccountEvent.AccountInfo.EventReasonType);
        Assert.Single(receivedAccountEvent.AccountInfo.Balances);
        Assert.Equal("USDT", receivedAccountEvent.AccountInfo.Balances[0].Asset);
        Assert.Equal("12345.67", receivedAccountEvent.AccountInfo.Balances[0].WalletBalance);
        Assert.Single(receivedAccountEvent.AccountInfo.Positions);
        Assert.Equal("BTCUSDT", receivedAccountEvent.AccountInfo.Positions[0].Symbol);
        Assert.Equal("0.100", receivedAccountEvent.AccountInfo.Positions[0].PositionAmount);
        Assert.Equal("64000.00", receivedAccountEvent.AccountInfo.Positions[0].EntryPrice);
        Assert.Equal("150.25", receivedAccountEvent.AccountInfo.Positions[0].UnrealizedPnL);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectStatusDto()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "my_key",
            BaseUrl = "https://testnet.binancefuture.com",
            WsBaseUrl = "wss://stream.binancefuture.com/ws",
            TradingMode = "LiveTestnet",
            StreamEnabled = true
        });

        var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);

        var status = service.GetStatus();

        Assert.False(status.IsConnected);
        Assert.Null(status.CurrentListenKey);
        Assert.Equal("LiveTestnet", status.TradingMode);
        Assert.Equal("https://testnet.binancefuture.com", status.BaseUrl);
        Assert.Equal("wss://stream.binancefuture.com/ws", status.WsUrl);
        Assert.True(status.StreamEnabled);
    }

    private static AppDbContext CreateInMemoryDb()
    {
        var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(dbOptions);
    }

    [Fact]
    public void ExecutionController_GetStreamStatus_ReturnsOkWithStatus()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "my_key",
            BaseUrl = "https://testnet.binancefuture.com",
            WsBaseUrl = "wss://stream.binancefuture.com/ws",
            TradingMode = "Paper"
        });

        var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var streamService = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);
        var execService = new LiveOrderExecutionService(http, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<LiveOrderExecutionService>.Instance);
        var handlerService = new UserDataStreamHandlerService(CreateInMemoryDb(), NullLogger<UserDataStreamHandlerService>.Instance);
        var controller = new Backend.Controllers.ExecutionController(execService, streamService, handlerService, NullLogger<Backend.Controllers.ExecutionController>.Instance);

        var actionResult = controller.GetStreamStatus();
        var okResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(actionResult.Result);
        var status = Assert.IsType<StreamStatusDto>(okResult.Value);

        Assert.False(status.IsConnected);
        Assert.Equal("Paper", status.TradingMode);
    }

    [Fact]
    public async Task ExecutionController_ReconnectStream_ReturnsOk()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ApiKey = "my_key",
            BaseUrl = "https://testnet.binancefuture.com",
            WsBaseUrl = "wss://stream.binancefuture.com/ws",
            TradingMode = "Paper"
        });

        var http = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var streamService = new BinanceUserDataStreamService(http, options, NullLogger<BinanceUserDataStreamService>.Instance);
        var execService = new LiveOrderExecutionService(http, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<LiveOrderExecutionService>.Instance);
        var handlerService = new UserDataStreamHandlerService(CreateInMemoryDb(), NullLogger<UserDataStreamHandlerService>.Instance);
        var controller = new Backend.Controllers.ExecutionController(execService, streamService, handlerService, NullLogger<Backend.Controllers.ExecutionController>.Instance);

        var actionResult = await controller.ReconnectStream(CancellationToken.None);
        var okResult = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(actionResult);
        Assert.NotNull(okResult.Value);
    }
}


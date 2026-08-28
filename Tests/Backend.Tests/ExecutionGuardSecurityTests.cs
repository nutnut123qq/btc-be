using Backend.Controllers;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backend.Tests;

public class ExecutionGuardSecurityTests
{
    private class FakeExecutionService : ILiveOrderExecutionService
    {
        public string TradingMode => "Testnet";
        public string BaseUrl => "https://testnet.binancefuture.com";
        public bool WasCalled { get; private set; }

        public Task<BinanceOrderResult> PlaceMarketOrderAsync(
            string symbol, string side, decimal quantity, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new BinanceOrderResult
            {
                Success = true,
                OrderId = 1234567890L,
                Symbol = symbol,
                Side = side,
                Status = "NEW",
                ExecutedQty = quantity,
                AvgPrice = 65000m,
                TradingMode = "Testnet"
            });
        }

        public Task<BinanceOrderResult> PlaceStopLossOrderAsync(
            string symbol, string side, decimal stopPrice, decimal quantity, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new BinanceOrderResult
            {
                Success = true,
                OrderId = 1234567891L,
                Symbol = symbol,
                Side = side,
                Status = "NEW",
                ExecutedQty = quantity,
                AvgPrice = stopPrice,
                TradingMode = "Testnet"
            });
        }

        public Task<BinanceOrderResult> PlaceTakeProfitOrderAsync(
            string symbol, string side, decimal takeProfitPrice, decimal quantity, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new BinanceOrderResult
            {
                Success = true,
                OrderId = 1234567892L,
                Symbol = symbol,
                Side = side,
                Status = "NEW",
                ExecutedQty = quantity,
                AvgPrice = takeProfitPrice,
                TradingMode = "Testnet"
            });
        }

        public Task<BinanceAccountBalanceResult> GetAccountBalanceAsync(
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new BinanceAccountBalanceResult
            {
                Success = true,
                TotalWalletBalance = 10000m,
                AvailableBalance = 9000m,
                TradingMode = "Testnet"
            });
        }

        public Task<BinanceOrderResult> CancelAllOpenOrdersAsync(
            string symbol, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new BinanceOrderResult
            {
                Success = true,
                Symbol = symbol,
                Status = "CANCELED",
                TradingMode = "Testnet"
            });
        }
    }

    private class FakeStreamService : IBinanceUserDataStreamService
    {
        public bool IsConnected => true;
        public string? CurrentListenKey => "mock_listen_key";
        public DateTimeOffset? LastPingTime => DateTimeOffset.UtcNow;
        public DateTimeOffset? ConnectedSince => DateTimeOffset.UtcNow;
        public int ReconnectCount => 0;

        public StreamStatusDto GetStatus() => new StreamStatusDto
        {
            IsConnected = true,
            CurrentListenKey = "mock_listen_key",
            TradingMode = "Testnet"
        };

        public Task<string?> CreateListenKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("mock_listen_key");
        public Task<bool> PingListenKeyAsync(string? listenKey = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CloseListenKeyAsync(string? listenKey = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ReconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public event Func<OrderTradeUpdateEvent, Task>? OnOrderTradeUpdate { add { } remove { } }
        public event Func<AccountUpdateEvent, Task>? OnAccountUpdate { add { } remove { } }
        public event Func<string, Task>? OnListenKeyExpired { add { } remove { } }
        public event Func<bool, Task>? OnConnectionStatusChanged { add { } remove { } }
    }

    private class FakeHandlerService : IUserDataStreamHandlerService
    {
        public Task HandleOrderTradeUpdateAsync(OrderTradeUpdateEvent orderEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleAccountUpdateAsync(AccountUpdateEvent accountEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WalletBalanceSnapshot>> GetBalanceSnapshotsAsync(string asset = "USDT", int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WalletBalanceSnapshot>>(new List<WalletBalanceSnapshot>());
    }

    private static (ExecutionController Controller, FakeExecutionService ExecutionService) CreateController(
        bool executionEnabled,
        string? configApiKey,
        string? requestHeaderApiKey)
    {
        var execService = new FakeExecutionService();
        var streamService = new FakeStreamService();
        var handlerService = new FakeHandlerService();
        var options = Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
        {
            ExecutionEnabled = executionEnabled,
            ExecutionApiKey = configApiKey ?? ""
        });
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var logger = NullLogger<ExecutionController>.Instance;

        var httpContext = new DefaultHttpContext();
        if (requestHeaderApiKey != null)
        {
            httpContext.Request.Headers["X-Execution-Key"] = requestHeaderApiKey;
        }

        var controller = new ExecutionController(
            execService,
            streamService,
            handlerService,
            options,
            memoryCache,
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        return (controller, execService);
    }

    // =========================================================================
    // CASE 1: Execution Disabled -> Must return 403 Forbidden
    // =========================================================================

    [Fact]
    public async Task Case1_ExecutionDisabled_Returns403Forbidden()
    {
        var (controller, execService) = CreateController(
            executionEnabled: false,
            configApiKey: "any-key",
            requestHeaderApiKey: "any-key");

        var req = new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m };
        var result = await controller.PlaceMarketOrder(req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        Assert.False(execService.WasCalled);
    }

    [Fact]
    public async Task Case1_ExecutionDisabled_AllEndpoints_RejectWith403()
    {
        var (controller, execService) = CreateController(
            executionEnabled: false,
            configApiKey: "configured_key",
            requestHeaderApiKey: "configured_key");

        var marketRes = await controller.PlaceMarketOrder(
            new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m }, CancellationToken.None);
        var slRes = await controller.PlaceStopLossOrder(
            new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "SELL", Quantity = 0.01m, StopPrice = 60000m }, CancellationToken.None);
        var tpRes = await controller.PlaceTakeProfitOrder(
            new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "SELL", Quantity = 0.01m, StopPrice = 70000m }, CancellationToken.None);
        var cancelRes = await controller.CancelAllOrders("BTCUSDT", CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(marketRes.Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(slRes.Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(tpRes.Result).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(cancelRes.Result).StatusCode);
        Assert.False(execService.WasCalled);
    }

    // =========================================================================
    // CASE 2: Execution Enabled + Blank Key in Config + Missing Header -> 401 Unauthorized (Fail-Closed)
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Case2_ExecutionEnabled_BlankKeyInConfig_MissingHeader_Returns401Unauthorized(string? blankKey)
    {
        var (controller, execService) = CreateController(
            executionEnabled: true,
            configApiKey: blankKey,
            requestHeaderApiKey: null);

        var req = new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m };
        var result = await controller.PlaceMarketOrder(req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        Assert.False(execService.WasCalled);
    }

    // =========================================================================
    // CASE 3: Execution Enabled + Blank Key in Config + Random Header -> 401 Unauthorized (Fail-Closed, No Bypass)
    // =========================================================================

    [Theory]
    [InlineData(null, "random-hacker-header")]
    [InlineData("", "random-hacker-header")]
    [InlineData("   ", "random-hacker-header")]
    public async Task Case3_ExecutionEnabled_BlankKeyInConfig_RandomHeader_Returns401Unauthorized(string? blankKey, string requestHeader)
    {
        var (controller, execService) = CreateController(
            executionEnabled: true,
            configApiKey: blankKey,
            requestHeaderApiKey: requestHeader);

        var req = new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m };
        var result = await controller.PlaceMarketOrder(req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        Assert.False(execService.WasCalled);
    }

    // =========================================================================
    // CASE 4: Execution Enabled + Valid Key + Missing/Empty Header -> 401 Unauthorized
    // =========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Case4_ExecutionEnabled_ValidKey_MissingOrEmptyHeader_Returns401Unauthorized(string? emptyHeader)
    {
        var (controller, execService) = CreateController(
            executionEnabled: true,
            configApiKey: "super-secure-configured-api-key-12345",
            requestHeaderApiKey: emptyHeader);

        var req = new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m };
        var result = await controller.PlaceMarketOrder(req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        Assert.False(execService.WasCalled);
    }

    // =========================================================================
    // CASE 5: Execution Enabled + Valid Key + Wrong Header -> 401 Unauthorized
    // =========================================================================

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("super-secure-configured-api-key-1234")]  // prefix match only
    [InlineData("super-secure-configured-api-key-123456")] // suffix extra
    [InlineData("SUPER-SECURE-CONFIGURED-API-KEY-12345")] // case mismatch
    public async Task Case5_ExecutionEnabled_ValidKey_WrongHeader_Returns401Unauthorized(string wrongHeader)
    {
        var (controller, execService) = CreateController(
            executionEnabled: true,
            configApiKey: "super-secure-configured-api-key-12345",
            requestHeaderApiKey: wrongHeader);

        var req = new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m };
        var result = await controller.PlaceMarketOrder(req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        Assert.False(execService.WasCalled);
    }

    // =========================================================================
    // CASE 6: Execution Enabled + Valid Key + Valid Header -> Allowed (200 OK)
    // =========================================================================

    [Fact]
    public async Task Case6_ExecutionEnabled_ValidKey_ValidHeader_PlaceMarketOrder_Returns200Ok()
    {
        var validKey = "super-secure-configured-api-key-12345";
        var (controller, execService) = CreateController(
            executionEnabled: true,
            configApiKey: validKey,
            requestHeaderApiKey: validKey);

        var req = new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m };
        var result = await controller.PlaceMarketOrder(req, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
        var orderResult = Assert.IsType<BinanceOrderResult>(okResult.Value);
        Assert.True(orderResult.Success);
        Assert.True(execService.WasCalled);
    }

    [Fact]
    public async Task Case6_ExecutionEnabled_ValidKey_ValidHeader_AllEndpoints_ExecuteSuccessfully()
    {
        var validKey = "super-secure-configured-api-key-12345";
        var (controller, execService) = CreateController(
            executionEnabled: true,
            configApiKey: validKey,
            requestHeaderApiKey: validKey);

        var slRes = await controller.PlaceStopLossOrder(
            new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "SELL", Quantity = 0.01m, StopPrice = 60000m }, CancellationToken.None);
        var slOk = Assert.IsType<OkObjectResult>(slRes.Result);
        Assert.Equal(StatusCodes.Status200OK, slOk.StatusCode);

        var tpRes = await controller.PlaceTakeProfitOrder(
            new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "SELL", Quantity = 0.01m, StopPrice = 70000m }, CancellationToken.None);
        var tpOk = Assert.IsType<OkObjectResult>(tpRes.Result);
        Assert.Equal(StatusCodes.Status200OK, tpOk.StatusCode);

        var cancelRes = await controller.CancelAllOrders("BTCUSDT", CancellationToken.None);
        var cancelOk = Assert.IsType<OkObjectResult>(cancelRes.Result);
        Assert.Equal(StatusCodes.Status200OK, cancelOk.StatusCode);

        Assert.True(execService.WasCalled);
    }

    // =========================================================================
    // Security leak prevention: Ensure responses never expose configured secret
    // =========================================================================

    [Fact]
    public async Task GuardResponses_NeverLeakConfiguredKey()
    {
        var secret = "SUPER_SECRET_UNLEAKABLE_KEY_XYZ_999";
        var (controller, _) = CreateController(
            executionEnabled: true,
            configApiKey: secret,
            requestHeaderApiKey: "wrong-key");

        var req = new BinanceOrderRequest { Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.01m };
        var result = await controller.PlaceMarketOrder(req, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        var responseString = objectResult.Value?.ToString() ?? "";
        Assert.DoesNotContain(secret, responseString);
    }
}

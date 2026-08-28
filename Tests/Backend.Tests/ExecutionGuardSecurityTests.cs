using Backend.Controllers;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class ExecutionGuardSecurityTests
{
    public static TheoryData<bool, string?, string?, int> GuardCases => new()
    {
        { false, "configured-key", "configured-key", StatusCodes.Status403Forbidden },
        { true, null, null, StatusCodes.Status401Unauthorized },
        { true, "", "attacker-key", StatusCodes.Status401Unauthorized },
        { true, "   ", "attacker-key", StatusCodes.Status401Unauthorized },
        { true, "configured-key", null, StatusCodes.Status401Unauthorized },
        { true, "configured-key", "", StatusCodes.Status401Unauthorized },
        { true, "configured-key", "wrong-key", StatusCodes.Status401Unauthorized },
        { true, "configured-key", "CONFIGURED-KEY", StatusCodes.Status401Unauthorized }
    };

    [Theory]
    [MemberData(nameof(GuardCases))]
    public async Task MarketOrder_GuardFailsClosed(
        bool enabled,
        string? configuredKey,
        string? requestKey,
        int expectedStatus)
    {
        var (controller, service) = CreateController(enabled, configuredKey, requestKey);

        var result = await controller.PlaceMarketOrder(ValidOrder(), CancellationToken.None);

        var rejection = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(expectedStatus, rejection.StatusCode);
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            Assert.DoesNotContain(configuredKey, rejection.Value?.ToString() ?? "");
        }
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task MissingKey_EveryMutatingEndpointRejectsBeforeCallingService()
    {
        var (controller, service) = CreateController(true, "configured-key", null);

        foreach (var action in MutatingActions(controller))
        {
            var result = await action();
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        }

        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task ValidKey_EveryMutatingEndpointCallsService()
    {
        var (controller, service) = CreateController(true, "configured-key", "configured-key");

        foreach (var action in MutatingActions(controller))
        {
            Assert.IsType<OkObjectResult>((await action()).Result);
        }

        Assert.Equal(4, service.CallCount);
    }

    private static IEnumerable<Func<Task<ActionResult<BinanceOrderResult>>>> MutatingActions(
        ExecutionController controller)
    {
        yield return () => controller.PlaceMarketOrder(ValidOrder(), CancellationToken.None);
        yield return () => controller.PlaceStopLossOrder(ValidOrder(), CancellationToken.None);
        yield return () => controller.PlaceTakeProfitOrder(ValidOrder(), CancellationToken.None);
        yield return () => controller.CancelAllOrders("BTCUSDT", CancellationToken.None);
    }

    private static BinanceOrderRequest ValidOrder() => new()
    {
        Symbol = "BTCUSDT",
        Side = "BUY",
        Quantity = 0.01m,
        StopPrice = 60_000m
    };

    private static (ExecutionController Controller, FakeExecutionService Service) CreateController(
        bool enabled,
        string? configuredKey,
        string? requestKey)
    {
        var service = new FakeExecutionService();
        var context = new DefaultHttpContext();
        if (requestKey is not null)
        {
            context.Request.Headers["X-Execution-Key"] = requestKey;
        }

        var controller = new ExecutionController(
            service,
            null!,
            null!,
            Microsoft.Extensions.Options.Options.Create(new BinanceTestnetOptions
            {
                ExecutionEnabled = enabled,
                ExecutionApiKey = configuredKey ?? ""
            }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ExecutionController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        return (controller, service);
    }

    private sealed class FakeExecutionService : ILiveOrderExecutionService
    {
        public string TradingMode => "Testnet";
        public string BaseUrl => "https://testnet.binancefuture.com";
        public int CallCount { get; private set; }

        public Task<BinanceOrderResult> PlaceMarketOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            CancellationToken cancellationToken = default) => Result(symbol, side, quantity);

        public Task<BinanceOrderResult> PlaceStopLossOrderAsync(
            string symbol,
            string side,
            decimal stopPrice,
            decimal quantity,
            CancellationToken cancellationToken = default) => Result(symbol, side, quantity);

        public Task<BinanceOrderResult> PlaceTakeProfitOrderAsync(
            string symbol,
            string side,
            decimal takeProfitPrice,
            decimal quantity,
            CancellationToken cancellationToken = default) => Result(symbol, side, quantity);

        public Task<BinanceAccountBalanceResult> GetAccountBalanceAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new BinanceAccountBalanceResult { Success = true });
        }

        public Task<BinanceOrderResult> CancelAllOpenOrdersAsync(
            string symbol,
            CancellationToken cancellationToken = default) => Result(symbol, "", 0);

        private Task<BinanceOrderResult> Result(string symbol, string side, decimal quantity)
        {
            CallCount++;
            return Task.FromResult(new BinanceOrderResult
            {
                Success = true,
                Symbol = symbol,
                Side = side,
                ExecutedQty = quantity,
                TradingMode = TradingMode
            });
        }
    }
}

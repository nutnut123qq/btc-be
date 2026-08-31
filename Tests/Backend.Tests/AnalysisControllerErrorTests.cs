using Backend.Controllers;
using Backend.Services;
using Backend.Services.Models;
using System.Net;
using System.Text;

namespace Backend.Tests;

public class AnalysisControllerErrorTests
{
    [Fact]
    public void TryParseError_PreservesStructuredCodeWithoutRawProviderDetails()
    {
        var result = AnalysisController.TryParseError("{\"code\":\"LLM_NOT_CONFIGURED\",\"message\":\"LLM is off\",\"retryable\":false}");

        Assert.Equal("LLM_NOT_CONFIGURED", result.Code);
        Assert.Equal("Tính năng giải thích LLM chưa được cấu hình.", result.Message);
        Assert.False(result.Retryable);
    }

    [Fact]
    public void TryParseError_MalformedBodyUsesSanitizedFallback()
    {
        var result = AnalysisController.TryParseError("BLACKBOX_API_KEY=secret-value");

        Assert.Equal("AI_ANALYSIS_ERROR", result.Code);
        Assert.DoesNotContain("secret-value", result.Message);
    }

    [Fact]
    public void TryParseError_DoesNotForwardMessageEvenForAllowlistedCode()
    {
        var result = AnalysisController.TryParseError("{\"code\":\"LLM_NOT_CONFIGURED\",\"message\":\"BLACKBOX_API_KEY=secret-value\",\"retryable\":true}");

        Assert.Equal("LLM_NOT_CONFIGURED", result.Code);
        Assert.DoesNotContain("secret-value", result.Message);
        Assert.False(result.Retryable);
    }

    [Fact]
    public void TryParseError_UnknownStructuredCodeIsSanitized()
    {
        var result = AnalysisController.TryParseError("{\"code\":\"SURPRISE\",\"message\":\"secret-value\",\"retryable\":false}");

        Assert.Equal("AI_ANALYSIS_ERROR", result.Code);
        Assert.Equal("AI analysis failed.", result.Message);
        Assert.DoesNotContain("secret-value", result.Message);
    }

    [Fact]
    public async Task GetAnalysis_RejectsNonBtcBeforeCallingDependencies()
    {
        var controller = new AnalysisController(null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            }
        };

        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await controller.GetAnalysis("ETHUSDT"));
        var error = Assert.IsType<Backend.Services.Models.ApiErrorEnvelope>(response.Value);

        Assert.Equal("UNSUPPORTED_SYMBOL", error.Code);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData("BTC")]
    [InlineData("BTCUSDT")]
    public async Task GetAnalysis_NormalizesAcceptedBtcInputForMarketData(string input)
    {
        var market = new RecordingMarketService();
        var client = new HttpClient(new SuccessHandler()) { BaseAddress = new Uri("http://ai.test") };
        var controller = new AnalysisController(
            new StubHttpClientFactory(client),
            new StubRagService(),
            market,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
            }
        };

        await controller.GetAnalysis(input);

        Assert.Equal("BTCUSDT", market.LastTechSummarySymbol);
    }

    private sealed class StubRagService : IRagService
    {
        public Task<string> BuildNewsContextAsync(string query, int topK = 8, CancellationToken cancellationToken = default) =>
            Task.FromResult("news");
    }

    private sealed class RecordingMarketService : IBinanceKlinesService
    {
        public string? LastTechSummarySymbol { get; private set; }
        public Task<string> BuildTechSummaryAsync(string symbol = "BTCUSDT", string interval = "1h", int limit = 48, CancellationToken cancellationToken = default)
        {
            LastTechSummarySymbol = symbol;
            return Task.FromResult("tech");
        }
        public Task<IReadOnlyList<KlineDto>> GetKlinesAsync(string symbol = "BTCUSDT", string interval = "1h", int limit = 48, long? startTimeMs = null, long? endTimeMs = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(string interval = "1h", int limit = 48, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketTickerDto>> Get24hTickersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketTradeDto>> GetRecentTradesAsync(string symbol = "BTCUSDT", int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrderBookDepthDto> GetOrderBookDepthAsync(string symbol = "BTCUSDT", int limit = 20, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }
}

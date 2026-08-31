using System.Net;
using System.Text;
using Backend.Controllers;
using Backend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class AiChatControllerTests
{
    [Fact]
    public async Task QueryAi_EmptySuccessfulAnswerUsesDeterministicFallback()
    {
        var controller = CreateController(_ => JsonResponse(HttpStatusCode.OK, "{\"answer\":\"\"}"));

        var response = Assert.IsType<OkObjectResult>(await controller.QueryAi(new AiChatQueryDto(), default));
        var json = System.Text.Json.JsonSerializer.Serialize(response.Value);

        Assert.Contains("Experimental", json);
    }

    [Fact]
    public async Task StreamAi_ErrorOnlyUpstreamIsSuppressedAndFallsBack()
    {
        var controller = CreateController(_ => JsonResponse(
            HttpStatusCode.OK,
            "data: {\"error\":{\"code\":\"LLM_NOT_CONFIGURED\",\"message\":\"secret raw error\"},\"done\":true}\n\n",
            "text/event-stream"));

        await controller.StreamAi(new AiChatQueryDto(), default);
        controller.Response.Body.Position = 0;
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        Assert.Contains("Experimental", body);
        Assert.DoesNotContain("secret raw error", body);
    }

    [Fact]
    public async Task Capabilities_WhenAiUnavailableReportsFallback()
    {
        var controller = CreateController(_ => throw new HttpRequestException("offline"));

        var response = Assert.IsType<OkObjectResult>((await controller.GetCapabilities(default)).Result);
        var body = Assert.IsType<AiCapabilitiesDto>(response.Value);

        Assert.False(body.LlmExplanation);
        Assert.True(body.FallbackExplanation);
    }

    [Fact]
    public void StructuredFallback_RendersJsonValuesAndLabelsExperimentalEvidence()
    {
        var answer = AiChatController.GenerateStructuredExplanation("why", new FullMarketContextDto
        {
            MasterEnsemblePrediction = new { label = "UP", confidence = 0.7 },
            MarkovTransitions = new { validated = false, probability = 0.6 },
            MarketRegime = new { regimeType = "TrendingUp" }
        });

        Assert.Contains("\"label\":\"UP\"", answer);
        Assert.Contains("\"validated\":false", answer);
        Assert.Contains("Experimental", answer);
        Assert.DoesNotContain("AnonymousType", answer);
        Assert.DoesNotContain("Backend.Services", answer);
        Assert.DoesNotContain("Master Ensemble", answer);
    }

    [Fact]
    public async Task StreamAi_OversizedUpstreamIsDiscardedForBoundedFallback()
    {
        var oversized = new string('x', AiChatController.MaxBufferedSseChars + 1);
        var sse = $"data: {{\"token\":\"{oversized}\",\"done\":false}}\n\n";
        var controller = CreateController(_ => JsonResponse(HttpStatusCode.OK, sse, "text/event-stream"));

        await controller.StreamAi(new AiChatQueryDto(), default);
        controller.Response.Body.Position = 0;
        var body = await new StreamReader(controller.Response.Body).ReadToEndAsync();

        Assert.True(body.Length < AiChatController.MaxBufferedSseChars);
        Assert.Contains("Experimental", body);
    }

    private static AiChatController CreateController(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("http://ai.test") };
        var controller = new AiChatController(
            new StubContextService(),
            new StubHttpClientFactory(client),
            NullLogger<AiChatController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.Response.Body = new MemoryStream();
        return controller;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body, string mediaType = "application/json") => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType)
    };

    private sealed class StubContextService : IAiContextService
    {
        public Task<FullMarketContextDto> GetFullMarketContextAsync(string symbol = "BTCUSDT", string timeframe = "1h", CancellationToken ct = default) =>
            Task.FromResult(new FullMarketContextDto { Symbol = symbol, Timeframe = timeframe, CurrentPrice = 100 });
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}

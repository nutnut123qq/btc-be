using System.Net;
using System.Text;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class OpenRouterEmbeddingClientTests
{
    [Fact]
    public async Task EmbedAsyncSendsBearerTokenAndOpenAiShape()
    {
        var handler = new CapturingHandler();
        var factory = new StubHttpClientFactory(new HttpClient(handler));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "test-key" })
            .Build();
        var client = new OpenRouterEmbeddingClient(factory, configuration, NullLogger<OpenRouterEmbeddingClient>.Instance);

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.None, result.Error);
        Assert.Equal(768, result.Vector?.Length);
        Assert.Equal("test-key", handler.BearerToken);
        Assert.Equal("nvidia/llama-nemotron-embed-vl-1b-v2:free", handler.Model);
        Assert.Equal(768, handler.Dimensions);
        Assert.Equal(new[] { "bitcoin" }, handler.Input);
    }

    [Fact]
    public void ModelIdDefaultsToFreeNemotronEmbed()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("nvidia/llama-nemotron-embed-vl-1b-v2:free", client.ModelId);
        Assert.Equal(768, client.EmbeddingDimensions);
    }

    [Fact]
    public void ModelAndDimensionsReadFromConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = "k",
                ["OpenRouter:EmbeddingModel"] = "baai/bge-base-en-v1.5",
                ["OpenRouter:EmbeddingDimensions"] = "768"
            })
            .Build();
        var client = new OpenRouterEmbeddingClient(
            new StubHttpClientFactory(new HttpClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)))),
            configuration, NullLogger<OpenRouterEmbeddingClient>.Instance);

        Assert.Equal("baai/bge-base-en-v1.5", client.ModelId);
        Assert.Equal(768, client.EmbeddingDimensions);
    }

    [Fact]
    public async Task MissingApiKeyIsNotConfiguredAndReturnsNotConfigured()
    {
        var client = new OpenRouterEmbeddingClient(
            new StubHttpClientFactory(new HttpClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)))),
            new ConfigurationBuilder().Build(),
            NullLogger<OpenRouterEmbeddingClient>.Instance);

        Assert.False(client.IsConfigured);
        var result = await client.EmbedAsync("bitcoin");
        Assert.Equal(EmbeddingErrorKind.NotConfigured, result.Error);
        Assert.Null(result.Vector);
    }

    [Fact]
    public async Task Http401IsClassifiedAsAuth()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid key\"}}", Encoding.UTF8, "application/json")
        }));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.Auth, result.Error);
        Assert.Null(result.Vector);
    }

    [Fact]
    public async Task Http429IsClassifiedAsRateLimited()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.RateLimited, result.Error);
    }

    [Fact]
    public async Task Http500IsClassifiedAsProviderError()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.ProviderError, result.Error);
    }

    [Fact]
    public async Task TimeoutIsClassifiedAsTimeoutOrNetwork()
    {
        var client = CreateClient(new ThrowingHandler(new TaskCanceledException("timeout")));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.TimeoutOrNetwork, result.Error);
    }

    [Fact]
    public async Task MalformedJsonIsClassifiedAsInvalidResponse()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json")
        }));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.InvalidResponse, result.Error);
        Assert.Null(result.Vector);
    }

    [Fact]
    public async Task WrongDimensionVectorIsClassifiedAsInvalidResponse()
    {
        var values = string.Join(',', Enumerable.Repeat("0.1", 4));
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"data\":[{{\"embedding\":[{values}]}}]}}", Encoding.UTF8, "application/json")
        }));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.InvalidResponse, result.Error);
        Assert.Null(result.Vector);
    }

    private static OpenRouterEmbeddingClient CreateClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenRouter:ApiKey"] = "test-key" })
            .Build();
        return new OpenRouterEmbeddingClient(
            new StubHttpClientFactory(new HttpClient(handler)),
            configuration, NullLogger<OpenRouterEmbeddingClient>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? BearerToken { get; private set; }
        public string? Model { get; private set; }
        public int? Dimensions { get; private set; }
        public string[]? Input { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BearerToken = request.Headers.Authorization?.Parameter;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            Model = doc.RootElement.GetProperty("model").GetString();
            Dimensions = doc.RootElement.GetProperty("dimensions").GetInt32();
            Input = doc.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var values = string.Join(',', Enumerable.Repeat("0", 768));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"data\":[{{\"embedding\":[{values}]}}]}}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class JsonHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw exception;
    }
}

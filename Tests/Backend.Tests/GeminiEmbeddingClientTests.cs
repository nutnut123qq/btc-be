using System.Net;
using System.Text;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class GeminiEmbeddingClientTests
{
    [Fact]
    public async Task EmbedAsyncSendsApiKeyInHeaderNotUrl()
    {
        var handler = new CapturingHandler();
        var factory = new StubHttpClientFactory(new HttpClient(handler));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = "test-api-key" })
            .Build();
        var client = new GeminiEmbeddingClient(factory, configuration, NullLogger<GeminiEmbeddingClient>.Instance);

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.None, result.Error);
        Assert.Equal(768, result.Vector?.Length);
        Assert.Equal("", handler.RequestUri?.Query);
        Assert.Equal("test-api-key", handler.ApiKey);
    }

    [Fact]
    public void ModelIdDefaultsToGeminiEmbedding001()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Equal("gemini-embedding-001", client.ModelId);
    }

    [Fact]
    public void ModelIdReadsGeminiEmbeddingModelConfigAndStripsModelsPrefix()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gemini:ApiKey"] = "k",
                ["Gemini:EmbeddingModel"] = "models/text-embedding-004"
            })
            .Build();
        var client = new GeminiEmbeddingClient(
            new StubHttpClientFactory(new HttpClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)))),
            configuration, NullLogger<GeminiEmbeddingClient>.Instance);

        Assert.Equal("text-embedding-004", client.ModelId);
    }

    [Fact]
    public async Task MissingApiKeyIsNotConfiguredAndReturnsNotConfigured()
    {
        var client = new GeminiEmbeddingClient(
            new StubHttpClientFactory(new HttpClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)))),
            new ConfigurationBuilder().Build(),
            NullLogger<GeminiEmbeddingClient>.Instance);

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
            Content = new StringContent("{\"error\":{\"message\":\"API key invalid\"}}", Encoding.UTF8, "application/json")
        }));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.Auth, result.Error);
        Assert.Null(result.Vector);
    }

    [Fact]
    public async Task Http403IsClassifiedAsAuth()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.Auth, result.Error);
    }

    [Fact]
    public async Task Http429IsClassifiedAsRateLimited()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.RateLimited, result.Error);
        Assert.Null(result.Vector);
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
        var client = CreateClient(new ThrowingHandler(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.TimeoutOrNetwork, result.Error);
    }

    [Fact]
    public async Task NetworkFailureIsClassifiedAsTimeoutOrNetwork()
    {
        var client = CreateClient(new ThrowingHandler(new HttpRequestException("Connection refused")));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.TimeoutOrNetwork, result.Error);
    }

    [Fact]
    public async Task MalformedJsonIsClassifiedAsInvalidResponse()
    {
        var client = CreateClient(new JsonHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("this is not json", Encoding.UTF8, "application/json")
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
            Content = new StringContent($"{{\"embedding\":{{\"values\":[{values}]}}}}", Encoding.UTF8, "application/json")
        }));

        var result = await client.EmbedAsync("bitcoin");

        Assert.Equal(EmbeddingErrorKind.InvalidResponse, result.Error);
        Assert.Null(result.Vector);
    }

    private static GeminiEmbeddingClient CreateClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gemini:ApiKey"] = "test-api-key" })
            .Build();
        return new GeminiEmbeddingClient(
            new StubHttpClientFactory(new HttpClient(handler)),
            configuration, NullLogger<GeminiEmbeddingClient>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.GetValues("x-goog-api-key").Single();
            var values = string.Join(',', Enumerable.Repeat("0", 768));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"embedding\":{{\"values\":[{values}]}}}}", Encoding.UTF8, "application/json")
            });
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

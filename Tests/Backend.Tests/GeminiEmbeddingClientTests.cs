using System.Net;
using System.Text;
using Backend.Services;
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

        var embedding = await client.EmbedAsync("bitcoin");

        Assert.Equal(768, embedding?.Length);
        Assert.Equal("", handler.RequestUri?.Query);
        Assert.Equal("test-api-key", handler.ApiKey);
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
}

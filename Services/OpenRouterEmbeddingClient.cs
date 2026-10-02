using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// OpenRouter embeddings API (OpenAI-compatible <c>POST /api/v1/embeddings</c>).
/// Default model <c>nvidia/llama-nemotron-embed-vl-1b-v2:free</c> is a free-tier model
/// verified to honor <c>dimensions=768</c>, so stored vectors keep satisfying the
/// vector(768) column and its out-of-band trigger.
/// Model id comes from <c>OpenRouter:EmbeddingModel</c> and is written to
/// NewsChunks.EmbeddingModel for provenance; different-model vectors are never mixed.
/// </summary>
public class OpenRouterEmbeddingClient : IEmbeddingClient
{
    internal const string DefaultModelId = "nvidia/llama-nemotron-embed-vl-1b-v2:free";
    internal const int DefaultDimensions = 768;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _apiKey;
    private readonly ILogger<OpenRouterEmbeddingClient> _logger;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
    public string ModelId { get; }
    public int EmbeddingDimensions { get; }

    public OpenRouterEmbeddingClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenRouterEmbeddingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        var configuredKey = configuration["OpenRouter:ApiKey"];
        _apiKey = !string.IsNullOrWhiteSpace(configuredKey)
            ? configuredKey
            : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        var configuredModel = configuration["OpenRouter:EmbeddingModel"]?.Trim();
        ModelId = string.IsNullOrEmpty(configuredModel) ? DefaultModelId : configuredModel;
        EmbeddingDimensions = configuration.GetValue("OpenRouter:EmbeddingDimensions", DefaultDimensions);
        _logger = logger;
    }

    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return EmbeddingResult.Fail(EmbeddingErrorKind.NotConfigured, "No OpenRouter API key is configured.");

        var client = _httpClientFactory.CreateClient("OpenRouterEmbedding");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var body = new EmbedRequest
        {
            Model = ModelId,
            Input = new[] { text },
            Dimensions = EmbeddingDimensions
        };

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync("https://openrouter.ai/api/v1/embeddings", body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            return EmbeddingResult.Fail(EmbeddingErrorKind.TimeoutOrNetwork, $"Request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return EmbeddingResult.Fail(EmbeddingErrorKind.TimeoutOrNetwork, $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return EmbeddingResult.Fail(EmbeddingErrorKind.ProviderError, $"{ex.GetType().Name}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                var detail = $"HTTP {(int)response.StatusCode}: {Truncate(err, 300)}";
                _logger.LogWarning("OpenRouter embed failed: {Status} {Body}", response.StatusCode, err);
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => EmbeddingErrorKind.Auth,
                    HttpStatusCode.TooManyRequests => EmbeddingErrorKind.RateLimited,
                    _ => EmbeddingErrorKind.ProviderError
                };
                return EmbeddingResult.Fail(kind, detail);
            }

            float[]? values;
            try
            {
                var json = await response.Content.ReadFromJsonAsync<EmbedResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
                values = json?.Data is { Length: > 0 } data ? data[0].Embedding : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenRouter embed returned an unreadable payload");
                return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse, $"Unparseable response: {ex.Message}");
            }

            if (values == null)
                return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse, "Response had no data[0].embedding array.");
            if (values.Length != EmbeddingDimensions)
                return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse,
                    $"Unexpected embedding size {values.Length}; expected {EmbeddingDimensions}.");
            if (!EmbeddingResult.IsUsableVector(values, EmbeddingDimensions))
                return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse, "Embedding contains NaN or infinite values.");

            return EmbeddingResult.Ok(values);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed class EmbedRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("input")]
        public string[] Input { get; set; } = Array.Empty<string>();

        [JsonPropertyName("dimensions")]
        public int Dimensions { get; set; }
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("data")]
        public EmbedData[] Data { get; set; } = Array.Empty<EmbedData>();
    }

    private sealed class EmbedData
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}

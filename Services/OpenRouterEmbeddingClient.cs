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
        => (await EmbedBatchAsync(new[] { text }, cancellationToken))[0];

    /// <summary>
    /// Sends the whole batch as one <c>input</c> array — OpenRouter free-tier quotas count
    /// requests, so batching multiplies daily throughput by the batch size.
    /// Elements are mapped back to inputs positionally when the response length matches the
    /// input length, otherwise via each element's <c>index</c> field (OpenAI-compatible shape).
    /// </summary>
    public async Task<EmbeddingResult[]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return FailAll(EmbeddingErrorKind.NotConfigured, "No OpenRouter API key is configured.", texts.Count);

        var client = _httpClientFactory.CreateClient("OpenRouterEmbedding");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var body = new EmbedRequest
        {
            Model = ModelId,
            Input = texts.ToArray(),
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
            return FailAll(EmbeddingErrorKind.TimeoutOrNetwork, $"Request timed out: {ex.Message}", texts.Count);
        }
        catch (HttpRequestException ex)
        {
            return FailAll(EmbeddingErrorKind.TimeoutOrNetwork, $"Network error: {ex.Message}", texts.Count);
        }
        catch (Exception ex)
        {
            return FailAll(EmbeddingErrorKind.ProviderError, $"{ex.GetType().Name}: {ex.Message}", texts.Count);
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
                return FailAll(kind, detail, texts.Count);
            }

            EmbedData[]? items;
            try
            {
                var json = await response.Content.ReadFromJsonAsync<EmbedResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
                items = json?.Data;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenRouter embed returned an unreadable payload");
                return FailAll(EmbeddingErrorKind.InvalidResponse, $"Unparseable response: {ex.Message}", texts.Count);
            }

            if (items is not { Length: > 0 })
                return FailAll(EmbeddingErrorKind.InvalidResponse, "Response had no data array.", texts.Count);

            var positional = items.Length == texts.Count;
            var results = new EmbeddingResult[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                var item = positional ? items[i] : items.FirstOrDefault(d => d.Index == i);
                results[i] = item == null
                    ? EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse, "Response had no data element for this input.")
                    : Validate(item.Embedding);
            }
            return results;
        }
    }

    private EmbeddingResult Validate(float[] values)
    {
        if (values.Length != EmbeddingDimensions)
            return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse,
                $"Unexpected embedding size {values.Length}; expected {EmbeddingDimensions}.");
        if (!EmbeddingResult.IsUsableVector(values, EmbeddingDimensions))
            return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse, "Embedding contains NaN or infinite values.");
        return EmbeddingResult.Ok(values);
    }

    private static EmbeddingResult[] FailAll(EmbeddingErrorKind kind, string detail, int count)
        => Enumerable.Repeat(EmbeddingResult.Fail(kind, detail), count).ToArray();

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
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}

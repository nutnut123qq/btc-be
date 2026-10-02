using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Gemini embedding API (768-dim); stored as PostgreSQL real[] on the backend.
/// Model id comes from <c>Gemini:EmbeddingModel</c> (default "gemini-embedding-001") so the
/// same value drives the request URL and the provenance written to NewsChunks.EmbeddingModel.
/// </summary>
public class GeminiEmbeddingClient : IGeminiEmbeddingClient
{
    internal const string DefaultModelId = "gemini-embedding-001";
    internal const int Dimensions = 768;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _apiKey;
    private readonly ILogger<GeminiEmbeddingClient> _logger;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);
    public string ModelId { get; }
    public int EmbeddingDimensions => Dimensions;

    public GeminiEmbeddingClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GeminiEmbeddingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        var configuredKey = configuration["Gemini:ApiKey"];
        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        _apiKey = !string.IsNullOrWhiteSpace(configuredKey)
            ? configuredKey
            : !string.IsNullOrWhiteSpace(geminiKey)
                ? geminiKey
                : Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        var configuredModel = configuration["Gemini:EmbeddingModel"]?.Trim();
        // Accept "models/<id>" too so the same value works in .env-style config.
        ModelId = string.IsNullOrEmpty(configuredModel)
            ? DefaultModelId
            : configuredModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? configuredModel["models/".Length..]
                : configuredModel;
        _logger = logger;
    }

    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return EmbeddingResult.Fail(EmbeddingErrorKind.NotConfigured, "No Gemini API key is configured.");

        var client = _httpClientFactory.CreateClient("GeminiEmbedding");
        client.DefaultRequestHeaders.Add("x-goog-api-key", _apiKey);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelId}:embedContent";

        var body = new EmbedRequest
        {
            Model = $"models/{ModelId}",
            Content = new EmbedContent { Parts = new[] { new EmbedPart { Text = text } } },
            OutputDimensionality = Dimensions
        };

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(url, body,
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
                _logger.LogWarning("Gemini embed failed: {Status} {Body}", response.StatusCode, err);
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
                values = json?.Embedding?.Values;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gemini embed returned an unreadable payload");
                return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse, $"Unparseable response: {ex.Message}");
            }

            if (values == null)
                return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse, "Response had no embedding.values array.");
            if (values.Length != Dimensions)
                return EmbeddingResult.Fail(EmbeddingErrorKind.InvalidResponse,
                    $"Unexpected embedding size {values.Length}; expected {Dimensions}.");
            if (!EmbeddingResult.IsUsableVector(values, Dimensions))
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

        [JsonPropertyName("content")]
        public EmbedContent Content { get; set; } = null!;

        [JsonPropertyName("outputDimensionality")]
        public int OutputDimensionality { get; set; }
    }

    private sealed class EmbedContent
    {
        [JsonPropertyName("parts")]
        public EmbedPart[] Parts { get; set; } = Array.Empty<EmbedPart>();
    }

    private sealed class EmbedPart
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embedding")]
        public EmbedValues? Embedding { get; set; }
    }

    private sealed class EmbedValues
    {
        [JsonPropertyName("values")]
        public float[] Values { get; set; } = Array.Empty<float>();
    }
}

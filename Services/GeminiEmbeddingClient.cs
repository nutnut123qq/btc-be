using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Services;

/// <summary>
/// Gemini embedding API (768-dim); stored as PostgreSQL real[] on the backend.
/// </summary>
public class GeminiEmbeddingClient : IGeminiEmbeddingClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiEmbeddingClient> _logger;

    public GeminiEmbeddingClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GeminiEmbeddingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Gemini API key not configured; skipping embedding.");
            return null;
        }

        var client = _httpClientFactory.CreateClient("GeminiEmbedding");
        var url =
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:embedContent?key={Uri.EscapeDataString(apiKey)}";

        var body = new EmbedRequest
        {
            Model = "models/gemini-embedding-001",
            Content = new EmbedContent { Parts = new[] { new EmbedPart { Text = text } } },
            OutputDimensionality = 768
        };

        try
        {
            var response = await client.PostAsJsonAsync(url, body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Gemini embed failed: {Status} {Body}", response.StatusCode, err);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<EmbedResponse>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            var values = json?.Embedding?.Values;
            if (values == null || values.Length != 768)
            {
                _logger.LogWarning("Unexpected embedding size: {Len}", values?.Length ?? 0);
                return null;
            }

            return values;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini embed error");
            return null;
        }
    }

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

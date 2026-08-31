using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController : ControllerBase
{
    private static readonly HashSet<string> SupportedSymbols = ["BTC", "BTCUSDT"];
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRagService _ragService;
    private readonly IBinanceKlinesService _binanceKlines;
    private readonly ILogger<AnalysisController> _logger;

    public AnalysisController(
        IHttpClientFactory httpClientFactory,
        IRagService ragService,
        IBinanceKlinesService binanceKlines,
        ILogger<AnalysisController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _ragService = ragService;
        _binanceKlines = binanceKlines;
        _logger = logger;
    }

    [HttpGet]
    [HttpGet("analyze")]
    [HttpGet("bitcoin")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("expensive")]
    public async Task<IActionResult> GetAnalysis([FromQuery] string symbol = "BTCUSDT", CancellationToken cancellationToken = default)
    {
        var cleanSymbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.Trim().ToUpperInvariant();
        if (!SupportedSymbols.Contains(cleanSymbol))
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "UNSUPPORTED_SYMBOL",
                Message = "Only BTC analysis is supported in this research phase.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }

        try
        {
            const string marketSymbol = "BTCUSDT";
            const string baseAsset = "BTC";

            var newsQuery = $"{baseAsset} {marketSymbol} cryptocurrency market news regulation ETF price";
            var newsContext = await _ragService.BuildNewsContextAsync(
                newsQuery,
                topK: 8,
                cancellationToken);

            var techContext = await _binanceKlines.BuildTechSummaryAsync(
                symbol: marketSymbol,
                interval: "1h",
                limit: 48,
                cancellationToken: cancellationToken);

            var client = _httpClientFactory.CreateClient("AIService");
            var requestBody = new AnalyzePayload
            {
                Symbol = baseAsset,
                NewsContext = newsContext,
                TechContext = techContext
            };

            var response = await client.PostAsJsonAsync("/api/analyze", requestBody, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync(cancellationToken);
                return Content(result, "application/json");
            }

            var errBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("AI Service returned {StatusCode}", response.StatusCode);
            var upstreamError = TryParseError(errBody);
            return StatusCode((int)response.StatusCode, new ApiErrorEnvelope
            {
                Code = upstreamError.Code,
                Message = upstreamError.Message,
                Retryable = upstreamError.Retryable,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Cannot reach AI service (HTTP)");
            return StatusCode(StatusCodes.Status502BadGateway, new ApiErrorEnvelope
            {
                Code = "AI_SERVICE_UNAVAILABLE",
                Message = "Cannot reach the AI service.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "AI service request timed out");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ApiErrorEnvelope
            {
                Code = "AI_SERVICE_TIMEOUT",
                Message = "AI service did not respond in time.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Bitcoin analysis pipeline");
            return StatusCode(StatusCodes.Status500InternalServerError, new ApiErrorEnvelope
            {
                Code = "ANALYSIS_PIPELINE_ERROR",
                Message = "Internal error in analysis pipeline.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    internal static (string Code, string Message, bool Retryable) TryParseError(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var code))
            {
                return code.GetString() switch
                {
                    "LLM_NOT_CONFIGURED" => ("LLM_NOT_CONFIGURED", "Tính năng giải thích LLM chưa được cấu hình.", false),
                    "LLM_PROVIDER_UNAVAILABLE" => ("LLM_PROVIDER_UNAVAILABLE", "Dịch vụ giải thích LLM tạm thời không khả dụng.", true),
                    "LLM_PROVIDER_ERROR" => ("LLM_PROVIDER_UNAVAILABLE", "Dịch vụ giải thích LLM tạm thời không khả dụng.", true),
                    _ => ("AI_ANALYSIS_ERROR", "AI analysis failed.", true)
                };
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Upstream body is intentionally not exposed to the browser.
        }

        return ("AI_ANALYSIS_ERROR", "AI analysis failed.", true);
    }

    private sealed class AnalyzePayload
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "BTC";

        [JsonPropertyName("news_context")]
        public string NewsContext { get; set; } = "";

        [JsonPropertyName("tech_context")]
        public string TechContext { get; set; } = "";
    }
}

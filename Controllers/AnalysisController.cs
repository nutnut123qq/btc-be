using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRagService _ragService;
    private readonly IBinanceKlinesService _binanceKlines;
    private readonly ILogger<AnalysisController> _logger;
    private readonly IHostEnvironment _env;

    public AnalysisController(
        IHttpClientFactory httpClientFactory,
        IRagService ragService,
        IBinanceKlinesService binanceKlines,
        ILogger<AnalysisController> logger,
        IHostEnvironment env)
    {
        _httpClientFactory = httpClientFactory;
        _ragService = ragService;
        _binanceKlines = binanceKlines;
        _logger = logger;
        _env = env;
    }

    [HttpGet("bitcoin")]
    public async Task<IActionResult> GetBitcoinAnalysis(CancellationToken cancellationToken)
    {
        try
        {
            var newsContext = await _ragService.BuildNewsContextAsync(
                "Bitcoin BTC cryptocurrency market news regulation ETF price",
                topK: 8,
                cancellationToken);

            var techContext = await _binanceKlines.BuildTechSummaryAsync(
                interval: "1h",
                limit: 48,
                cancellationToken);

            var client = _httpClientFactory.CreateClient("AIService");
            var requestBody = new AnalyzePayload
            {
                Symbol = "BTC",
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
            _logger.LogWarning("AI Service returned {StatusCode}: {Body}", response.StatusCode, errBody);
            return StatusCode((int)response.StatusCode, errBody);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Cannot reach AI service (HTTP)");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                "Cannot reach the AI service. Start it from the ai/ folder (e.g. python main.py on port 8000) "
                + "and check AiService:BaseUrl in appsettings. "
                + (_env.IsDevelopment() ? ex.Message : ""));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "AI service request timed out");
            return StatusCode(
                StatusCodes.Status504GatewayTimeout,
                "AI service did not respond in time (graph + Ollama can be slow). "
                + "Retry, or raise AiService:RequestTimeoutMinutes in appsettings.json (0 = no limit).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Bitcoin analysis pipeline");
            var detail = _env.IsDevelopment() ? ex.ToString() : ex.Message;
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                "Internal error in analysis pipeline (RAG, market data, or AI). "
                + (_env.IsDevelopment() ? detail : "See server logs for details."));
        }
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

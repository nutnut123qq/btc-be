using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PredictionController : ControllerBase
{
    private readonly IWindowDatasetService _windowDataset;
    private readonly AppDbContext _db;
    private readonly HttpClient _aiClient;
    private readonly ILogger<PredictionController> _logger;

    public PredictionController(
        IWindowDatasetService windowDataset,
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<PredictionController> logger)
    {
        _windowDataset = windowDataset;
        _db = db;
        _aiClient = httpClientFactory.CreateClient("AIService");
        _logger = logger;
    }

    [HttpGet("latest")]
    public async Task<ActionResult<object>> GetLatestPrediction(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int windowSize = 5,
        [FromQuery] string horizon = "1h",
        [FromQuery] string? modelName = null,
        CancellationToken cancellationToken = default)
    {
        if (!new[] { 5, 10, 15, 20, 25 }.Contains(windowSize))
            return BadRequest(new ApiErrorEnvelope { Code = "INVALID_WINDOW_SIZE", Message = "windowSize must be 5,10,15,20,25.", Retryable = false, RequestId = HttpContext.TraceIdentifier });
        if (!new[] { "1h", "4h", "1d" }.Contains(horizon))
            return BadRequest(new ApiErrorEnvelope { Code = "INVALID_HORIZON", Message = "horizon must be 1h, 4h, 1d.", Retryable = false, RequestId = HttpContext.TraceIdentifier });

        var featureResult = await _windowDataset.BuildLatestFeatureVectorAsync(symbol, timeframe, windowSize, cancellationToken);
        if (featureResult == null)
            return NotFound(new ApiErrorEnvelope { Code = "NO_FEATURE_VECTOR", Message = "Not enough recent feature data to build vector.", Retryable = true, RequestId = HttpContext.TraceIdentifier });

        var (vector, windowStartMs, windowEndMs) = featureResult.Value;

        var requestBody = new
        {
            symbol,
            timeframe,
            window_size = windowSize,
            horizon,
            feature_vector = vector,
            model_name = modelName
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _aiClient.PostAsync("/api/predict", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("AI predict failed: {Status} {Error}", response.StatusCode, error);
                return StatusCode((int)response.StatusCode, new ApiErrorEnvelope { Code = "AI_PREDICT_ERROR", Message = error, Retryable = true, RequestId = HttpContext.TraceIdentifier });
            }

            var resultJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;

            var prediction = new ModelPrediction
            {
                Symbol = symbol,
                Timeframe = timeframe,
                WindowSize = windowSize,
                Horizon = horizon,
                PredictedLabel = root.GetProperty("label").GetInt32(),
                ProbDown = root.GetProperty("prob_down").GetDouble(),
                ProbSideways = root.GetProperty("prob_sideways").GetDouble(),
                ProbUp = root.GetProperty("prob_up").GetDouble(),
                ModelVersion = root.GetProperty("model_version").GetString() ?? "unknown",
                WindowEndMs = windowEndMs,
                CreatedAtUtc = DateTime.UtcNow,
            };

            _db.ModelPredictions.Add(prediction);
            await _db.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                requestId = HttpContext.TraceIdentifier,
                symbol,
                timeframe,
                windowSize,
                horizon,
                windowStartMs,
                windowEndMs,
                prediction = new
                {
                    label = prediction.PredictedLabel,
                    confidence = root.GetProperty("confidence").GetDouble(),
                    prob_down = prediction.ProbDown,
                    prob_sideways = prediction.ProbSideways,
                    prob_up = prediction.ProbUp,
                    model_version = prediction.ModelVersion,
                    inference_ms = root.GetProperty("inference_ms").GetDouble(),
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI prediction service");
            return StatusCode(502, new ApiErrorEnvelope { Code = "AI_SERVICE_UNAVAILABLE", Message = "Failed to reach AI prediction service.", Retryable = true, RequestId = HttpContext.TraceIdentifier });
        }
    }

    [HttpGet("history")]
    public async Task<ActionResult<object>> GetPredictionHistory(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var items = await _db.ModelPredictions
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Ok(new { symbol, timeframe, count = items.Count, items });
    }

    [HttpGet("models")]
    public async Task<ActionResult<object>> GetAvailableModels(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _aiClient.GetAsync("/api/predict/models", cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch models from AI service");
            return StatusCode(502, new ApiErrorEnvelope { Code = "AI_SERVICE_UNAVAILABLE", Message = "Failed to reach AI service.", Retryable = true, RequestId = HttpContext.TraceIdentifier });
        }
    }
}

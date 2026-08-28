using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IMemoryCache _cache;
    private readonly ILogger<PredictionController> _logger;
    private static readonly TimeSpan LatestTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AccuracyTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ModelsTtl = TimeSpan.FromSeconds(60);

    [ActivatorUtilitiesConstructor]
    public PredictionController(
        IWindowDatasetService windowDataset,
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<PredictionController> logger)
    {
        _windowDataset = windowDataset;
        _db = db;
        _aiClient = httpClientFactory.CreateClient("AIService");
        _cache = cache;
        _logger = logger;
    }

    public PredictionController(
        IWindowDatasetService windowDataset,
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<PredictionController> logger)
        : this(windowDataset, db, httpClientFactory, new MemoryCache(new MemoryCacheOptions()), logger)
    {
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

        var cacheKey = $"pred:latest:{symbol.ToUpperInvariant()}:{timeframe}:{windowSize}:{horizon}:{modelName ?? "default"}";
        if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
        {
            return Ok(cached);
        }

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
            var responseObj = new
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
            };

            _cache.Set(cacheKey, responseObj, LatestTtl);
            return Ok(responseObj);
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

        var formattedItems = items.Select(x =>
        {
            int? actualLabel = null;
            bool? isCorrect = null;
            if (x.TargetReturn.HasValue)
            {
                actualLabel = x.TargetReturn.Value > 0.15 ? 1 : (x.TargetReturn.Value < -0.15 ? -1 : 0);
                isCorrect = x.PredictedLabel == actualLabel.Value;
            }
            return new
            {
                x.Id,
                x.Symbol,
                x.Timeframe,
                x.WindowSize,
                x.Horizon,
                x.PredictedLabel,
                x.ProbDown,
                x.ProbSideways,
                x.ProbUp,
                x.TargetReturn,
                ActualLabel = actualLabel,
                IsCorrect = isCorrect,
                x.ModelVersion,
                x.WindowEndMs,
                x.CreatedAtUtc
            };
        });

        return Ok(new { symbol, timeframe, count = items.Count, items = formattedItems });
    }

    [HttpPost("audit")]
    [Backend.Filters.AdminGuard]
    public async Task<ActionResult<object>> AuditPredictions(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        CancellationToken cancellationToken = default)
    {
        var pending = await _db.ModelPredictions
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.TargetReturn == null)
            .ToListAsync(cancellationToken);

        int evaluatedCount = 0;

        foreach (var p in pending)
        {
            long horizonMs = p.Horizon switch
            {
                "4h" => 4 * 3600 * 1000L,
                "1d" => 24 * 3600 * 1000L,
                _ => 3600 * 1000L
            };

            long targetTimeMs = p.WindowEndMs + horizonMs;

            var entryKline = await _db.Klines.AsNoTracking()
                .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs <= p.WindowEndMs)
                .OrderByDescending(k => k.OpenTimeMs)
                .FirstOrDefaultAsync(cancellationToken);

            var targetKline = await _db.Klines.AsNoTracking()
                .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs >= targetTimeMs)
                .OrderBy(k => k.OpenTimeMs)
                .FirstOrDefaultAsync(cancellationToken);

            if (entryKline != null && targetKline != null && entryKline.Close > 0)
            {
                var retPct = (double)((targetKline.Close - entryKline.Close) / entryKline.Close * 100m);
                p.TargetReturn = retPct;
                evaluatedCount++;
            }
        }

        if (evaluatedCount > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new
        {
            symbol,
            timeframe,
            totalPending = pending.Count,
            evaluatedCount,
            message = $"Audit complete. Evaluated {evaluatedCount} predictions."
        });
    }

    [HttpGet("accuracy")]
    public async Task<ActionResult<object>> GetModelAccuracy(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"pred:accuracy:{symbol.ToUpperInvariant()}:{timeframe}";
        if (_cache.TryGetValue(cacheKey, out object? cached) && cached != null)
        {
            return Ok(cached);
        }

        var items = await _db.ModelPredictions
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .ToListAsync(cancellationToken);

        int total = items.Count;
        var evaluated = items.Where(x => x.TargetReturn.HasValue).ToList();

        int trueCount = 0;
        int falseCount = 0;

        foreach (var p in evaluated)
        {
            int actualLabel = p.TargetReturn!.Value > 0.15 ? 1 : (p.TargetReturn!.Value < -0.15 ? -1 : 0);
            if (p.PredictedLabel == actualLabel)
                trueCount++;
            else
                falseCount++;
        }

        int pendingCount = total - evaluated.Count;
        double winRatePct = evaluated.Count > 0 ? Math.Round((double)trueCount / evaluated.Count * 100.0, 1) : 0;

        var result = new
        {
            symbol,
            timeframe,
            totalPredictions = total,
            evaluatedCount = evaluated.Count,
            trueCount,
            falseCount,
            pendingCount,
            winRatePct
        };

        _cache.Set(cacheKey, result, AccuracyTtl);
        return Ok(result);
    }

    [HttpGet("models")]
    public async Task<ActionResult<object>> GetAvailableModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = "pred:models";
        if (_cache.TryGetValue(cacheKey, out string? cachedJson) && cachedJson != null)
        {
            return Content(cachedJson, "application/json");
        }

        try
        {
            var response = await _aiClient.GetAsync("/api/predict/models", cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            _cache.Set(cacheKey, json, ModelsTtl);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch models from AI service");
            return StatusCode(502, new ApiErrorEnvelope { Code = "AI_SERVICE_UNAVAILABLE", Message = "Failed to reach AI service.", Retryable = true, RequestId = HttpContext.TraceIdentifier });
        }
    }
}

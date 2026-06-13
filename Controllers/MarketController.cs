using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MarketController : ControllerBase
{
    private readonly IBinanceKlinesService _binance;
    private readonly IPatternSearchService _patternSearch;
    private readonly IWindowVectorIndexer _vectorIndexer;
    private readonly ICandlePatternIndexer _patternIndexer;
    private readonly IWindowDatasetService _windowDataset;
    private readonly IMlDatasetService _mlDataset;
    private readonly AppDbContext _db;
    private readonly ILogger<MarketController> _logger;

    public MarketController(
        IBinanceKlinesService binance,
        IPatternSearchService patternSearch,
        IWindowVectorIndexer vectorIndexer,
        ICandlePatternIndexer patternIndexer,
        IWindowDatasetService windowDataset,
        IMlDatasetService mlDataset,
        AppDbContext db,
        ILogger<MarketController> logger)
    {
        _binance = binance;
        _patternSearch = patternSearch;
        _vectorIndexer = vectorIndexer;
        _patternIndexer = patternIndexer;
        _windowDataset = windowDataset;
        _mlDataset = mlDataset;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// BTC/USDT klines from Binance public API (for charts).
    /// </summary>
    [HttpGet("btc/klines")]
    public async Task<ActionResult<IReadOnlyList<KlineDto>>> GetBtcKlines(
        [FromQuery] string interval = "1h",
        [FromQuery] int limit = 48,
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] long? startTimeMs = null,
        [FromQuery] long? endTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
            return BadRequest("limit must be between 1 and 1000.");

        try
        {
            var data = await _binance.GetKlinesAsync(
                symbol: symbol,
                interval: interval,
                limit: limit,
                startTimeMs: startTimeMs,
                endTimeMs: endTimeMs,
                cancellationToken: cancellationToken);
            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Binance klines");
            return StatusCode(502, new ApiErrorEnvelope
            {
                Code = "MARKET_UPSTREAM_ERROR",
                Message = "Failed to fetch market data from Binance.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpGet("candles/around")]
    public async Task<ActionResult<CandlesAroundResponse>> GetCandlesAround(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "15m",
        [FromQuery] long timeMs = 0,
        [FromQuery] int beforeBars = 100,
        [FromQuery] int afterBars = 100,
        CancellationToken cancellationToken = default)
    {
        if (timeMs <= 0)
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_TIME",
                Message = "timeMs must be a positive unix timestamp in milliseconds.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        beforeBars = Math.Clamp(beforeBars, 0, 2000);
        afterBars = Math.Clamp(afterBars, 0, 2000);

        try
        {
            var tfMs = ResolveIntervalMs(timeframe);
            var limit = Math.Clamp(beforeBars + afterBars + 1, 1, 1000);
            var data = await _binance.GetKlinesAsync(
                symbol: symbol,
                interval: timeframe,
                limit: limit,
                startTimeMs: timeMs - (beforeBars * tfMs),
                endTimeMs: timeMs + (afterBars * tfMs),
                cancellationToken: cancellationToken);
            var ordered = data.OrderBy(x => x.OpenTimeMs).ToList();
            var resolved = ordered.Count == 0
                ? (long?)null
                : ordered.OrderBy(x => Math.Abs(x.OpenTimeMs - timeMs)).First().OpenTimeMs;
            return Ok(new CandlesAroundResponse
            {
                RequestId = HttpContext.TraceIdentifier,
                Symbol = symbol,
                Timeframe = timeframe,
                RequestedTimeMs = timeMs,
                ResolvedTimeMs = resolved,
                Candles = ordered
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch around candles");
            return StatusCode(502, new ApiErrorEnvelope
            {
                Code = "MARKET_AROUND_FETCH_FAILED",
                Message = "Failed to fetch candles around target time.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpPost("pattern-search")]
    public async Task<ActionResult<PatternSearchResponse>> PatternSearch(
        [FromBody] PatternSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.WindowSize is < 5 or > 100)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_WINDOW_SIZE",
                Message = "windowSize must be between 5 and 100.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        if (request.TopK is < 1 or > 50)
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_TOPK",
                Message = "topK must be between 1 and 50.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        request.FeatureType = PatternVectorFeatureType.Normalize(request.FeatureType);
        if (string.IsNullOrEmpty(request.FeatureType))
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_FEATURE_TYPE",
                Message = "featureType must be one of: open, high, low, close, all, returns_shape.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }

        try
        {
            var response = await _patternSearch.SearchAsync(request, HttpContext.TraceIdentifier, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pattern search failed");
            return StatusCode(502, new ApiErrorEnvelope
            {
                Code = "PATTERN_SEARCH_FAILED",
                Message = "Pattern search failed.",
                Retryable = true,
                RequestId = HttpContext.TraceIdentifier
            });
        }
    }

    [HttpPost("pattern-index/rebuild")]
    public async Task<ActionResult<object>> RebuildPatternIndex(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "15m",
        [FromQuery] string featureType = "all",
        [FromQuery] int lookbackBars = 5000,
        [FromQuery] int windowSize = 10,
        CancellationToken cancellationToken = default)
    {
        featureType = PatternVectorFeatureType.Normalize(featureType);
        if (string.IsNullOrEmpty(featureType))
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_FEATURE_TYPE",
                Message = "featureType must be one of: open, high, low, close, all, returns_shape.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        var started = DateTime.UtcNow;
        var upserted = await _vectorIndexer.BuildFullAsync(symbol, timeframe, featureType, lookbackBars, windowSize, cancellationToken);
        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            featureType,
            windowSize,
            upserted,
            durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds
        });
    }

    [HttpPost("pattern-index/warmup")]
    public async Task<ActionResult<object>> WarmupPatternIndex(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "15m",
        [FromQuery] int lookbackBars = 5000,
        [FromQuery] int? windowSize = null,
        CancellationToken cancellationToken = default)
    {
        var features = new[] { "open", "high", "low", "close", "all", "returns_shape" };
        var windowSizes = windowSize.HasValue ? new[] { windowSize.Value } : new[] { 5, 10, 15, 20, 25 };
        var total = 0;
        foreach (var ws in windowSizes)
        {
            foreach (var f in features)
            {
                total += await _vectorIndexer.BuildFullAsync(symbol, timeframe, f, lookbackBars, ws, cancellationToken);
            }
        }
        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            windowSizes,
            upserted = total
        });
    }

    [HttpGet("pattern-index/status")]
    public async Task<ActionResult<object>> PatternIndexStatus(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "15m",
        [FromQuery] string featureType = "all",
        [FromQuery] int windowSize = 10,
        CancellationToken cancellationToken = default)
    {
        featureType = PatternVectorFeatureType.Normalize(featureType);
        if (string.IsNullOrEmpty(featureType))
        {
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_FEATURE_TYPE",
                Message = "featureType must be one of: open, high, low, close, all, returns_shape.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });
        }
        var (count, lastUpdatedUtc) = await _vectorIndexer.GetStatusAsync(symbol, timeframe, featureType, windowSize, cancellationToken);
        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            featureType,
            windowSize,
            count,
            lastUpdatedUtc
        });
    }

    [HttpGet("candle-patterns")]
    public async Task<ActionResult<object>> GetCandlePatterns(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] long? fromMs = null,
        [FromQuery] long? toMs = null,
        [FromQuery] string? category = null,
        [FromQuery] string? patternType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Clamp(page, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.CandlePatterns.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe);

        if (fromMs.HasValue)
            query = query.Where(x => x.OpenTimeMs >= fromMs.Value);
        if (toMs.HasValue)
            query = query.Where(x => x.OpenTimeMs <= toMs.Value);
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(x => x.PatternCategory == category);
        if (!string.IsNullOrWhiteSpace(patternType))
            query = query.Where(x => x.PatternType == patternType);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.OpenTimeMs)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            fromMs,
            toMs,
            category,
            patternType,
            page,
            pageSize,
            total,
            items
        });
    }

    [HttpGet("window-dataset")]
    public async Task<ActionResult<object>> GetWindowDataset(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int windowSize = 10,
        [FromQuery] string horizon = "1d",
        [FromQuery] int? label = null,
        [FromQuery] int page = 1,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (!new[] { 5, 10, 15, 20, 25 }.Contains(windowSize))
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_WINDOW_SIZE",
                Message = "windowSize must be one of: 5, 10, 15, 20, 25.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });

        if (!new[] { "1h", "4h", "1d" }.Contains(horizon))
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_HORIZON",
                Message = "horizon must be one of: 1h, 4h, 1d.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });

        page = Math.Clamp(page, 1, 1000);
        take = Math.Clamp(take, 1, 1000);

        var query = _db.WindowClassificationDatasets.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.WindowSize == windowSize && x.Horizon == horizon);

        if (label.HasValue)
            query = query.Where(x => x.Label == label.Value);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.WindowStartMs)
            .Skip((page - 1) * take)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            windowSize,
            horizon,
            label,
            page,
            take,
            total,
            items
        });
    }

    [HttpPost("ml-dataset/build")]
    public async Task<ActionResult<object>> BuildMlDataset(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var count = await _mlDataset.BuildAsync(symbol, timeframe, cancellationToken);
        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            touched = count,
            durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds
        });
    }

    [HttpPost("window-dataset/build")]
    public async Task<ActionResult<object>> BuildWindowDataset(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int windowSize = 10,
        [FromQuery] string horizon = "1d",
        CancellationToken cancellationToken = default)
    {
        if (!new[] { 5, 10, 15, 20, 25 }.Contains(windowSize))
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_WINDOW_SIZE",
                Message = "windowSize must be one of: 5, 10, 15, 20, 25.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });

        if (!new[] { "1h", "4h", "1d" }.Contains(horizon))
            return BadRequest(new ApiErrorEnvelope
            {
                Code = "INVALID_HORIZON",
                Message = "horizon must be one of: 1h, 4h, 1d.",
                Retryable = false,
                RequestId = HttpContext.TraceIdentifier
            });

        var started = DateTime.UtcNow;
        var count = await _windowDataset.BuildAsync(symbol, timeframe, windowSize, horizon, cancellationToken);
        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            windowSize,
            horizon,
            inserted = count,
            durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds
        });
    }

    [HttpPost("candle-patterns/index")]
    public async Task<ActionResult<object>> IndexCandlePatterns(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int lookbackBars = 500,
        CancellationToken cancellationToken = default)
    {
        lookbackBars = Math.Clamp(lookbackBars, 10, 5_000);
        var started = DateTime.UtcNow;
        var indexed = await _patternIndexer.BuildFullAsync(symbol, timeframe, lookbackBars, cancellationToken);
        return Ok(new
        {
            requestId = HttpContext.TraceIdentifier,
            symbol,
            timeframe,
            lookbackBars,
            indexed,
            durationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds
        });
    }

    private static long ResolveIntervalMs(string interval) =>
        interval switch
        {
            "1m" => 60_000L,
            "5m" => 300_000L,
            "15m" => 900_000L,
            "30m" => 1_800_000L,
            "1h" => 3_600_000L,
            "4h" => 14_400_000L,
            "1d" => 86_400_000L,
            _ => 900_000L
        };
}

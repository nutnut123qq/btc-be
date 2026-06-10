using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/discovery")]
public class RuleDiscoveryController : ControllerBase
{
    private readonly IBinanceKlinesService _binance;
    private readonly AppDbContext _db;
    private readonly CandleVolumeIndexer _volumeIndexer;
    private readonly ILogger<RuleDiscoveryController> _logger;

    public RuleDiscoveryController(
        IBinanceKlinesService binance,
        AppDbContext db,
        CandleVolumeIndexer volumeIndexer,
        ILogger<RuleDiscoveryController> logger)
    {
        _binance = binance;
        _db = db;
        _volumeIndexer = volumeIndexer;
        _logger = logger;
    }

    /// <summary>
    /// Chạy Rule Discovery — tự động quét dữ liệu lịch sử để tìm rules có win rate cao.
    /// </summary>
    [HttpPost("run")]
    public async Task<ActionResult<object>> RunDiscovery(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int lookbackBars = 3000,
        [FromQuery] int futureBars = 3,
        [FromQuery] double minWinRate = 0.50,
        [FromQuery] int minSamples = 5,
        [FromQuery] double minAvgReturnPct = 0.1,
        [FromQuery] bool saveToDb = true,
        CancellationToken cancellationToken = default)
    {
        lookbackBars = Math.Clamp(lookbackBars, 200, 5000);
        futureBars = Math.Clamp(futureBars, 1, 20);

        var started = DateTime.UtcNow;
        var klines = await _binance.GetKlinesAsync(symbol, timeframe, lookbackBars, cancellationToken: cancellationToken);
        if (klines.Count < 200)
            return BadRequest(new { message = "Không đủ dữ liệu để discovery (cần ít nhất 200 nến)." });

        // Pre-compute volume stats để lần sau query nhanh
        var indexed = await _volumeIndexer.IndexAsync(symbol, timeframe, klines, cancellationToken);
        var volumeStats = await _volumeIndexer.GetStatsAsync(symbol, timeframe, cancellationToken);

        var candidates = CandleRuleDiscoveryEngine.Discover(
            klines, symbol, timeframe,
            futureBars, minWinRate, minSamples, minAvgReturnPct,
            volumeStats);

        int savedCount = 0;
        if (saveToDb && candidates.Count > 0)
        {
            // Xóa các discovered rules cũ của cặp này để tránh chồng chéo
            var oldAutoRules = await _db.CandleSequenceRules
                .Where(r => r.Symbol == symbol && r.Timeframe == timeframe && r.IsAutoDiscovered)
                .ToListAsync(cancellationToken);

            if (oldAutoRules.Count > 0)
            {
                _db.CandleSequenceRules.RemoveRange(oldAutoRules);
            }

            foreach (var c in candidates)
            {
                _db.CandleSequenceRules.Add(new CandleSequenceRule
                {
                    Name = c.Name,
                    Description = $"{c.Description} | WinRate={c.WinRate:P1} AvgRet={c.AvgReturnPct:F2}% Samples={c.SampleCount} PF={c.ProfitFactor:F2}",
                    Symbol = symbol,
                    Timeframe = timeframe,
                    RequiredBars = c.RequiredBars,
                    IsEnabled = true,
                    CooldownMinutes = 60,
                    ConditionsJson = CandleSequenceRuleMappers.SerializeConditions(c.Conditions),
                    Action = "ALERT",
                    Priority = 0,
                    IsAutoDiscovered = true,
                    WinRate = c.WinRate,
                    AvgReturn = c.AvgReturnPct,
                    SampleCount = c.SampleCount,
                    CreatedAtUtc = DateTime.UtcNow
                });
                savedCount++;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        var latencyMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        _logger.LogInformation(
            "discovery_done symbol={Symbol} timeframe={Timeframe} bars={Bars} candidates={Candidates} saved={Saved} latencyMs={LatencyMs}",
            symbol, timeframe, klines.Count, candidates.Count, savedCount, latencyMs);

        return Ok(new
        {
            symbol,
            timeframe,
            lookbackBars,
            futureBars,
            barsAnalyzed = klines.Count,
            candidatesFound = candidates.Count,
            savedToDb = savedCount,
            latencyMs,
            rules = candidates.Select(c => new
            {
                c.Name,
                c.Description,
                c.WinRate,
                c.AvgReturnPct,
                c.ProfitFactor,
                c.SampleCount,
                c.MaxDrawdownPct,
                conditions = c.Conditions
            })
        });
    }

    /// <summary>
    /// Lấy danh sách rules đã được tự động phát hiện.
    /// </summary>
    [HttpGet("rules")]
    public async Task<ActionResult<object>> GetDiscoveredRules(
        [FromQuery] string? symbol = null,
        [FromQuery] string? timeframe = null,
        CancellationToken cancellationToken = default)
    {
        var q = _db.CandleSequenceRules
            .AsNoTracking()
            .Where(r => r.IsAutoDiscovered)
            .OrderByDescending(r => r.WinRate * r.AvgReturn)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(symbol)) q = q.Where(r => r.Symbol == symbol);
        if (!string.IsNullOrWhiteSpace(timeframe)) q = q.Where(r => r.Timeframe == timeframe);

        var items = await q.ToListAsync(cancellationToken);
        return Ok(items);
    }

    /// <summary>
    /// Pre-compute volume stats cho chuỗi nến.
    /// </summary>
    [HttpPost("index-volume")]
    public async Task<ActionResult<object>> IndexVolume(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int lookbackBars = 2000,
        CancellationToken cancellationToken = default)
    {
        lookbackBars = Math.Clamp(lookbackBars, 100, 5000);
        var klines = await _binance.GetKlinesAsync(symbol, timeframe, lookbackBars, cancellationToken: cancellationToken);
        var indexed = await _volumeIndexer.IndexAsync(symbol, timeframe, klines, cancellationToken);
        return Ok(new { symbol, timeframe, bars = klines.Count, indexed });
    }

    /// <summary>
    /// Lấy volume stats đã indexed.
    /// </summary>
    [HttpGet("volume-stats")]
    public async Task<ActionResult<object>> GetVolumeStats(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var items = await _db.CandleVolumeStats
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .OrderByDescending(x => x.OpenTimeMs)
            .Take(take)
            .ToListAsync(cancellationToken);
        return Ok(new { symbol, timeframe, count = items.Count, items });
    }

    /// <summary>
    /// Evaluate discovered rules trên dữ liệu nến hiện tại.
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<ActionResult<object>> Evaluate(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "1h",
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 10, 200);
        var klines = await _binance.GetKlinesAsync(symbol, timeframe, limit, cancellationToken: cancellationToken);
        if (klines.Count == 0)
            return BadRequest(new { message = "No kline data available." });

        var engine = HttpContext.RequestServices.GetRequiredService<ICandleSequenceRulesEngine>();
        var signals = await engine.EvaluateAsync(symbol, timeframe, klines, cancellationToken);
        return Ok(new { symbol, timeframe, bars = klines.Count, signals });
    }

    /// <summary>
    /// Xóa tất cả discovered rules.
    /// </summary>
    [HttpPost("clear")]
    public async Task<IActionResult> ClearDiscoveredRules(CancellationToken cancellationToken)
    {
        var rules = await _db.CandleSequenceRules.Where(r => r.IsAutoDiscovered).ToListAsync(cancellationToken);
        if (rules.Count > 0)
        {
            _db.CandleSequenceRules.RemoveRange(rules);
            await _db.SaveChangesAsync(cancellationToken);
        }
        return Ok(new { removed = rules.Count });
    }
}

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
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public RuleDiscoveryController(
        IBinanceKlinesService binance,
        AppDbContext db,
        CandleVolumeIndexer volumeIndexer,
        ILogger<RuleDiscoveryController> logger,
        ProductionTimeframePolicy? timeframePolicy = null)
    {
        _binance = binance;
        _db = db;
        _volumeIndexer = volumeIndexer;
        _logger = logger;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
    }

    /// <summary>
    /// Chạy bounded rule discovery với selection/OOS split theo thời gian và ghi đầy đủ trial ledger.
    /// </summary>
    [HttpPost("run")]
    [Backend.Filters.AdminGuard]
    public async Task<ActionResult<object>> RunDiscovery(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "4h",
        [FromQuery] int lookbackBars = 3000,
        [FromQuery] int futureBars = 3,
        [FromQuery] double minWinRate = 0.50,
        [FromQuery] int minSamples = 30,
        [FromQuery] double minAvgReturnPct = 0,
        [FromQuery] int candidateBudget = 128,
        [FromQuery] double selectionFraction = 0.70,
        [FromQuery] double labelDeadZonePct = 0.30,
        [FromQuery] double roundTripCostBps = 30,
        [FromQuery] bool saveToDb = true,
        CancellationToken cancellationToken = default)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));

        lookbackBars = Math.Clamp(lookbackBars, 200, 5000);
        futureBars = Math.Clamp(futureBars, 1, 20);
        minSamples = Math.Clamp(minSamples, 10, 1000);
        candidateBudget = Math.Clamp(candidateBudget, 1, 500);
        selectionFraction = Math.Clamp(selectionFraction, 0.55, 0.85);
        labelDeadZonePct = Math.Clamp(labelDeadZonePct, 0, 10);
        roundTripCostBps = Math.Clamp(roundTripCostBps, 0, 1000);

        var started = DateTime.UtcNow;
        var fetchedKlines = await _binance.GetKlinesAsync(symbol, timeframe, lookbackBars, cancellationToken: cancellationToken);
        var decisionTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var klines = fetchedKlines.Where(k => k.CloseTimeMs > 0 && k.CloseTimeMs <= decisionTimeMs).ToList();
        if (klines.Count < 200)
            return BadRequest(new { message = "Không đủ dữ liệu để discovery (cần ít nhất 200 nến)." });

        // Pre-compute volume stats để lần sau query nhanh
        var indexed = await _volumeIndexer.IndexAsync(symbol, timeframe, klines, cancellationToken);
        var volumeStats = await _volumeIndexer.GetStatsAsync(symbol, timeframe, cancellationToken);

        var discoveryOptions = new CandleRuleDiscoveryEngine.DiscoveryOptions
        {
            FutureBars = futureBars,
            CandidateBudget = candidateBudget,
            SelectionFraction = selectionFraction,
            LabelDeadZonePct = labelDeadZonePct,
            RoundTripCostBps = roundTripCostBps,
            MinWinRate = minWinRate,
            MinSelectionSamples = minSamples,
            MinEvaluationSamples = minSamples,
            // Kept query name for API compatibility; v2 applies it to return after explicit costs.
            MinNetAvgReturnPct = minAvgReturnPct
        };
        var discovery = CandleRuleDiscoveryEngine.DiscoverWithLedger(
            klines, symbol, timeframe, discoveryOptions, volumeStats);
        var candidates = discovery.SelectedRules;

        int savedCount = 0;
        long? runId = null;
        if (saveToDb)
        {
            var run = new RuleDiscoveryRun
            {
                MethodVersion = discovery.Method,
                Symbol = symbol,
                Timeframe = timeframe,
                FutureBars = futureBars,
                CandidateBudget = discovery.CandidateBudget,
                TrialCount = discovery.TrialCount,
                LabelDeadZonePct = discovery.LabelDeadZonePct,
                RoundTripCostBps = discovery.RoundTripCostBps,
                SelectionStartTimeMs = discovery.SelectionStartTimeMs,
                SelectionEndTimeMs = discovery.SelectionEndTimeMs,
                EvaluationStartTimeMs = discovery.EvaluationStartTimeMs,
                EvaluationEndTimeMs = discovery.EvaluationEndTimeMs
            };
            _db.RuleDiscoveryRuns.Add(run);
            await _db.SaveChangesAsync(cancellationToken);
            runId = run.Id;

            foreach (var trial in discovery.Trials)
            {
                _db.RuleDiscoveryTrials.Add(new RuleDiscoveryTrial
                {
                    RunId = run.Id,
                    TrialNumber = trial.TrialNumber,
                    CandidateKey = trial.CandidateKey,
                    ConditionsJson = CandleSequenceRuleMappers.SerializeConditions(trial.Conditions),
                    Status = trial.Status,
                    RejectedReason = trial.RejectedReason,
                    SelectionSampleCount = trial.Selection?.SampleCount ?? 0,
                    SelectionWinRate = trial.Selection?.WinRate,
                    SelectionNetAvgReturnPct = trial.Selection?.NetAvgReturnPct,
                    EvaluationSampleCount = trial.Evaluation?.SampleCount ?? 0,
                    EvaluationWinRate = trial.Evaluation?.WinRate,
                    EvaluationWinRateCi95Low = trial.Evaluation?.WinRateCi95Low,
                    EvaluationWinRateCi95High = trial.Evaluation?.WinRateCi95High,
                    BaselineWinRate = trial.Evaluation?.BaselineWinRate,
                    OosLift = trial.Evaluation?.OosLift,
                    EvaluationNetAvgReturnPct = trial.Evaluation?.NetAvgReturnPct
                });
            }

            // Old search results remain auditable but may not keep producing alerts.
            var oldAutoRules = await _db.CandleSequenceRules
                .Where(r => r.Symbol == symbol && r.Timeframe == timeframe && r.IsAutoDiscovered && r.IsEnabled)
                .ToListAsync(cancellationToken);
            foreach (var oldRule in oldAutoRules)
            {
                oldRule.IsEnabled = false;
                oldRule.CapabilityState = "retired";
                oldRule.UpdatedAtUtc = DateTime.UtcNow;
            }

            foreach (var trial in discovery.Trials.Where(x => x.Status == "selected" && x.Evaluation is not null))
            {
                var c = trial.Evaluation!;
                _db.CandleSequenceRules.Add(new CandleSequenceRule
                {
                    Name = c.Name,
                    Description = $"{c.Description} | WinRate={c.WinRate:P1} AvgRet={c.AvgReturnPct:F2}% Samples={c.SampleCount} PF={c.ProfitFactor:F2}",
                    Symbol = symbol,
                    Timeframe = timeframe,
                    RequiredBars = c.RequiredBars,
                    // OOS selection alone is development evidence, not a validated predictive alert.
                    IsEnabled = false,
                    CooldownMinutes = 60,
                    ConditionsJson = CandleSequenceRuleMappers.SerializeConditions(c.Conditions),
                    Action = "ALERT",
                    Priority = 0,
                    IsAutoDiscovered = true,
                    WinRate = c.WinRate,
                    AvgReturn = c.AvgReturnPct,
                    SampleCount = c.SampleCount,
                    CapabilityState = "experimental",
                    MethodVersion = discovery.Method,
                    DiscoveryRunId = run.Id,
                    SelectionStartTimeMs = discovery.SelectionStartTimeMs,
                    SelectionEndTimeMs = discovery.SelectionEndTimeMs,
                    EvaluationStartTimeMs = discovery.EvaluationStartTimeMs,
                    EvaluationEndTimeMs = discovery.EvaluationEndTimeMs,
                    SelectionSampleCount = trial.Selection?.SampleCount ?? 0,
                    OosSampleCount = c.SampleCount,
                    OosWinRate = c.WinRate,
                    OosWinRateCi95Low = c.WinRateCi95Low,
                    OosWinRateCi95High = c.WinRateCi95High,
                    BaselineWinRate = c.BaselineWinRate,
                    OosLift = c.OosLift,
                    OosGrossAvgReturnPct = c.AvgReturnPct,
                    OosNetAvgReturnPct = c.NetAvgReturnPct,
                    LabelDeadZonePct = discovery.LabelDeadZonePct,
                    RoundTripCostBps = discovery.RoundTripCostBps,
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
            method = discovery.Method,
            runId,
            discovery.CandidateBudget,
            discovery.TrialCount,
            selectionInterval = new { startTimeMs = discovery.SelectionStartTimeMs, endTimeMs = discovery.SelectionEndTimeMs },
            evaluationInterval = new { startTimeMs = discovery.EvaluationStartTimeMs, endTimeMs = discovery.EvaluationEndTimeMs },
            discovery.LabelDeadZonePct,
            discovery.RoundTripCostBps,
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
                c.NetAvgReturnPct,
                c.ProfitFactor,
                c.SampleCount,
                c.WinRateCi95Low,
                c.WinRateCi95High,
                c.MaxDrawdownPct,
                c.BaselineWinRate,
                c.OosLift,
                conditions = c.Conditions
            }),
            rejected = discovery.Trials.Count(x => x.Status == "rejected"),
            trialLedger = discovery.Trials.Select(x => new
            {
                x.TrialNumber,
                x.CandidateKey,
                x.Status,
                x.RejectedReason,
                selectionSamples = x.Selection?.SampleCount ?? 0,
                evaluationSamples = x.Evaluation?.SampleCount ?? 0
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
        if (!string.IsNullOrWhiteSpace(timeframe))
        {
            timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
            q = q.Where(r => r.Timeframe == timeframe);
        }

        var items = await q.ToListAsync(cancellationToken);
        return Ok(items);
    }

    /// <summary>
    /// Pre-compute volume stats cho chuỗi nến.
    /// </summary>
    [HttpPost("index-volume")]
    [Backend.Filters.AdminGuard]
    public async Task<ActionResult<object>> IndexVolume(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "4h",
        [FromQuery] int lookbackBars = 2000,
        CancellationToken cancellationToken = default)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));

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
        [FromQuery] string timeframe = "4h",
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
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
    [Backend.Filters.AdminGuard]
    public async Task<ActionResult<object>> Evaluate(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string timeframe = "4h",
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        timeframe = ProductionTimeframePolicy.Canonicalize(timeframe);
        if (!_timeframePolicy.IsActive(timeframe))
            return BadRequest(ProductionTimeframeApiError.Create(_timeframePolicy, timeframe, HttpContext.TraceIdentifier));

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
    [Backend.Filters.AdminGuard]
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

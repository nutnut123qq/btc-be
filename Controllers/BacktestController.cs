using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BacktestController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(AppDbContext db, ILogger<BacktestController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("runs")]
    public async Task<ActionResult<object>> GetRuns(
        [FromQuery] string symbol = "BTCUSDT",
        [FromQuery] string? timeframe = null,
        [FromQuery] int take = 50,
        [FromQuery] bool includeLegacy = false,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 200);
        var query = _db.BacktestRuns.AsNoTracking().Where(x => x.Symbol == symbol);
        if (!string.IsNullOrWhiteSpace(timeframe))
            query = query.Where(x => x.Timeframe == timeframe);
        if (!includeLegacy)
            query = query.Where(x => x.ValidityStatus == ValidityStatuses.Valid && x.ArchivedAtUtc == null);

        var items = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.Symbol,
                x.Timeframe,
                x.WindowSize,
                x.Horizon,
                x.ModelName,
                x.StartTimeMs,
                x.EndTimeMs,
                x.TotalTrades,
                x.WinRate,
                x.TotalReturnPct,
                x.BuyHoldReturnPct,
                x.MaxDrawdownPct,
                x.SharpeRatio,
                x.ProfitFactor,
                x.FinalEquity,
                x.PipelineVersion,
                x.EvaluationVersion,
                x.ValidityStatus,
                x.InvalidReason,
                x.ArchivedAtUtc,
                x.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { symbol, timeframe, count = items.Count, items });
    }

    [HttpGet("runs/{id:int}")]
    public async Task<ActionResult<object>> GetRunDetail(
        int id,
        [FromQuery] bool includeLegacy = false,
        CancellationToken cancellationToken = default)
    {
        var run = await _db.BacktestRuns
            .AsNoTracking()
            .Include(x => x.Trades.OrderBy(t => t.EntryTimeMs).Take(1000))
            .FirstOrDefaultAsync(x => x.Id == id
                && (includeLegacy || (x.ValidityStatus == ValidityStatuses.Valid && x.ArchivedAtUtc == null)), cancellationToken);

        if (run == null)
            return NotFound(new ApiErrorEnvelope { Code = "BACKTEST_NOT_FOUND", Message = $"Backtest run {id} not found.", Retryable = false, RequestId = HttpContext.TraceIdentifier });

        return Ok(new
        {
            run.Id,
            run.Symbol,
            run.Timeframe,
            run.WindowSize,
            run.Horizon,
            run.ModelName,
            run.StartTimeMs,
            run.EndTimeMs,
            run.FeeBps,
            run.SlippageBps,
            run.TotalTrades,
            run.WinRate,
            run.TotalReturnPct,
            run.BuyHoldReturnPct,
            run.MaxDrawdownPct,
            run.SharpeRatio,
            run.SortinoRatio,
            run.ProfitFactor,
            run.FinalEquity,
            run.MetricsJson,
            run.EquityCurveJson,
            run.PipelineVersion,
            run.EvaluationVersion,
            run.ValidityStatus,
            run.InvalidReason,
            run.ArchivedAtUtc,
            run.CreatedAtUtc,
            trades = run.Trades.Select(t => new
            {
                t.Id,
                t.EntryTimeMs,
                t.ExitTimeMs,
                t.Side,
                t.EntryPrice,
                t.ExitPrice,
                t.PnlPct,
                t.Confidence,
                t.TrueLabel,
            })
        });
    }

    [HttpPost("classify-records")]
    [Backend.Filters.AdminGuard]
    public async Task<ActionResult<object>> ClassifyRecords(
        [FromQuery] bool apply = false,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var runs = await _db.BacktestRuns.Include(x => x.Trades).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var predictions = await _db.ModelPredictions.OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var ensembles = await _db.EnsemblePredictionRecords.OrderBy(x => x.Id).ToListAsync(cancellationToken);

        var runProposals = runs.Select(run => (Record: run, Result: ResearchRecordClassifier.Classify(run))).ToList();
        var validPredictionFirstIds = predictions
            .Where(x => x.ValidityStatus != ValidityStatuses.Invalid)
            .Where(ResearchRecordClassifier.IsStructurallyValid)
            .GroupBy(x => new { x.Symbol, x.Timeframe, x.WindowSize, x.Horizon, x.WindowEndMs, x.ModelVersion })
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id).First().Id);
        var predictionProposals = predictions.Select(item =>
        {
            var key = new { item.Symbol, item.Timeframe, item.WindowSize, item.Horizon, item.WindowEndMs, item.ModelVersion };
            var duplicate = ResearchRecordClassifier.IsStructurallyValid(item)
                && validPredictionFirstIds.TryGetValue(key, out var firstId)
                && item.Id != firstId;
            return (Record: item, Result: ResearchRecordClassifier.Classify(item, duplicate));
        }).ToList();
        var structurallyValidFirstIds = ensembles
            .Where(x => x.ValidityStatus != ValidityStatuses.Invalid && x.SourcePredictionId == null)
            .Where(ResearchRecordClassifier.IsStructurallyValid)
            .GroupBy(x => new { x.Symbol, x.Timeframe, x.TimeMs })
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id).First().Id);
        var ensembleProposals = ensembles.Select(item =>
        {
            var key = new { item.Symbol, item.Timeframe, item.TimeMs };
            var duplicate = item.SourcePredictionId == null
                && ResearchRecordClassifier.IsStructurallyValid(item)
                && structurallyValidFirstIds.TryGetValue(key, out var firstId)
                && item.Id != firstId;
            return (Record: item, Result: ResearchRecordClassifier.Classify(item, duplicate));
        }).ToList();

        if (apply)
        {
            foreach (var proposal in runProposals)
                Apply(proposal.Record, proposal.Result.Status, proposal.Result.Reason, now);
            foreach (var proposal in predictionProposals)
                Apply(proposal.Record, proposal.Result.Status, proposal.Result.Reason, now);
            foreach (var proposal in ensembleProposals)
                Apply(proposal.Record, proposal.Result.Status, proposal.Result.Reason, now);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Ok(new
        {
            dryRun = !apply,
            backtests = Summarize(runProposals.Select(x => ((long)x.Record.Id, x.Record.ValidityStatus, x.Result.Status, x.Result.Reason))),
            predictions = Summarize(predictionProposals.Select(x => ((long)x.Record.Id, x.Record.ValidityStatus, x.Result.Status, x.Result.Reason))),
            ensembles = Summarize(ensembleProposals.Select(x => (x.Record.Id, x.Record.ValidityStatus, x.Result.Status, x.Result.Reason)))
        });
    }

    private static object Summarize(IEnumerable<(long Id, string Current, string Proposed, string? Reason)> source)
    {
        var items = source.OrderBy(x => x.Id).ToList();
        return new
        {
            total = items.Count,
            valid = items.Count(x => x.Proposed == ValidityStatuses.Valid),
            legacy = items.Count(x => x.Proposed == ValidityStatuses.Legacy),
            invalid = items.Count(x => x.Proposed == ValidityStatuses.Invalid),
            items = items.Select(x => new { x.Id, currentStatus = x.Current, proposedStatus = x.Proposed, x.Reason })
        };
    }

    private static void Apply(BacktestRun record, string status, string? reason, DateTime now)
    {
        record.ValidityStatus = status;
        record.InvalidReason = reason;
        record.ArchivedAtUtc = status == ValidityStatuses.Invalid ? record.ArchivedAtUtc ?? now : null;
    }

    private static void Apply(ModelPrediction record, string status, string? reason, DateTime now)
    {
        record.ValidityStatus = status;
        record.InvalidReason = reason;
        record.ArchivedAtUtc = status == ValidityStatuses.Invalid ? record.ArchivedAtUtc ?? now : null;
    }

    private static void Apply(EnsemblePredictionRecord record, string status, string? reason, DateTime now)
    {
        record.ValidityStatus = status;
        record.InvalidReason = reason;
        record.ArchivedAtUtc = status == ValidityStatuses.Invalid ? record.ArchivedAtUtc ?? now : null;
    }

}

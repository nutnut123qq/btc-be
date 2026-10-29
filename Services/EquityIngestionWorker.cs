using System.Collections.Concurrent;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>Per-symbol DATA-plane status of the equity lane (EQX spec §4).</summary>
public static class EquityIngestionStatuses
{
    /// <summary>The session's 1d bar is stored and the symbol's pipeline completed for it.</summary>
    public const string UpToDate = "up-to-date";

    /// <summary>The provider has not served the session's finalized bar yet; retried next tick.</summary>
    public const string PendingProvider = "pending-provider";

    /// <summary>The last attempt failed (provider/recompute/observe error); retried next tick.</summary>
    public const string Failed = "failed";

    /// <summary>MaxAttemptsPerSession exhausted for this session; no more retries until the next one.</summary>
    public const string GaveUp = "gave-up";

    /// <summary>Worker or provider lane switched off by configuration.</summary>
    public const string Disabled = "disabled";
}

/// <summary>Last known worker state for one symbol (in-process; derived again after restart).</summary>
public sealed record EquityIngestionSymbolState(
    string Symbol,
    string Status,
    long? SessionOpenTimeMs,
    int Attempts,
    int MaxAttempts,
    DateTime? LastAttemptAtUtc,
    DateTime? LastCompletedAtUtc,
    string? Detail);

/// <summary>
/// Singleton holder of per-symbol data-plane status. Deliberately separate from
/// <see cref="WorkerHeartbeatStore"/> (process liveness): a provider that is late or a symbol
/// that gave up must never read as a dead worker (AGENTS pitfall #40).
/// </summary>
public sealed class EquityIngestionStatusStore
{
    private readonly ConcurrentDictionary<string, EquityIngestionSymbolState> _states = new(StringComparer.Ordinal);
    private volatile bool _workerActive;

    /// <summary>True while an enabled <see cref="EquityIngestionWorker"/> runs in this process.</summary>
    public bool WorkerActive => _workerActive;

    public void SetWorkerActive(bool active) => _workerActive = active;

    public EquityIngestionSymbolState? Get(string symbol) =>
        _states.TryGetValue(symbol, out var state) ? state : null;

    public void Set(EquityIngestionSymbolState state) => _states[state.Symbol] = state;

    public IReadOnlyCollection<EquityIngestionSymbolState> Snapshot() => _states.Values.ToArray();
}

/// <summary>
/// Seam (EQX §4/§5/§8): runs after a Decision symbol completed sync → recompute → observe for a
/// session. The production implementation is <see cref="EquitySessionPostProcessor"/>
/// (base-rates rebuild → quant forecast → outcome resolution → context sync).
/// </summary>
public interface IEquitySessionPostProcessor
{
    /// <summary>Runs the post-observe steps; returns a short detail for the worker status (never throws for a step).</summary>
    Task<string?> RunAsync(InstrumentDescriptor instrument, long sessionOpenTimeMs, CancellationToken cancellationToken);
}

public sealed class NoOpEquitySessionPostProcessor : IEquitySessionPostProcessor
{
    public Task<string?> RunAsync(InstrumentDescriptor instrument, long sessionOpenTimeMs, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

public enum EquityLaneRunOutcome
{
    Completed,
    PendingProvider,
    Failed,
    Disabled,
}

public sealed record EquityLaneRunResult(EquityLaneRunOutcome Outcome, string? Detail = null);

/// <summary>One symbol's per-session work (sync → recompute → observe → post-process).</summary>
public interface IEquityLanePipeline
{
    Task<EquityLaneRunResult> RunAsync(InstrumentDescriptor instrument, long sessionOpenTimeMs, CancellationToken cancellationToken);
}

/// <summary>
/// Real pipeline over the shared lane services, sequential and bounded like the admin
/// endpoints: sync (all the symbol's timeframes, request-bounded) → if TechnicalAnalysis,
/// recompute each timeframe that got new/revised bars (or whose indicators lag the stored
/// candles, e.g. after a restart) → if Decision, observe every journaled timeframe
/// (<see cref="MbbSignalObservationService.JournalTimeframes"/>; latest finalized bar only,
/// never a backfill) → post-process seam.
/// </summary>
public sealed class EquityLanePipeline : IEquityLanePipeline
{
    private readonly AppDbContext _db;
    private readonly MbbIngestionService _ingestion;
    private readonly MbbTechnicalRecomputeService _recompute;
    private readonly MbbSignalObservationService _observations;
    private readonly IEquitySessionPostProcessor _postProcessor;

    public EquityLanePipeline(
        AppDbContext db,
        MbbIngestionService ingestion,
        MbbTechnicalRecomputeService recompute,
        MbbSignalObservationService observations,
        IEquitySessionPostProcessor? postProcessor = null)
    {
        _db = db;
        _ingestion = ingestion;
        _recompute = recompute;
        _observations = observations;
        _postProcessor = postProcessor ?? new NoOpEquitySessionPostProcessor();
    }

    public async Task<EquityLaneRunResult> RunAsync(
        InstrumentDescriptor instrument, long sessionOpenTimeMs, CancellationToken cancellationToken)
    {
        var symbol = instrument.Symbol;
        var sync = await _ingestion.ForSymbol(symbol).SyncAsync(cancellationToken);
        if (sync.Status == MbbSyncStatuses.Disabled)
            return new(EquityLaneRunOutcome.Disabled, "MbbIngestion:Enabled=false - provider calls are off");

        var errors = sync.Timeframes.Where(t => t.Error is not null)
            .Select(t => $"{t.Timeframe}: {t.Error}").ToArray();
        var hasSessionBar = await _db.InstrumentCandles.AsNoTracking()
            .AnyAsync(c => c.Symbol == symbol
                           && c.Timeframe == EquityCandleTimeframes.Day
                           && c.OpenTimeMs == sessionOpenTimeMs, cancellationToken);
        if (errors.Length > 0)
            return new(EquityLaneRunOutcome.Failed, "sync: " + string.Join("; ", errors));
        if (!hasSessionBar)
            return new(EquityLaneRunOutcome.PendingProvider,
                $"1d bar {sessionOpenTimeMs} not served by the provider yet");

        var steps = new List<string> { $"sync {sync.Status}" };
        if (EquityScope.Has(instrument, EquityCapability.TechnicalAnalysis))
        {
            foreach (var tf in instrument.ActiveTimeframes)
            {
                var tfSync = sync.Timeframes.FirstOrDefault(t => t.Timeframe == tf);
                var changed = tfSync is not null && tfSync.InsertedBars + tfSync.UpdatedBars > 0;
                if (!changed && !await IndicatorsLagCandlesAsync(symbol, tf, cancellationToken))
                    continue;
                var report = await _recompute.RecomputeAsync(symbol, tf, cancellationToken);
                if (report.Status == MbbRecomputeStatuses.Partial)
                    return new(EquityLaneRunOutcome.Failed,
                        $"recompute {tf} partial: " + string.Join(", ", report.Modules
                            .Where(m => m.Status == MbbRecomputeModuleStatuses.Failed).Select(m => m.Module)));
                steps.Add($"recompute {tf} {report.Status}");
            }
        }

        if (EquityScope.Has(instrument, EquityCapability.Decision))
        {
            // Journaled timeframes only: an active-but-unjournaled timeframe (BID 1h, EQTECH-1) would
            // violate CK_MbbSignalObservations_Scope and fail the whole symbol, post-processing included.
            foreach (var tf in MbbSignalObservationService.JournalTimeframes(instrument))
            {
                var observed = await _observations.RecordAsync(symbol, tf, cancellationToken);
                if (observed.Status == "finalized-bar-not-ingested")
                    return new(EquityLaneRunOutcome.PendingProvider,
                        $"observe {tf}: latest finalized bar not ingested yet");
                steps.Add($"observe {tf} {observed.Status}");
            }
            var post = await _postProcessor.RunAsync(instrument, sessionOpenTimeMs, cancellationToken);
            if (!string.IsNullOrWhiteSpace(post)) steps.Add(post);
        }

        return new(EquityLaneRunOutcome.Completed, string.Join("; ", steps));
    }

    private async Task<bool> IndicatorsLagCandlesAsync(string symbol, string timeframe, CancellationToken ct)
    {
        var latestCandle = await _db.InstrumentCandles.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
            .MaxAsync(c => (long?)c.OpenTimeMs, ct);
        if (latestCandle is null) return false;
        var latestIndicator = await _db.TechnicalIndicators.AsNoTracking()
            .Where(t => t.Symbol == symbol && t.Timeframe == timeframe)
            .MaxAsync(t => (long?)t.OpenTimeMs, ct);
        return latestIndicator != latestCandle;
    }
}

/// <summary>
/// Equity lane scheduler (EQX spec §4). Disabled unless EquityIngestion:Enabled=true (and only
/// registered when BackgroundWorkers:Enabled). Each tick targets the latest finalized HOSE
/// session — today's once local time ≥ RunAfterLocal (15:15), otherwise the previous trading
/// session, which also makes the first tick after a restart a recovery run — and walks the
/// configured symbols SEQUENTIALLY through <see cref="IEquityLanePipeline"/>:
/// - a symbol already up-to-date / gave-up for that session is skipped;
/// - a busy lane (<see cref="EquityLaneLocks"/>, e.g. an admin sync in flight) is skipped this tick;
/// - pending-provider / failed count one attempt; at MaxAttemptsPerSession the symbol is gave-up
///   for the session (no unbounded backoff); a new session resets the counter;
/// - one symbol failing never blocks the others.
/// Heartbeat (WorkerHeartbeatStore "EquityIngestionWorker") reports PROCESS liveness only:
/// data-plane problems surface as Partial with per-symbol detail, never as Failed (pitfall #40).
/// </summary>
public sealed class EquityIngestionWorker : BackgroundService
{
    public const string WorkerName = "EquityIngestionWorker";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EquityIngestionOptions _options;
    private readonly EquityIngestionStatusStore _status;
    private readonly VietnamMarketCalendar _calendar;
    private readonly InstrumentCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EquityIngestionWorker> _logger;

    public EquityIngestionWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<EquityIngestionOptions> options,
        EquityIngestionStatusStore status,
        VietnamMarketCalendar calendar,
        InstrumentCatalog? catalog = null,
        TimeProvider? timeProvider = null,
        ILogger<EquityIngestionWorker>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _status = status;
        _calendar = calendar;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityIngestionWorker>.Instance;
    }

    /// <summary>
    /// Configured symbols ∩ catalog instruments with the Ingestion capability, catalog order.
    /// No configured symbols = all Ingestion instruments.
    /// </summary>
    internal IReadOnlyList<InstrumentDescriptor> EffectiveInstruments()
    {
        var configured = (_options.Symbols ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        var wanted = configured.Select(InstrumentCatalog.Canonicalize).ToHashSet(StringComparer.Ordinal);
        return _catalog.Instruments
            .Where(i => (configured.Length == 0 || wanted.Contains(i.Symbol)) && EquityScope.Has(i, EquityCapability.Ingestion))
            .ToArray();
    }

    internal TimeOnly RunAfter() =>
        TimeOnly.TryParseExact(_options.RunAfterLocal, "HH:mm", out var t) ? t : new TimeOnly(15, 15);

    /// <summary>
    /// Latest finalized session the worker should have: today's after RunAfterLocal, else the
    /// last trading session before today (weekend/holiday aware via the calendar).
    /// </summary>
    internal long? TargetSessionOpenMs(long nowMs)
    {
        var local = VietnamMarketCalendar.ToLocal(nowMs);
        var today = DateOnly.FromDateTime(local.DateTime);
        var effectiveNow = TimeOnly.FromDateTime(local.DateTime) >= RunAfter()
            ? nowMs
            : VietnamMarketCalendar.ToUtcMs(today, TimeOnly.MinValue);
        return _calendar.LastExpectedFinalizedOpenUtcMs(EquityCandleTimeframes.Day, effectiveNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("EquityIngestionWorker disabled (EquityIngestion:Enabled=false)");
            _status.SetWorkerActive(false);
            return;
        }

        _status.SetWorkerActive(true);
        var poll = TimeSpan.FromMinutes(Math.Max(1, _options.PollMinutes));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCycleWithHeartbeatAsync(stoppingToken);
                await Task.Delay(poll, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _status.SetWorkerActive(false);
        }
    }

    private async Task RunCycleWithHeartbeatAsync(CancellationToken ct)
    {
        var started = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await WorkerHeartbeatStore.MarkStartedAsync(db, WorkerName, started, ct);
            }
            var report = await RunOnceAsync(ct);
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await WorkerHeartbeatStore.MarkCompletedAsync(db, WorkerName, started,
                    _timeProvider.GetUtcNow().UtcDateTime, report, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Only a crash of the cycle itself (e.g. database unreachable) lands here.
            _logger.LogError(ex, "EquityIngestionWorker cycle crashed");
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await WorkerHeartbeatStore.MarkFailedAsync(db, WorkerName, started,
                    _timeProvider.GetUtcNow().UtcDateTime, ex, ct);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                _logger.LogWarning(inner, "EquityIngestionWorker could not record its failed heartbeat");
            }
        }
    }

    /// <summary>One tick over every effective symbol. Never throws for a symbol's failure.</summary>
    internal async Task<WorkerCycleReport> RunOnceAsync(CancellationToken ct)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var session = TargetSessionOpenMs(nowMs);
        if (session is null)
            return WorkerCycleReport.Idle(detail: "no finalized session in the calendar window");

        var maxAttempts = Math.Max(1, _options.MaxAttemptsPerSession);
        int attempted = 0, succeeded = 0, failed = 0, skipped = 0;
        var notes = new List<string>();

        foreach (var instrument in EffectiveInstruments())
        {
            ct.ThrowIfCancellationRequested();
            var symbol = instrument.Symbol;
            var previous = _status.Get(symbol);
            var sameSession = previous?.SessionOpenTimeMs == session;
            var attempts = sameSession ? previous!.Attempts : 0;
            if (sameSession && previous!.Status is EquityIngestionStatuses.UpToDate or EquityIngestionStatuses.GaveUp)
            {
                skipped++;
                continue;
            }

            using var lease = EquityLaneLocks.TryAcquire(symbol);
            if (lease is null)
            {
                skipped++;
                notes.Add($"{symbol}=lane-busy");
                continue;
            }

            attempted++;
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            EquityLaneRunResult result;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IEquityLanePipeline>();
                result = await pipeline.RunAsync(instrument, session.Value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Equity lane run failed for {Symbol}", symbol);
                result = new EquityLaneRunResult(EquityLaneRunOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
            }

            string status;
            DateTime? completedAt = sameSession ? previous!.LastCompletedAtUtc : null;
            switch (result.Outcome)
            {
                case EquityLaneRunOutcome.Completed:
                    status = EquityIngestionStatuses.UpToDate;
                    attempts++;
                    completedAt = now;
                    succeeded++;
                    break;
                case EquityLaneRunOutcome.Disabled:
                    status = EquityIngestionStatuses.Disabled;
                    skipped++;
                    attempted--;
                    break;
                default:
                    attempts++;
                    status = attempts >= maxAttempts
                        ? EquityIngestionStatuses.GaveUp
                        : result.Outcome == EquityLaneRunOutcome.PendingProvider
                            ? EquityIngestionStatuses.PendingProvider
                            : EquityIngestionStatuses.Failed;
                    failed++;
                    break;
            }

            var detail = result.Detail is { Length: > 500 } d ? d[..500] : result.Detail;
            _status.Set(new EquityIngestionSymbolState(
                symbol, status, session, attempts, maxAttempts, now, completedAt, detail));
            if (status != EquityIngestionStatuses.UpToDate)
                notes.Add($"{symbol}={status}");
            _logger.LogInformation("Equity lane {Symbol} session {Session}: {Status} (attempt {Attempt}/{Max}) {Detail}",
                symbol, session, status, attempts, maxAttempts, detail);
        }

        var outcome = attempted == 0
            ? WorkerCycleOutcome.Idle
            : failed > 0 ? WorkerCycleOutcome.Partial : WorkerCycleOutcome.Succeeded;
        return new WorkerCycleReport(outcome, attempted, succeeded, failed, skipped, 0,
            notes.Count == 0 ? $"session {session}" : $"session {session}: {string.Join(", ", notes)}");
    }
}

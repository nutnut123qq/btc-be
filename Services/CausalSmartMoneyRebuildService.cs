using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Backend.Services;

public interface ICausalSmartMoneyRebuildService
{
    Task<CausalSmartMoneyRebuildResult> RebuildAsync(CausalSmartMoneyRebuildRequest request, CancellationToken ct = default);
    Task<CausalSmartMoneyCoverageResponse> GetCoverageAsync(string symbol, string timeframe, CancellationToken ct = default);
}

/// <summary>
/// Historical, causal SMC materializer. It reads only finalized stored candles, evaluates
/// each gap-free segment independently, and never reads or mutates SmartMoneyStructures.
/// </summary>
public sealed class CausalSmartMoneyRebuildService(
    AppDbContext db,
    ProductionSymbolPolicy symbolPolicy,
    ProductionTimeframePolicy timeframePolicy,
    TimeProvider timeProvider) : ICausalSmartMoneyRebuildService
{
    internal const int MaximumBatchCandles = 5_000;
    internal const int MaximumContextCandles = 100_000;
    internal const int MaximumBatchEvents = 50_000;
    internal const long MaximumEvidenceBytes = 20 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ApplyGates = new(StringComparer.Ordinal);

    public async Task<CausalSmartMoneyRebuildResult> RebuildAsync(
        CausalSmartMoneyRebuildRequest request,
        CancellationToken ct = default)
    {
        var symbol = symbolPolicy.EnsureActive(ProductionSymbolPolicy.Canonicalize(request.Symbol));
        var timeframe = timeframePolicy.EnsureActive(ProductionTimeframePolicy.Canonicalize(request.Timeframe));
        if (request.MaxCandles is < 1 or > MaximumBatchCandles)
            throw new ArgumentOutOfRangeException(nameof(request.MaxCandles),
                $"maxCandles must be between 1 and {MaximumBatchCandles}.");
        if (request.PreviewFromBeginning && !request.DryRun)
            throw new ArgumentException("previewFromBeginning is allowed only when dryRun=true.", nameof(request.PreviewFromBeginning));

        if (request.DryRun)
            return await RebuildCoreAsync(request, symbol, timeframe, ct);

        var gate = ApplyGates.GetOrAdd($"{symbol}\u001f{timeframe}\u001f{SmartMoneyService.CalculationVersion}",
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await RebuildCoreAsync(request, symbol, timeframe, ct);
        }
        catch (Exception ex)
        {
            await TryMarkFailedAsync(symbol, timeframe, ex.Message);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CausalSmartMoneyRebuildResult> RebuildCoreAsync(
        CausalSmartMoneyRebuildRequest request,
        string symbol,
        string timeframe,
        CancellationToken ct)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var checkpoint = request.PreviewFromBeginning
            ? null
            : await db.CausalSmartMoneyRebuildCheckpoints
                .SingleOrDefaultAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.CalculationVersion == SmartMoneyService.CalculationVersion, ct);
        var previousCheckpoint = checkpoint?.LastProcessedOpenTimeMs;

        var candidates = await db.Klines.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.CloseTimeMs <= nowMs
                && (!previousCheckpoint.HasValue || x.OpenTimeMs > previousCheckpoint.Value))
            .OrderBy(x => x.OpenTimeMs)
            .Take(request.MaxCandles)
            .ToArrayAsync(ct);

        if (candidates.Length == 0)
        {
            if (!request.DryRun && checkpoint is not null && checkpoint.Status != "complete")
            {
                checkpoint.Status = "complete";
                checkpoint.LastError = null;
                checkpoint.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return BuildResult(request, symbol, timeframe, previousCheckpoint, checkpoint?.CoverageStartOpenTimeMs,
                candidates, [], new SegmentPartition([], []), [], 0, 0, 0,
                request.DryRun ? "dry_run" : checkpoint?.Status ?? "complete");
        }

        var batchEndOpen = candidates[^1].OpenTimeMs;
        var contextDescending = await db.Klines.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.OpenTimeMs <= batchEndOpen && x.CloseTimeMs <= nowMs)
            .OrderByDescending(x => x.OpenTimeMs)
            .Take(MaximumContextCandles + 1)
            .ToArrayAsync(ct);
        if (contextDescending.Length > MaximumContextCandles)
            throw new CausalSmartMoneyRebuildLimitException("SMC_CONTEXT_LIMIT_EXCEEDED",
                $"Causal SMC rebuild needs more than {MaximumContextCandles} source candles through this batch. Refusing to truncate causal history.");

        Array.Reverse(contextDescending);
        var ledger = await db.KlineGapStates.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.Status != KlineGapStatuses.Filled
                && x.StartOpenTimeMs <= batchEndOpen)
            .OrderBy(x => x.StartOpenTimeMs)
            .ToArrayAsync(ct);
        var partition = Partition(contextDescending, timeframe, ledger);
        var candidateOpenTimes = candidates.Select(x => x.OpenTimeMs).ToHashSet();
        var candidateCloseTimes = candidates.Select(x => x.CloseTimeMs).ToHashSet();
        var pending = new List<PendingEvent>();

        foreach (var segment in partition.Segments)
        {
            if (!segment.Any(x => candidateOpenTimes.Contains(x.OpenTimeMs))) continue;
            var structures = SmartMoneyService.DetectStructures(segment, symbol, timeframe);
            var byOpen = segment.Select((row, index) => (row.OpenTimeMs, index)).ToDictionary(x => x.OpenTimeMs, x => x.index);
            var byClose = segment.Select((row, index) => (row.CloseTimeMs, index)).ToDictionary(x => x.CloseTimeMs, x => x.index);
            foreach (var item in structures)
            {
                if (!candidateCloseTimes.Contains(item.AvailableTimeMs)
                    && (!item.MitigatedAtMs.HasValue || !candidateCloseTimes.Contains(item.MitigatedAtMs.Value)))
                    continue;
                var evidence = SmartMoneyService.BuildEvidence(item, segment, byOpen, byClose);
                var decisionEvidence = ToDecisionEvidence(evidence);
                pending.Add(new PendingEvent(item, evidence, decisionEvidence, segment[0].OpenTimeMs,
                    segment[^1].CloseTimeMs, segment.Count, JsonSerializer.Serialize(decisionEvidence, JsonOptions)));
            }
        }

        if (pending.Count > MaximumBatchEvents)
            throw new CausalSmartMoneyRebuildLimitException("SMC_EVENT_LIMIT_EXCEEDED",
                $"Batch produces {pending.Count} SMC event mutations, exceeding the {MaximumBatchEvents} event cap.");
        var evidenceBytes = pending.Sum(x => (long)Encoding.UTF8.GetByteCount(x.EvidenceJson));
        if (evidenceBytes > MaximumEvidenceBytes)
            throw new CausalSmartMoneyRebuildLimitException("SMC_EVIDENCE_SIZE_LIMIT_EXCEEDED",
                $"Batch produces {evidenceBytes} serialized evidence bytes, exceeding the {MaximumEvidenceBytes}-byte cap.");

        var coverageStart = checkpoint?.CoverageStartOpenTimeMs ?? candidates[0].OpenTimeMs;
        if (request.DryRun)
            return BuildResult(request, symbol, timeframe, previousCheckpoint, coverageStart,
                candidates, contextDescending, partition, pending, 0, 0, 0, "dry_run");

        await using IDbContextTransaction? transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        checkpoint ??= new CausalSmartMoneyRebuildCheckpoint
        {
            Symbol = symbol,
            Timeframe = timeframe,
            CalculationVersion = SmartMoneyService.CalculationVersion,
            CoverageStartOpenTimeMs = coverageStart
        };
        if (checkpoint.Id == 0) db.CausalSmartMoneyRebuildCheckpoints.Add(checkpoint);
        checkpoint.Status = "running";
        checkpoint.LastError = null;
        checkpoint.UpdatedAtUtc = DateTime.UtcNow;

        var eventIds = pending.Select(x => x.Evidence.EventId).Distinct().ToArray();
        var existing = eventIds.Length == 0
            ? []
            : await db.CausalSmartMoneyEvents
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.CalculationVersion == SmartMoneyService.CalculationVersion
                    && eventIds.Contains(x.EventId))
                .ToArrayAsync(ct);
        var byId = existing.ToDictionary(x => x.EventId, StringComparer.Ordinal);
        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        foreach (var item in pending)
        {
            if (!byId.TryGetValue(item.Evidence.EventId, out var stored))
            {
                stored = item.ToEntity(symbol, timeframe);
                db.CausalSmartMoneyEvents.Add(stored);
                byId.Add(stored.EventId, stored);
                inserted++;
                continue;
            }

            item.AssertImmutableCore(stored);

            if (stored.State == item.Evidence.StateAtAsOf
                && stored.MitigatedAtMs == item.Structure.MitigatedAtMs
                && stored.EvaluatedThroughCloseTimeMs >= item.EvaluatedThroughCloseTimeMs)
            {
                unchanged++;
                continue;
            }
            item.ApplyTo(stored);
            updated++;
        }

        checkpoint.LastProcessedOpenTimeMs = candidates[^1].OpenTimeMs;
        checkpoint.CoverageStartOpenTimeMs ??= candidates[0].OpenTimeMs;
        checkpoint.LatestSegmentStartOpenTimeMs = partition.Segments.LastOrDefault()?.FirstOrDefault()?.OpenTimeMs;
        checkpoint.ProcessedCandleCount += candidates.LongLength;
        checkpoint.MaterializedEventCount = await db.CausalSmartMoneyEvents.AsNoTracking()
            .LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.CalculationVersion == SmartMoneyService.CalculationVersion, ct) + inserted;
        var batchLastOpenTimeMs = candidates[^1].OpenTimeMs;
        var hasMore = await db.Klines.AsNoTracking().AnyAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
            && x.CloseTimeMs <= nowMs && x.OpenTimeMs > batchLastOpenTimeMs, ct);
        checkpoint.Status = hasMore ? "checkpointed" : "complete";
        checkpoint.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        return BuildResult(request, symbol, timeframe, previousCheckpoint, checkpoint.CoverageStartOpenTimeMs,
            candidates, contextDescending, partition, pending, inserted, updated, unchanged, checkpoint.Status);
    }

    public async Task<CausalSmartMoneyCoverageResponse> GetCoverageAsync(
        string symbol,
        string timeframe,
        CancellationToken ct = default)
    {
        symbol = symbolPolicy.EnsureActive(ProductionSymbolPolicy.Canonicalize(symbol));
        timeframe = timeframePolicy.EnsureActive(ProductionTimeframePolicy.Canonicalize(timeframe));
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var checkpoint = await db.CausalSmartMoneyRebuildCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.CalculationVersion == SmartMoneyService.CalculationVersion, ct);
        var counts = await db.CausalSmartMoneyEvents.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.CalculationVersion == SmartMoneyService.CalculationVersion)
            .GroupBy(x => x.EventType)
            .Select(x => new { EventType = x.Key, Count = x.LongCount() })
            .ToArrayAsync(ct);
        var invalidDuration = await db.Klines.AsNoTracking()
            .LongCountAsync(x => x.Symbol == symbol && x.Timeframe == timeframe && x.CloseTimeMs <= nowMs
                && (x.CloseTimeMs - x.OpenTimeMs + 1 != intervalMs), ct);
        var gaps = await db.KlineGapStates.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.Status != KlineGapStatuses.Filled)
            .ToArrayAsync(ct);

        return new CausalSmartMoneyCoverageResponse
        {
            Symbol = symbol,
            Timeframe = timeframe,
            CalculationVersion = SmartMoneyService.CalculationVersion,
            LastProcessedOpenTimeMs = checkpoint?.LastProcessedOpenTimeMs,
            CoverageStartOpenTimeMs = checkpoint?.CoverageStartOpenTimeMs,
            LatestSegmentStartOpenTimeMs = checkpoint?.LatestSegmentStartOpenTimeMs,
            ProcessedCandleCount = checkpoint?.ProcessedCandleCount ?? 0,
            MaterializedEventCount = checkpoint?.MaterializedEventCount ?? 0,
            CheckpointStatus = checkpoint?.Status ?? "not_started",
            PersistedEventCount = counts.Sum(x => x.Count),
            EventsByType = counts.ToDictionary(x => x.EventType, x => x.Count, StringComparer.Ordinal),
            InvalidDurationRows = invalidDuration,
            HistoricalPendingGapRanges = gaps.LongCount(x => x.Status == KlineGapStatuses.Pending
                && x.StartOpenTimeMs <= nowMs - intervalMs),
            UnavailableGapRanges = gaps.LongCount(x => x.Status == KlineGapStatuses.Unavailable),
            TrailingNotYetFinalizedGapRanges = gaps.LongCount(x => x.Status == KlineGapStatuses.Pending
                && x.StartOpenTimeMs > nowMs - intervalMs)
        };
    }

    private async Task TryMarkFailedAsync(string symbol, string timeframe, string error)
    {
        try
        {
            db.ChangeTracker.Clear();
            var checkpoint = await db.CausalSmartMoneyRebuildCheckpoints
                .SingleOrDefaultAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.CalculationVersion == SmartMoneyService.CalculationVersion, CancellationToken.None)
                ?? new CausalSmartMoneyRebuildCheckpoint
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    CalculationVersion = SmartMoneyService.CalculationVersion
                };
            if (checkpoint.Id == 0) db.CausalSmartMoneyRebuildCheckpoints.Add(checkpoint);
            checkpoint.Status = "failed";
            checkpoint.LastError = error.Length <= 2000 ? error : error[..2000];
            checkpoint.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original rebuild exception if the failure journal is unavailable.
        }
    }

    internal static SegmentPartition Partition(
        IReadOnlyList<Kline> ordered,
        string timeframe,
        IReadOnlyList<KlineGapState> ledger)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        var segments = new List<IReadOnlyList<Kline>>();
        var boundaries = new List<CausalSmartMoneyGapBoundaryDto>();
        var current = new List<Kline>();
        Kline? previous = null;

        foreach (var row in ordered)
        {
            var validDuration = row.OpenTimeMs <= long.MaxValue - (intervalMs - 1)
                && row.CloseTimeMs == row.OpenTimeMs + intervalMs - 1;
            if (!validDuration)
            {
                Flush();
                boundaries.Add(new CausalSmartMoneyGapBoundaryDto
                {
                    InvalidOpenTimeMs = row.OpenTimeMs,
                    BoundaryType = "invalid_duration",
                    LedgerStatus = "not_applicable"
                });
                // Preserve the timestamp only for physical gap reconciliation. The invalid row
                // is never added to a segment, so detector state still resets here.
                previous = row;
                continue;
            }

            if (previous is not null && row.OpenTimeMs - previous.OpenTimeMs != intervalMs)
            {
                Flush();
                var start = previous.OpenTimeMs + intervalMs;
                var end = row.OpenTimeMs - intervalMs;
                var state = ledger.FirstOrDefault(x => x.StartOpenTimeMs <= end && x.EndOpenTimeMs >= start);
                boundaries.Add(new CausalSmartMoneyGapBoundaryDto
                {
                    PreviousOpenTimeMs = previous.OpenTimeMs,
                    NextOpenTimeMs = row.OpenTimeMs,
                    MissingBars = Math.Max(0, (row.OpenTimeMs - previous.OpenTimeMs) / intervalMs - 1),
                    BoundaryType = "missing_candles",
                    LedgerStatus = state?.Status ?? "untracked",
                    GapStateId = state?.Id
                });
            }

            current.Add(row);
            previous = row;
        }
        Flush();
        return new SegmentPartition(segments, boundaries);

        void Flush()
        {
            if (current.Count > 0) segments.Add(current.ToArray());
            current.Clear();
        }
    }

    internal static TechnicalEventEvidenceDto ToDecisionEvidence(TechnicalEventEvidenceDto evidence)
    {
        var isFvg = evidence.EventType is "FVG_BULL" or "FVG_BEAR";
        return new TechnicalEventEvidenceDto
        {
            EventId = evidence.EventId,
            EventType = evidence.EventType,
            Description = evidence.Description,
            OriginTimeMs = evidence.OriginTimeMs,
            AvailableTimeMs = evidence.AvailableTimeMs,
            ReferenceTimeMs = evidence.ReferenceTimeMs,
            Price = evidence.Price,
            HighPrice = evidence.HighPrice,
            LowPrice = evidence.LowPrice,
            CalculationVersion = evidence.CalculationVersion,
            StateAtAsOf = isFvg ? "active" : "confirmed",
            MitigatedAtMs = null,
            InvalidatedAtMs = null,
            MitigationRule = evidence.MitigationRule,
            InvalidationRule = evidence.InvalidationRule,
            SourceCandles = evidence.SourceCandles.Where(x => x.Role != "fvg-mitigation").ToArray(),
            DetectionConditions = evidence.DetectionConditions,
            Limitations = evidence.Limitations
        };
    }

    private static CausalSmartMoneyRebuildResult BuildResult(
        CausalSmartMoneyRebuildRequest request,
        string symbol,
        string timeframe,
        long? previousCheckpoint,
        long? coverageStart,
        IReadOnlyList<Kline> candidates,
        IReadOnlyList<Kline> context,
        SegmentPartition partition,
        IReadOnlyList<PendingEvent> pending,
        int inserted,
        int updated,
        int existing,
        string status)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        return new CausalSmartMoneyRebuildResult
        {
            Symbol = symbol,
            Timeframe = timeframe,
            CalculationVersion = SmartMoneyService.CalculationVersion,
            DryRun = request.DryRun,
            PreviousCheckpointOpenTimeMs = previousCheckpoint,
            CoverageStartOpenTimeMs = coverageStart,
            BatchStartOpenTimeMs = candidates.FirstOrDefault()?.OpenTimeMs,
            LastProcessedOpenTimeMs = candidates.LastOrDefault()?.OpenTimeMs ?? previousCheckpoint,
            CandidateCandles = candidates.Count,
            ValidCandidateCandles = candidates.Count(x => x.OpenTimeMs <= long.MaxValue - (intervalMs - 1)
                && x.CloseTimeMs == x.OpenTimeMs + intervalMs - 1),
            InvalidDurationCandles = candidates.Count(x => x.OpenTimeMs > long.MaxValue - (intervalMs - 1)
                || x.CloseTimeMs != x.OpenTimeMs + intervalMs - 1),
            ContextCandles = context.Count,
            ContiguousSegments = partition.Segments.Count,
            EstimatedEvents = pending.Count,
            EstimatedEvidenceBytes = pending.Sum(x => (long)Encoding.UTF8.GetByteCount(x.EvidenceJson)),
            InsertedEvents = inserted,
            UpdatedEvents = updated,
            ExistingEvents = existing,
            Status = status,
            GapBoundaries = partition.Boundaries,
            Limitations =
            [
                "Only finalized stored BTCUSDT candles from the requested active timeframe are used.",
                "All pivot, trend and active FVG state resets after a missing candle or invalid-duration row.",
                "Each batch replays the bounded causal prefix through its end; it never scans future candles.",
                "Legacy SmartMoneyStructures rows are neither read nor overwritten.",
                $"Hard limits: {MaximumBatchCandles} candidate candles, {MaximumContextCandles} context candles, {MaximumBatchEvents} event mutations and {MaximumEvidenceBytes} evidence bytes."
            ]
        };
    }

    internal sealed record SegmentPartition(
        IReadOnlyList<IReadOnlyList<Kline>> Segments,
        IReadOnlyList<CausalSmartMoneyGapBoundaryDto> Boundaries);

    private sealed record PendingEvent(
        SmartMoneyStructure Structure,
        TechnicalEventEvidenceDto Evidence,
        TechnicalEventEvidenceDto DecisionEvidence,
        long SegmentStartOpenTimeMs,
        long EvaluatedThroughCloseTimeMs,
        int AnalysisCandleCount,
        string EvidenceJson)
    {
        public CausalSmartMoneyEvent ToEntity(string symbol, string timeframe) => new()
        {
            Symbol = symbol,
            Timeframe = timeframe,
            EventId = Evidence.EventId,
            EventType = Structure.EventType,
            OriginTimeMs = Structure.OriginTimeMs,
            AvailableTimeMs = Structure.AvailableTimeMs,
            ReferenceTimeMs = Structure.ReferenceTimeMs,
            Price = Structure.Price,
            HighPrice = Structure.HighPrice,
            LowPrice = Structure.LowPrice,
            State = Evidence.StateAtAsOf,
            MitigatedAtMs = Structure.MitigatedAtMs,
            MitigationSourceOpenTimeMs = Evidence.SourceCandles.FirstOrDefault(x => x.Role == "fvg-mitigation")?.OpenTimeMs,
            CalculationVersion = SmartMoneyService.CalculationVersion,
            SegmentStartOpenTimeMs = SegmentStartOpenTimeMs,
            EvaluatedThroughCloseTimeMs = EvaluatedThroughCloseTimeMs,
            AnalysisCandleCount = AnalysisCandleCount,
            DecisionSourceCandleCount = DecisionEvidence.SourceCandles.Count,
            DecisionSourceOpenTimeMsJson = JsonSerializer.Serialize(DecisionEvidence.SourceCandles.Select(x => x.OpenTimeMs), JsonOptions),
            DecisionEvidenceJson = EvidenceJson,
            DecisionEvidenceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EvidenceJson))).ToLowerInvariant()
        };

        public void ApplyTo(CausalSmartMoneyEvent target)
        {
            target.State = Evidence.StateAtAsOf;
            target.MitigatedAtMs = Structure.MitigatedAtMs;
            target.MitigationSourceOpenTimeMs = Evidence.SourceCandles.FirstOrDefault(x => x.Role == "fvg-mitigation")?.OpenTimeMs;
            target.EvaluatedThroughCloseTimeMs = EvaluatedThroughCloseTimeMs;
            target.AnalysisCandleCount = AnalysisCandleCount;
            target.UpdatedAtUtc = DateTime.UtcNow;
        }

        public void AssertImmutableCore(CausalSmartMoneyEvent target)
        {
            var sourceTimes = JsonSerializer.Serialize(DecisionEvidence.SourceCandles.Select(x => x.OpenTimeMs), JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EvidenceJson))).ToLowerInvariant();
            if (target.EventType != Structure.EventType
                || target.OriginTimeMs != Structure.OriginTimeMs
                || target.AvailableTimeMs != Structure.AvailableTimeMs
                || target.ReferenceTimeMs != Structure.ReferenceTimeMs
                || target.Price != Structure.Price
                || target.HighPrice != Structure.HighPrice
                || target.LowPrice != Structure.LowPrice
                || target.SegmentStartOpenTimeMs != SegmentStartOpenTimeMs
                || target.DecisionSourceCandleCount != DecisionEvidence.SourceCandles.Count
                || target.DecisionSourceOpenTimeMsJson != sourceTimes
                || target.DecisionEvidenceJson != EvidenceJson
                || target.DecisionEvidenceSha256 != hash)
            {
                throw new CausalSmartMoneyRebuildLimitException("SMC_IMMUTABLE_CORE_DRIFT",
                    $"Stored decision core for event '{target.EventId}' differs under calculation version '{target.CalculationVersion}'. Bump the calculation version before rebuilding.");
            }
        }
    }
}

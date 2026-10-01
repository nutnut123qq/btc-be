using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public interface ITechnicalEvidenceRebuildService
{
    Task<TechnicalEvidenceRebuildResult> RebuildAsync(
        TechnicalEvidenceRebuildRequest request,
        CancellationToken ct = default);

    Task<TechnicalEvidenceCoverageResponse> GetCoverageAsync(
        string symbol,
        string timeframe,
        CancellationToken ct = default);
}

/// <summary>
/// Materializes only event-bearing causal envelopes. Full per-candle state remains an
/// on-demand replay concern so a historical rebuild cannot silently create a multi-GB table.
/// </summary>
public sealed class TechnicalEvidenceRebuildService(
    AppDbContext db,
    ISmartMoneyService replay,
    ITechnicalModuleContractProvider contract,
    ProductionSymbolPolicy symbolPolicy,
    ProductionTimeframePolicy timeframePolicy,
    TimeProvider timeProvider) : ITechnicalEvidenceRebuildService
{
    internal const int MaximumBatchCandles = 100;
    internal const long MaximumBatchEnvelopeBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ApplyGates = new(StringComparer.Ordinal);

    public async Task<TechnicalEvidenceRebuildResult> RebuildAsync(
        TechnicalEvidenceRebuildRequest request,
        CancellationToken ct = default)
    {
        var symbol = symbolPolicy.EnsureActive(ProductionSymbolPolicy.Canonicalize(request.Symbol));
        var timeframe = timeframePolicy.EnsureActive(ProductionTimeframePolicy.Canonicalize(request.Timeframe));
        if (request.MaxCandles is < 1 or > MaximumBatchCandles)
            throw new ArgumentOutOfRangeException(nameof(request.MaxCandles),
                $"maxCandles must be between 1 and {MaximumBatchCandles}.");

        if (request.DryRun)
            return await RebuildCoreAsync(request, symbol, timeframe, ct);

        var gateKey = $"{symbol}\u001f{timeframe}\u001f{contract.ContractVersion}\u001f{contract.Sha256}";
        var gate = ApplyGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
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

    private async Task<TechnicalEvidenceRebuildResult> RebuildCoreAsync(
        TechnicalEvidenceRebuildRequest request,
        string symbol,
        string timeframe,
        CancellationToken ct)
    {

        var checkpoint = await db.TechnicalEvidenceRebuildCheckpoints
            .SingleOrDefaultAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.ModuleContractVersion == contract.ContractVersion
                && x.ModuleContractSha256 == contract.Sha256, ct);
        var previousCheckpoint = checkpoint?.LastProcessedCloseTimeMs;
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var candidateQuery = db.Klines.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.CloseTimeMs <= nowMs
                && (!previousCheckpoint.HasValue || x.CloseTimeMs > previousCheckpoint.Value))
            .Select(x => x.CloseTimeMs);
        long[] candidates;
        if (previousCheckpoint.HasValue)
        {
            candidates = await candidateQuery.OrderBy(x => x).Take(request.MaxCandles).ToArrayAsync(ct);
        }
        else
        {
            // Bootstrap only the recent bounded tail. A per-candle replay intentionally loads
            // causal context, so an apparent full-history loop would be quadratic and misleading.
            candidates = await candidateQuery.OrderByDescending(x => x).Take(request.MaxCandles).ToArrayAsync(ct);
            Array.Sort(candidates);
        }

        var pending = new List<PendingEvidence>();
        foreach (var closeTimeMs in candidates)
        {
            var result = await replay.GetReplayAsync(symbol, timeframe, closeTimeMs, 5, ct);
            if (result.EffectiveAsOfTimeMs != closeTimeMs) continue;
            CollectSparse(result, pending);
        }

        var estimatedBytes = pending.Sum(x => (long)Encoding.UTF8.GetByteCount(x.EnvelopeJson));
        if (estimatedBytes > MaximumBatchEnvelopeBytes)
            throw new TechnicalEvidenceRebuildLimitException(estimatedBytes, MaximumBatchEnvelopeBytes);

        if (request.DryRun)
            return Result(request, symbol, timeframe, previousCheckpoint, candidates, pending,
                checkpoint?.CoverageStartCloseTimeMs ?? (candidates.Length == 0 ? null : candidates[0]),
                inserted: 0, existing: 0, status: "dry_run");

        checkpoint ??= new TechnicalEvidenceRebuildCheckpoint
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModuleContractVersion = contract.ContractVersion,
            ModuleContractSha256 = contract.Sha256
        };
        if (checkpoint.Id == 0) db.TechnicalEvidenceRebuildCheckpoints.Add(checkpoint);
        checkpoint.Status = "running";
        checkpoint.LastError = null;
        checkpoint.UpdatedAtUtc = DateTime.UtcNow;

        var existingKeys = pending.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.TechnicalEvidenceRecords.AsNoTracking()
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.ModuleContractVersion == contract.ContractVersion
                    && x.ModuleContractSha256 == contract.Sha256
                    && candidates.Contains(x.AsOfTimeMs))
                .Select(x => new { x.LayerKey, x.AsOfTimeMs, x.CalculationVersion })
                .ToArrayAsync(ct))
                .Select(x => Key(x.LayerKey, x.AsOfTimeMs, x.CalculationVersion))
                .ToHashSet(StringComparer.Ordinal);

        var inserted = 0;
        foreach (var item in pending)
        {
            if (!existingKeys.Add(Key(item.LayerKey, item.AsOfTimeMs, item.CalculationVersion))) continue;
            db.TechnicalEvidenceRecords.Add(item.ToEntity(symbol, timeframe, contract));
            inserted++;
        }

        checkpoint.LastProcessedCloseTimeMs = candidates.Length == 0 ? previousCheckpoint : candidates[^1];
        checkpoint.CoverageStartCloseTimeMs ??= candidates.Length == 0 ? null : candidates[0];
        checkpoint.HistoricalBackfill = false;
        checkpoint.MaterializedRecordCount += inserted;
        checkpoint.Status = candidates.Length < request.MaxCandles ? "complete" : "checkpointed";
        checkpoint.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Result(request, symbol, timeframe, previousCheckpoint, candidates, pending,
            checkpoint.CoverageStartCloseTimeMs, inserted, pending.Count - inserted, checkpoint.Status);
    }

    private async Task TryMarkFailedAsync(string symbol, string timeframe, string error)
    {
        try
        {
            db.ChangeTracker.Clear();
            var checkpoint = await db.TechnicalEvidenceRebuildCheckpoints
                .SingleOrDefaultAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.ModuleContractVersion == contract.ContractVersion
                    && x.ModuleContractSha256 == contract.Sha256, CancellationToken.None);
            checkpoint ??= new TechnicalEvidenceRebuildCheckpoint
            {
                Symbol = symbol,
                Timeframe = timeframe,
                ModuleContractVersion = contract.ContractVersion,
                ModuleContractSha256 = contract.Sha256
            };
            if (checkpoint.Id == 0) db.TechnicalEvidenceRebuildCheckpoints.Add(checkpoint);
            checkpoint.Status = "failed";
            checkpoint.LastError = error.Length <= 2000 ? error : error[..2000];
            checkpoint.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the original rebuild exception when the failure journal itself is unavailable.
        }
    }

    public async Task<TechnicalEvidenceCoverageResponse> GetCoverageAsync(
        string symbol,
        string timeframe,
        CancellationToken ct = default)
    {
        symbol = symbolPolicy.EnsureActive(ProductionSymbolPolicy.Canonicalize(symbol));
        timeframe = timeframePolicy.EnsureActive(ProductionTimeframePolicy.Canonicalize(timeframe));
        var checkpoint = await db.TechnicalEvidenceRebuildCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.ModuleContractVersion == contract.ContractVersion
                && x.ModuleContractSha256 == contract.Sha256, ct);
        var counts = await db.TechnicalEvidenceRecords.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.ModuleContractVersion == contract.ContractVersion
                && x.ModuleContractSha256 == contract.Sha256)
            .GroupBy(x => x.LayerKey)
            .Select(x => new { LayerKey = x.Key, Count = x.LongCount() })
            .ToArrayAsync(ct);
        return new TechnicalEvidenceCoverageResponse
        {
            Symbol = symbol,
            Timeframe = timeframe,
            ModuleContractVersion = contract.ContractVersion,
            ModuleContractSha256 = contract.Sha256,
            LastProcessedCloseTimeMs = checkpoint?.LastProcessedCloseTimeMs,
            CoverageStartCloseTimeMs = checkpoint?.CoverageStartCloseTimeMs,
            HistoricalBackfill = checkpoint?.HistoricalBackfill ?? false,
            CheckpointStatus = checkpoint?.Status ?? "not_started",
            SparseRecordCount = counts.Sum(x => x.Count),
            RecordsByLayer = counts.ToDictionary(x => x.LayerKey, x => x.Count, StringComparer.Ordinal)
        };
    }

    private static void CollectSparse(TechnicalReplayResponse replayResult, List<PendingEvidence> output)
    {
        var close = replayResult.EffectiveAsOfTimeMs!.Value;
        Add(replayResult.Layers.Indicators,
            replayResult.Layers.Indicators.Payload?.AvailableTimeMs == close
                && replayResult.Layers.Indicators.Payload.Events.Count > 0, close, output);
        Add(replayResult.Layers.CandlePatterns,
            replayResult.Layers.CandlePatterns.Payload?.Events.Any(x => x.AvailableTimeMs == close) == true,
            close, output);
        Add(replayResult.Layers.VolumeAnomaly,
            replayResult.Layers.VolumeAnomaly.Payload?.AvailableTimeMs == close
                && replayResult.Layers.VolumeAnomaly.Payload.TriggeredEvents.Count > 0, close, output);
        Add(replayResult.Layers.MarketRegime,
            replayResult.Layers.MarketRegime.Payload?.AvailableTimeMs == close
                && replayResult.Layers.MarketRegime.Payload.EventType is not null, close, output);
        Add(replayResult.Layers.Fibonacci,
            replayResult.Layers.Fibonacci.Payload?.AvailableTimeMs == close, close, output);
        Add(replayResult.Layers.VolumeProfile,
            replayResult.Layers.VolumeProfile.Lineage.AvailableTimeMs == close
                && replayResult.Layers.VolumeProfile.Payload?.Events.Count > 0, close, output);
        Add(replayResult.Layers.Confluence,
            replayResult.Layers.Confluence.Payload?.AvailableTimeMs == close
                && replayResult.Layers.Confluence.Payload.TriggeredEvents.Count > 0, close, output);
    }

    private static void Add<T>(TechnicalLayerEnvelopeDto<T> envelope, bool eventAtClose,
        long close, List<PendingEvidence> output)
    {
        if (!eventAtClose) return;
        output.Add(new PendingEvidence(
            envelope.LayerKey,
            close,
            envelope.Lineage.CalculationVersion,
            envelope.Availability,
            envelope.Lineage.AvailableTimeMs,
            envelope.Lineage.SourceStartTimeMs,
            envelope.Lineage.SourceEndTimeMs,
            envelope.Lineage.SourceCandleCount,
            JsonSerializer.Serialize(envelope, JsonOptions)));
    }

    private TechnicalEvidenceRebuildResult Result(
        TechnicalEvidenceRebuildRequest request,
        string symbol,
        string timeframe,
        long? previousCheckpoint,
        IReadOnlyList<long> candidates,
        IReadOnlyCollection<PendingEvidence> pending,
        long? coverageStart,
        int inserted,
        int existing,
        string status) => new()
    {
        Symbol = symbol,
        Timeframe = timeframe,
        DryRun = request.DryRun,
        ModuleContractVersion = contract.ContractVersion,
        ModuleContractSha256 = contract.Sha256,
        PreviousCheckpointCloseTimeMs = previousCheckpoint,
        CoverageStartCloseTimeMs = coverageStart,
        BatchStartCloseTimeMs = candidates.Count == 0 ? null : candidates[0],
        HistoricalBackfill = false,
        LastProcessedCloseTimeMs = candidates.Count == 0 ? previousCheckpoint : candidates[^1],
        CandidateCandles = candidates.Count,
        EstimatedSparseRecords = pending.Count,
        EstimatedEnvelopeBytes = pending.Sum(x => (long)Encoding.UTF8.GetByteCount(x.EnvelopeJson)),
        InsertedRecords = inserted,
        ExistingRecords = existing,
        Status = status,
        Limitations =
        [
            "Only event-bearing layer envelopes are persisted; no-event state is reconstructed on demand.",
            "The first batch bootstraps only the most recent bounded tail; later batches start strictly after the contract-hash checkpoint.",
            "historicalBackfill=false: this endpoint never presents the quadratic per-candle replay path as a complete historical rebuild.",
            $"Each call is capped at {MaximumBatchCandles} finalized candles and {MaximumBatchEnvelopeBytes} serialized bytes.",
            "Legacy derived tables are neither read nor overwritten."
        ]
    };

    private static string Key(string layer, long close, string calculationVersion) =>
        $"{layer}\u001f{close}\u001f{calculationVersion}";

    private sealed record PendingEvidence(
        string LayerKey,
        long AsOfTimeMs,
        string CalculationVersion,
        string Availability,
        long? AvailableTimeMs,
        long? SourceStartTimeMs,
        long? SourceEndTimeMs,
        int SourceCandleCount,
        string EnvelopeJson)
    {
        public TechnicalEvidenceRecord ToEntity(
            string symbol,
            string timeframe,
            ITechnicalModuleContractProvider contract) => new()
        {
            Symbol = symbol,
            Timeframe = timeframe,
            LayerKey = LayerKey,
            AsOfTimeMs = AsOfTimeMs,
            ModuleContractVersion = contract.ContractVersion,
            ModuleContractSha256 = contract.Sha256,
            CalculationVersion = CalculationVersion,
            Availability = Availability,
            AvailableTimeMs = AvailableTimeMs,
            SourceStartTimeMs = SourceStartTimeMs,
            SourceEndTimeMs = SourceEndTimeMs,
            SourceCandleCount = SourceCandleCount,
            EnvelopeJson = EnvelopeJson
        };
    }
}

public sealed class TechnicalEvidenceRebuildLimitException(long estimatedBytes, long maximumBytes)
    : Exception($"Sparse technical evidence batch is estimated at {estimatedBytes} bytes, exceeding the {maximumBytes}-byte cap.")
{
    public long EstimatedBytes { get; } = estimatedBytes;
    public long MaximumBytes { get; } = maximumBytes;
}

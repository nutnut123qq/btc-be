using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class SmartMoneyService : ISmartMoneyService
{
    internal const string CalculationVersion = "smc-causal-v2";
    internal const int MaxReplayAnalysisBars = 100_000;
    private readonly AppDbContext _db;
    private readonly ProductionTimeframePolicy _timeframePolicy;
    private readonly ProductionSymbolPolicy _symbolPolicy;
    private readonly ITechnicalReplayLayerService _replayLayers;
    private readonly ITechnicalModuleContractProvider _moduleContract;

    public SmartMoneyService(
        AppDbContext db,
        ProductionTimeframePolicy? timeframePolicy = null,
        ProductionSymbolPolicy? symbolPolicy = null,
        ITechnicalReplayLayerService? replayLayers = null,
        ITechnicalModuleContractProvider? moduleContract = null)
    {
        _db = db;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
        _symbolPolicy = symbolPolicy ?? new ProductionSymbolPolicy();
        _moduleContract = moduleContract ?? new BuiltInTechnicalModuleContractProvider();
        _replayLayers = replayLayers ?? new TechnicalReplayLayerService(_moduleContract);
    }

    public async Task<List<SmartMoneyStructure>> GetSmartMoneyStructuresAsync(
        string symbol,
        string timeframe,
        int lookbackBars,
        CancellationToken ct = default)
    {
        symbol = _symbolPolicy.EnsureActive(symbol);
        timeframe = _timeframePolicy.EnsureActive(timeframe);
        lookbackBars = Math.Clamp(lookbackBars, 5, 10_000);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var klines = await _db.Klines.AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.CloseTimeMs <= nowMs)
            .OrderByDescending(k => k.OpenTimeMs)
            .Take(lookbackBars)
            .ToListAsync(ct);

        if (klines.Count < 5) return [];

        var orderedKlines = klines.OrderBy(k => k.OpenTimeMs).ToList();
        var structures = DetectStructures(orderedKlines, symbol, timeframe);
        await PersistIdempotentlyAsync(structures, symbol, timeframe, ct);
        return structures;
    }

    public async Task<TechnicalReplayResponse> GetReplayAsync(
        string symbol,
        string timeframe,
        long asOfTimeMs,
        int lookbackBars,
        CancellationToken ct = default)
    {
        symbol = _symbolPolicy.EnsureActive(symbol);
        timeframe = _timeframePolicy.EnsureActive(timeframe);
        if (asOfTimeMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(asOfTimeMs), "asOfTimeMs must be a positive Unix timestamp in milliseconds.");
        if (lookbackBars is < 5 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(lookbackBars), "lookbackBars must be between 5 and 10000.");

        // Full causal context is required because BOS/CHoCH classification depends on prior
        // confirmed breaks. The hard cap fails closed rather than silently changing old events
        // when a rolling lookback boundary moves.
        var klines = await _db.Klines.AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe
                && k.CloseTimeMs > 0 && k.CloseTimeMs <= asOfTimeMs)
            .OrderByDescending(k => k.OpenTimeMs)
            .Take(MaxReplayAnalysisBars + 1)
            .ToListAsync(ct);
        var completeHistory = klines.OrderBy(k => k.OpenTimeMs).ToList();
        var ordered = LatestContiguousSegment(completeHistory, timeframe);
        // If the capped sample contains a gap, the latest segment is complete and older rows
        // cannot affect it. A fully contiguous capped sample may extend beyond the rows read,
        // so fail closed instead of inventing an initial trend state at the cap boundary.
        if (klines.Count > MaxReplayAnalysisBars && ordered.Count > MaxReplayAnalysisBars)
            throw new TechnicalReplayContextLimitException(MaxReplayAnalysisBars);
        var effectiveAsOf = ordered.Count == 0 ? (long?)null : ordered[^1].CloseTimeMs;
        var structures = DetectStructures(ordered, symbol, timeframe);
        var displayRows = ordered.Skip(Math.Max(0, ordered.Count - lookbackBars)).ToArray();
        var replayWindowStart = displayRows.Length == 0 ? (long?)null : displayRows[0].OpenTimeMs;
        var indexByOpenTime = ordered
            .Select((row, index) => (row.OpenTimeMs, index))
            .ToDictionary(x => x.OpenTimeMs, x => x.index);
        var indexByCloseTime = ordered
            .Select((row, index) => (row.CloseTimeMs, index))
            .ToDictionary(x => x.CloseTimeMs, x => x.index);
        var events = structures
            .Where(x => x.AvailableTimeMs <= asOfTimeMs
                && (!replayWindowStart.HasValue || x.OriginTimeMs >= replayWindowStart.Value))
            .OrderBy(x => x.AvailableTimeMs)
            .ThenBy(x => x.OriginTimeMs)
            .ThenBy(x => x.EventType, StringComparer.Ordinal)
            .Select(x => BuildEvidence(x, ordered, indexByOpenTime, indexByCloseTime))
            .ToArray();
        var hasGapBoundary = completeHistory.Count != ordered.Count;
        var layers = _replayLayers.Build(asOfTimeMs, ordered, replayWindowStart);
        var checkpoint = await _db.TechnicalEvidenceRebuildCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.ModuleContractVersion == _moduleContract.ContractVersion
                && x.ModuleContractSha256 == _moduleContract.Sha256, ct);
        var hasProcessedCurrent = effectiveAsOf.HasValue
            && checkpoint?.LastProcessedCloseTimeMs >= effectiveAsOf.Value;
        var checkpointStatus = hasProcessedCurrent ? checkpoint!.Status : "not_processed_at_as_of";
        var materializedLayerKeys = effectiveAsOf.HasValue
            ? (await _db.TechnicalEvidenceRecords.AsNoTracking()
                .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                    && x.AsOfTimeMs == effectiveAsOf.Value
                    && x.ModuleContractVersion == _moduleContract.ContractVersion
                    && x.ModuleContractSha256 == _moduleContract.Sha256)
                .Select(x => x.LayerKey)
                .Distinct()
                .ToArrayAsync(ct)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var coverage = _replayLayers.BuildCoverage(layers, hasGapBoundary)
            .Select(x => new TechnicalLayerCoverageDto
            {
                LayerKey = x.LayerKey,
                Availability = x.Availability,
                SourceBars = x.SourceBars,
                RequiredWarmupBars = x.RequiredWarmupBars,
                LatestAvailableTimeMs = x.LatestAvailableTimeMs,
                HasGapBoundary = x.HasGapBoundary,
                CheckpointStatus = checkpointStatus,
                StorageStatus = materializedLayerKeys.Contains(x.LayerKey)
                    ? "sparse_event_envelope_materialized"
                    : hasProcessedCurrent ? "on_demand_state_checkpoint_processed" : "on_demand_state_not_checkpointed",
                IsEventEnvelopeMaterializedAtAsOf = materializedLayerKeys.Contains(x.LayerKey)
            }).ToArray();

        return new TechnicalReplayResponse
        {
            ModuleContractVersion = _moduleContract.ContractVersion,
            ModuleContractSha256 = _moduleContract.Sha256,
            Symbol = symbol,
            Timeframe = timeframe,
            RequestedAsOfTimeMs = asOfTimeMs,
            EffectiveAsOfTimeMs = effectiveAsOf,
            LastFinalizedCandleCloseTimeMs = effectiveAsOf,
            RequestedLookbackBars = lookbackBars,
            ReplayWindowStartTimeMs = replayWindowStart,
            ContiguousSegmentStartTimeMs = ordered.Count == 0 ? null : ordered[0].OpenTimeMs,
            SourceCandleCount = displayRows.Length,
            AnalysisCandleCount = ordered.Count,
            CalculationVersion = CalculationVersion,
            Provenance = new TechnicalReplayProvenanceDto(),
            Limitations =
            [
                "The full latest gap-free stored history segment is evaluated for causal state; only events originating inside the requested lookback window are returned.",
                "History before the latest candle gap is intentionally excluded because state and FVG lifecycle cannot be proven across missing candles.",
                "A candle whose close time does not match its timeframe duration is treated as a causal boundary and is excluded with all earlier history.",
                "OHLCV candles do not preserve intrabar path, so lifecycle changes are timestamped at candle close.",
                "SMC invalidation has no independently validated rule in this calculation version and is therefore reported as unavailable."
            ],
            Candles = displayRows.Select(ToReplayCandle).ToArray(),
            Events = events,
            Layers = layers,
            Coverage = coverage,
            Administration = new TechnicalReplayAdministrationDto
            {
                HasGapBoundary = hasGapBoundary,
                ContextLimitBars = MaxReplayAnalysisBars,
                LegacySmartMoneyStatus = "isolated; replay reconstructs from finalized Klines and does not read legacy SmartMoneyStructures",
                RebuildRequired = !hasProcessedCurrent,
                RebuildReason = hasProcessedCurrent ? null : "Canonical sparse evidence checkpoint has not processed this finalized close and contract hash."
            }
        };
    }

    internal static List<SmartMoneyStructure> DetectStructures(
        IReadOnlyList<Kline> orderedKlines,
        string symbol,
        string timeframe)
    {
        var structures = new List<SmartMoneyStructure>();
        var rows = LatestContiguousSegment(orderedKlines, timeframe);
        if (rows.Count < 3) return structures;

        Pivot? activeHigh = null;
        Pivot? activeLow = null;
        var currentTrend = 0;
        var activeBullFvgs = new PriorityQueue<SmartMoneyStructure, double>();
        var activeBearFvgs = new PriorityQueue<SmartMoneyStructure, double>();

        for (var i = 0; i < rows.Count; i++)
        {
            var current = rows[i];
            var availableAt = CloseOrOpenTime(current);

            // Resolve only gaps observed on finalized candles after the FVG became available.
            while (activeBullFvgs.TryPeek(out var bullishFvg, out _)
                && bullishFvg.LowPrice.HasValue
                && (double)current.Low <= bullishFvg.LowPrice.Value)
            {
                activeBullFvgs.Dequeue();
                bullishFvg.IsMitigated = true;
                bullishFvg.MitigatedAtMs = availableAt;
            }
            while (activeBearFvgs.TryPeek(out var bearishFvg, out _)
                && bearishFvg.HighPrice.HasValue
                && (double)current.High >= bearishFvg.HighPrice.Value)
            {
                activeBearFvgs.Dequeue();
                bearishFvg.IsMitigated = true;
                bearishFvg.MitigatedAtMs = availableAt;
            }

            // A five-bar pivot at i-2 becomes knowable only when bar i is finalized.
            if (i >= 4)
            {
                var pivotIndex = i - 2;
                var pivot = rows[pivotIndex];
                if (IsSwingHigh(rows, pivotIndex))
                {
                    activeHigh = new Pivot((double)pivot.High, pivot.OpenTimeMs);
                    structures.Add(CreateEvent(symbol, timeframe, "SWING_HIGH", pivot.OpenTimeMs,
                        availableAt, (double)pivot.High, "Swing High (confirmed after two bars)"));
                }

                if (IsSwingLow(rows, pivotIndex))
                {
                    activeLow = new Pivot((double)pivot.Low, pivot.OpenTimeMs);
                    structures.Add(CreateEvent(symbol, timeframe, "SWING_LOW", pivot.OpenTimeMs,
                        availableAt, (double)pivot.Low, "Swing Low (confirmed after two bars)"));
                }
            }

            if (activeHigh is not null && (double)current.Close > activeHigh.Price)
            {
                var eventType = currentTrend == 1 ? "BOS_BULL" : "CHOCH_BULL";
                structures.Add(CreateEvent(symbol, timeframe, eventType, current.OpenTimeMs, availableAt,
                    (double)current.Close, eventType == "BOS_BULL" ? "Bullish BOS" : "Bullish CHOCH",
                    activeHigh.OriginTimeMs));
                activeHigh = null;
                currentTrend = 1;
            }

            if (activeLow is not null && (double)current.Close < activeLow.Price)
            {
                var eventType = currentTrend == -1 ? "BOS_BEAR" : "CHOCH_BEAR";
                structures.Add(CreateEvent(symbol, timeframe, eventType, current.OpenTimeMs, availableAt,
                    (double)current.Close, eventType == "BOS_BEAR" ? "Bearish BOS" : "Bearish CHOCH",
                    activeLow.OriginTimeMs));
                activeLow = null;
                currentTrend = -1;
            }

            // A three-candle FVG is available only after its final defining candle closes.
            if (i >= 2)
            {
                var first = rows[i - 2];
                var middle = rows[i - 1];
                if (first.High < current.Low)
                {
                    var fvg = CreateFvg(symbol, timeframe, "FVG_BULL", first, middle, current, availableAt);
                    structures.Add(fvg);
                    activeBullFvgs.Enqueue(fvg, -fvg.LowPrice!.Value);
                }
                else if (first.Low > current.High)
                {
                    var fvg = CreateFvg(symbol, timeframe, "FVG_BEAR", first, middle, current, availableAt);
                    structures.Add(fvg);
                    activeBearFvgs.Enqueue(fvg, fvg.HighPrice!.Value);
                }
            }
        }

        return structures;
    }

    private async Task PersistIdempotentlyAsync(
        IReadOnlyList<SmartMoneyStructure> calculated,
        string symbol,
        string timeframe,
        CancellationToken ct)
    {
        if (calculated.Count == 0) return;
        var minOrigin = calculated.Min(x => x.OriginTimeMs);
        var existing = await _db.SmartMoneyStructures
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe
                && x.CalculationVersion == CalculationVersion && x.OriginTimeMs >= minOrigin)
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(LogicalKey);

        foreach (var item in calculated)
        {
            if (!byKey.TryGetValue(LogicalKey(item), out var stored))
            {
                _db.SmartMoneyStructures.Add(item);
                continue;
            }

            stored.Price = item.Price;
            stored.HighPrice = item.HighPrice;
            stored.LowPrice = item.LowPrice;
            stored.ReferenceTimeMs = item.ReferenceTimeMs;
            stored.IsMitigated = item.IsMitigated;
            stored.MitigatedAtMs = item.MitigatedAtMs;
            stored.Description = item.Description;
            item.Id = stored.Id;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static SmartMoneyStructure CreateFvg(
        string symbol,
        string timeframe,
        string eventType,
        Kline first,
        Kline middle,
        Kline current,
        long availableAt)
    {
        var bullish = eventType == "FVG_BULL";
        var highPrice = bullish ? (double)current.Low : (double)first.Low;
        var lowPrice = bullish ? (double)first.High : (double)current.High;
        return new SmartMoneyStructure
        {
            Symbol = symbol,
            Timeframe = timeframe,
            TimeMs = middle.OpenTimeMs,
            OriginTimeMs = middle.OpenTimeMs,
            AvailableTimeMs = availableAt,
            EventType = eventType,
            Price = (highPrice + lowPrice) / 2,
            HighPrice = highPrice,
            LowPrice = lowPrice,
            Description = bullish ? "Bullish FVG" : "Bearish FVG",
            CalculationVersion = CalculationVersion
        };
    }

    private static string LogicalKey(SmartMoneyStructure item) =>
        $"{item.EventType}|{item.OriginTimeMs}|{item.AvailableTimeMs}|{item.CalculationVersion}";

    private static SmartMoneyStructure CreateEvent(
        string symbol,
        string timeframe,
        string eventType,
        long originTimeMs,
        long availableTimeMs,
        double price,
        string description,
        long? referenceTimeMs = null) => new()
        {
            Symbol = symbol,
            Timeframe = timeframe,
            TimeMs = originTimeMs,
            OriginTimeMs = originTimeMs,
            AvailableTimeMs = availableTimeMs,
            ReferenceTimeMs = referenceTimeMs,
            EventType = eventType,
            Price = price,
            Description = description,
            CalculationVersion = CalculationVersion
        };

    internal static TechnicalEventEvidenceDto BuildEvidence(
        SmartMoneyStructure item,
        IReadOnlyList<Kline> ordered,
        IReadOnlyDictionary<long, int> indexByOpenTime,
        IReadOnlyDictionary<long, int> indexByCloseTime)
    {
        var isFvg = item.EventType is "FVG_BULL" or "FVG_BEAR";
        return new TechnicalEventEvidenceDto
        {
            EventId = StableEventId(item),
            EventType = item.EventType,
            Description = item.Description,
            OriginTimeMs = item.OriginTimeMs,
            AvailableTimeMs = item.AvailableTimeMs,
            ReferenceTimeMs = item.ReferenceTimeMs,
            Price = item.Price,
            HighPrice = item.HighPrice,
            LowPrice = item.LowPrice,
            CalculationVersion = item.CalculationVersion,
            StateAtAsOf = isFvg ? (item.IsMitigated ? "mitigated" : "active") : "confirmed",
            MitigatedAtMs = isFvg ? item.MitigatedAtMs : null,
            InvalidatedAtMs = null,
            MitigationRule = item.EventType switch
            {
                "FVG_BULL" => "A later finalized candle trades at or below the bullish gap's lower boundary (complete fill).",
                "FVG_BEAR" => "A later finalized candle trades at or above the bearish gap's upper boundary (complete fill).",
                _ => null
            },
            InvalidationRule = null,
            SourceCandles = GetSourceCandles(item, ordered, indexByOpenTime, indexByCloseTime),
            DetectionConditions = DetectionConditions(item.EventType),
            Limitations = isFvg
                ? ["Invalidation is unavailable because this calculation version defines mitigation only.", "Intrabar fill order cannot be recovered from OHLCV candles."]
                : []
        };
    }

    private static IReadOnlyList<TechnicalSourceCandleDto> GetSourceCandles(
        SmartMoneyStructure item,
        IReadOnlyList<Kline> ordered,
        IReadOnlyDictionary<long, int> indexByOpenTime,
        IReadOnlyDictionary<long, int> indexByCloseTime)
    {
        var result = new List<TechnicalSourceCandleDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddAt(int index, string role)
        {
            if (index < 0 || index >= ordered.Count) return;
            var row = ordered[index];
            if (!seen.Add($"{role}|{row.OpenTimeMs}")) return;
            result.Add(ToSource(row, role));
        }

        var originIndex = indexByOpenTime.GetValueOrDefault(item.OriginTimeMs, -1);
        if (item.EventType is "SWING_HIGH" or "SWING_LOW")
        {
            AddAt(originIndex - 2, "pivot-left-2");
            AddAt(originIndex - 1, "pivot-left-1");
            AddAt(originIndex, "pivot-origin");
            AddAt(originIndex + 1, "pivot-right-1");
            AddAt(originIndex + 2, "pivot-right-2-confirmation");
        }
        else if (item.EventType is "FVG_BULL" or "FVG_BEAR")
        {
            AddAt(originIndex - 1, "fvg-first");
            AddAt(originIndex, "fvg-middle-origin");
            AddAt(originIndex + 1, "fvg-final-confirmation");
            if (item.MitigatedAtMs is long mitigatedAt)
            {
                var mitigationIndex = indexByCloseTime.GetValueOrDefault(mitigatedAt, -1);
                AddAt(mitigationIndex, "fvg-mitigation");
            }
        }
        else
        {
            if (item.ReferenceTimeMs is long reference)
            {
                var referenceIndex = indexByOpenTime.GetValueOrDefault(reference, -1);
                AddAt(referenceIndex - 2, "reference-pivot-left-2");
                AddAt(referenceIndex - 1, "reference-pivot-left-1");
                AddAt(referenceIndex, "reference-pivot-origin");
                AddAt(referenceIndex + 1, "reference-pivot-right-1");
                AddAt(referenceIndex + 2, "reference-pivot-right-2-confirmation");
            }
            AddAt(originIndex, "structure-break-confirmation");
        }

        return result;
    }

    private static IReadOnlyList<string> DetectionConditions(string eventType) => eventType switch
    {
        "SWING_HIGH" => ["Origin high is strictly greater than the highs of the two candles on each side.", "Event becomes available only when the second right-hand candle closes."],
        "SWING_LOW" => ["Origin low is strictly lower than the lows of the two candles on each side.", "Event becomes available only when the second right-hand candle closes."],
        "BOS_BULL" => ["A finalized candle closes above the latest confirmed swing high.", "Prior detected structure direction is bullish."],
        "CHOCH_BULL" => ["A finalized candle closes above the latest confirmed swing high.", "Prior detected structure direction is not bullish."],
        "BOS_BEAR" => ["A finalized candle closes below the latest confirmed swing low.", "Prior detected structure direction is bearish."],
        "CHOCH_BEAR" => ["A finalized candle closes below the latest confirmed swing low.", "Prior detected structure direction is not bearish."],
        "FVG_BULL" => ["The first candle's high is strictly below the third candle's low.", "Event becomes available only when the third defining candle closes."],
        "FVG_BEAR" => ["The first candle's low is strictly above the third candle's high.", "Event becomes available only when the third defining candle closes."],
        _ => []
    };

    private static TechnicalSourceCandleDto ToSource(Kline row, string role) => new()
    {
        Role = role,
        OpenTimeMs = row.OpenTimeMs,
        CloseTimeMs = row.CloseTimeMs,
        Open = row.Open,
        High = row.High,
        Low = row.Low,
        Close = row.Close,
        Volume = row.Volume
    };

    private static TechnicalReplayCandleDto ToReplayCandle(Kline row) => new()
    {
        OpenTimeMs = row.OpenTimeMs,
        CloseTimeMs = row.CloseTimeMs,
        Open = row.Open,
        High = row.High,
        Low = row.Low,
        Close = row.Close,
        Volume = row.Volume
    };

    internal static IReadOnlyList<Kline> LatestContiguousSegment(
        IReadOnlyList<Kline> orderedRows,
        string timeframe)
    {
        if (orderedRows.Count < 2) return orderedRows;
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0) return [];

        var start = 0;
        var previousValidIndex = -1;
        for (var i = 0; i < orderedRows.Count; i++)
        {
            var row = orderedRows[i];
            var hasExpectedDuration = row.OpenTimeMs <= long.MaxValue - (intervalMs - 1)
                && row.CloseTimeMs == row.OpenTimeMs + intervalMs - 1;
            if (!hasExpectedDuration)
            {
                start = i + 1;
                previousValidIndex = -1;
                continue;
            }

            if (previousValidIndex >= 0
                && row.OpenTimeMs - orderedRows[previousValidIndex].OpenTimeMs != intervalMs)
                start = i;
            previousValidIndex = i;
        }

        return start == 0 ? orderedRows : orderedRows.Skip(start).ToArray();
    }

    internal static string StableEventId(SmartMoneyStructure item) =>
        $"{item.CalculationVersion}:{item.Symbol}:{item.Timeframe}:{item.EventType}:{item.OriginTimeMs}:{item.AvailableTimeMs}:{item.ReferenceTimeMs?.ToString() ?? "none"}";

    private static bool IsSwingHigh(IReadOnlyList<Kline> rows, int i) =>
        i >= 2 && i + 2 < rows.Count
        && rows[i].High > rows[i - 1].High && rows[i].High > rows[i - 2].High
        && rows[i].High > rows[i + 1].High && rows[i].High > rows[i + 2].High;

    private static bool IsSwingLow(IReadOnlyList<Kline> rows, int i) =>
        i >= 2 && i + 2 < rows.Count
        && rows[i].Low < rows[i - 1].Low && rows[i].Low < rows[i - 2].Low
        && rows[i].Low < rows[i + 1].Low && rows[i].Low < rows[i + 2].Low;

    private static long CloseOrOpenTime(Kline row) => row.CloseTimeMs > 0 ? row.CloseTimeMs : row.OpenTimeMs;

    private sealed record Pivot(double Price, long OriginTimeMs);
}

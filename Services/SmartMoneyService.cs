using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class SmartMoneyService : ISmartMoneyService
{
    internal const string CalculationVersion = "smc-causal-v2";
    private readonly AppDbContext _db;
    private readonly ProductionTimeframePolicy _timeframePolicy;

    public SmartMoneyService(AppDbContext db, ProductionTimeframePolicy? timeframePolicy = null)
    {
        _db = db;
        _timeframePolicy = timeframePolicy ?? new ProductionTimeframePolicy();
    }

    public async Task<List<SmartMoneyStructure>> GetSmartMoneyStructuresAsync(
        string symbol,
        string timeframe,
        int lookbackBars,
        CancellationToken ct = default)
    {
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

    internal static List<SmartMoneyStructure> DetectStructures(
        IReadOnlyList<Kline> orderedKlines,
        string symbol,
        string timeframe)
    {
        var structures = new List<SmartMoneyStructure>();
        if (orderedKlines.Count < 3) return structures;

        Pivot? activeHigh = null;
        Pivot? activeLow = null;
        var currentTrend = 0;

        for (var i = 0; i < orderedKlines.Count; i++)
        {
            var current = orderedKlines[i];
            var availableAt = CloseOrOpenTime(current);

            // A five-bar pivot at i-2 becomes knowable only when bar i is finalized.
            if (i >= 4)
            {
                var pivotIndex = i - 2;
                var pivot = orderedKlines[pivotIndex];
                if (IsSwingHigh(orderedKlines, pivotIndex))
                {
                    activeHigh = new Pivot((double)pivot.High, pivot.OpenTimeMs);
                    structures.Add(CreateEvent(symbol, timeframe, "SWING_HIGH", pivot.OpenTimeMs,
                        availableAt, (double)pivot.High, "Swing High (confirmed after two bars)"));
                }

                if (IsSwingLow(orderedKlines, pivotIndex))
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
                var first = orderedKlines[i - 2];
                var middle = orderedKlines[i - 1];
                if (first.High < current.Low)
                {
                    structures.Add(CreateFvg(symbol, timeframe, "FVG_BULL", first, middle, current, availableAt));
                }
                else if (first.Low > current.High)
                {
                    structures.Add(CreateFvg(symbol, timeframe, "FVG_BEAR", first, middle, current, availableAt));
                }
            }

            foreach (var fvg in structures.Where(x => !x.IsMitigated && x.AvailableTimeMs < availableAt))
            {
                var mitigated = fvg.EventType == "FVG_BULL"
                    ? fvg.LowPrice.HasValue && (double)current.Low <= fvg.LowPrice.Value
                    : fvg.EventType == "FVG_BEAR" && fvg.HighPrice.HasValue && (double)current.High >= fvg.HighPrice.Value;
                if (!mitigated) continue;
                fvg.IsMitigated = true;
                fvg.MitigatedAtMs = availableAt;
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

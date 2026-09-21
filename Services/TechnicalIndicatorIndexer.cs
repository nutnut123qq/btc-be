using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Tính và lưu các chỉ báo kỹ thuật từ Klines trong DB.
/// Hỗ trợ incremental indexing: chỉ tính cho các nến chưa có chỉ báo.
/// </summary>
public class TechnicalIndicatorIndexer
{
    // EMA200 seeded from an SMA still carries seed error. 1,000 bars leaves
    // roughly (199/201)^(1000-200) ~= 0.034% of that initial error while
    // remaining small for the production 1h/4h/1d datasets.
    internal const int RequiredWarmupBars = 1_000;

    private readonly AppDbContext _db;
    private readonly ILogger<TechnicalIndicatorIndexer> _logger;
    private readonly IndexingOptions _options;

    /// <summary>
    /// Projection nhẹ để tránh giữ hàng triệu entity Kline đầy đủ trong memory.
    /// </summary>
    internal readonly record struct Bar(
        long OpenTimeMs,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume,
        decimal QuoteVolume,
        decimal TakerBuyVolume,
        decimal TakerBuyQuoteVolume,
        long TradeCount);

    public TechnicalIndicatorIndexer(
        AppDbContext db,
        ILogger<TechnicalIndicatorIndexer> logger,
        IOptions<IndexingOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Incremental indexing từ DB. Chỉ load klines từ max existing - warmup thay vì toàn bộ lịch sử.
    /// </summary>
    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0)
        {
            _logger.LogWarning("Invalid timeframe {Timeframe} for technical indicator indexing", timeframe);
            return 0;
        }

        var maxExistingMs = await _db.TechnicalIndicators
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .Select(x => (long?)x.OpenTimeMs)
            .MaxAsync(cancellationToken);

        var maxKlineMs = await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .Select(k => (long?)k.OpenTimeMs)
            .MaxAsync(cancellationToken);

        if (maxExistingMs.HasValue && maxKlineMs.HasValue && maxExistingMs.Value >= maxKlineMs.Value)
        {
            _logger.LogInformation("Technical indicators already up-to-date for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        // Load the complete active-timeframe history. OBV and UTC-session VWAP
        // have cumulative state that cannot be reconstructed from a generic
        // fixed lookback without persisted seeds. Production scope is only
        // BTC 1h/4h/1d, so exact replay is both bounded and preferable to a
        // silently approximate incremental value.
        long? startMs = null;
        var endMs = maxKlineMs;

        var bars = await LoadBarsAsync(symbol, timeframe, startMs, endMs, cancellationToken);
        if (bars.Count < 50)
        {
            _logger.LogWarning("Not enough klines in incremental range for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        var existingKeys = await LoadExistingKeysAsync(symbol, timeframe, bars[0].OpenTimeMs, bars[^1].OpenTimeMs, cancellationToken);
        return await IndexBarsAsync(symbol, timeframe, bars, existingKeys, cancellationToken);
    }

    /// <summary>
    /// Index từ klines đã có sẵn (caller cung cấp). Tránh query DB lại.
    /// </summary>
    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count < 50)
        {
            _logger.LogWarning("Not enough klines to compute indicators for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        var bars = klines.Select(k => new Bar(
            k.OpenTimeMs,
            k.Open,
            k.High,
            k.Low,
            k.Close,
            k.Volume,
            k.QuoteVolume,
            k.TakerBuyVolume,
            k.TakerBuyQuoteVolume,
            k.TradeCount)).ToList();

        var startMs = klines[0].OpenTimeMs;
        var endMs = klines[^1].OpenTimeMs;
        var existingKeys = await LoadExistingKeysAsync(symbol, timeframe, startMs, endMs, cancellationToken);

        return await IndexBarsAsync(symbol, timeframe, bars, existingKeys, cancellationToken);
    }

    private async Task<int> IndexBarsAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<Bar> bars,
        HashSet<long> existingKeys,
        CancellationToken cancellationToken)
    {
        var indicators = CalculateIndicators(bars, Timeframes.IntervalToMs(timeframe));

        var batchSize = Math.Max(100, _options.TechnicalIndicatorsBatchSize);
        var batch = new List<TechnicalIndicator>(batchSize);
        var totalAdded = 0;

        for (int i = 0; i < bars.Count; i++)
        {
            var k = bars[i];
            if (existingKeys.Contains(k.OpenTimeMs))
                continue;

            var atr = indicators.Atr14[i];
            var atrValue = atr.GetValueOrDefault();
            var macdNorm = indicators.Macd.MacdLine[i].HasValue && atrValue > 0
                ? (double?)(indicators.Macd.MacdLine[i].GetValueOrDefault() / atrValue)
                : null;
            var macdSignalNorm = indicators.Macd.SignalLine[i].HasValue && atrValue > 0
                ? (double?)(indicators.Macd.SignalLine[i].GetValueOrDefault() / atrValue)
                : null;
            var macdHistogramNorm = indicators.Macd.Histogram[i].HasValue && atrValue > 0
                ? (double?)(indicators.Macd.Histogram[i].GetValueOrDefault() / atrValue)
                : null;

            batch.Add(new TechnicalIndicator
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeMs = k.OpenTimeMs,
                Rsi14 = indicators.Rsi14[i],
                Ema12 = indicators.Ema12[i],
                Ema26 = indicators.Ema26[i],
                Ema50 = indicators.Ema50[i],
                Ema200 = indicators.Ema200[i],
                Sma50 = indicators.Sma50[i],
                Sma200 = indicators.Sma200[i],
                Macd = indicators.Macd.MacdLine[i],
                MacdSignal = indicators.Macd.SignalLine[i],
                MacdHistogram = indicators.Macd.Histogram[i],
                MacdNorm = macdNorm,
                MacdSignalNorm = macdSignalNorm,
                MacdHistogramNorm = macdHistogramNorm,
                BollingerUpper = indicators.Bollinger.Upper[i],
                BollingerMiddle = indicators.Bollinger.Middle[i],
                BollingerLower = indicators.Bollinger.Lower[i],
                Atr14 = atr,
                Obv = indicators.Obv[i],
                ObvEma50 = indicators.ObvEma50[i].HasValue ? (double?)indicators.ObvEma50[i].GetValueOrDefault() : null,
                Vwap = indicators.Vwap[i],
                RollingVwap24 = indicators.RollingVwap24[i]
            });

            if (batch.Count >= batchSize)
            {
                _db.TechnicalIndicators.AddRange(batch);
                await _db.SaveChangesAsync(cancellationToken);
                totalAdded += batch.Count;
                batch.Clear();
                _db.ChangeTracker.Clear();
            }
        }

        if (batch.Count > 0)
        {
            _db.TechnicalIndicators.AddRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            totalAdded += batch.Count;
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation(
            "Indexed {Added} technical indicators for {Symbol} {Timeframe}",
            totalAdded, symbol, timeframe);

        return totalAdded;
    }

    private async Task<HashSet<long>> LoadExistingKeysAsync(
        string symbol,
        string timeframe,
        long startMs,
        long endMs,
        CancellationToken cancellationToken)
    {
        var keys = await _db.TechnicalIndicators
            .AsNoTracking()
            .Where(x =>
                x.Symbol == symbol &&
                x.Timeframe == timeframe &&
                x.OpenTimeMs >= startMs &&
                x.OpenTimeMs <= endMs)
            .Select(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        return keys.ToHashSet();
    }

    private async Task<IReadOnlyList<Bar>> LoadBarsAsync(
        string symbol,
        string timeframe,
        long? startMs,
        long? endMs,
        CancellationToken cancellationToken)
    {
        var finalizedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.CloseTimeMs <= finalizedAtMs);

        if (startMs.HasValue) query = query.Where(k => k.OpenTimeMs >= startMs.Value);
        if (endMs.HasValue) query = query.Where(k => k.OpenTimeMs <= endMs.Value);

        return await query
            .OrderBy(k => k.OpenTimeMs)
            .Select(k => new Bar(
                k.OpenTimeMs,
                k.Open,
                k.High,
                k.Low,
                k.Close,
                k.Volume,
                k.QuoteVolume,
                k.TakerBuyVolume,
                k.TakerBuyQuoteVolume,
                k.TradeCount))
            .ToListAsync(cancellationToken);
    }

    internal static IndicatorSeries CalculateIndicators(IReadOnlyList<Bar> bars, long intervalMs)
    {
        var series = IndicatorSeries.Empty(bars.Count);
        if (bars.Count == 0)
            return series;

        var segmentStart = 0;
        for (var i = 1; i <= bars.Count; i++)
        {
            // A gap makes prior-close and cumulative state unknowable. Restart
            // every indicator and expose null warmup rather than bridge it.
            if (i < bars.Count && intervalMs > 0 && bars[i].OpenTimeMs - bars[i - 1].OpenTimeMs == intervalMs)
                continue;

            FillSegment(series, bars, segmentStart, i - segmentStart);
            segmentStart = i;
        }
        return series;
    }

    private static void FillSegment(IndicatorSeries target, IReadOnlyList<Bar> bars, int start, int count)
    {
        var segment = bars.Skip(start).Take(count).ToArray();
        var closes = segment.Select(k => k.Close).ToList();
        var ema12 = ComputeEma(closes, 12);
        var ema26 = ComputeEma(closes, 26);
        var ema50 = ComputeEma(closes, 50);
        var ema200 = ComputeEma(closes, 200);
        var sma50 = ComputeSma(closes, 50);
        var sma200 = ComputeSma(closes, 200);
        var rsi14 = ComputeRsi(closes, 14);
        var bollinger = ComputeBollinger(closes, 20, 2.0);
        var atr14 = ComputeAtr(segment, 14);
        var obv = ComputeObv(segment);
        var obvEma50 = ComputeEmaOfDouble(obv, 50);
        var macd = ComputeMacd(ema12, ema26);
        var vwap = ComputeVwap(segment);
        var rollingVwap = ComputeRollingVwap(segment, 24);

        for (var i = 0; i < count; i++)
        {
            var at = start + i;
            target.Ema12[at] = ema12[i]; target.Ema26[at] = ema26[i];
            target.Ema50[at] = ema50[i]; target.Ema200[at] = ema200[i];
            target.Sma50[at] = sma50[i]; target.Sma200[at] = sma200[i];
            target.Rsi14[at] = rsi14[i]; target.Atr14[at] = atr14[i];
            target.Obv[at] = obv[i]; target.ObvEma50[at] = obvEma50[i];
            target.Vwap[at] = vwap[i]; target.RollingVwap24[at] = rollingVwap[i];
            target.Bollinger.Upper[at] = bollinger.Upper[i];
            target.Bollinger.Middle[at] = bollinger.Middle[i];
            target.Bollinger.Lower[at] = bollinger.Lower[i];
            target.Macd.MacdLine[at] = macd.MacdLine[i];
            target.Macd.SignalLine[at] = macd.SignalLine[i];
            target.Macd.Histogram[at] = macd.Histogram[i];
        }
    }

    internal sealed record IndicatorSeries(
        List<decimal?> Ema12, List<decimal?> Ema26, List<decimal?> Ema50, List<decimal?> Ema200,
        List<decimal?> Sma50, List<decimal?> Sma200, List<double?> Rsi14,
        (List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower) Bollinger,
        List<double?> Atr14, List<double?> Obv, List<decimal?> ObvEma50,
        (List<double?> MacdLine, List<double?> SignalLine, List<double?> Histogram) Macd,
        List<decimal?> Vwap, List<decimal?> RollingVwap24)
    {
        public static IndicatorSeries Empty(int count) => new(
            NullDecimals(count), NullDecimals(count), NullDecimals(count), NullDecimals(count),
            NullDecimals(count), NullDecimals(count), NullDoubles(count),
            (NullDecimals(count), NullDecimals(count), NullDecimals(count)),
            NullDoubles(count), NullDoubles(count), NullDecimals(count),
            (NullDoubles(count), NullDoubles(count), NullDoubles(count)),
            NullDecimals(count), NullDecimals(count));

        private static List<decimal?> NullDecimals(int count) => Enumerable.Repeat<decimal?>(null, count).ToList();
        private static List<double?> NullDoubles(int count) => Enumerable.Repeat<double?>(null, count).ToList();
    }

    internal static List<decimal?> ComputeEma(IReadOnlyList<decimal> prices, int period)
    {
        var result = new List<decimal?>(prices.Count);
        decimal multiplier = 2m / (period + 1);
        decimal? prevEma = null;

        for (int i = 0; i < prices.Count; i++)
        {
            if (i < period - 1)
            {
                result.Add(null);
                continue;
            }
            if (prevEma == null)
            {
                var sma = prices.Take(period).Average();
                prevEma = sma;
                result.Add(prevEma);
                continue;
            }
            prevEma = ((prices[i] - prevEma.Value) * multiplier) + prevEma.Value;
            result.Add(prevEma);
        }
        return result;
    }

    private static List<decimal?> ComputeSma(IReadOnlyList<decimal> prices, int period)
    {
        var result = new List<decimal?>(prices.Count);
        decimal sum = 0;
        var window = new Queue<decimal>(period);

        for (int i = 0; i < prices.Count; i++)
        {
            sum += prices[i];
            window.Enqueue(prices[i]);
            if (window.Count > period)
                sum -= window.Dequeue();

            if (i < period - 1)
                result.Add(null);
            else
                result.Add(sum / period);
        }
        return result;
    }

    internal static List<double?> ComputeRsi(IReadOnlyList<decimal> prices, int period)
    {
        var result = new List<double?>(prices.Count);
        double avgGain = 0, avgLoss = 0;

        for (int i = 0; i < prices.Count; i++)
        {
            if (i == 0)
            {
                result.Add(null);
                continue;
            }
            var delta = (double)(prices[i] - prices[i - 1]);
            var gain = delta > 0 ? delta : 0;
            var loss = delta < 0 ? -delta : 0;

            if (i <= period)
            {
                avgGain += gain;
                avgLoss += loss;
                if (i < period)
                {
                    result.Add(null);
                    continue;
                }
                avgGain /= period;
                avgLoss /= period;
            }
            else
            {
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }

            if (avgLoss == 0)
                result.Add(avgGain == 0 ? 50 : 100);
            else
            {
                var rs = avgGain / avgLoss;
                result.Add(100 - 100 / (1 + rs));
            }
        }
        return result;
    }

    private static (List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower) ComputeBollinger(
        IReadOnlyList<decimal> prices, int period, double stdDevMultiplier)
    {
        var upper = new List<decimal?>(prices.Count);
        var middle = new List<decimal?>(prices.Count);
        var lower = new List<decimal?>(prices.Count);

        decimal sum = 0;
        double sumSq = 0;
        var window = new Queue<decimal>(period);

        for (int i = 0; i < prices.Count; i++)
        {
            var price = prices[i];
            sum += price;
            sumSq += (double)price * (double)price;
            window.Enqueue(price);
            if (window.Count > period)
            {
                var removed = window.Dequeue();
                sum -= removed;
                sumSq -= (double)removed * (double)removed;
            }

            if (i < period - 1)
            {
                upper.Add(null); middle.Add(null); lower.Add(null);
                continue;
            }

            var sma = sum / period;
            var mean = (double)sma;
            var variance = sumSq / period - mean * mean;
            if (variance < 0) variance = 0;
            var std = (decimal)Math.Sqrt(variance);

            upper.Add(sma + std * (decimal)stdDevMultiplier);
            middle.Add(sma);
            lower.Add(sma - std * (decimal)stdDevMultiplier);
        }
        return (upper, middle, lower);
    }

    internal static List<double?> ComputeAtr(IReadOnlyList<Bar> bars, int period)
    {
        var result = new List<double?>(bars.Count);
        var trs = new List<double>(period + 1);

        for (int i = 0; i < bars.Count; i++)
        {
            var high = (double)bars[i].High;
            var low = (double)bars[i].Low;
            var prevClose = i > 0 ? (double)bars[i - 1].Close : (double)bars[i].Open;
            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trs.Add(tr);

            if (i < period - 1)
            {
                result.Add(null);
                continue;
            }
            if (i == period - 1)
            {
                var atr = trs.Take(period).Average();
                result.Add(atr);
            }
            else
            {
                var prevAtr = result[i - 1]!.Value;
                var atr = (prevAtr * (period - 1) + tr) / period;
                result.Add(atr);
            }
        }
        return result;
    }

    private static List<double?> ComputeObv(IReadOnlyList<Bar> bars)
    {
        var result = new List<double?>(bars.Count);
        double obv = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            if (i == 0)
            {
                // Conventional OBV starts at zero. The absolute level is
                // arbitrary; only causal changes carry information.
                obv = 0;
            }
            else
            {
                if (bars[i].Close > bars[i - 1].Close)
                    obv += (double)bars[i].Volume;
                else if (bars[i].Close < bars[i - 1].Close)
                    obv -= (double)bars[i].Volume;
            }
            result.Add(obv);
        }
        return result;
    }

    private static (List<double?> MacdLine, List<double?> SignalLine, List<double?> Histogram) ComputeMacd(
        IReadOnlyList<decimal?> ema12, IReadOnlyList<decimal?> ema26)
    {
        var macd = new List<double?>(ema12.Count);
        for (int i = 0; i < ema12.Count; i++)
        {
            if (ema12[i].HasValue && ema26[i].HasValue)
                macd.Add((double)(ema12[i]!.Value - ema26[i]!.Value));
            else
                macd.Add(null);
        }

        var signal = ComputeEmaOfDouble(macd, 9);
        var hist = new List<double?>(macd.Count);
        for (int i = 0; i < macd.Count; i++)
        {
            if (macd[i].HasValue && signal[i].HasValue)
                hist.Add(macd[i]!.Value - (double)signal[i]!.Value);
            else
                hist.Add(null);
        }
        var signalDouble = signal.Select(s => s.HasValue ? (double?)s.Value : null).ToList();
        return (macd, signalDouble, hist);
    }

    private static List<decimal?> ComputeEmaOfDouble(IReadOnlyList<double?> values, int period)
    {
        var result = new List<decimal?>(values.Count);
        decimal multiplier = 2m / (period + 1);
        decimal? prevEma = null;

        for (int i = 0; i < values.Count; i++)
        {
            if (!values[i].HasValue)
            {
                result.Add(null);
                continue;
            }
            var price = (decimal)values[i]!.Value;
            if (prevEma == null)
            {
                var seed = values.Take(i + 1).Where(v => v.HasValue).Select(v => (decimal)v!.Value).ToList();
                if (seed.Count < period)
                {
                    result.Add(null);
                    continue;
                }
                prevEma = seed.TakeLast(period).Average();
                result.Add(prevEma);
                continue;
            }
            prevEma = ((price - prevEma.Value) * multiplier) + prevEma.Value;
            result.Add(prevEma);
        }
        return result;
    }

    private static List<decimal?> ComputeVwap(IReadOnlyList<Bar> bars)
    {
        var result = new List<decimal?>(bars.Count);
        decimal cumTpVol = 0;
        decimal cumVol = 0;
        long currentDay = long.MinValue;

        for (int i = 0; i < bars.Count; i++)
        {
            var k = bars[i];
            var day = k.OpenTimeMs / 86_400_000L;
            if (day != currentDay)
            {
                cumTpVol = 0;
                cumVol = 0;
                currentDay = day;
            }
            var tp = (k.High + k.Low + k.Close) / 3m;
            cumTpVol += tp * k.Volume;
            cumVol += k.Volume;
            result.Add(cumVol > 0 ? cumTpVol / cumVol : null);
        }
        return result;
    }

    private static List<decimal?> ComputeRollingVwap(IReadOnlyList<Bar> bars, int period)
    {
        var result = new List<decimal?>(bars.Count);
        var tpVols = new Queue<decimal>(period);
        var vols = new Queue<decimal>(period);
        decimal cumTpVol = 0;
        decimal cumVol = 0;

        for (int i = 0; i < bars.Count; i++)
        {
            var k = bars[i];
            var tp = (k.High + k.Low + k.Close) / 3m;
            var tpVol = tp * k.Volume;

            tpVols.Enqueue(tpVol);
            vols.Enqueue(k.Volume);
            cumTpVol += tpVol;
            cumVol += k.Volume;

            if (tpVols.Count > period)
            {
                cumTpVol -= tpVols.Dequeue();
                cumVol -= vols.Dequeue();
            }

            result.Add(cumVol > 0 ? cumTpVol / cumVol : null);
        }
        return result;
    }
}

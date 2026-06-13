using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Tính và lưu các chỉ báo kỹ thuật từ Klines trong DB.
/// </summary>
public class TechnicalIndicatorIndexer
{
    private readonly AppDbContext _db;
    private readonly ILogger<TechnicalIndicatorIndexer> _logger;

    public TechnicalIndicatorIndexer(AppDbContext db, ILogger<TechnicalIndicatorIndexer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> IndexAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var klines = await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderBy(k => k.OpenTimeMs)
            .ToListAsync(cancellationToken);

        if (klines.Count < 50)
        {
            _logger.LogWarning("Not enough klines to compute indicators for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        var existing = await _db.TechnicalIndicators
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .Select(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);
        var existingSet = new HashSet<long>(existing);

        var ema12 = ComputeEma(klines.Select(k => k.Close).ToList(), 12);
        var ema26 = ComputeEma(klines.Select(k => k.Close).ToList(), 26);
        var ema50 = ComputeEma(klines.Select(k => k.Close).ToList(), 50);
        var ema200 = ComputeEma(klines.Select(k => k.Close).ToList(), 200);
        var rsi14 = ComputeRsi(klines.Select(k => k.Close).ToList(), 14);
        var bb = ComputeBollinger(klines.Select(k => k.Close).ToList(), 20, 2.0);
        var atr14 = ComputeAtr(klines, 14);
        var obv = ComputeObv(klines);
        var obvEma50 = ComputeEmaOfDouble(obv, 50);
        var macd = ComputeMacd(ema12, ema26);
        var vwap = ComputeVwap(klines, timeframe);
        var rollingVwap = ComputeRollingVwap(klines, 24);

        var toAdd = new List<TechnicalIndicator>();
        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
            if (existingSet.Contains(k.OpenTimeMs)) continue;

            var atr = atr14[i];
            var macdNorm = macd.MacdLine[i].HasValue && atr.HasValue && atr.Value > 0
                ? (double?)(macd.MacdLine[i].Value / atr.Value)
                : null;
            var macdSignalNorm = macd.SignalLine[i].HasValue && atr.HasValue && atr.Value > 0
                ? (double?)(macd.SignalLine[i].Value / atr.Value)
                : null;
            var macdHistogramNorm = macd.Histogram[i].HasValue && atr.HasValue && atr.Value > 0
                ? (double?)(macd.Histogram[i].Value / atr.Value)
                : null;

            toAdd.Add(new TechnicalIndicator
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeMs = k.OpenTimeMs,
                Rsi14 = rsi14[i],
                Ema12 = ema12[i],
                Ema26 = ema26[i],
                Ema50 = ema50[i],
                Ema200 = ema200[i],
                Macd = macd.MacdLine[i],
                MacdSignal = macd.SignalLine[i],
                MacdHistogram = macd.Histogram[i],
                MacdNorm = macdNorm,
                MacdSignalNorm = macdSignalNorm,
                MacdHistogramNorm = macdHistogramNorm,
                BollingerUpper = bb.Upper[i],
                BollingerMiddle = bb.Middle[i],
                BollingerLower = bb.Lower[i],
                Atr14 = atr,
                Obv = obv[i],
                ObvEma50 = obvEma50[i].HasValue ? (double?)obvEma50[i].Value : null,
                Vwap = vwap[i],
                RollingVwap24 = rollingVwap[i]
            });
        }

        if (toAdd.Count > 0)
        {
            _db.TechnicalIndicators.AddRange(toAdd);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Indexed {Count} technical indicators for {Symbol} {Timeframe}", toAdd.Count, symbol, timeframe);
        }

        return toAdd.Count;
    }

    private static List<decimal?> ComputeEma(IReadOnlyList<decimal> prices, int period)
    {
        var result = new List<decimal?>();
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

    private static List<double?> ComputeRsi(IReadOnlyList<decimal> prices, int period)
    {
        var result = new List<double?>();
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

            if (i < period)
            {
                result.Add(null);
                avgGain += gain;
                avgLoss += loss;
                if (i == period - 1)
                {
                    avgGain /= period;
                    avgLoss /= period;
                }
                continue;
            }

            if (i == period)
            {
                avgGain = (avgGain + gain) / period; // smooth
                avgLoss = (avgLoss + loss) / period;
            }
            else
            {
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }

            if (avgLoss == 0)
                result.Add(100);
            else
            {
                var rs = avgGain / avgLoss;
                result.Add(100 - (100 / (1 + rs)));
            }
        }
        return result;
    }

    private static (List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower) ComputeBollinger(
        IReadOnlyList<decimal> prices, int period, double stdDevMultiplier)
    {
        var upper = new List<decimal?>();
        var middle = new List<decimal?>();
        var lower = new List<decimal?>();

        for (int i = 0; i < prices.Count; i++)
        {
            if (i < period - 1)
            {
                upper.Add(null); middle.Add(null); lower.Add(null);
                continue;
            }
            var slice = prices.Skip(i - period + 1).Take(period).Select(p => (double)p).ToList();
            var sma = (decimal)slice.Average();
            var variance = slice.Average(p => (p - (double)sma) * (p - (double)sma));
            var std = (decimal)Math.Sqrt(variance);
            upper.Add(sma + std * (decimal)stdDevMultiplier);
            middle.Add(sma);
            lower.Add(sma - std * (decimal)stdDevMultiplier);
        }
        return (upper, middle, lower);
    }

    private static List<double?> ComputeAtr(IReadOnlyList<Kline> klines, int period)
    {
        var result = new List<double?>();
        var trs = new List<double>();

        for (int i = 0; i < klines.Count; i++)
        {
            var high = (double)klines[i].High;
            var low = (double)klines[i].Low;
            var prevClose = i > 0 ? (double)klines[i - 1].Close : (double)klines[i].Open;
            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trs.Add(tr);

            if (i < period)
            {
                result.Add(null);
                continue;
            }
            if (i == period)
            {
                var atr = trs.Take(period + 1).Average();
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

    private static List<double?> ComputeObv(IReadOnlyList<Kline> klines)
    {
        var result = new List<double?>();
        double obv = 0;
        for (int i = 0; i < klines.Count; i++)
        {
            if (i == 0)
            {
                obv = (double)klines[i].Volume;
            }
            else
            {
                if (klines[i].Close > klines[i - 1].Close)
                    obv += (double)klines[i].Volume;
                else if (klines[i].Close < klines[i - 1].Close)
                    obv -= (double)klines[i].Volume;
            }
            result.Add(obv);
        }
        return result;
    }

    private static (List<double?> MacdLine, List<double?> SignalLine, List<double?> Histogram) ComputeMacd(
        IReadOnlyList<decimal?> ema12, IReadOnlyList<decimal?> ema26)
    {
        var macd = new List<double?>();
        for (int i = 0; i < ema12.Count; i++)
        {
            if (ema12[i].HasValue && ema26[i].HasValue)
                macd.Add((double)(ema12[i]!.Value - ema26[i]!.Value));
            else
                macd.Add(null);
        }

        var signal = ComputeEmaOfDouble(macd, 9);
        var hist = new List<double?>();
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
        var result = new List<decimal?>();
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
                // Find first 'period' valid values for SMA seed
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

    private static List<decimal?> ComputeVwap(IReadOnlyList<Kline> klines, string timeframe)
    {
        // VWAP reset mỗi ngày cho timeframe < 1d
        var result = new List<decimal?>();
        decimal cumTpVol = 0;
        decimal cumVol = 0;
        long currentDay = 0;

        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
            var day = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime.Date.Ticks;
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

    private static List<decimal?> ComputeRollingVwap(IReadOnlyList<Kline> klines, int period)
    {
        var result = new List<decimal?>();
        var tpVols = new Queue<decimal>();
        var vols = new Queue<decimal>();
        decimal cumTpVol = 0;
        decimal cumVol = 0;

        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
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

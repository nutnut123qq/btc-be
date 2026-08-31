using Backend.Data;
using Backend.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Computes ML-ready per-bar features and price targets into MlFeatureStore and PriceTargets.
/// Hỗ trợ incremental indexing: chỉ tính cho các nến chưa có feature/target.
/// </summary>
public class MlDatasetService : IMlDatasetService
{
    private readonly AppDbContext _db;
    private readonly ILogger<MlDatasetService> _logger;
    private readonly IndexingOptions _options;
    private readonly DataAuditCache? _auditCache;

    public MlDatasetService(
        AppDbContext db,
        ILogger<MlDatasetService> logger,
        IOptions<IndexingOptions> options,
        DataAuditCache? auditCache = null)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
        _auditCache = auditCache;
    }

    public async Task<int> BuildAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
    {
        var intervalMs = Timeframes.IntervalToMs(timeframe);
        if (intervalMs <= 0)
        {
            _logger.LogWarning("Invalid timeframe {Timeframe} for ML dataset build", timeframe);
            return 0;
        }

        var existingFeatureTimes = (await _db.MlFeatureStores
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .Select(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var existingTargetTimes = (await _db.PriceTargets
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .Select(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var maxFeatureTime = existingFeatureTimes.Count > 0 ? existingFeatureTimes.Max() : (long?)null;
        var maxTargetTime = existingTargetTimes.Count > 0 ? existingTargetTimes.Max() : (long?)null;
        var maxExistingTime = maxFeatureTime.HasValue && maxTargetTime.HasValue
            ? Math.Max(maxFeatureTime.Value, maxTargetTime.Value)
            : maxFeatureTime ?? maxTargetTime ?? 0L;

        var warmupBars = _options.MlDatasetWarmupBars;
        // ponytail: cap klines loaded per call (memory safety) so a huge backfill gap (1m ≈ 3M bars)
        // can't load millions of klines/indicators/patterns at once and OOM. Cap by COUNT via Take so
        // the window is always anchored to real data; large gaps converge over repeated calls, and a
        // one-shot rebuild loops BuildAsync until it returns 0.
        const int maxBarsPerBuild = 200_000;

        long startMs = maxExistingTime == 0L
            ? 0L
            : Math.Max(0L, maxExistingTime - warmupBars * intervalMs);

        var klines = await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe && k.OpenTimeMs >= startMs)
            .OrderBy(k => k.OpenTimeMs)
            .Take(warmupBars + maxBarsPerBuild)
            .ToListAsync(cancellationToken);

        if (klines.Count < 300)
        {
            _logger.LogWarning("Not enough klines to build ML dataset for {Symbol} {Timeframe}: {Count}", symbol, timeframe, klines.Count);
            return 0;
        }

        // Upper bound of the loaded window — co-loads (indicators/patterns/…) track the same range.
        var endMsValue = klines[^1].OpenTimeMs;

        var allKlineTimes = klines.Select(k => k.OpenTimeMs).ToList();
        var missingFeatureTimes = allKlineTimes.Where(t => !existingFeatureTimes.Contains(t)).ToHashSet();
        var missingTargetTimes = allKlineTimes.Where(t => !existingTargetTimes.Contains(t)).ToHashSet();

        if (missingFeatureTimes.Count == 0 && missingTargetTimes.Count == 0)
        {
            _logger.LogInformation("ML dataset already up-to-date for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        if (klines.Count < 300)
        {
            _logger.LogWarning("Not enough klines in incremental range for {Symbol} {Timeframe}", symbol, timeframe);
            return 0;
        }

        var indicators = await _db.TechnicalIndicators
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.OpenTimeMs >= startMs && x.OpenTimeMs <= endMsValue)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        var orderedIndicators = indicators.OrderBy(x => x.Key).ToList();
        var indicatorIndexByTime = new Dictionary<long, int>(orderedIndicators.Count);
        for (int ii = 0; ii < orderedIndicators.Count; ii++)
            indicatorIndexByTime[orderedIndicators[ii].Key] = ii;

        var volumeStats = await _db.CandleVolumeStats
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.OpenTimeMs >= startMs && x.OpenTimeMs <= endMsValue)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        var marketMetrics = await _db.MarketMetrics
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.OpenTimeMs >= startMs && x.OpenTimeMs <= endMsValue)
            .OrderBy(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        var patterns = await _db.CandlePatterns
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe && x.OpenTimeMs >= startMs && x.OpenTimeMs <= endMsValue)
            .OrderBy(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        var activeRules = await _db.CandleSequenceRules
            .AsNoTracking()
            .CountAsync(r => r.Symbol == symbol && r.Timeframe == timeframe && r.IsEnabled, cancellationToken);

        var batchSize = Math.Max(100, _options.MlFeatureBatchSize);
        var featureAdds = new List<MlFeatureStore>(batchSize);
        var targetAdds = new List<PriceTarget>(batchSize * 5);
        var totalFeatureAdds = 0;
        var totalTargetAdds = 0;

        var closes = klines.Select(k => (double)k.Close).ToArray();
        var volumes = klines.Select(k => (double)k.Volume).ToArray();
        var closeZscores = ComputeRollingZscore(closes, 20);
        var volumeZscores = ComputeRollingZscore(volumes, 20);
        var volumeSmaRatios = ComputeRollingSmaRatio(volumes, 20);

        async Task FlushAddsAsync()
        {
            if (featureAdds.Count > 0)
                _db.MlFeatureStores.AddRange(featureAdds);
            if (targetAdds.Count > 0)
                _db.PriceTargets.AddRange(targetAdds);
            if (featureAdds.Count > 0 || targetAdds.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                _db.ChangeTracker.Clear();
            }
            totalFeatureAdds += featureAdds.Count;
            totalTargetAdds += targetAdds.Count;
            featureAdds.Clear();
            targetAdds.Clear();
        }

        int patternPointer = 0;
        int metricPointer = 0;

        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
            var needFeature = missingFeatureTimes.Contains(k.OpenTimeMs);
            var needTarget = missingTargetTimes.Contains(k.OpenTimeMs);
            if (!needFeature && !needTarget)
                continue;

            while (patternPointer < patterns.Count && patterns[patternPointer].OpenTimeMs <= k.OpenTimeMs)
                patternPointer++;
            var recentPattern = patternPointer > 0 ? patterns[patternPointer - 1] : null;

            while (metricPointer < marketMetrics.Count && marketMetrics[metricPointer].OpenTimeMs <= k.OpenTimeMs)
                metricPointer++;
            var nearestMetric = metricPointer > 0 ? marketMetrics[metricPointer - 1] : null;

            if (needFeature)
            {
                var newFeature = ComputeFeatures(
                    klines, i, indicators, orderedIndicators, indicatorIndexByTime, volumeStats,
                    nearestMetric, recentPattern, activeRules,
                    closes, volumes, timeframe,
                    closeZscores, volumeZscores, volumeSmaRatios);

                if (newFeature != null && newFeature.NullRatio <= 0.25)
                {
                    newFeature.Symbol = symbol;
                    newFeature.Timeframe = timeframe;
                    newFeature.OpenTimeMs = k.OpenTimeMs;
                    featureAdds.Add(newFeature);
                }
            }

            if (needTarget)
            {
                var newTarget = ComputeTargets(klines, i, timeframe);
                if (newTarget != null)
                {
                    newTarget.Symbol = symbol;
                    newTarget.Timeframe = timeframe;
                    newTarget.OpenTimeMs = k.OpenTimeMs;
                    targetAdds.Add(newTarget);
                }
            }

            if (featureAdds.Count >= batchSize)
                await FlushAddsAsync();
        }

        await FlushAddsAsync();

        _logger.LogInformation(
            "ML dataset built for {Symbol} {Timeframe}: {FeatureAdds} feature adds, {TargetAdds} target adds",
            symbol, timeframe, totalFeatureAdds, totalTargetAdds);

        if (totalFeatureAdds + totalTargetAdds > 0)
            _auditCache?.Invalidate(symbol);
        return totalFeatureAdds + totalTargetAdds;
    }

    private MlFeatureStore? ComputeFeatures(
        List<Kline> klines,
        int idx,
        Dictionary<long, TechnicalIndicator> indicators,
        IReadOnlyList<KeyValuePair<long, TechnicalIndicator>> orderedIndicators,
        Dictionary<long, int> indicatorIndexByTime,
        Dictionary<long, CandleVolumeStats> volumeStats,
        MarketMetrics? nearestMetric,
        CandlePattern? recentPattern,
        int activeRuleCount,
        double[] closes,
        double[] volumes,
        string timeframe,
        double?[] closeZscores,
        double?[] volumeZscores,
        double?[] volumeSmaRatios)
    {
        var k = klines[idx];
        var close = (double)k.Close;
        var range = (double)(k.High - k.Low);
        var body = Math.Abs((double)(k.Close - k.Open));

        var feature = new MlFeatureStore();
        int totalFields = 0;
        int nullFields = 0;

        double? V(double? value)
        {
            totalFields++;
            if (!value.HasValue) nullFields++;
            return value;
        }

        int? VI(int? value)
        {
            totalFields++;
            if (!value.HasValue) nullFields++;
            return value;
        }

        // Price action
        feature.ClosePctChange1 = V(SafeReturn(closes, idx, 1));
        feature.ClosePctChange4 = V(SafeReturn(closes, idx, 4));
        feature.ClosePctChange24 = V(SafeReturn(closes, idx, BarsForHorizon(timeframe, "1d")));
        feature.HighLowRangePct = V(range / close * 100.0);
        feature.BodyPct = V(range > 0 ? body / range : null);
        feature.UpperWickPct = V(range > 0 ? ((double)k.High - Math.Max((double)k.Open, (double)k.Close)) / range : null);
        feature.LowerWickPct = V(range > 0 ? (Math.Min((double)k.Open, (double)k.Close) - (double)k.Low) / range : null);
        feature.CloseZscore = V(closeZscores[idx]);

        // Volume
        feature.VolumeZscore = V(volumeZscores[idx]);
        feature.VolumeSma20Ratio = V(volumeSmaRatios[idx]);
        feature.TakerBuyRatio = V(k.Volume > 0 ? (double)(k.TakerBuyVolume / k.Volume) : null);

        // Technicals
        if (indicators.TryGetValue(k.OpenTimeMs, out var ind))
        {
            feature.Rsi14 = V(ind.Rsi14);
            var indIdx = indicatorIndexByTime.TryGetValue(k.OpenTimeMs, out var iiVal) ? iiVal : -1;
            feature.Rsi14Slope = V(indIdx >= 0 ? ComputeSlope(orderedIndicators, indIdx, i => i.Rsi14, 5) : null);
            feature.MacdNorm = V(ind.MacdNorm);
            feature.MacdSignalNorm = V(ind.MacdSignalNorm);
            feature.MacdHistogramNorm = V(ind.MacdHistogramNorm);
            feature.Ema12Dist = V(DistPct(close, ind.Ema12));
            feature.Ema26Dist = V(DistPct(close, ind.Ema26));
            feature.Ema50Dist = V(DistPct(close, ind.Ema50));
            feature.Ema200Dist = V(DistPct(close, ind.Ema200));
            feature.Sma50Dist = V(DistPct(close, ind.Sma50));
            feature.Sma200Dist = V(DistPct(close, ind.Sma200));

            if (ind.BollingerUpper.HasValue && ind.BollingerLower.HasValue && ind.BollingerMiddle.HasValue)
            {
                var width = (double)(ind.BollingerUpper.Value - ind.BollingerLower.Value);
                feature.BollingerWidth = V(ind.BollingerMiddle.Value != 0 ? width / (double)ind.BollingerMiddle.Value * 100.0 : null);
                feature.BollingerPosition = V(width > 0 ? ((double)(k.Close - ind.BollingerLower.Value) / width) : null);
            }
            else
            {
                feature.BollingerWidth = V(null);
                feature.BollingerPosition = V(null);
            }

            feature.Atr14Pct = V(ind.Atr14.HasValue ? ind.Atr14.Value / close * 100.0 : null);
            feature.ObvEmaDist = V(ObvEmaDist(ind));
            feature.VwapDist = V(DistPct(close, ind.Vwap));
            feature.RollingVwapDist = V(DistPct(close, ind.RollingVwap24));
        }
        else
        {
            feature.Rsi14 = V(null);
            feature.Rsi14Slope = V(null);
            feature.MacdNorm = V(null);
            feature.MacdSignalNorm = V(null);
            feature.MacdHistogramNorm = V(null);
            feature.Ema12Dist = V(null);
            feature.Ema26Dist = V(null);
            feature.Ema50Dist = V(null);
            feature.Ema200Dist = V(null);
            feature.Sma50Dist = V(null);
            feature.Sma200Dist = V(null);
            feature.BollingerWidth = V(null);
            feature.BollingerPosition = V(null);
            feature.Atr14Pct = V(null);
            feature.ObvEmaDist = V(null);
            feature.VwapDist = V(null);
            feature.RollingVwapDist = V(null);
        }

        // Futures & Order Flow features
        if (nearestMetric != null)
        {
            feature.FundingRateNorm = V(nearestMetric.FundingRate.HasValue ? nearestMetric.FundingRate.Value * 100.0 : null);
            feature.GlobalLsRatio = V(nearestMetric.LongShortRatio);
            feature.TopTraderLsRatio = V(nearestMetric.LongShortRatio);
            feature.OiChangePct24 = V(nearestMetric.OiDeltaPct);
        }
        else
        {
            feature.FundingRateNorm = V(null);
            feature.GlobalLsRatio = V(null);
            feature.TopTraderLsRatio = V(null);
            feature.OiChangePct24 = V(null);
        }

        // Pattern context
        feature.RecentPatternEncoded = VI(recentPattern != null ? EncodePattern(recentPattern.PatternType) : (int?)null);
        feature.ActiveRuleCount = VI(activeRuleCount);

        feature.NullRatio = totalFields == 0 ? 1.0 : (double)nullFields / totalFields;
        return feature;
    }

    private static PriceTarget? ComputeTargets(List<Kline> klines, int idx, string timeframe)
    {
        var k = klines[idx];
        var close = (double)k.Close;

        var target = new PriceTarget();

        target.TargetReturn1h = FutureReturnH(klines, idx, timeframe, "1h");
        target.TargetDirection1h = Direction(target.TargetReturn1h, DirectionThreshold("1h"));

        target.TargetReturn4h = FutureReturnH(klines, idx, timeframe, "4h");
        target.TargetDirection4h = Direction(target.TargetReturn4h, DirectionThreshold("4h"));

        target.TargetReturn1d = FutureReturnH(klines, idx, timeframe, "1d");
        target.TargetDirection1d = Direction(target.TargetReturn1d, DirectionThreshold("1d"));

        target.TargetReturn3d = FutureReturnH(klines, idx, timeframe, "3d");
        target.TargetDirection3d = Direction(target.TargetReturn3d, DirectionThreshold("3d"));

        target.TargetReturn7d = FutureReturnH(klines, idx, timeframe, "7d");
        target.TargetDirection7d = Direction(target.TargetReturn7d, DirectionThreshold("7d"));

        // Triple-barrier labels (first touch of upper/lower barrier within horizon).
        (target.TargetDirectionTb1h, target.TargetReturnTb1h) = TripleBarrier(klines, idx, timeframe, "1h");
        (target.TargetDirectionTb4h, target.TargetReturnTb4h) = TripleBarrier(klines, idx, timeframe, "4h");
        (target.TargetDirectionTb1d, target.TargetReturnTb1d) = TripleBarrier(klines, idx, timeframe, "1d");

        var dayBars = BarsForHorizon(timeframe, "1d");
        if (dayBars <= 500)
        {
            target.TargetVolatility1d = FutureVolatility(klines, idx, dayBars);
            target.TargetMaxDrawdown1d = FutureMaxDrawdown(klines, idx, dayBars);
        }

        if (!target.TargetReturn1d.HasValue)
            return null;
        return target;
    }

    internal static int BarsForHorizon(string timeframe, string horizon)
    {
        var tfMinutes = timeframe switch
        {
            "1m" => 1,
            "5m" => 5,
            "15m" => 15,
            "30m" => 30,
            "1h" => 60,
            "4h" => 240,
            "1d" => 1440,
            _ => 60
        };
        var horizonMinutes = horizon switch
        {
            "1h" => 60,
            "4h" => 240,
            "1d" => 1440,
            "3d" => 4320,
            "7d" => 10080,
            _ => 60
        };
        // ponytail: raw division, may be 0 when horizon is finer than the timeframe (e.g. "1h" on a "4h" tf).
        // Callers treat <=0 as "not a valid label" instead of the old Max(1,..) which faked a 1-bar target
        // and produced byte-identical duplicate labels across horizons on 4h/1d timeframes.
        return horizonMinutes / tfMinutes;
    }

    // ponytail: null when horizon < timeframe (bars<=0) — no more fake 1-bar labels on coarse timeframes.
    private static double? FutureReturnH(List<Kline> klines, int idx, string timeframe, string horizon)
    {
        var bars = BarsForHorizon(timeframe, horizon);
        return bars <= 0 ? null : FutureReturn(klines, idx, bars);
    }

    internal static double? FutureReturn(List<Kline> klines, int idx, int bars)
    {
        if (idx + bars >= klines.Count) return null;
        var future = (double)klines[idx + bars].Close;
        var current = (double)klines[idx].Close;
        return (future - current) / current * 100.0;
    }

    /// <summary>
    /// Triple-barrier label: walk forward bars and return the first touched barrier.
    /// Returns (label, return_at_touch) where label is 1 (upper), -1 (lower), 0 (time barrier / neither).
    /// </summary>
    internal static (int?, double?) TripleBarrier(List<Kline> klines, int idx, string timeframe, string horizon)
    {
        var bars = BarsForHorizon(timeframe, horizon);
        if (bars <= 0 || idx + bars >= klines.Count) return (null, null);

        var threshold = DirectionThreshold(horizon);
        var currentClose = (double)klines[idx].Close;
        var upperBarrier = currentClose * (1.0 + threshold / 100.0);
        var lowerBarrier = currentClose * (1.0 - threshold / 100.0);

        for (int i = 1; i <= bars && idx + i < klines.Count; i++)
        {
            var k = klines[idx + i];
            var high = (double)k.High;
            var low = (double)k.Low;

            if (high >= upperBarrier)
            {
                var ret = (high - currentClose) / currentClose * 100.0;
                return (1, ret);
            }
            if (low <= lowerBarrier)
            {
                var ret = (low - currentClose) / currentClose * 100.0;
                return (-1, ret);
            }
        }

        // Time barrier: return close-to-close at horizon end.
        var endClose = (double)klines[idx + bars].Close;
        var endRet = (endClose - currentClose) / currentClose * 100.0;
        return (0, endRet);
    }

    internal static int? Direction(double? ret, double threshold)
    {
        if (!ret.HasValue) return null;
        if (ret.Value > threshold) return 1;
        if (ret.Value < -threshold) return -1;
        return 0;
    }

    // ponytail: per-horizon neutral band (BTC vol grows ~sqrt(t)); the old flat ±0.3% made the "sideway"
    // class 58% at 1h but 4% at 7d. These are tunable knobs — upgrade path is ATR-scaled / triple-barrier.
    internal static double DirectionThreshold(string horizon) => horizon switch
    {
        "1h" => 0.3,
        "4h" => 0.6,
        "1d" => 1.2,
        "3d" => 2.0,
        "7d" => 3.0,
        _ => 0.3,
    };

    private static double? FutureVolatility(List<Kline> klines, int idx, int bars)
    {
        if (idx + bars >= klines.Count) return null;
        var rets = new List<double>();
        for (int i = idx + 1; i <= idx + bars && i < klines.Count; i++)
        {
            var r = ((double)klines[i].Close - (double)klines[i - 1].Close) / (double)klines[i - 1].Close;
            rets.Add(r);
        }
        if (rets.Count < 2) return null;
        var avg = rets.Average();
        return Math.Sqrt(rets.Average(r => (r - avg) * (r - avg))) * 100.0;
    }

    private static double? FutureMaxDrawdown(List<Kline> klines, int idx, int bars)
    {
        if (idx + bars >= klines.Count) return null;
        double peak = (double)klines[idx].Close;
        double maxDd = 0;
        for (int i = idx + 1; i <= idx + bars && i < klines.Count; i++)
        {
            var price = (double)klines[i].Close;
            if (price > peak) peak = price;
            var dd = (peak - price) / peak * 100.0;
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }

    private static double? SafeReturn(double[] values, int idx, int barsBack)
    {
        if (idx - barsBack < 0) return null;
        var prev = values[idx - barsBack];
        if (prev == 0) return null;
        return (values[idx] - prev) / prev * 100.0;
    }

    private static double?[] ComputeRollingZscore(double[] values, int period)
    {
        var result = new double?[values.Length];
        double sum = 0;
        double sumSq = 0;
        var window = new Queue<double>(period);

        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            sumSq += values[i] * values[i];
            window.Enqueue(values[i]);
            if (window.Count > period)
            {
                var removed = window.Dequeue();
                sum -= removed;
                sumSq -= removed * removed;
            }

            if (i < period - 1)
            {
                result[i] = null;
            }
            else
            {
                var mean = sum / period;
                var variance = sumSq / period - mean * mean;
                if (variance <= 0)
                    result[i] = 0;
                else
                    result[i] = (values[i] - mean) / Math.Sqrt(variance);
            }
        }
        return result;
    }

    private static double?[] ComputeRollingSmaRatio(double[] values, int period)
    {
        var result = new double?[values.Length];
        double sum = 0;
        var window = new Queue<double>(period);

        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            window.Enqueue(values[i]);
            if (window.Count > period)
                sum -= window.Dequeue();

            if (i < period - 1 || sum == 0)
                result[i] = null;
            else
                result[i] = values[i] / (sum / period);
        }
        return result;
    }

    private static double? DistPct(double close, decimal? ema)
    {
        if (!ema.HasValue || ema.Value == 0) return null;
        return (close - (double)ema.Value) / (double)ema.Value * 100.0;
    }

    internal static double? ObvEmaDist(TechnicalIndicator ind)
    {
        if (!ind.Obv.HasValue || !ind.ObvEma50.HasValue || ind.ObvEma50.Value == 0) return null;
        var dist = (ind.Obv.Value - ind.ObvEma50.Value) / Math.Abs(ind.ObvEma50.Value) * 100.0;
        // ponytail: OBV EMA crosses ~0 -> denominator vanishes -> dist blew up to ±3.3M and killed any
        // scaler. p99 is only ±32, so clamping at ±1000 keeps all real signal. Upgrade path: OBV z-score.
        return Math.Clamp(dist, -1000.0, 1000.0);
    }

    private static double? ComputeSlope(
        IReadOnlyList<KeyValuePair<long, TechnicalIndicator>> orderedIndicators,
        int idx,
        Func<TechnicalIndicator, double?> selector,
        int lookbackBars)
    {
        if (idx < lookbackBars) return null;

        var xs = Enumerable.Range(0, lookbackBars).Select(i => (double)i).ToArray();
        var ys = new List<double>(lookbackBars);
        for (int i = idx - lookbackBars + 1; i <= idx; i++)
        {
            var v = selector(orderedIndicators[i].Value);
            if (!v.HasValue) return null;
            ys.Add(v.Value);
        }

        var xMean = xs.Average();
        var yMean = ys.Average();
        double num = 0, den = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            num += (xs[i] - xMean) * (ys[i] - yMean);
            den += (xs[i] - xMean) * (xs[i] - xMean);
        }
        return den == 0 ? 0 : num / den;
    }

    // ponytail: fixed order -> deterministic stable code. Append NEW patterns at the END only,
    // so existing codes never shift. Old code used String.GetHashCode() which is randomized per
    // process in .NET Core (same pattern -> different int across runs / at serving time).
    private static readonly string[] PatternOrder =
    {
        "Doji", "DragonflyDoji", "GravestoneDoji", "Hammer", "HangingMan", "InvertedHammer",
        "ShootingStar", "SpinningTop", "BullishMarubozu", "BearishMarubozu",
        "BullishEngulfing", "BearishEngulfing", "PiercingLine", "DarkCloudCover", "BullishHarami",
        "BearishHarami", "TweezerBottoms", "TweezerTops", "MorningStar", "EveningStar",
        "ThreeWhiteSoldiers", "ThreeBlackCrows", "ThreeInsideUp", "ThreeInsideDown",
    };

    private static readonly Dictionary<string, int> PatternCodes =
        PatternOrder
            .Select((name, i) => (name, code: i + 1))
            .ToDictionary(x => x.name, x => x.code);

    internal static int EncodePattern(string patternType)
        => PatternCodes.TryGetValue(patternType, out var code) ? code : 0;
}

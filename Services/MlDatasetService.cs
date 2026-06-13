using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Computes ML-ready per-bar features and price targets into MlFeatureStore and PriceTargets.
/// </summary>
public class MlDatasetService : IMlDatasetService
{
    private readonly AppDbContext _db;
    private readonly ILogger<MlDatasetService> _logger;

    public MlDatasetService(AppDbContext db, ILogger<MlDatasetService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> BuildAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
    {
        var klines = await _db.Klines
            .AsNoTracking()
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderBy(k => k.OpenTimeMs)
            .ToListAsync(cancellationToken);

        if (klines.Count < 300)
        {
            _logger.LogWarning("Not enough klines to build ML dataset for {Symbol} {Timeframe}: {Count}", symbol, timeframe, klines.Count);
            return 0;
        }

        var indicators = await _db.TechnicalIndicators
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        var volumeStats = await _db.CandleVolumeStats
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        var marketMetrics = await _db.MarketMetrics
            .AsNoTracking()
            .Where(x => x.Symbol == symbol)
            .OrderBy(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        var patterns = await _db.CandlePatterns
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .OrderBy(x => x.OpenTimeMs)
            .ToListAsync(cancellationToken);

        var activeRules = await _db.CandleSequenceRules
            .AsNoTracking()
            .CountAsync(r => r.Symbol == symbol && r.Timeframe == timeframe && r.IsEnabled, cancellationToken);

        var existingFeatures = await _db.MlFeatureStores
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        var existingTargets = await _db.PriceTargets
            .Where(x => x.Symbol == symbol && x.Timeframe == timeframe)
            .ToDictionaryAsync(x => x.OpenTimeMs, cancellationToken);

        var featureUpdates = new List<MlFeatureStore>();
        var featureAdds = new List<MlFeatureStore>();
        var targetUpdates = new List<PriceTarget>();
        var targetAdds = new List<PriceTarget>();

        var closes = klines.Select(k => (double)k.Close).ToArray();
        var volumes = klines.Select(k => (double)k.Volume).ToArray();

        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
            var feature = existingFeatures.TryGetValue(k.OpenTimeMs, out var f) ? f : null;
            var target = existingTargets.TryGetValue(k.OpenTimeMs, out var t) ? t : null;

            var newFeature = ComputeFeatures(
                klines, i, indicators, volumeStats, marketMetrics, patterns, activeRules,
                closes, volumes, timeframe);
            var newTarget = ComputeTargets(klines, i, timeframe);

            if (newFeature == null || newFeature.NullRatio > 0.25)
                continue;

            if (feature == null)
            {
                newFeature.Symbol = symbol;
                newFeature.Timeframe = timeframe;
                newFeature.OpenTimeMs = k.OpenTimeMs;
                featureAdds.Add(newFeature);
            }
            else
            {
                CopyFeatureValues(newFeature, feature);
                feature.UpdatedAtUtc = DateTime.UtcNow;
                featureUpdates.Add(feature);
            }

            if (target == null)
            {
                var pt = newTarget ?? new PriceTarget();
                pt.Symbol = symbol;
                pt.Timeframe = timeframe;
                pt.OpenTimeMs = k.OpenTimeMs;
                if (newTarget != null)
                    targetAdds.Add(pt);
            }
            else if (newTarget != null)
            {
                CopyTargetValues(newTarget, target);
                targetUpdates.Add(target);
            }
        }

        if (featureAdds.Count > 0)
            _db.MlFeatureStores.AddRange(featureAdds);
        if (targetAdds.Count > 0)
            _db.PriceTargets.AddRange(targetAdds);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "ML dataset built for {Symbol} {Timeframe}: {FeatureAdds} feature adds, {FeatureUpdates} updates, {TargetAdds} target adds, {TargetUpdates} updates",
            symbol, timeframe, featureAdds.Count, featureUpdates.Count, targetAdds.Count, targetUpdates.Count);

        return featureAdds.Count + featureUpdates.Count + targetAdds.Count + targetUpdates.Count;
    }

    private MlFeatureStore? ComputeFeatures(
        List<Kline> klines,
        int idx,
        Dictionary<long, TechnicalIndicator> indicators,
        Dictionary<long, CandleVolumeStats> volumeStats,
        List<MarketMetrics> marketMetrics,
        List<CandlePattern> patterns,
        int activeRuleCount,
        double[] closes,
        double[] volumes,
        string timeframe)
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
        feature.CloseZscore = V(ComputeZscore(closes, idx, 20));

        // Volume
        feature.VolumeZscore = V(ComputeZscore(volumes, idx, 20));
        feature.VolumeSma20Ratio = V(ComputeSmaRatio(volumes, idx, 20));
        feature.TakerBuyRatio = V(k.Volume > 0 ? (double)(k.TakerBuyVolume / k.Volume) : null);

        // Technicals
        if (indicators.TryGetValue(k.OpenTimeMs, out var ind))
        {
            feature.Rsi14 = V(ind.Rsi14);
            feature.Rsi14Slope = V(ComputeSlope(indicators, k.OpenTimeMs, i => i.Rsi14, 5));
            feature.MacdNorm = V(ind.MacdNorm);
            feature.MacdSignalNorm = V(ind.MacdSignalNorm);
            feature.MacdHistogramNorm = V(ind.MacdHistogramNorm);
            feature.Ema12Dist = V(DistPct(close, ind.Ema12));
            feature.Ema26Dist = V(DistPct(close, ind.Ema26));
            feature.Ema50Dist = V(DistPct(close, ind.Ema50));
            feature.Ema200Dist = V(DistPct(close, ind.Ema200));

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
            feature.BollingerWidth = V(null);
            feature.BollingerPosition = V(null);
            feature.Atr14Pct = V(null);
            feature.ObvEmaDist = V(null);
            feature.VwapDist = V(null);
            feature.RollingVwapDist = V(null);
        }

        // Market metrics: find nearest before current time
        var nearestMetric = marketMetrics.LastOrDefault(m => m.OpenTimeMs <= k.OpenTimeMs);
        if (nearestMetric != null)
        {
            feature.FundingRateZscore = V(nearestMetric.FundingRateZscore);
            feature.OiDeltaPct = V(nearestMetric.OiDeltaPct);
            feature.LongLiquidationUsd = V(nearestMetric.LongLiquidationUsd);
            feature.ShortLiquidationUsd = V(nearestMetric.ShortLiquidationUsd);
        }
        else
        {
            feature.FundingRateZscore = V(null);
            feature.OiDeltaPct = V(null);
            feature.LongLiquidationUsd = V(null);
            feature.ShortLiquidationUsd = V(null);
        }

        // Pattern context
        var recentPattern = patterns.LastOrDefault(p => p.OpenTimeMs <= k.OpenTimeMs);
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

        target.TargetReturn1h = FutureReturn(klines, idx, BarsForHorizon(timeframe, "1h"));
        target.TargetDirection1h = Direction(target.TargetReturn1h);

        target.TargetReturn4h = FutureReturn(klines, idx, BarsForHorizon(timeframe, "4h"));
        target.TargetDirection4h = Direction(target.TargetReturn4h);

        target.TargetReturn1d = FutureReturn(klines, idx, BarsForHorizon(timeframe, "1d"));
        target.TargetDirection1d = Direction(target.TargetReturn1d);

        target.TargetReturn3d = FutureReturn(klines, idx, BarsForHorizon(timeframe, "3d"));
        target.TargetDirection3d = Direction(target.TargetReturn3d);

        target.TargetReturn7d = FutureReturn(klines, idx, BarsForHorizon(timeframe, "7d"));
        target.TargetDirection7d = Direction(target.TargetReturn7d);

        var dayBars = BarsForHorizon(timeframe, "1d");
        target.TargetVolatility1d = FutureVolatility(klines, idx, dayBars);
        target.TargetMaxDrawdown1d = FutureMaxDrawdown(klines, idx, dayBars);

        if (!target.TargetReturn1d.HasValue)
            return null;
        return target;
    }

    private static int BarsForHorizon(string timeframe, string horizon)
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
        return Math.Max(1, horizonMinutes / tfMinutes);
    }

    private static double? FutureReturn(List<Kline> klines, int idx, int bars)
    {
        if (idx + bars >= klines.Count) return null;
        var future = (double)klines[idx + bars].Close;
        var current = (double)klines[idx].Close;
        return (future - current) / current * 100.0;
    }

    private static int? Direction(double? ret)
    {
        if (!ret.HasValue) return null;
        if (ret.Value > 0.3) return 1;
        if (ret.Value < -0.3) return -1;
        return 0;
    }

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

    private static double? ComputeZscore(double[] values, int idx, int period)
    {
        if (idx < period - 1) return null;
        var slice = values.Skip(idx - period + 1).Take(period).ToArray();
        var avg = slice.Average();
        var std = Math.Sqrt(slice.Average(x => (x - avg) * (x - avg)));
        if (std == 0) return 0;
        return (values[idx] - avg) / std;
    }

    private static double? ComputeSmaRatio(double[] values, int idx, int period)
    {
        if (idx < period - 1) return null;
        var slice = values.Skip(idx - period + 1).Take(period).ToArray();
        var avg = slice.Average();
        if (avg == 0) return null;
        return values[idx] / avg;
    }

    private static double? DistPct(double close, decimal? ema)
    {
        if (!ema.HasValue || ema.Value == 0) return null;
        return (close - (double)ema.Value) / (double)ema.Value * 100.0;
    }

    private static double? ObvEmaDist(TechnicalIndicator ind)
    {
        if (!ind.Obv.HasValue || !ind.ObvEma50.HasValue || ind.ObvEma50.Value == 0) return null;
        return (ind.Obv.Value - ind.ObvEma50.Value) / Math.Abs(ind.ObvEma50.Value) * 100.0;
    }

    private static double? ComputeSlope(
        Dictionary<long, TechnicalIndicator> indicators,
        long openTimeMs,
        Func<TechnicalIndicator, double?> selector,
        int lookbackBars)
    {
        var ordered = indicators.OrderBy(x => x.Key).ToList();
        var idx = ordered.FindIndex(x => x.Key == openTimeMs);
        if (idx < lookbackBars) return null;

        var xs = Enumerable.Range(0, lookbackBars).Select(i => (double)i).ToArray();
        var ys = new List<double>();
        for (int i = idx - lookbackBars + 1; i <= idx; i++)
        {
            var v = selector(ordered[i].Value);
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

    private static int EncodePattern(string patternType)
    {
        return Math.Abs(patternType.GetHashCode() % 1000);
    }

    private static void CopyFeatureValues(MlFeatureStore source, MlFeatureStore dest)
    {
        dest.CloseZscore = source.CloseZscore;
        dest.ClosePctChange1 = source.ClosePctChange1;
        dest.ClosePctChange4 = source.ClosePctChange4;
        dest.ClosePctChange24 = source.ClosePctChange24;
        dest.HighLowRangePct = source.HighLowRangePct;
        dest.BodyPct = source.BodyPct;
        dest.UpperWickPct = source.UpperWickPct;
        dest.LowerWickPct = source.LowerWickPct;
        dest.Rsi14 = source.Rsi14;
        dest.Rsi14Slope = source.Rsi14Slope;
        dest.MacdNorm = source.MacdNorm;
        dest.MacdSignalNorm = source.MacdSignalNorm;
        dest.MacdHistogramNorm = source.MacdHistogramNorm;
        dest.Ema12Dist = source.Ema12Dist;
        dest.Ema26Dist = source.Ema26Dist;
        dest.Ema50Dist = source.Ema50Dist;
        dest.Ema200Dist = source.Ema200Dist;
        dest.BollingerWidth = source.BollingerWidth;
        dest.BollingerPosition = source.BollingerPosition;
        dest.Atr14Pct = source.Atr14Pct;
        dest.ObvEmaDist = source.ObvEmaDist;
        dest.VwapDist = source.VwapDist;
        dest.RollingVwapDist = source.RollingVwapDist;
        dest.VolumeZscore = source.VolumeZscore;
        dest.VolumeSma20Ratio = source.VolumeSma20Ratio;
        dest.TakerBuyRatio = source.TakerBuyRatio;
        dest.FundingRateZscore = source.FundingRateZscore;
        dest.OiDeltaPct = source.OiDeltaPct;
        dest.LongLiquidationUsd = source.LongLiquidationUsd;
        dest.ShortLiquidationUsd = source.ShortLiquidationUsd;
        dest.RecentPatternEncoded = source.RecentPatternEncoded;
        dest.ActiveRuleCount = source.ActiveRuleCount;
        dest.NullRatio = source.NullRatio;
    }

    private static void CopyTargetValues(PriceTarget source, PriceTarget dest)
    {
        dest.TargetReturn1h = source.TargetReturn1h;
        dest.TargetDirection1h = source.TargetDirection1h;
        dest.TargetReturn4h = source.TargetReturn4h;
        dest.TargetDirection4h = source.TargetDirection4h;
        dest.TargetReturn1d = source.TargetReturn1d;
        dest.TargetDirection1d = source.TargetDirection1d;
        dest.TargetReturn3d = source.TargetReturn3d;
        dest.TargetDirection3d = source.TargetDirection3d;
        dest.TargetReturn7d = source.TargetReturn7d;
        dest.TargetDirection7d = source.TargetDirection7d;
        dest.TargetVolatility1d = source.TargetVolatility1d;
        dest.TargetMaxDrawdown1d = source.TargetMaxDrawdown1d;
    }
}

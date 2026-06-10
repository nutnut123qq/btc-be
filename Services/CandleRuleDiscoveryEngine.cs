using Backend.Data;
using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Tự động khám phá Rules chuỗi nến từ dữ liệu lịch sử.
/// Thử nhiều combinations điều kiện, chọn cái có win rate cao.
/// </summary>
public static class CandleRuleDiscoveryEngine
{
    public class DiscoveredRuleCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<SequenceRuleCondition> Conditions { get; set; } = new();
        public int RequiredBars { get; set; } = 10;
        public double WinRate { get; set; }
        public double AvgReturnPct { get; set; }
        public double ProfitFactor { get; set; }
        public int SampleCount { get; set; }
        public double MaxDrawdownPct { get; set; }
    }

    public static List<DiscoveredRuleCandidate> Discover(
        IReadOnlyList<KlineDto> klines,
        string symbol,
        string timeframe,
        int futureBars = 5,
        double minWinRate = 0.55,
        int minSamples = 15,
        double minAvgReturnPct = 0.3,
        IReadOnlyList<CandleVolumeStats>? volumeStats = null)
    {
        if (klines.Count < 200)
            return new List<DiscoveredRuleCandidate>();

        var volDict = volumeStats != null
            ? volumeStats.ToDictionary(v => v.OpenTimeMs)
            : new Dictionary<long, CandleVolumeStats>();

        var candidates = GenerateCandidates();
        var results = new List<DiscoveredRuleCandidate>();

        foreach (var conditions in candidates)
        {
            var stats = EvaluateCandidate(conditions, klines, futureBars, volDict);
            if (stats == null) continue;

            if (stats.SampleCount >= minSamples && stats.WinRate >= minWinRate && stats.AvgReturnPct >= minAvgReturnPct)
            {
                results.Add(stats);
            }
        }

        // Lọc bỏ các rules quá giống nhau (cùng conditions)
        var deduped = results
            .GroupBy(r => JsonSummary(r.Conditions))
            .Select(g => g.OrderByDescending(x => x.WinRate * x.AvgReturnPct).First())
            .OrderByDescending(r => r.WinRate * r.AvgReturnPct)
            .Take(100)
            .ToList();

        return deduped;
    }

    private static List<List<SequenceRuleCondition>> GenerateCandidates()
    {
        var singles = new List<List<SequenceRuleCondition>>();

        // Consecutive bars
        foreach (var count in new[] { 2, 3, 4 })
        {
            singles.Add(new() { new SequenceRuleCondition { Type = "consecutive_bars", Direction = "green", Count = count } });
            singles.Add(new() { new SequenceRuleCondition { Type = "consecutive_bars", Direction = "higher_close", Count = count } });
            singles.Add(new() { new SequenceRuleCondition { Type = "consecutive_bars", Direction = "red", Count = count } });
            singles.Add(new() { new SequenceRuleCondition { Type = "consecutive_bars", Direction = "lower_close", Count = count } });
        }

        // Volume
        foreach (var mult in new[] { 1.1, 1.2, 1.3, 1.5 })
            singles.Add(new() { new SequenceRuleCondition { Type = "volume_compare", Operator = "gt", Reference = "sma", Period = 20, Multiplier = mult } });

        // Range
        foreach (var mult in new[] { 0.6, 0.7, 0.8 })
            singles.Add(new() { new SequenceRuleCondition { Type = "range_compare", Operator = "lt", Reference = "avg", Period = 5, Multiplier = mult } });
        foreach (var mult in new[] { 1.3, 1.5, 2.0 })
            singles.Add(new() { new SequenceRuleCondition { Type = "range_compare", Operator = "gt", Reference = "avg", Period = 5, Multiplier = mult } });

        // Body
        singles.Add(new() { new SequenceRuleCondition { Type = "body_ratio", Operator = "gt", Value = 0.55 } });
        singles.Add(new() { new SequenceRuleCondition { Type = "body_ratio", Operator = "lt", Value = 0.35 } });

        // Shadow
        singles.Add(new() { new SequenceRuleCondition { Type = "shadow_ratio", Side = "upper", Operator = "gt", Multiplier = 1.5 } });
        singles.Add(new() { new SequenceRuleCondition { Type = "shadow_ratio", Side = "lower", Operator = "gt", Multiplier = 1.5 } });

        // Close position
        singles.Add(new() { new SequenceRuleCondition { Type = "close_position", Position = "top_25" } });
        singles.Add(new() { new SequenceRuleCondition { Type = "close_position", Position = "bottom_25" } });
        singles.Add(new() { new SequenceRuleCondition { Type = "close_position", Position = "middle" } });

        // Pairs
        var pairs = new List<List<SequenceRuleCondition>>();
        for (int i = 0; i < singles.Count; i++)
        {
            for (int j = i + 1; j < singles.Count && j < i + 20; j++)
            {
                pairs.Add(new List<SequenceRuleCondition>(singles[i]) { singles[j][0] });
            }
        }

        var all = new List<List<SequenceRuleCondition>>(singles);
        all.AddRange(pairs.Take(1000));

        // Fallback: các rules phổ biến đã kiểm chứng — luôn được evaluate
        var fallbacks = new List<List<SequenceRuleCondition>>
        {
            new() { new SequenceRuleCondition { Type = "consecutive_bars", Direction = "green", Count = 3 }, new SequenceRuleCondition { Type = "volume_compare", Operator = "gt", Reference = "sma", Period = 20, Multiplier = 1.2 } },
            new() { new SequenceRuleCondition { Type = "consecutive_bars", Direction = "higher_close", Count = 3 }, new SequenceRuleCondition { Type = "volume_compare", Operator = "gt", Reference = "sma", Period = 20, Multiplier = 1.1 } },
            new() { new SequenceRuleCondition { Type = "consecutive_bars", Direction = "red", Count = 3 }, new SequenceRuleCondition { Type = "volume_compare", Operator = "gt", Reference = "sma", Period = 20, Multiplier = 1.2 } },
            new() { new SequenceRuleCondition { Type = "shadow_ratio", Side = "upper", Operator = "gt", Multiplier = 2.0 }, new SequenceRuleCondition { Type = "close_position", Position = "bottom_25" } },
            new() { new SequenceRuleCondition { Type = "shadow_ratio", Side = "lower", Operator = "gt", Multiplier = 2.0 }, new SequenceRuleCondition { Type = "close_position", Position = "top_25" } },
            new() { new SequenceRuleCondition { Type = "range_compare", Operator = "lt", Reference = "avg", Period = 5, Multiplier = 0.7 }, new SequenceRuleCondition { Type = "body_ratio", Operator = "gt", Value = 0.6 } },
        };
        all.AddRange(fallbacks);
        return all;
    }

    private static DiscoveredRuleCandidate? EvaluateCandidate(
        List<SequenceRuleCondition> conditions,
        IReadOnlyList<KlineDto> klines,
        int futureBars,
        Dictionary<long, CandleVolumeStats> volDict)
    {
        var returns = new List<double>();
        int requiredBars = Math.Max(10, conditions.Count * 3);

        for (int i = requiredBars; i < klines.Count - futureBars; i++)
        {
            bool match = true;
            foreach (var cond in conditions)
            {
                if (!EvaluateCondition(cond, klines, i, volDict))
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;

            var current = (double)klines[i].Close;
            var future = (double)klines[i + futureBars].Close;
            var ret = (future - current) / current * 100.0;
            returns.Add(ret);
        }

        if (returns.Count < 10) return null;

        var wins = returns.Count(r => r > 0.3); // win nếu tăng > 0.3%
        var losses = returns.Count(r => r < -0.3);
        var neutrals = returns.Count - wins - losses;

        double winRate = wins / (double)(wins + losses + 0.0001);
        double avgRet = returns.Average();
        double profitFactor = returns.Where(r => r > 0).Sum() / (returns.Where(r => r < 0).Sum() * -1 + 0.0001);
        double maxDd = 0;
        double peak = 0;
        double cum = 0;
        foreach (var r in returns.OrderBy(x => x)) // simplified
        {
            cum += r;
            if (cum > peak) peak = cum;
            var dd = peak - cum;
            if (dd > maxDd) maxDd = dd;
        }

        var name = string.Join(" + ", conditions.Select(c => c.Type switch
        {
            "consecutive_bars" => $"{c.Count}{c.Direction}",
            "volume_compare" => $"Vol>{c.Multiplier}x",
            "range_compare" => $"Range{(c.Operator == "lt" ? "<" : ">")}{(int)(c.Multiplier * 100)}",
            "body_ratio" => $"Body{(c.Operator == "lt" ? "<" : ">")}{(int)(c.Value * 100)}",
            "shadow_ratio" => $"{c.Side}Wick>{c.Multiplier}x",
            "close_position" => $"Close{c.Position}",
            _ => c.Type
        }));

        return new DiscoveredRuleCandidate
        {
            Name = name,
            Description = $"Auto-discovered: {name} | Future {futureBars} bars",
            Conditions = conditions,
            RequiredBars = requiredBars,
            WinRate = winRate,
            AvgReturnPct = avgRet,
            ProfitFactor = profitFactor,
            SampleCount = returns.Count,
            MaxDrawdownPct = maxDd
        };
    }

    private static bool EvaluateCondition(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int idx, Dictionary<long, CandleVolumeStats> volDict)
    {
        if (idx < 0 || idx >= klines.Count) return false;
        var k = klines[idx];

        return cond.Type.ToLowerInvariant() switch
        {
            "consecutive_bars" => EvaluateConsecutive(cond, klines, idx),
            "range_compare" => EvaluateRangeCompare(cond, klines, idx),
            "volume_compare" => EvaluateVolumeCompare(cond, klines, idx, volDict),
            "body_ratio" => EvaluateBodyRatio(cond, k),
            "shadow_ratio" => EvaluateShadowRatio(cond, k),
            "close_position" => EvaluateClosePosition(cond, k),
            _ => false
        };
    }

    private static bool EvaluateConsecutive(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int endIdx)
    {
        if (!cond.Count.HasValue || string.IsNullOrWhiteSpace(cond.Direction)) return false;
        int count = 0;
        for (int i = endIdx; i >= 0; i--)
        {
            bool match = cond.Direction.ToLowerInvariant() switch
            {
                "green" => klines[i].Close > klines[i].Open,
                "red" => klines[i].Close < klines[i].Open,
                "higher_close" => i > 0 && klines[i].Close > klines[i - 1].Close,
                "lower_close" => i > 0 && klines[i].Close < klines[i - 1].Close,
                _ => false
            };
            if (match) count++; else break;
        }
        return count >= cond.Count.Value;
    }

    private static bool EvaluateRangeCompare(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int idx)
    {
        if (!cond.Period.HasValue || !cond.Multiplier.HasValue) return false;
        var currentRange = klines[idx].High - klines[idx].Low;
        int start = Math.Max(0, idx - cond.Period.Value);
        int len = idx - start;
        if (len <= 0) return false;
        var avgRange = klines.Skip(start).Take(len).Average(k => k.High - k.Low);
        if (avgRange <= 0) return false;
        var threshold = avgRange * (decimal)cond.Multiplier.Value;
        return Compare(currentRange, threshold, cond.Operator);
    }

    private static bool EvaluateVolumeCompare(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int idx, Dictionary<long, CandleVolumeStats> volDict)
    {
        if (!cond.Multiplier.HasValue) return false;
        var currentVol = klines[idx].Volume;
        var openTime = klines[idx].OpenTimeMs;

        // Ưu tiên dùng pre-computed stats nếu có
        if (volDict.TryGetValue(openTime, out var stats) && cond.Reference?.ToLowerInvariant() == "sma" && cond.Period == 20)
        {
            return Compare(stats.VolumeAnomalyRatio, cond.Multiplier.Value, cond.Operator);
        }

        if (volDict.TryGetValue(openTime, out stats) && cond.Reference?.ToLowerInvariant() == "prev")
        {
            return Compare(stats.VolumeVsPrevious, cond.Multiplier.Value, cond.Operator);
        }

        if (volDict.TryGetValue(openTime, out stats) && cond.Reference?.ToLowerInvariant() == "max10")
        {
            return Compare(stats.VolumeVsMax10, cond.Multiplier.Value, cond.Operator);
        }

        // Fallback: tính on-the-fly
        if (!cond.Period.HasValue) return false;
        int start = Math.Max(0, idx - cond.Period.Value);
        int len = idx - start;
        if (len <= 0) return false;
        var avgVol = klines.Skip(start).Take(len).Average(k => k.Volume);
        if (avgVol <= 0) return false;
        var threshold = avgVol * (decimal)cond.Multiplier.Value;
        return Compare(currentVol, threshold, cond.Operator);
    }

    private static bool EvaluateBodyRatio(SequenceRuleCondition cond, KlineDto k)
    {
        var range = k.High - k.Low;
        if (range <= 0) return false;
        var ratio = (double)(Math.Abs(k.Close - k.Open) / range);
        if (!cond.Value.HasValue) return false;
        return Compare(ratio, cond.Value.Value, cond.Operator);
    }

    private static bool EvaluateShadowRatio(SequenceRuleCondition cond, KlineDto k)
    {
        var body = Math.Abs(k.Close - k.Open);
        if (body <= 0) return false;
        var shadow = (cond.Side ?? "upper").ToLowerInvariant() switch
        {
            "upper" => k.High - Math.Max(k.Open, k.Close),
            "lower" => Math.Min(k.Open, k.Close) - k.Low,
            _ => 0m
        };
        if (!cond.Multiplier.HasValue) return false;
        var threshold = body * (decimal)cond.Multiplier.Value;
        return Compare(shadow, threshold, cond.Operator);
    }

    private static bool EvaluateClosePosition(SequenceRuleCondition cond, KlineDto k)
    {
        var range = k.High - k.Low;
        if (range <= 0) return false;
        var ratio = (double)((k.Close - k.Low) / range);
        return (cond.Position ?? "top_25").ToLowerInvariant() switch
        {
            "top_25" => ratio >= 0.75,
            "top_50" => ratio >= 0.50,
            "bottom_25" => ratio <= 0.25,
            "bottom_50" => ratio <= 0.50,
            "middle" => ratio >= 0.35 && ratio <= 0.65,
            _ => false
        };
    }

    private static bool Compare(double a, double b, string? op) =>
        (op ?? "gt").ToLowerInvariant() switch
        {
            "gt" => a > b,
            "gte" => a >= b,
            "lt" => a < b,
            "lte" => a <= b,
            "eq" => Math.Abs(a - b) < 0.000001,
            _ => a > b
        };

    private static bool Compare(decimal a, decimal b, string? op) =>
        (op ?? "gt").ToLowerInvariant() switch
        {
            "gt" => a > b,
            "gte" => a >= b,
            "lt" => a < b,
            "lte" => a <= b,
            "eq" => a == b,
            _ => a > b
        };

    private static string JsonSummary(List<SequenceRuleCondition> conditions)
    {
        return string.Join("|", conditions.Select(c =>
            $"{c.Type}:{c.Direction}:{c.Count}:{c.Operator}:{c.Multiplier}:{c.Value}:{c.Side}:{c.Position}"));
    }
}

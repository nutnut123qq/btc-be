using Backend.Data;
using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Bounded, chronological discovery for descriptive candle conditions.
/// Selection and held-out evaluation are deliberately separated. A rejected trial is evidence too.
/// </summary>
public static class CandleRuleDiscoveryEngine
{
    public const string MethodVersion = "rule-discovery-oos-v2";

    public sealed class DiscoveryOptions
    {
        public int FutureBars { get; init; } = 1;
        public int CandidateBudget { get; init; } = 128;
        public double SelectionFraction { get; init; } = 0.70;
        public double LabelDeadZonePct { get; init; } = 0.30;
        public double RoundTripCostBps { get; init; } = 30;
        public double MinWinRate { get; init; } = 0.50;
        public int MinSelectionSamples { get; init; } = 30;
        public int MinEvaluationSamples { get; init; } = 30;
        public double MinNetAvgReturnPct { get; init; }
    }

    public sealed class DiscoveryResult
    {
        public string Method { get; init; } = MethodVersion;
        public int CandidateBudget { get; init; }
        public int TrialCount { get; init; }
        public long SelectionStartTimeMs { get; init; }
        public long SelectionEndTimeMs { get; init; }
        public long EvaluationStartTimeMs { get; init; }
        public long EvaluationEndTimeMs { get; init; }
        public double LabelDeadZonePct { get; init; }
        public double RoundTripCostBps { get; init; }
        public List<DiscoveryTrial> Trials { get; init; } = new();
        public List<DiscoveredRuleCandidate> SelectedRules => Trials
            .Where(x => x.Status == "selected" && x.Evaluation is not null)
            .Select(x => x.Evaluation!)
            .OrderByDescending(x => x.OosLift)
            .ThenByDescending(x => x.NetAvgReturnPct)
            .Take(100)
            .ToList();
    }

    public sealed class DiscoveryTrial
    {
        public int TrialNumber { get; init; }
        public string CandidateKey { get; init; } = string.Empty;
        public List<SequenceRuleCondition> Conditions { get; init; } = new();
        public string Status { get; set; } = "rejected";
        public string? RejectedReason { get; set; }
        public DiscoveredRuleCandidate? Selection { get; set; }
        public DiscoveredRuleCandidate? Evaluation { get; set; }
    }

    public class DiscoveredRuleCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<SequenceRuleCondition> Conditions { get; set; } = new();
        public int RequiredBars { get; set; } = 10;
        public double WinRate { get; set; }
        public double AvgReturnPct { get; set; }
        public double NetAvgReturnPct { get; set; }
        public double ProfitFactor { get; set; }
        public int SampleCount { get; set; }
        public double MaxDrawdownPct { get; set; }
        public double BaselineWinRate { get; set; }
        public double OosLift { get; set; }
        public double WinRateCi95Low { get; set; }
        public double WinRateCi95High { get; set; }
    }

    /// <summary>Compatibility helper. Production callers should persist <see cref="DiscoverWithLedger"/>.</summary>
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
        return DiscoverWithLedger(klines, symbol, timeframe, new DiscoveryOptions
        {
            FutureBars = futureBars,
            MinWinRate = minWinRate,
            MinSelectionSamples = minSamples,
            MinEvaluationSamples = minSamples,
            MinNetAvgReturnPct = minAvgReturnPct,
            RoundTripCostBps = 0
        }, volumeStats).SelectedRules;
    }

    public static DiscoveryResult DiscoverWithLedger(
        IReadOnlyList<KlineDto> klines,
        string symbol,
        string timeframe,
        DiscoveryOptions options,
        IReadOnlyList<CandleVolumeStats>? volumeStats = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        if (klines.Count < 200)
            return new DiscoveryResult
            {
                CandidateBudget = options.CandidateBudget,
                LabelDeadZonePct = options.LabelDeadZonePct,
                RoundTripCostBps = options.RoundTripCostBps
            };

        var ordered = klines.OrderBy(x => x.OpenTimeMs).ToArray();
        var split = Math.Clamp((int)Math.Floor(ordered.Length * options.SelectionFraction), 100, ordered.Length - 50);
        var volDict = volumeStats?.GroupBy(v => v.OpenTimeMs).ToDictionary(g => g.Key, g => g.Last())
            ?? new Dictionary<long, CandleVolumeStats>();
        var candidates = GenerateCandidates()
            .GroupBy(JsonSummary)
            .Select(g => g.First())
            .Take(options.CandidateBudget)
            .ToList();
        var trials = new List<DiscoveryTrial>(candidates.Count);
        var baseline = EvaluateBaseline(ordered, split, ordered.Length, options);

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var conditions = candidates[candidateIndex];
            var trial = new DiscoveryTrial
            {
                TrialNumber = candidateIndex + 1,
                CandidateKey = JsonSummary(conditions),
                Conditions = conditions
            };
            var selection = EvaluateCandidate(conditions, ordered, 0, split, options, volDict, baselineWinRate: 0);
            trial.Selection = selection;
            var selectionFailure = Gate(selection, options.MinSelectionSamples, options);
            if (selectionFailure is not null)
            {
                trial.RejectedReason = $"selection:{selectionFailure}";
                trials.Add(trial);
                continue;
            }

            var evaluation = EvaluateCandidate(conditions, ordered, split, ordered.Length, options, volDict, baseline.WinRate);
            trial.Evaluation = evaluation;
            var evaluationFailure = Gate(evaluation, options.MinEvaluationSamples, options);
            if (evaluationFailure is not null)
            {
                trial.RejectedReason = $"held_out:{evaluationFailure}";
                trials.Add(trial);
                continue;
            }

            trial.Status = "selected";
            trials.Add(trial);
        }

        return new DiscoveryResult
        {
            CandidateBudget = options.CandidateBudget,
            TrialCount = trials.Count,
            SelectionStartTimeMs = ordered[0].OpenTimeMs,
            SelectionEndTimeMs = ordered[split - 1].OpenTimeMs,
            EvaluationStartTimeMs = ordered[split].OpenTimeMs,
            EvaluationEndTimeMs = ordered[^1].OpenTimeMs,
            LabelDeadZonePct = options.LabelDeadZonePct,
            RoundTripCostBps = options.RoundTripCostBps,
            Trials = trials
        };
    }

    private static string? Gate(DiscoveredRuleCandidate? result, int minSamples, DiscoveryOptions options)
    {
        if (result is null) return "no_matches";
        if (result.SampleCount < minSamples) return $"samples_{result.SampleCount}_below_{minSamples}";
        if (result.WinRate < options.MinWinRate) return $"win_rate_{result.WinRate:F4}_below_{options.MinWinRate:F4}";
        if (result.NetAvgReturnPct < options.MinNetAvgReturnPct)
            return $"net_avg_{result.NetAvgReturnPct:F4}_below_{options.MinNetAvgReturnPct:F4}";
        return null;
    }

    private static void Validate(DiscoveryOptions options)
    {
        if (options.FutureBars is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options.FutureBars));
        if (options.CandidateBudget is < 1 or > 2_000) throw new ArgumentOutOfRangeException(nameof(options.CandidateBudget));
        if (options.SelectionFraction is <= 0.5 or >= 0.9) throw new ArgumentOutOfRangeException(nameof(options.SelectionFraction));
        if (options.MinSelectionSamples < 1 || options.MinEvaluationSamples < 1) throw new ArgumentOutOfRangeException(nameof(options.MinEvaluationSamples));
        if (options.LabelDeadZonePct < 0 || options.RoundTripCostBps < 0) throw new ArgumentOutOfRangeException(nameof(options.LabelDeadZonePct));
    }

    private static List<List<SequenceRuleCondition>> GenerateCandidates()
    {
        var singles = new List<List<SequenceRuleCondition>>();
        foreach (var count in new[] { 2, 3, 4 })
        {
            singles.Add(new() { new() { Type = "consecutive_bars", Direction = "green", Count = count } });
            singles.Add(new() { new() { Type = "consecutive_bars", Direction = "higher_close", Count = count } });
            singles.Add(new() { new() { Type = "consecutive_bars", Direction = "red", Count = count } });
            singles.Add(new() { new() { Type = "consecutive_bars", Direction = "lower_close", Count = count } });
        }
        foreach (var mult in new[] { 1.1, 1.2, 1.3, 1.5 })
            singles.Add(new() { new() { Type = "volume_compare", Operator = "gt", Reference = "sma", Period = 20, Multiplier = mult } });
        foreach (var mult in new[] { 0.6, 0.7, 0.8 })
            singles.Add(new() { new() { Type = "range_compare", Operator = "lt", Reference = "avg", Period = 5, Multiplier = mult } });
        foreach (var mult in new[] { 1.3, 1.5, 2.0 })
            singles.Add(new() { new() { Type = "range_compare", Operator = "gt", Reference = "avg", Period = 5, Multiplier = mult } });
        singles.Add(new() { new() { Type = "body_ratio", Operator = "gt", Value = 0.55 } });
        singles.Add(new() { new() { Type = "body_ratio", Operator = "lt", Value = 0.35 } });
        singles.Add(new() { new() { Type = "shadow_ratio", Side = "upper", Operator = "gt", Multiplier = 1.5 } });
        singles.Add(new() { new() { Type = "shadow_ratio", Side = "lower", Operator = "gt", Multiplier = 1.5 } });
        singles.Add(new() { new() { Type = "close_position", Position = "top_25" } });
        singles.Add(new() { new() { Type = "close_position", Position = "bottom_25" } });
        singles.Add(new() { new() { Type = "close_position", Position = "middle" } });

        var all = new List<List<SequenceRuleCondition>>(singles);
        for (var i = 0; i < singles.Count; i++)
        for (var j = i + 1; j < singles.Count && j < i + 20; j++)
            all.Add(new List<SequenceRuleCondition>(singles[i]) { singles[j][0] });
        return all;
    }

    private static DiscoveredRuleCandidate? EvaluateCandidate(
        List<SequenceRuleCondition> conditions,
        IReadOnlyList<KlineDto> klines,
        int rangeStart,
        int rangeEndExclusive,
        DiscoveryOptions options,
        Dictionary<long, CandleVolumeStats> volDict,
        double baselineWinRate)
    {
        var grossReturns = new List<double>();
        var requiredBars = Math.Max(10, conditions.Select(ConditionLookback).DefaultIfEmpty(1).Max());
        var start = Math.Max(rangeStart, requiredBars);
        for (var i = start; i + options.FutureBars < rangeEndExclusive; i++)
        {
            if (!conditions.All(cond => EvaluateCondition(cond, klines, i, volDict))) continue;
            var current = (double)klines[i].Close;
            if (current <= 0) continue;
            var future = (double)klines[i + options.FutureBars].Close;
            grossReturns.Add((future - current) / current * 100.0);
            i += options.FutureBars - 1;
        }
        if (grossReturns.Count == 0) return null;

        var costPct = options.RoundTripCostBps / 100.0;
        var netReturns = grossReturns.Select(r => r - costPct).ToArray();
        var wins = grossReturns.Count(r => r > options.LabelDeadZonePct);
        var name = Name(conditions);
        var winRate = wins / (double)grossReturns.Count;
        var (ciLow, ciHigh) = Wilson95(wins, grossReturns.Count);
        return new DiscoveredRuleCandidate
        {
            Name = name,
            Description = $"{MethodVersion}: {name}; future={options.FutureBars} bars; non-overlapping outcomes",
            Conditions = conditions,
            RequiredBars = requiredBars,
            WinRate = winRate,
            AvgReturnPct = grossReturns.Average(),
            NetAvgReturnPct = netReturns.Average(),
            ProfitFactor = netReturns.Where(r => r > 0).Sum() / (Math.Abs(netReturns.Where(r => r < 0).Sum()) + 0.0001),
            SampleCount = grossReturns.Count,
            MaxDrawdownPct = MaxDrawdown(netReturns),
            BaselineWinRate = baselineWinRate,
            OosLift = winRate - baselineWinRate,
            WinRateCi95Low = ciLow,
            WinRateCi95High = ciHigh
        };
    }

    private static DiscoveredRuleCandidate EvaluateBaseline(
        IReadOnlyList<KlineDto> klines, int start, int endExclusive, DiscoveryOptions options)
    {
        var returns = new List<double>();
        for (var i = start; i + options.FutureBars < endExclusive; i += options.FutureBars)
        {
            var current = (double)klines[i].Close;
            if (current > 0) returns.Add(((double)klines[i + options.FutureBars].Close - current) / current * 100.0);
        }
        var wins = returns.Count(x => x > options.LabelDeadZonePct);
        var (ciLow, ciHigh) = Wilson95(wins, returns.Count);
        return new DiscoveredRuleCandidate
        {
            SampleCount = returns.Count,
            WinRate = returns.Count == 0 ? 0 : wins / (double)returns.Count,
            WinRateCi95Low = ciLow,
            WinRateCi95High = ciHigh
        };
    }

    internal static (double Low, double High) Wilson95(int successes, int sampleCount)
    {
        if (sampleCount <= 0) return (0, 1);
        const double z = 1.959963984540054;
        var p = successes / (double)sampleCount;
        var z2OverN = z * z / sampleCount;
        var center = (p + z2OverN / 2) / (1 + z2OverN);
        var halfWidth = z * Math.Sqrt((p * (1 - p) + z * z / (4 * sampleCount)) / sampleCount) / (1 + z2OverN);
        return (Math.Max(0, center - halfWidth), Math.Min(1, center + halfWidth));
    }

    private static int ConditionLookback(SequenceRuleCondition condition) => condition.Type switch
    {
        "consecutive_bars" => Math.Max(2, condition.Count ?? 1),
        "range_compare" or "volume_compare" => Math.Max(2, condition.Period ?? 1),
        _ => 1
    };

    private static double MaxDrawdown(IEnumerable<double> chronologicalNetReturns)
    {
        var maximum = 0d;
        var peak = 0d;
        var cumulative = 0d;
        foreach (var value in chronologicalNetReturns)
        {
            cumulative += value;
            peak = Math.Max(peak, cumulative);
            maximum = Math.Max(maximum, peak - cumulative);
        }
        return maximum;
    }

    private static string Name(IEnumerable<SequenceRuleCondition> conditions) => string.Join(" + ", conditions.Select(c => c.Type switch
    {
        "consecutive_bars" => $"{c.Count}{c.Direction}",
        "volume_compare" => $"Vol>{c.Multiplier}x",
        "range_compare" => $"Range{(c.Operator == "lt" ? "<" : ">")}{(int)(c.Multiplier.GetValueOrDefault() * 100)}",
        "body_ratio" => $"Body{(c.Operator == "lt" ? "<" : ">")}{(int)(c.Value.GetValueOrDefault() * 100)}",
        "shadow_ratio" => $"{c.Side}Wick>{c.Multiplier}x",
        "close_position" => $"Close{c.Position}",
        _ => c.Type
    }));

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
        var count = 0;
        for (var i = endIdx; i >= 0; i--)
        {
            var match = cond.Direction.ToLowerInvariant() switch
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
        var start = Math.Max(0, idx - cond.Period.Value);
        var len = idx - start;
        if (len <= 0) return false;
        var average = klines.Skip(start).Take(len).Average(k => k.High - k.Low);
        return average > 0 && Compare(klines[idx].High - klines[idx].Low, average * (decimal)cond.Multiplier.Value, cond.Operator);
    }

    private static bool EvaluateVolumeCompare(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int idx, Dictionary<long, CandleVolumeStats> volDict)
    {
        if (!cond.Multiplier.HasValue) return false;
        if (volDict.TryGetValue(klines[idx].OpenTimeMs, out var stats) && cond.Reference?.Equals("sma", StringComparison.OrdinalIgnoreCase) == true && cond.Period == 20)
            return Compare(stats.VolumeAnomalyRatio, cond.Multiplier.Value, cond.Operator);
        if (!cond.Period.HasValue) return false;
        var start = Math.Max(0, idx - cond.Period.Value);
        var len = idx - start;
        if (len <= 0) return false;
        var average = klines.Skip(start).Take(len).Average(k => k.Volume);
        return average > 0 && Compare(klines[idx].Volume, average * (decimal)cond.Multiplier.Value, cond.Operator);
    }

    private static bool EvaluateBodyRatio(SequenceRuleCondition cond, KlineDto k)
    {
        var range = k.High - k.Low;
        return range > 0 && cond.Value.HasValue && Compare((double)(Math.Abs(k.Close - k.Open) / range), cond.Value.Value, cond.Operator);
    }

    private static bool EvaluateShadowRatio(SequenceRuleCondition cond, KlineDto k)
    {
        var body = Math.Abs(k.Close - k.Open);
        if (body <= 0 || !cond.Multiplier.HasValue) return false;
        var shadow = cond.Side?.Equals("lower", StringComparison.OrdinalIgnoreCase) == true
            ? Math.Min(k.Open, k.Close) - k.Low
            : k.High - Math.Max(k.Open, k.Close);
        return Compare(shadow, body * (decimal)cond.Multiplier.Value, cond.Operator);
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
            "middle" => ratio is >= 0.35 and <= 0.65,
            _ => false
        };
    }

    private static bool Compare(double a, double b, string? op) => (op ?? "gt").ToLowerInvariant() switch
    {
        "gte" => a >= b, "lt" => a < b, "lte" => a <= b, "eq" => Math.Abs(a - b) < 0.000001, _ => a > b
    };

    private static bool Compare(decimal a, decimal b, string? op) => (op ?? "gt").ToLowerInvariant() switch
    {
        "gte" => a >= b, "lt" => a < b, "lte" => a <= b, "eq" => a == b, _ => a > b
    };

    private static string JsonSummary(IEnumerable<SequenceRuleCondition> conditions) => string.Join("|", conditions.Select(c =>
        $"{c.Type}:{c.Direction}:{c.Count}:{c.Operator}:{c.Multiplier}:{c.Value}:{c.Side}:{c.Position}:{c.Period}"));
}

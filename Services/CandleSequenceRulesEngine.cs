using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class CandleSequenceRulesEngine : ICandleSequenceRulesEngine
{
    private readonly AppDbContext _db;
    private readonly ILogger<CandleSequenceRulesEngine> _logger;

    public CandleSequenceRulesEngine(AppDbContext db, ILogger<CandleSequenceRulesEngine> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CandleSequenceRuleSignalDto>> EvaluateAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default)
    {
        if (klines.Count == 0)
            return Array.Empty<CandleSequenceRuleSignalDto>();

        var rules = await _db.CandleSequenceRules
            .AsNoTracking()
            .Where(r => r.IsEnabled && r.Symbol == symbol && r.Timeframe == timeframe)
            .OrderByDescending(r => r.Priority)
            .ToListAsync(cancellationToken);

        if (rules.Count == 0)
            return Array.Empty<CandleSequenceRuleSignalDto>();

        var signals = new List<CandleSequenceRuleSignalDto>();
        var last = klines[^1];

        foreach (var rule in rules)
        {
            if (klines.Count < rule.RequiredBars)
                continue;

            var conditions = CandleSequenceRuleMappers.DeserializeConditions(rule.ConditionsJson);
            if (conditions.Count == 0)
                continue;

            var allMatch = true;
            foreach (var cond in conditions)
            {
                if (!EvaluateCondition(cond, klines))
                {
                    allMatch = false;
                    break;
                }
            }

            if (!allMatch)
                continue;

            // Check cooldown
            var since = DateTime.UtcNow.AddMinutes(-rule.CooldownMinutes);
            var recentlyTriggered = await _db.CandleSequenceSignals
                .AsNoTracking()
                .AnyAsync(s => s.RuleId == rule.Id && s.CreatedAtUtc >= since, cancellationToken);

            if (recentlyTriggered)
            {
                _logger.LogDebug("Sequence rule {RuleId} {RuleName} matched but in cooldown", rule.Id, rule.Name);
                continue;
            }

            signals.Add(new CandleSequenceRuleSignalDto
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                Symbol = symbol,
                Timeframe = timeframe,
                Action = rule.Action,
                Message = $"[{rule.Name}] {rule.Description}",
                TriggerClose = last.Close,
                TriggerTimeMs = last.OpenTimeMs,
                Priority = rule.Priority
            });
        }

        return signals;
    }

    private static bool EvaluateCondition(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines)
    {
        int idx = klines.Count - 1 + cond.BarOffset;
        if (idx < 0 || idx >= klines.Count)
            return false;

        var k = klines[idx];
        var op = (cond.Operator ?? "gt").ToLowerInvariant();

        return cond.Type.ToLowerInvariant() switch
        {
            "consecutive_bars" => EvaluateConsecutiveBars(cond, klines, idx),
            "range_compare" => EvaluateRangeCompare(cond, klines, idx),
            "volume_compare" => EvaluateVolumeCompare(cond, klines, idx),
            "body_ratio" => EvaluateBodyRatio(cond, k),
            "shadow_ratio" => EvaluateShadowRatio(cond, k),
            "close_position" => EvaluateClosePosition(cond, k),
            _ => false
        };
    }

    private static bool EvaluateConsecutiveBars(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int endIdx)
    {
        if (!cond.Count.HasValue || string.IsNullOrWhiteSpace(cond.Direction))
            return false;

        int count = 0;
        for (int i = endIdx; i >= 0; i--)
        {
            bool match = cond.Direction.ToLowerInvariant() switch
            {
                "green" => klines[i].Close > klines[i].Open,
                "red" => klines[i].Close < klines[i].Open,
                "higher_close" => i > 0 && klines[i].Close > klines[i - 1].Close,
                "lower_close" => i > 0 && klines[i].Close < klines[i - 1].Close,
                "higher_high" => i > 0 && klines[i].High > klines[i - 1].High,
                "lower_low" => i > 0 && klines[i].Low < klines[i - 1].Low,
                _ => false
            };

            if (match)
                count++;
            else
                break;
        }

        return count >= cond.Count.Value;
    }

    private static bool EvaluateRangeCompare(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int idx)
    {
        if (!cond.Period.HasValue || !cond.Multiplier.HasValue)
            return false;

        var currentRange = klines[idx].High - klines[idx].Low;
        int start = Math.Max(0, idx - cond.Period.Value);
        int len = idx - start;
        if (len <= 0) return false;

        var avgRange = klines.Skip(start).Take(len).Average(k => k.High - k.Low);
        if (avgRange <= 0) return false;

        var threshold = avgRange * (decimal)cond.Multiplier.Value;
        return Compare(currentRange, threshold, cond.Operator);
    }

    private static bool EvaluateVolumeCompare(SequenceRuleCondition cond, IReadOnlyList<KlineDto> klines, int idx)
    {
        if (!cond.Period.HasValue || !cond.Multiplier.HasValue)
            return false;

        var currentVol = klines[idx].Volume;
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
        var pos = (cond.Position ?? "top_25").ToLowerInvariant();
        var closeRatio = (double)((k.Close - k.Low) / range);

        return pos switch
        {
            "top_25" => closeRatio >= 0.75,
            "top_50" => closeRatio >= 0.50,
            "bottom_25" => closeRatio <= 0.25,
            "bottom_50" => closeRatio <= 0.50,
            "middle" => closeRatio >= 0.35 && closeRatio <= 0.65,
            _ => false
        };
    }

    private static bool Compare(double a, double b, string? op)
    {
        return (op ?? "gt").ToLowerInvariant() switch
        {
            "gt" => a > b,
            "gte" => a >= b,
            "lt" => a < b,
            "lte" => a <= b,
            "eq" => Math.Abs(a - b) < 0.000001,
            _ => a > b
        };
    }

    private static bool Compare(decimal a, decimal b, string? op)
    {
        return (op ?? "gt").ToLowerInvariant() switch
        {
            "gt" => a > b,
            "gte" => a >= b,
            "lt" => a < b,
            "lte" => a <= b,
            "eq" => a == b,
            _ => a > b
        };
    }
}

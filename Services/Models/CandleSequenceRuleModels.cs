using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Services.Models;

/// <summary>Điều kiện động cho CandleSequenceRule.</summary>
public class SequenceRuleCondition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // consecutive_bars, range_compare, volume_compare, body_ratio, shadow_ratio, close_position

    [JsonPropertyName("direction")]
    public string? Direction { get; set; } // higher, lower, green, red

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("operator")]
    public string? Operator { get; set; } // gt, lt, gte, lte, eq

    [JsonPropertyName("reference")]
    public string? Reference { get; set; } // avg, sma, body, range, prev

    [JsonPropertyName("period")]
    public int? Period { get; set; }

    [JsonPropertyName("multiplier")]
    public double? Multiplier { get; set; }

    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("side")]
    public string? Side { get; set; } // upper, lower

    [JsonPropertyName("position")]
    public string? Position { get; set; } // top_25, bottom_25, middle

    [JsonPropertyName("barOffset")]
    public int BarOffset { get; set; } = 0;
}

public class CandleSequenceRuleSignalDto
{
    public long RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public string Action { get; set; } = "ALERT";
    public string Message { get; set; } = string.Empty;
    public decimal TriggerClose { get; set; }
    public long TriggerTimeMs { get; set; }
    public int Priority { get; set; }
}

public class CreateCandleSequenceRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int RequiredBars { get; set; } = 10;
    public bool IsEnabled { get; set; } = true;
    public int CooldownMinutes { get; set; } = 60;
    public List<SequenceRuleCondition> Conditions { get; set; } = new();
    public string Action { get; set; } = "ALERT";
    public int Priority { get; set; } = 0;
}

public class UpdateCandleSequenceRuleRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int RequiredBars { get; set; } = 10;
    public bool IsEnabled { get; set; } = true;
    public int CooldownMinutes { get; set; } = 60;
    public List<SequenceRuleCondition> Conditions { get; set; } = new();
    public string Action { get; set; } = "ALERT";
    public int Priority { get; set; } = 0;
}

public static class CandleSequenceRuleMappers
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string SerializeConditions(List<SequenceRuleCondition> conditions)
        => JsonSerializer.Serialize(conditions, JsonOpts);

    public static List<SequenceRuleCondition> DeserializeConditions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<SequenceRuleCondition>();
        return JsonSerializer.Deserialize<List<SequenceRuleCondition>>(json, JsonOpts) ?? new List<SequenceRuleCondition>();
    }
}

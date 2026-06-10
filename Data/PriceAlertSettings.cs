namespace Backend.Data;

/// <summary>Per-user price alert rule configuration (edited via API / FE).</summary>
public class PriceAlertSettings
{
    public string UserId { get; set; } = "default";
    public bool Enabled { get; set; }
    public decimal? PriceAboveUsd { get; set; }
    public decimal? PriceBelowUsd { get; set; }
    public string KlineInterval { get; set; } = "1m";
    public int CooldownMinutes { get; set; } = 30;
    public DateTimeOffset UpdatedAt { get; set; }
}

namespace Backend.Data;

/// <summary>
/// Labels / targets cho supervised learning, tính từ future price action.
/// </summary>
public class PriceTarget
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long OpenTimeMs { get; set; }

    // 1h horizon
    public double? TargetReturn1h { get; set; }
    public int? TargetDirection1h { get; set; }  // -1: down >0.3%, 0: sideway, 1: up >0.3%

    // 4h horizon
    public double? TargetReturn4h { get; set; }
    public int? TargetDirection4h { get; set; }

    // 1d horizon
    public double? TargetReturn1d { get; set; }
    public int? TargetDirection1d { get; set; }

    // 3d horizon
    public double? TargetReturn3d { get; set; }
    public int? TargetDirection3d { get; set; }

    // 7d horizon
    public double? TargetReturn7d { get; set; }
    public int? TargetDirection7d { get; set; }

    // Risk targets
    public double? TargetVolatility1d { get; set; }
    public double? TargetMaxDrawdown1d { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

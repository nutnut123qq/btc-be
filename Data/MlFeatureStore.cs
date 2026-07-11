using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// Feature vector đã được chuẩn hóa, sẵn sàng để train AI/ML.
/// Mỗi bar tương ứng với 1 row.
/// </summary>
public class MlFeatureStore
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long OpenTimeMs { get; set; }

    // --- Price action features ---
    public double? CloseZscore { get; set; }
    public double? ClosePctChange1 { get; set; }
    public double? ClosePctChange4 { get; set; }
    public double? ClosePctChange24 { get; set; }
    public double? HighLowRangePct { get; set; }
    public double? BodyPct { get; set; }
    public double? UpperWickPct { get; set; }
    public double? LowerWickPct { get; set; }

    // --- Technical indicator features ---
    public double? Rsi14 { get; set; }
    public double? Rsi14Slope { get; set; }
    public double? MacdNorm { get; set; }
    public double? MacdSignalNorm { get; set; }
    public double? MacdHistogramNorm { get; set; }
    public double? Ema12Dist { get; set; }
    public double? Ema26Dist { get; set; }
    public double? Ema50Dist { get; set; }
    public double? Ema200Dist { get; set; }
    public double? Sma50Dist { get; set; }
    public double? Sma200Dist { get; set; }
    public double? BollingerWidth { get; set; }
    public double? BollingerPosition { get; set; }
    public double? Atr14Pct { get; set; }
    public double? ObvEmaDist { get; set; }
    public double? VwapDist { get; set; }
    public double? RollingVwapDist { get; set; }

    // --- Volume features ---
    public double? VolumeZscore { get; set; }
    public double? VolumeSma20Ratio { get; set; }
    public double? TakerBuyRatio { get; set; }

    // ponytail: removed 9 dead columns — 4 microstructure (funding/OI/liq, 97-100% NULL, no data
    // source) + 5 PCA components (99.3% NULL, non-stationary, never in the training vector).

    // --- Pattern / rule context ---
    public int? RecentPatternEncoded { get; set; }
    public int? ActiveRuleCount { get; set; }

    // --- Metadata ---
    public double NullRatio { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

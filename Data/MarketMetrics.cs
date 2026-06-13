using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// Dữ liệu thị trường phái sinh (funding rate, open interest) từ Binance Futures.
/// </summary>
public class MarketMetrics
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long OpenTimeMs { get; set; }

    // Funding rate (8h intervals, nhưng lưu theo timestamp gần nhất)
    public double? FundingRate { get; set; }

    // Open Interest (USD)
    public double? OpenInterest { get; set; }

    // Long/Short ratio
    public double? LongShortRatio { get; set; }

    // Normalized / delta features
    public double? FundingRateZscore { get; set; }
    public double? OiDeltaPct { get; set; }

    // Liquidation data (nếu có)
    public double? LongLiquidationUsd { get; set; }
    public double? ShortLiquidationUsd { get; set; }
}

using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// Các chỉ báo kỹ thuật tính từ Klines, lưu sẵn để AI query nhanh.
/// </summary>
public class TechnicalIndicator
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long OpenTimeMs { get; set; }

    // RSI
    public double? Rsi14 { get; set; }

    // EMAs
    [Column(TypeName = "numeric")]
    public decimal? Ema12 { get; set; }
    [Column(TypeName = "numeric")]
    public decimal? Ema26 { get; set; }
    [Column(TypeName = "numeric")]
    public decimal? Ema50 { get; set; }
    [Column(TypeName = "numeric")]
    public decimal? Ema200 { get; set; }

    // MACD
    public double? Macd { get; set; }
    public double? MacdSignal { get; set; }
    public double? MacdHistogram { get; set; }

    // Bollinger Bands
    [Column(TypeName = "numeric")]
    public decimal? BollingerUpper { get; set; }
    [Column(TypeName = "numeric")]
    public decimal? BollingerMiddle { get; set; }
    [Column(TypeName = "numeric")]
    public decimal? BollingerLower { get; set; }

    // ATR
    public double? Atr14 { get; set; }

    // OBV
    public double? Obv { get; set; }

    // VWAP
    [Column(TypeName = "numeric")]
    public decimal? Vwap { get; set; }

    // --- Normalized / enhanced features ---
    // EMA 50 của OBV (thay vì OBV raw)
    public double? ObvEma50 { get; set; }

    // Rolling VWAP 24 bars (không reset theo ngày)
    [Column(TypeName = "numeric")]
    public decimal? RollingVwap24 { get; set; }

    // MACD normalized by ATR or price (better for ML)
    public double? MacdNorm { get; set; }
    public double? MacdSignalNorm { get; set; }
    public double? MacdHistogramNorm { get; set; }
}

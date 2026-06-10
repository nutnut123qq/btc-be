using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// Raw OHLCV candle data từ Binance, lưu trong DB để train AI & backtest.
/// </summary>
public class Kline
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long OpenTimeMs { get; set; }
    public long CloseTimeMs { get; set; }
    [Column(TypeName = "numeric")]
    public decimal Open { get; set; }
    [Column(TypeName = "numeric")]
    public decimal High { get; set; }
    [Column(TypeName = "numeric")]
    public decimal Low { get; set; }
    [Column(TypeName = "numeric")]
    public decimal Close { get; set; }
    [Column(TypeName = "numeric")]
    public decimal Volume { get; set; }
    [Column(TypeName = "numeric")]
    public decimal QuoteVolume { get; set; }
    public int TradeCount { get; set; }
    [Column(TypeName = "numeric")]
    public decimal TakerBuyVolume { get; set; }
    [Column(TypeName = "numeric")]
    public decimal TakerBuyQuoteVolume { get; set; }
}

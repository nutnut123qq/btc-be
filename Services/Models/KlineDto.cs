namespace Backend.Services.Models;

public class KlineDto
{
    public long OpenTimeMs { get; set; }
    public string TimeIso { get; set; } = string.Empty;
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public long CloseTimeMs { get; set; }
    public decimal QuoteVolume { get; set; }
    public int TradeCount { get; set; }
    public decimal TakerBuyVolume { get; set; }
    public decimal TakerBuyQuoteVolume { get; set; }
}

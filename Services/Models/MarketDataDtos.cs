namespace Backend.Services.Models;

public class MarketTickerDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal LastPrice { get; set; }
    public decimal PriceChange { get; set; }
    public decimal PriceChangePercent { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal Volume { get; set; }
    public decimal QuoteVolume { get; set; }
    public decimal BidPrice { get; set; }
    public decimal AskPrice { get; set; }
    public int Count { get; set; }
    public long CloseTimeMs { get; set; }
}

public class MarketTradeDto
{
    public long Id { get; set; }
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    public decimal QuoteQty { get; set; }
    public long TimeMs { get; set; }
    public bool IsBuyerMaker { get; set; }
    public bool IsBuyer { get; set; } // true if taker bought (isBuyerMaker == false), false if taker sold
}

public class OrderBookEntryDto
{
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    public decimal Total { get; set; }
}

public class OrderBookDepthDto
{
    public string Symbol { get; set; } = string.Empty;
    public long LastUpdateId { get; set; }
    public IReadOnlyList<OrderBookEntryDto> Bids { get; set; } = Array.Empty<OrderBookEntryDto>();
    public IReadOnlyList<OrderBookEntryDto> Asks { get; set; } = Array.Empty<OrderBookEntryDto>();
}

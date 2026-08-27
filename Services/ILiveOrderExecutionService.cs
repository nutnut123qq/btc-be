using System.Text.Json.Serialization;

namespace Backend.Services;

public record BinanceOrderRequest
{
    public string Symbol { get; init; } = "BTCUSDT";
    public string Side { get; init; } = "BUY"; // BUY or SELL
    public string Type { get; init; } = "MARKET"; // MARKET, STOP_MARKET, TAKE_PROFIT_MARKET
    public decimal Quantity { get; init; }
    public decimal? Price { get; init; }
    public decimal? StopPrice { get; init; }
    public bool? ReduceOnly { get; init; }
}

public record BinanceOrderResult
{
    public bool Success { get; init; }
    public long? OrderId { get; init; }
    public string? ClientOrderId { get; init; }
    public string Symbol { get; init; } = "";
    public string Side { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal ExecutedQty { get; init; }
    public decimal AvgPrice { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawResponseJson { get; init; }
    public string TradingMode { get; init; } = "Paper";
}

public record BinanceAccountBalanceResult
{
    public bool Success { get; init; }
    public decimal TotalWalletBalance { get; init; }
    public decimal AvailableBalance { get; init; }
    public decimal TotalUnrealizedProfit { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawResponseJson { get; init; }
    public string TradingMode { get; init; } = "Paper";
}

public interface ILiveOrderExecutionService
{
    string TradingMode { get; }
    string BaseUrl { get; }

    Task<BinanceOrderResult> PlaceMarketOrderAsync(
        string symbol,
        string side,
        decimal quantity,
        CancellationToken cancellationToken = default);

    Task<BinanceOrderResult> PlaceStopLossOrderAsync(
        string symbol,
        string side,
        decimal stopPrice,
        decimal quantity,
        CancellationToken cancellationToken = default);

    Task<BinanceOrderResult> PlaceTakeProfitOrderAsync(
        string symbol,
        string side,
        decimal takeProfitPrice,
        decimal quantity,
        CancellationToken cancellationToken = default);

    Task<BinanceAccountBalanceResult> GetAccountBalanceAsync(
        CancellationToken cancellationToken = default);

    Task<BinanceOrderResult> CancelAllOpenOrdersAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}

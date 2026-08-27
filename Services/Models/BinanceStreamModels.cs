using System.Text.Json.Serialization;

namespace Backend.Services.Models;

public class ListenKeyResponse
{
    [JsonPropertyName("listenKey")]
    public string ListenKey { get; set; } = string.Empty;
}

public class StreamStatusDto
{
    public bool IsConnected { get; set; }
    public string? CurrentListenKey { get; set; }
    public DateTimeOffset? LastPingTime { get; set; }
    public DateTimeOffset? ConnectedSince { get; set; }
    public int ReconnectCount { get; set; }
    public string TradingMode { get; set; } = "Paper";
    public string BaseUrl { get; set; } = string.Empty;
    public string WsUrl { get; set; } = string.Empty;
    public bool StreamEnabled { get; set; }
    public DateTimeOffset? LastEventReceivedTime { get; set; }
    public string? LastEventType { get; set; }
}

public class UserDataStreamBaseEvent
{
    [JsonPropertyName("e")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("E")]
    public long EventTime { get; set; }

    [JsonPropertyName("T")]
    public long? TransactionTime { get; set; }
}

public class ListenKeyExpiredEvent : UserDataStreamBaseEvent
{
    [JsonPropertyName("listenKey")]
    public string ListenKey { get; set; } = string.Empty;
}

public class OrderTradeUpdateEvent : UserDataStreamBaseEvent
{
    [JsonPropertyName("o")]
    public OrderInfo Order { get; set; } = new();
}

public class OrderInfo
{
    [JsonPropertyName("s")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("c")]
    public string ClientOrderId { get; set; } = string.Empty;

    [JsonPropertyName("S")]
    public string Side { get; set; } = string.Empty;

    [JsonPropertyName("o")]
    public string OrderType { get; set; } = string.Empty;

    [JsonPropertyName("f")]
    public string TimeInForce { get; set; } = string.Empty;

    [JsonPropertyName("q")]
    public string OriginalQuantity { get; set; } = "0";

    [JsonPropertyName("p")]
    public string OriginalPrice { get; set; } = "0";

    [JsonPropertyName("ap")]
    public string AveragePrice { get; set; } = "0";

    [JsonPropertyName("sp")]
    public string StopPrice { get; set; } = "0";

    [JsonPropertyName("x")]
    public string ExecutionType { get; set; } = string.Empty;

    [JsonPropertyName("X")]
    public string OrderStatus { get; set; } = string.Empty;

    [JsonPropertyName("i")]
    public long OrderId { get; set; }

    [JsonPropertyName("l")]
    public string LastFilledQuantity { get; set; } = "0";

    [JsonPropertyName("z")]
    public string AccumulatedFilledQuantity { get; set; } = "0";

    [JsonPropertyName("L")]
    public string LastFilledPrice { get; set; } = "0";

    [JsonPropertyName("N")]
    public string? CommissionAsset { get; set; }

    [JsonPropertyName("n")]
    public string? CommissionAmount { get; set; }

    [JsonPropertyName("T")]
    public long TradeTime { get; set; }

    [JsonPropertyName("t")]
    public long TradeId { get; set; }

    [JsonPropertyName("b")]
    public string? BidsNotional { get; set; }

    [JsonPropertyName("a")]
    public string? AskNotional { get; set; }

    [JsonPropertyName("m")]
    public bool IsMaker { get; set; }

    [JsonPropertyName("R")]
    public bool IsReduceOnly { get; set; }

    [JsonPropertyName("wt")]
    public string? WorkingType { get; set; }

    [JsonPropertyName("ot")]
    public string? OriginalOrderType { get; set; }

    [JsonPropertyName("ps")]
    public string PositionSide { get; set; } = "BOTH";

    [JsonPropertyName("cp")]
    public bool? CloseAll { get; set; }

    [JsonPropertyName("AP")]
    public string? ActivationPrice { get; set; }

    [JsonPropertyName("cr")]
    public string? CallbackRate { get; set; }

    [JsonPropertyName("rp")]
    public string RealizedProfit { get; set; } = "0";
}

public class AccountUpdateEvent : UserDataStreamBaseEvent
{
    [JsonPropertyName("a")]
    public AccountUpdateInfo AccountInfo { get; set; } = new();
}

public class AccountUpdateInfo
{
    [JsonPropertyName("m")]
    public string EventReasonType { get; set; } = string.Empty;

    [JsonPropertyName("B")]
    public List<BalanceUpdateInfo> Balances { get; set; } = new();

    [JsonPropertyName("P")]
    public List<PositionUpdateInfo> Positions { get; set; } = new();
}

public class BalanceUpdateInfo
{
    [JsonPropertyName("a")]
    public string Asset { get; set; } = string.Empty;

    [JsonPropertyName("wb")]
    public string WalletBalance { get; set; } = "0";

    [JsonPropertyName("cw")]
    public string CrossWalletBalance { get; set; } = "0";

    [JsonPropertyName("bc")]
    public string BalanceChange { get; set; } = "0";
}

public class PositionUpdateInfo
{
    [JsonPropertyName("s")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("pa")]
    public string PositionAmount { get; set; } = "0";

    [JsonPropertyName("ep")]
    public string EntryPrice { get; set; } = "0";

    [JsonPropertyName("bep")]
    public string? BreakEvenPrice { get; set; }

    [JsonPropertyName("cr")]
    public string? AccumulatedRealized { get; set; }

    [JsonPropertyName("up")]
    public string UnrealizedPnL { get; set; } = "0";

    [JsonPropertyName("mt")]
    public string MarginType { get; set; } = "cross";

    [JsonPropertyName("iw")]
    public string? IsolatedWallet { get; set; }

    [JsonPropertyName("ps")]
    public string PositionSide { get; set; } = "BOTH";
}

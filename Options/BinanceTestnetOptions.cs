namespace Backend.Options;

public class BinanceTestnetOptions
{
    public const string SectionName = "BinanceTestnet";

    public string BaseUrl { get; set; } = "https://testnet.binancefuture.com";
    public string WsBaseUrl { get; set; } = "wss://stream.binancefuture.com/ws";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string TradingMode { get; set; } = "Paper";
    public bool StreamEnabled { get; set; } = true;
    public int PingIntervalMinutes { get; set; } = 30;
    public int MaxReconnectBackoffSeconds { get; set; } = 30;
}

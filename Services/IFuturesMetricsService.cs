namespace Backend.Services;

public interface IFuturesMetricsService
{
    Task<FuturesMetricsDto> GetFuturesMetricsAsync(string symbol = "BTCUSDT", CancellationToken ct = default);
}

public class FuturesMetricsDto
{
    public string Symbol { get; set; } = "BTCUSDT";
    public double OpenInterestUsd { get; set; }
    public double LongShortRatio { get; set; }
    public double TakerBuySellRatio { get; set; }
    public double FundingRatePct { get; set; }
    public string SqueezeRiskSignal { get; set; } = "Neutral";
    public string SentimentSummary { get; set; } = string.Empty;
}

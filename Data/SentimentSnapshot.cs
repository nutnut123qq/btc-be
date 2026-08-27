namespace Backend.Data;

public class SentimentSnapshot
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public long TimeMs { get; set; }
    public int FearGreedScore { get; set; } = 50; // 0-100
    public double FundingRateZScore { get; set; }
    public double LongShortRatio { get; set; } = 1.0;
    public double NewsSentimentScore { get; set; } // -1.0 to +1.0
    public double AggregatedSentiment { get; set; } // -100 to +100
    public string SentimentLabel { get; set; } = "Neutral"; // ExtremeFear, Fear, Neutral, Greed, ExtremeGreed
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

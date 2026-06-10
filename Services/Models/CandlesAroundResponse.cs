namespace Backend.Services.Models;

public class CandlesAroundResponse
{
    public string RequestId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public long RequestedTimeMs { get; set; }
    public long? ResolvedTimeMs { get; set; }
    public List<KlineDto> Candles { get; set; } = [];
}

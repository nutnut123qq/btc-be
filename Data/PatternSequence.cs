namespace Backend.Data;

/// <summary>
/// Chuỗi pattern nến liên tiếp (Markov chain context) để phân tích xác suất chuyển đổi.
/// </summary>
public class PatternSequence
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
    public int WindowSize { get; set; }
    public string PatternChainJson { get; set; } = "[]";
    public int Count { get; set; } = 1;
}

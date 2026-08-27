namespace Backend.Services;

public interface IAiContextService
{
    Task<FullMarketContextDto> GetFullMarketContextAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken ct = default);
}

public class FullMarketContextDto
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public double CurrentPrice { get; set; }
    public long ContextTimeMs { get; set; }

    public object? ArchetypeMatch { get; set; }
    public object? MarkovTransitions { get; set; }
    public object? MarketRegime { get; set; }
    public object? MultiTimeframeConfluence { get; set; }
    public object? VolumeProfile { get; set; }
    public object? SmartMoneyStructures { get; set; }
    public object? SentimentAndNews { get; set; }
    public object? MasterEnsemblePrediction { get; set; }
    public object? ActivePaperTrade { get; set; }
}

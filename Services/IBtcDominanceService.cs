namespace Backend.Services;

public interface IBtcDominanceService
{
    Task<BtcDominanceDto> GetBtcDominanceAsync(CancellationToken ct = default);
}

public class BtcDominanceDto
{
    public double DominancePct { get; set; } = 56.4;
    public double Change24hPct { get; set; } = 0.85;
    public string MarketState { get; set; } = "BTC Season (Capital Inflow to BTC)";
    public string Summary { get; set; } = "BTC Dominance 56.4% (+0.85% 24h)";
}

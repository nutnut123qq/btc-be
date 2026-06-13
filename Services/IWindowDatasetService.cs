namespace Backend.Services;

public interface IWindowDatasetService
{
    Task<int> BuildAsync(string symbol, string timeframe, int windowSize, string horizon, CancellationToken ct = default);
    Task<int> BuildAllAsync(string symbol, string timeframe, CancellationToken ct = default);
}

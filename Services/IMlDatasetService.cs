namespace Backend.Services;

public interface IMlDatasetService
{
    Task<int> BuildAsync(string symbol, string timeframe, CancellationToken ct = default);
}

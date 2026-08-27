using Backend.Data;

namespace Backend.Services;

public interface IRegimeDetectionService
{
    Task<MarketRegime?> GetCurrentRegimeAsync(string symbol, string timeframe, CancellationToken ct = default);
    Task<List<MarketRegime>> GetRegimeHistoryAsync(string symbol, string timeframe, int limit, CancellationToken ct = default);
    Task BuildRegimesAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default);
    Task<object> GetRegimeSummaryAsync(string symbol, string timeframe, CancellationToken ct = default);
}

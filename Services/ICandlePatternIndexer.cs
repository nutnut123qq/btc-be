using Backend.Services.Models;

namespace Backend.Services;

public interface ICandlePatternIndexer
{
    Task<int> IndexAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<KlineDto> klines,
        CancellationToken cancellationToken = default);

    Task<int> BuildFullAsync(
        string symbol,
        string timeframe,
        int lookbackBars,
        CancellationToken cancellationToken = default);
}

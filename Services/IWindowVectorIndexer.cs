using Backend.Services.Models;

namespace Backend.Services;

public interface IWindowVectorIndexer
{
    Task<int> BuildFullAsync(
        string symbol,
        string timeframe,
        string featureType,
        int lookbackBars,
        int windowSize,
        CancellationToken cancellationToken = default);

    Task<int> UpsertIncrementalAsync(
        string symbol,
        string timeframe,
        string featureType,
        IReadOnlyList<KlineDto> rows,
        int windowSize,
        CancellationToken cancellationToken = default);

    Task<(int Count, DateTime? LastUpdatedUtc)> GetStatusAsync(
        string symbol,
        string timeframe,
        string featureType,
        int windowSize,
        CancellationToken cancellationToken = default);
}

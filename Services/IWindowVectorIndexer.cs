using Backend.Services.Models;

namespace Backend.Services;

public interface IWindowVectorIndexer
{
    /// <summary>
    /// Xây dựng window vectors cho nhiều feature types và window sizes trong một vòng lặp.
    /// </summary>
    Task<int> BuildAllForTimeframeAsync(
        string symbol,
        string timeframe,
        IReadOnlyList<IOhlcvBar> klines,
        IEnumerable<string> featureTypes,
        IEnumerable<int> windowSizes,
        CancellationToken cancellationToken = default);

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
        IReadOnlyList<IOhlcvBar> rows,
        int windowSize,
        CancellationToken cancellationToken = default);

    Task<(int Count, DateTime? LastUpdatedUtc)> GetStatusAsync(
        string symbol,
        string timeframe,
        string featureType,
        int windowSize,
        CancellationToken cancellationToken = default);
}

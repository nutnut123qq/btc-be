namespace Backend.Services;

public interface IWindowDatasetService
{
    Task<int> BuildAsync(string symbol, string timeframe, int windowSize, string horizon, int? maxSamples = null, CancellationToken ct = default);
    Task<int> BuildAllAsync(string symbol, string timeframe, CancellationToken ct = default);
    Task<int> BuildHorizonAsync(string symbol, string timeframe, string horizon, int? maxSamplesPerWindowSize = null, CancellationToken ct = default);
    Task<(float[] Vector, long WindowStartMs, long WindowEndMs)?> BuildLatestFeatureVectorAsync(string symbol, string timeframe, int windowSize, CancellationToken ct = default);
}

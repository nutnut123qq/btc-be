using Backend.Data;

namespace Backend.Services;

public interface ISentimentService
{
    Task<SentimentSnapshot> GetLatestSentimentAsync(string symbol, CancellationToken ct = default);
    Task<List<SentimentSnapshot>> GetSentimentHistoryAsync(string symbol, int limit, CancellationToken ct = default);
    Task<SentimentSnapshot> CalculateAndSaveSnapshotAsync(string symbol, CancellationToken ct = default);
}

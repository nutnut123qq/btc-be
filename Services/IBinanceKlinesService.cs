using Backend.Services.Models;

namespace Backend.Services;

public interface IBinanceKlinesService
{
    Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
        string symbol = "BTCUSDT",
        string interval = "1h",
        int limit = 48,
        long? startTimeMs = null,
        long? endTimeMs = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(string interval = "1h", int limit = 48, CancellationToken cancellationToken = default);

    Task<string> BuildTechSummaryAsync(string interval = "1h", int limit = 48, CancellationToken cancellationToken = default);
}

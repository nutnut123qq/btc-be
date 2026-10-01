using Backend.Services.Models;

namespace Backend.Services;

public interface IBinanceKlinesService
{
    Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
        string symbol = "BTCUSDT",
        string interval = "4h",
        int limit = 48,
        long? startTimeMs = null,
        long? endTimeMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uncached exact-source read for mutation previews. Production implementations must bypass
    /// display/request caches so apply can detect upstream drift from the dry run.
    /// </summary>
    Task<IReadOnlyList<KlineDto>> GetKlinesForVerificationAsync(
        string symbol,
        string interval,
        int limit,
        long startTimeMs,
        long endTimeMs,
        CancellationToken cancellationToken = default) =>
        GetKlinesAsync(symbol, interval, limit, startTimeMs, endTimeMs, cancellationToken);

    Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(string interval = "4h", int limit = 48, CancellationToken cancellationToken = default);

    Task<string> BuildTechSummaryAsync(string symbol = "BTCUSDT", string interval = "4h", int limit = 48, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketTickerDto>> Get24hTickersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketTradeDto>> GetRecentTradesAsync(string symbol = "BTCUSDT", int limit = 50, CancellationToken cancellationToken = default);

    Task<OrderBookDepthDto> GetOrderBookDepthAsync(string symbol = "BTCUSDT", int limit = 20, CancellationToken cancellationToken = default);
}


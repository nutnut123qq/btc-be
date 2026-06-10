using System.Globalization;
using System.Text.Json;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Lấy dữ liệu thị trường phái sinh (funding rate, open interest) từ Binance Futures.
/// </summary>
public class MarketMetricsIndexer
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MarketMetricsIndexer> _logger;

    public MarketMetricsIndexer(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<MarketMetricsIndexer> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<int> IndexFundingRateAsync(
        string symbol = "BTCUSDT",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Binance");
            var url = $"https://fapi.binance.com/fapi/v1/fundingRate?symbol={Uri.EscapeDataString(symbol)}&limit=1000";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance funding rate fetch failed: {Status}", response.StatusCode);
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;

            var existing = await _db.MarketMetrics
                .AsNoTracking()
                .Where(m => m.Symbol == symbol && m.FundingRate != null)
                .Select(m => m.OpenTimeMs)
                .ToListAsync(cancellationToken);
            var existingSet = new HashSet<long>(existing);

            var toAdd = new List<MarketMetrics>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var fundingTime = item.GetProperty("fundingTime").GetInt64();
                var rate = double.Parse(item.GetProperty("fundingRate").GetString()!, CultureInfo.InvariantCulture);
                if (existingSet.Contains(fundingTime)) continue;

                toAdd.Add(new MarketMetrics
                {
                    Symbol = symbol,
                    Timeframe = "8h",
                    OpenTimeMs = fundingTime,
                    FundingRate = rate
                });
            }

            if (toAdd.Count > 0)
            {
                _db.MarketMetrics.AddRange(toAdd);
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Indexed {Count} funding rates for {Symbol}", toAdd.Count, symbol);
            }
            return toAdd.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Funding rate indexing failed");
            return 0;
        }
    }

    public async Task<int> IndexOpenInterestAsync(
        string symbol = "BTCUSDT",
        string timeframe = "1h",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Binance");
            var url = $"https://fapi.binance.com/fapi/v1/openInterest?symbol={Uri.EscapeDataString(symbol)}&period={timeframe}&limit=500";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance open interest fetch failed: {Status}", response.StatusCode);
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;

            var existing = await _db.MarketMetrics
                .AsNoTracking()
                .Where(m => m.Symbol == symbol && m.Timeframe == timeframe && m.OpenInterest != null)
                .Select(m => m.OpenTimeMs)
                .ToListAsync(cancellationToken);
            var existingSet = new HashSet<long>(existing);

            var toAdd = new List<MarketMetrics>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var openTime = item.GetProperty("timestamp").GetInt64();
                var oi = double.Parse(item.GetProperty("sumOpenInterest").GetString()!, CultureInfo.InvariantCulture);
                if (existingSet.Contains(openTime)) continue;

                toAdd.Add(new MarketMetrics
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    OpenTimeMs = openTime,
                    OpenInterest = oi
                });
            }

            if (toAdd.Count > 0)
            {
                _db.MarketMetrics.AddRange(toAdd);
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Indexed {Count} open interest for {Symbol} {Timeframe}", toAdd.Count, symbol, timeframe);
            }
            return toAdd.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open interest indexing failed");
            return 0;
        }
    }
}

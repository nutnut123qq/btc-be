using System.Globalization;
using System.Text.Json;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Lấy dữ liệu thị trường phái sinh (funding rate, open interest, liquidation) từ Binance Futures.
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

            await UpdateFundingRateZscoreAsync(symbol, cancellationToken);
            return toAdd.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Funding rate indexing failed");
            return 0;
        }
    }

    private async Task UpdateFundingRateZscoreAsync(string symbol, CancellationToken cancellationToken)
    {
        var rates = await _db.MarketMetrics
            .Where(m => m.Symbol == symbol && m.FundingRate != null)
            .OrderBy(m => m.OpenTimeMs)
            .ToListAsync(cancellationToken);

        const int period = 30;
        for (int i = 0; i < rates.Count; i++)
        {
            if (i < period) continue;
            var slice = rates.Skip(i - period).Take(period).Select(r => r.FundingRate!.Value).ToList();
            var avg = slice.Average();
            var std = Math.Sqrt(slice.Average(r => (r - avg) * (r - avg)));
            rates[i].FundingRateZscore = std == 0 ? 0 : (rates[i].FundingRate!.Value - avg) / std;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Updated {Count} funding rate z-scores for {Symbol}", rates.Count, symbol);
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

            await UpdateOiDeltaAsync(symbol, timeframe, cancellationToken);
            return toAdd.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open interest indexing failed");
            return 0;
        }
    }

    private async Task UpdateOiDeltaAsync(string symbol, string timeframe, CancellationToken cancellationToken)
    {
        var bars = await _db.MarketMetrics
            .Where(m => m.Symbol == symbol && m.Timeframe == timeframe && m.OpenInterest != null)
            .OrderBy(m => m.OpenTimeMs)
            .ToListAsync(cancellationToken);

        // 24h lookback = 24 bars for 1h
        var lookback = timeframe == "1h" ? 24 : 1;
        for (int i = 0; i < bars.Count; i++)
        {
            var prevIdx = i - lookback;
            if (prevIdx < 0) continue;
            var prev = bars[prevIdx].OpenInterest;
            var curr = bars[i].OpenInterest;
            if (prev.HasValue && prev.Value > 0)
                bars[i].OiDeltaPct = (curr!.Value - prev.Value) / prev.Value * 100.0;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Updated {Count} OI deltas for {Symbol} {Timeframe}", bars.Count, symbol, timeframe);
    }

    public async Task<int> IndexLiquidationsAsync(
        string symbol = "BTCUSDT",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Binance");
            var endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var startTime = endTime - 7 * 24 * 60 * 60 * 1000; // 7 days
            var url = $"https://fapi.binance.com/fapi/v1/forceOrders?symbol={Uri.EscapeDataString(symbol)}&startTime={startTime}&endTime={endTime}&limit=1000";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance liquidation fetch failed: {Status} (may require API key)", response.StatusCode);
                return 0;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;

            // Aggregate by hour
            var buckets = new Dictionary<long, (double LongUsd, double ShortUsd)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var time = item.GetProperty("time").GetInt64();
                var hour = time / (60 * 60 * 1000) * (60 * 60 * 1000);
                var side = item.GetProperty("side").GetString();
                var price = double.Parse(item.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
                var qty = double.Parse(item.GetProperty("qty").GetString()!, CultureInfo.InvariantCulture);
                var usd = price * qty;

                if (!buckets.ContainsKey(hour))
                    buckets[hour] = (0, 0);
                var current = buckets[hour];
                if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase))
                    buckets[hour] = (current.LongUsd + usd, current.ShortUsd);
                else
                    buckets[hour] = (current.LongUsd, current.ShortUsd + usd);
            }

            var existing = await _db.MarketMetrics
                .AsNoTracking()
                .Where(m => m.Symbol == symbol && (m.LongLiquidationUsd != null || m.ShortLiquidationUsd != null))
                .Select(m => m.OpenTimeMs)
                .ToListAsync(cancellationToken);
            var existingSet = new HashSet<long>(existing);

            var toAdd = new List<MarketMetrics>();
            foreach (var kv in buckets)
            {
                if (existingSet.Contains(kv.Key)) continue;
                toAdd.Add(new MarketMetrics
                {
                    Symbol = symbol,
                    Timeframe = "1h",
                    OpenTimeMs = kv.Key,
                    LongLiquidationUsd = kv.Value.LongUsd,
                    ShortLiquidationUsd = kv.Value.ShortUsd
                });
            }

            if (toAdd.Count > 0)
            {
                _db.MarketMetrics.AddRange(toAdd);
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Indexed {Count} liquidation buckets for {Symbol}", toAdd.Count, symbol);
            }
            return toAdd.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Liquidation indexing failed");
            return 0;
        }
    }
}

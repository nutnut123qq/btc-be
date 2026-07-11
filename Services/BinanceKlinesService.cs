using System.Globalization;
using System.Text.Json;
using Backend.Services.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Services;

public class BinanceKlinesService : IBinanceKlinesService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BinanceKlinesService> _logger;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    public BinanceKlinesService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<BinanceKlinesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<KlineDto>> GetKlinesAsync(
        string symbol = "BTCUSDT",
        string interval = "1h",
        int limit = 48,
        long? startTimeMs = null,
        long? endTimeMs = null,
        CancellationToken cancellationToken = default)
    {
        // ponytail: 20s cache — chart refresh + repeated AI/analysis calls hit Binance once
        // instead of per request (latency + rate-limit). Closed/historical ranges are
        // immutable so 20s is conservative; raise TTL for endTime-bounded ranges if needed.
        var cacheKey = $"klines:{symbol}:{interval}:{limit}:{startTimeMs}:{endTimeMs}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<KlineDto>? cached) && cached is not null)
            return cached;

        var fresh = await FetchKlinesAsync(symbol, interval, limit, startTimeMs, endTimeMs, cancellationToken);
        _cache.Set(cacheKey, fresh, CacheTtl);
        return fresh;
    }

    private async Task<IReadOnlyList<KlineDto>> FetchKlinesAsync(
        string symbol,
        string interval,
        int limit,
        long? startTimeMs,
        long? endTimeMs,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("Binance");
        var query = new List<string>
        {
            $"symbol={Uri.EscapeDataString(symbol)}",
            $"interval={Uri.EscapeDataString(interval)}",
            $"limit={limit}"
        };
        if (startTimeMs.HasValue) query.Add($"startTime={startTimeMs.Value}");
        if (endTimeMs.HasValue) query.Add($"endTime={endTimeMs.Value}");
        var url = $"https://api.binance.com/api/v3/klines?{string.Join("&", query)}";

        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Binance klines failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Binance API error: {response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<KlineDto>();

        var list = new List<KlineDto>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.GetArrayLength() < 6)
                continue;

            var openTime = row[0].GetInt64();
            var open = decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture);
            var high = decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture);
            var low = decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture);
            var close = decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture);
            var volume = decimal.Parse(row[5].GetString()!, CultureInfo.InvariantCulture);
            var closeTime = row[6].GetInt64();
            var quoteVolume = row.GetArrayLength() > 7 ? decimal.Parse(row[7].GetString()!, CultureInfo.InvariantCulture) : 0m;
            var tradeCount = row.GetArrayLength() > 8 ? row[8].GetInt32() : 0;
            var takerBuyVolume = row.GetArrayLength() > 9 ? decimal.Parse(row[9].GetString()!, CultureInfo.InvariantCulture) : 0m;
            var takerBuyQuoteVolume = row.GetArrayLength() > 10 ? decimal.Parse(row[10].GetString()!, CultureInfo.InvariantCulture) : 0m;
            if (high < low)
                continue;

            list.Add(new KlineDto
            {
                OpenTimeMs = openTime,
                TimeIso = DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime.ToString("o"),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                CloseTimeMs = closeTime,
                QuoteVolume = quoteVolume,
                TradeCount = tradeCount,
                TakerBuyVolume = takerBuyVolume,
                TakerBuyQuoteVolume = takerBuyQuoteVolume
            });
        }

        return list
            .GroupBy(x => x.OpenTimeMs)
            .Select(g => g.Last())
            .OrderBy(x => x.OpenTimeMs)
            .ToList();
    }

    public async Task<IReadOnlyList<KlineDto>> GetBtcKlinesAsync(
        string interval = "1h",
        int limit = 48,
        CancellationToken cancellationToken = default)
    {
        return await GetKlinesAsync(
            symbol: "BTCUSDT",
            interval: interval,
            limit: limit,
            cancellationToken: cancellationToken);
    }

    public async Task<string> BuildTechSummaryAsync(
        string interval = "1h",
        int limit = 48,
        CancellationToken cancellationToken = default)
    {
        var klines = await GetBtcKlinesAsync(interval, limit, cancellationToken);
        if (klines.Count == 0)
            return "No kline data returned from Binance.";

        var first = klines[0];
        var last = klines[^1];
        var high = klines.Max(k => k.High);
        var low = klines.Min(k => k.Low);
        var changePct = first.Close != 0
            ? (double)((last.Close - first.Close) / first.Close * 100m)
            : 0;

        var closes = klines.Select(k => k.Close).ToList();
        var rsi = ComputeSimpleRsi(closes, period: 14);
        var rsiStr = double.IsNaN(rsi) ? "n/a (not enough bars)" : $"{rsi:F2}";

        var sma50 = ComputeSma(closes, period: 50);
        var sma200 = ComputeSma(closes, period: 200);
        var sma50Str = sma50.HasValue ? $"{sma50.Value:F2}" : "n/a (not enough bars)";
        var sma200Str = sma200.HasValue ? $"{sma200.Value:F2}" : "n/a (not enough bars)";
        var smaPosition = GetSmaPosition(last.Close, sma50, sma200);

        var patternResult = CandlePatternRecognizer.Recognize(klines, tailCount: Math.Min(30, klines.Count));
        var volumeSummary = VolumeAnalyzer.Summarize(patternResult.Candles);

        return $"""
            BTC/USDT ({interval} candles, last {klines.Count} bars from Binance).
            First bar close (oldest in window): {first.Close:F2} USDT at {first.TimeIso}.
            Last bar close (newest): {last.Close:F2} USDT at {last.TimeIso}.
            Period high: {high:F2}, period low: {low:F2}.
            Approximate change from oldest to newest close in window: {changePct:F2}%.
            Simple RSI(14) on closes (last window): {rsiStr}.
            SMA(50): {sma50Str}, SMA(200): {sma200Str}. Position: {smaPosition}.

            {patternResult.SummaryText}

            {volumeSummary}
            """;
    }

    private static double ComputeSimpleRsi(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count < period + 1)
            return double.NaN;

        double sumGain = 0, sumLoss = 0;
        var start = closes.Count - period;
        for (var i = start; i < closes.Count; i++)
        {
            var delta = (double)(closes[i] - closes[i - 1]);
            if (delta >= 0) sumGain += delta;
            else sumLoss -= delta;
        }

        var avgGain = sumGain / period;
        var avgLoss = sumLoss / period;
        if (avgLoss == 0)
            return 100;
        var rs = avgGain / avgLoss;
        return 100 - 100 / (1 + rs);
    }

    private static decimal? ComputeSma(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count < period)
            return null;
        var slice = closes.Skip(closes.Count - period).Take(period);
        return slice.Average();
    }

    private static string GetSmaPosition(decimal lastClose, decimal? sma50, decimal? sma200)
    {
        if (!sma50.HasValue || !sma200.HasValue)
            return "n/a (not enough bars for both SMAs)";

        var above50 = lastClose > sma50.Value;
        var above200 = lastClose > sma200.Value;
        var goldenCross = sma50.Value > sma200.Value;

        return (above50, above200, goldenCross) switch
        {
            (true, true, true) => "price above both SMAs (bullish / golden cross)",
            (true, true, false) => "price above both SMAs but SMA50 below SMA200 (mixed)",
            (false, false, false) => "price below both SMAs (bearish / death cross)",
            (false, false, true) => "price below both SMAs but SMA50 above SMA200 (mixed)",
            (true, false, _) => "price between SMA50 and SMA200 (testing SMA200 resistance)",
            (false, true, _) => "price between SMA200 and SMA50 (testing SMA50 support)",
        };
    }
}
